using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace MobileBridge
{
    /// <summary>
    /// Runs two local-network servers:
    ///   port 8765 — WebSocket (WS): binary frame push down, JSON touch up
    ///   port 8766 — HTTP static file server: serves client.html on GET /
    /// </summary>
    public sealed class WebSocketServer : IDisposable
    {
        public const int WsPort   = 8765;
        public const int HttpPort = 8766;

        private WebSocketSharp.Server.WebSocketServer _wsServer;
        private HttpServer                             _httpServer;
        private readonly string                        _clientHtmlPath;
        private bool                                   _disposed;

        // Session counter — incremented/decremented by WS callbacks (thread-pool threads).
        private int _sessionCount;

        // Broadcast diagnostics
        private long _totalBroadcasts;
        private long _totalBroadcastMs;
        private long _slowBroadcastCount;  // > 300 ms

        // Heartbeat
        private Timer _heartbeatTimer;

        public event Action<string> OnTouchMessage;

        /// <summary>Fired on the thread-pool when a new client session opens. Arg is session ID.</summary>
        public event Action<string> OnClientConnected;

        /// <summary>Fired on the thread-pool when a client sends {"type":"hello"}. Arg is session ID.</summary>
        public event Action<string> OnHelloReceived;

        public int ClientCount =>
            _wsServer?.WebSocketServices["/"]?.Sessions.Count ?? 0;

        public WebSocketServer(string clientHtmlPath)
        {
            _clientHtmlPath = clientHtmlPath;
        }

        public void Start()
        {
            // ── WebSocket server (port 8765) ───────────────────────────────────
            _wsServer = new WebSocketSharp.Server.WebSocketServer(WsPort, secure: false);
            _wsServer.AddWebSocketService<BridgeBehavior>("/", b => b.Init(FireTouchMessage, OnSessionEvent));
            _wsServer.Start();

            // ── HTTP server (port 8766) ────────────────────────────────────────
            _httpServer = new HttpServer(HttpPort, secure: false);
            _httpServer.OnGet += HandleHttpGet;
            _httpServer.Start();

            // ── Heartbeat timer (every 1 s) ───────────────────────────────────
            _heartbeatTimer = new Timer(_ => SendHeartbeat(), null,
                dueTime:  TimeSpan.FromSeconds(1),
                period:   TimeSpan.FromSeconds(1));

            MobileBridge.MBLog($"[MB][WebSocketServer] WS listening on :{WsPort}");
            MobileBridge.MBLog($"[MB][WebSocketServer] HTTP listening on :{HttpPort}");
        }

        public void Stop()
        {
            try { _heartbeatTimer?.Dispose(); _heartbeatTimer = null; } catch { }
            try { _wsServer?.Stop(); }   catch { }
            try { _httpServer?.Stop(); } catch { }

            MobileBridge.MBLog($"[MB][WebSocketServer] Stopped. totalBroadcasts={_totalBroadcasts} " +
                      $"avgBroadcastMs={(_totalBroadcasts > 0 ? _totalBroadcastMs / _totalBroadcasts : 0)} " +
                      $"slowBroadcasts={_slowBroadcastCount}");
        }

        /// <summary>Broadcast a text message to all connected clients.</summary>
        public void BroadcastText(string text)
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null || sessions.Count == 0)
            {
                MobileBridge.MBLog($"[MB][WebSocketServer] BroadcastText skipped — no sessions (text={text})");
                return;
            }
            MobileBridge.MBLog($"[MB][WebSocketServer] BroadcastText clients={sessions.Count} text={text}");
            try { sessions.Broadcast(text); }
            catch (Exception ex)
            {
                MobileBridge.MBLogWarning($"[MB][WebSocketServer] WARN broadcast_text_exception — {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Send a text message to a single session by ID.</summary>
        public void SendText(string sessionId, string text)
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null) return;
            MobileBridge.MBLog($"[MB][WebSocketServer] SendText id={sessionId} text={text}");
            try
            {
                sessions.SendTo(text, sessionId);
            }
            catch (Exception ex)
            {
                MobileBridge.MBLogWarning($"[MB][WebSocketServer] WARN send_text_exception id={sessionId} — {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Broadcast a JPEG frame to all connected clients.</summary>
        public Task BroadcastFrameAsync(byte[] jpegBytes)
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null) return Task.CompletedTask;

            int count = sessions.Count;
            if (count == 0) return Task.CompletedTask;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                sessions.Broadcast(jpegBytes);
            }
            catch (Exception ex)
            {
                sw.Stop();
                MobileBridge.MBLogWarning(
                    $"[MB][WebSocketServer] WARN broadcast_exception — " +
                    $"{ex.GetType().Name}: {ex.Message} clients={count}");
                return Task.CompletedTask;
            }
            sw.Stop();

            long ms = sw.ElapsedMilliseconds;
            Interlocked.Increment(ref _totalBroadcasts);
            Interlocked.Add(ref _totalBroadcastMs, ms);

            if (ms > 300)
            {
                Interlocked.Increment(ref _slowBroadcastCount);
                MobileBridge.MBLogWarning(
                    $"[MB][WebSocketServer] WARN broadcast_slow — " +
                    $"{ms}ms for {count} client(s) frameSize={jpegBytes.Length}B");
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        /// <summary>Broadcast a lightweight heartbeat text frame every second.
        /// Lets the client distinguish "ws alive but no video" from "truly disconnected".</summary>
        private void SendHeartbeat()
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null || sessions.Count == 0) return;
            try
            {
                long t = (long)(DateTime.UtcNow - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
                sessions.Broadcast($"{{\"type\":\"hb\",\"t\":{t}}}");
            }
            catch { }
        }

        // ── HTTP request handler ───────────────────────────────────────────────

        private void HandleHttpGet(object sender, HttpRequestEventArgs e)
        {
            var req  = e.Request;
            var res  = e.Response;
            var path = req.RawUrl?.Split('?')[0] ?? "/";

            try
            {
                if (path == "/" || path == "/index.html")
                    ServeClientHtml(res);
                else
                    res.StatusCode = 404;
            }
            catch (Exception ex)
            {
                MobileBridge.MBLogWarning($"[MB][WebSocketServer] HTTP error: {ex.Message}");
                res.StatusCode = 500;
            }
        }

        private void ServeClientHtml(HttpListenerResponse res)
        {
            if (File.Exists(_clientHtmlPath))
            {
                var html = File.ReadAllBytes(_clientHtmlPath);
                res.ContentType     = "text/html; charset=utf-8";
                res.ContentLength64 = html.Length;
                res.OutputStream.Write(html, 0, html.Length);
            }
            else
            {
                res.StatusCode = 503;
                var msg = System.Text.Encoding.UTF8.GetBytes("client.html not found");
                res.OutputStream.Write(msg, 0, msg.Length);
            }
        }

        // ── Touch message relay ────────────────────────────────────────────────

        private void FireTouchMessage(string msg) => OnTouchMessage?.Invoke(msg);

        private void OnSessionEvent(string sessionId, string eventName, string detail)
        {
            int count = ClientCount;
            MobileBridge.MBLog($"[MB][WebSocketServer] session_{eventName} id={sessionId} clients={count} {detail}");
            if (eventName == "open")
                OnClientConnected?.Invoke(sessionId);
            else if (eventName == "hello")
                OnHelloReceived?.Invoke(sessionId);
        }

        // ── WebSocket behaviour ────────────────────────────────────────────────

        private sealed class BridgeBehavior : WebSocketBehavior
        {
            private Action<string>              _onMessage;
            private Action<string,string,string> _onEvent;

            public void Init(Action<string> onMessage, Action<string,string,string> onEvent)
            {
                _onMessage = onMessage;
                _onEvent   = onEvent;
            }

            protected override void OnOpen()
            {
                _onEvent?.Invoke(ID, "open", "");
            }

            protected override void OnClose(CloseEventArgs e)
            {
                _onEvent?.Invoke(ID, "close", $"code={e.Code} reason={e.Reason} wasClean={e.WasClean}");
            }

            protected override void OnError(WebSocketSharp.ErrorEventArgs e)
            {
                MobileBridge.MBLogWarning($"[MB][WebSocketServer] WARN session_error id={ID} msg={e.Message}");
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                if (!e.IsText) return;
                // Check for hello handshake before forwarding as touch message
                try
                {
                    // Fast path: only parse if looks like a hello
                    if (e.Data.Contains("\"hello\""))
                    {
                        var msg = e.Data.Trim();
                        // Simple check without full JSON parse to avoid allocation on every touch event
                        if (msg.Contains("\"type\"") && msg.Contains("\"hello\""))
                        {
                            _onEvent?.Invoke(ID, "hello", "");
                            return;
                        }
                    }
                }
                catch { }
                _onMessage?.Invoke(e.Data);
            }
        }
    }
}
