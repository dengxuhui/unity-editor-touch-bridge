using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace MobileBridge.Editor
{
    /// <summary>
    /// Runs two local-network servers:
    ///   port 8765 — WebSocket (WS): binary frame push down, JSON touch up
    ///   port 8766 — HTTP static file server: serves client.html on GET /
    ///
    /// Lives in the Editor assembly so websocket-sharp.dll is never included
    /// in player builds.
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

        // Broadcast diagnostics — cumulative
        private long _totalBroadcasts;
        private long _totalBroadcastUs;    // microseconds (replaces ms for sub-ms precision)
        private long _slowBroadcastCount;  // > 300 ms
        private long _slowBroadcast50ms;   // > 50 ms
        private long _slowBroadcast5ms;    // > 5 ms
        private long _slowBroadcast1ms;    // > 1 ms

        // Per-second window (reset each heartbeat tick)
        private long _windowStartMs;
        private long _windowTxCount;
        private long _windowTxSumUs;
        private long _windowTxMaxUs;   // racy non-atomic update, OK for diagnostics
        private long _windowTxBytes;

        // Current session tracking (reset on session_open)
        private long _sessOpenMs;
        private long _sessTxFrames;
        private long _sessTxBytes;

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

            Debug.Log($"[MB][WebSocketServer] WS listening on :{WsPort}");
            Debug.Log($"[MB][WebSocketServer] HTTP listening on :{HttpPort}");
        }

        public void Stop()
        {
            try { _heartbeatTimer?.Dispose(); _heartbeatTimer = null; } catch { }
            try { _wsServer?.Stop(); }   catch { }
            try { _httpServer?.Stop(); } catch { }

            long avgUs = _totalBroadcasts > 0 ? _totalBroadcastUs / _totalBroadcasts : 0;
            Debug.Log(
                $"[MB][WebSocketServer] Stopped. " +
                $"totalBroadcasts={_totalBroadcasts} avgSendUs={avgUs} " +
                $"slow>1ms={_slowBroadcast1ms} slow>5ms={_slowBroadcast5ms} " +
                $"slow>50ms={_slowBroadcast50ms} slow>300ms={_slowBroadcastCount}");
        }

        /// <summary>Broadcast a text message to all connected clients.</summary>
        public void BroadcastText(string text)
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null || sessions.Count == 0)
            {
                Debug.Log($"[MB][WebSocketServer] BroadcastText skipped — no sessions (text={text})");
                return;
            }
            Debug.Log($"[MB][WebSocketServer] BroadcastText clients={sessions.Count} text={text}");
            try { sessions.Broadcast(text); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MB][WebSocketServer] WARN broadcast_text_exception — {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Send a text message to a single session by ID.</summary>
        public void SendText(string sessionId, string text)
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null) return;
            Debug.Log($"[MB][WebSocketServer] SendText id={sessionId} text={text}");
            try
            {
                sessions.SendTo(text, sessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MB][WebSocketServer] WARN send_text_exception id={sessionId} — {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Broadcast a JPEG frame to all connected clients (synchronous).</summary>
        public void BroadcastFrame(byte[] jpegBytes)
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions == null) return;

            int count = sessions.Count;
            if (count == 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                sessions.Broadcast(jpegBytes);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Debug.LogWarning(
                    $"[MB][WebSocketServer] WARN broadcast_exception — " +
                    $"{ex.GetType().Name}: {ex.Message} clients={count}");
                return;
            }
            sw.Stop();

            // Microsecond precision: 1 tick = 1/Frequency seconds
            long elapsedUs = sw.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

            Interlocked.Increment(ref _totalBroadcasts);
            Interlocked.Add(ref _totalBroadcastUs, elapsedUs);
            Interlocked.Increment(ref _windowTxCount);
            Interlocked.Add(ref _windowTxSumUs, elapsedUs);
            Interlocked.Add(ref _windowTxBytes, jpegBytes.Length);
            Interlocked.Add(ref _sessTxBytes, jpegBytes.Length);
            Interlocked.Increment(ref _sessTxFrames);

            // Approximate max (racy non-atomic, acceptable for diagnostics)
            if (elapsedUs > Volatile.Read(ref _windowTxMaxUs))
                Volatile.Write(ref _windowTxMaxUs, elapsedUs);

            // Slow-send buckets — strictly nested (only one bucket incremented per call)
            if      (elapsedUs >= 300_000) { Interlocked.Increment(ref _slowBroadcastCount); }
            else if (elapsedUs >=  50_000) { Interlocked.Increment(ref _slowBroadcast50ms);  }
            else if (elapsedUs >=   5_000) { Interlocked.Increment(ref _slowBroadcast5ms);   }
            else if (elapsedUs >=   1_000) { Interlocked.Increment(ref _slowBroadcast1ms);   }

            // Real-time warning for anything suspicious (> 50 ms = TCP send-buffer pressure)
            if (elapsedUs >= 50_000)
            {
                double ms = elapsedUs / 1000.0;
                Debug.LogWarning(
                    $"[MB][WebSocketServer] WARN broadcast_slow — " +
                    $"{ms:F1}ms for {count} client(s) frameSize={jpegBytes.Length}B");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        /// <summary>Broadcast a lightweight heartbeat text frame every second and print per-second STATS.</summary>
        private void SendHeartbeat()
        {
            var sessions = _wsServer?.WebSocketServices["/"]?.Sessions;
            if (sessions != null && sessions.Count > 0)
            {
                try
                {
                    long t = (long)(DateTime.UtcNow - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalMilliseconds;
                    sessions.Broadcast($"{{\"type\":\"hb\",\"t\":{t}}}");
                }
                catch { }
            }

            // ── Per-second STATS ─────────────────────────────────────────────
            long nowMs = (long)Environment.TickCount;
            if (_windowStartMs == 0) { _windowStartMs = nowMs; return; }

            long windowMs = nowMs - _windowStartMs;
            if (windowMs < 900) return;   // guard against timer jitter
            _windowStartMs = nowMs;

            long cnt     = Interlocked.Exchange(ref _windowTxCount,  0);
            long sumUs   = Interlocked.Exchange(ref _windowTxSumUs,  0);
            long maxUs   = Interlocked.Exchange(ref _windowTxMaxUs,  0);
            long txBytes = Interlocked.Exchange(ref _windowTxBytes,  0);

            float txFps   = windowMs > 0 ? cnt   * 1000f / windowMs : 0;
            float txKBs   = windowMs > 0 ? txBytes * 1000f / 1024f / windowMs : 0;
            float avgUs   = cnt > 0 ? sumUs / (float)cnt : 0;
            float maxMs   = maxUs / 1000f;
            int   clients = ClientCount;

            Debug.Log(
                $"[MB][WebSocketServer] STATS " +
                $"txFps={txFps:F1} txKBs={txKBs:F0} " +
                $"avgSendUs={avgUs:F0} maxSendMs={maxMs:F2} " +
                $"slow>1ms={_slowBroadcast1ms} slow>5ms={_slowBroadcast5ms} " +
                $"slow>50ms={_slowBroadcast50ms} slow>300ms={_slowBroadcastCount} " +
                $"clients={clients}");
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
                Debug.LogWarning($"[MB][WebSocketServer] HTTP error: {ex.Message}");
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

            if (eventName == "open")
            {
                _sessOpenMs  = (long)Environment.TickCount;
                Interlocked.Exchange(ref _sessTxFrames, 0);
                Interlocked.Exchange(ref _sessTxBytes,  0);
                OnClientConnected?.Invoke(sessionId);
            }
            else if (eventName == "close")
            {
                long durMs   = (long)Environment.TickCount - _sessOpenMs;
                long frames  = Interlocked.Read(ref _sessTxFrames);
                long txBytes = Interlocked.Read(ref _sessTxBytes);
                float avgKbps = durMs > 0 ? txBytes * 8f / durMs : 0;   // bytes/ms * 8 = kbps
                Debug.Log(
                    $"[MB][WebSocketServer] session_summary " +
                    $"id={sessionId} duration={durMs}ms " +
                    $"txFrames={frames} txMB={txBytes / 1048576f:F2} " +
                    $"avgKbps={avgKbps:F0} {detail}");
            }
            else if (eventName == "hello")
            {
                OnHelloReceived?.Invoke(sessionId);
            }

            Debug.Log($"[MB][WebSocketServer] session_{eventName} id={sessionId} clients={count} {detail}");
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
                Debug.LogWarning($"[MB][WebSocketServer] WARN session_error id={ID} msg={e.Message}");
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                if (!e.IsText) return;
                try
                {
                    if (e.Data.Contains("\"hello\""))
                    {
                        var msg = e.Data.Trim();
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
