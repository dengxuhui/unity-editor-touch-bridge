using System;
using System.IO;
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

        public event Action<string> OnTouchMessage;

        public int ClientCount =>
            _wsServer?.WebSocketServices["/"]?.Sessions.Count ?? 0;

        public WebSocketServer(string clientHtmlPath)
        {
            _clientHtmlPath = clientHtmlPath;
        }

        public void Start()
        {
            // ── WebSocket server (port 8765, WS) ──────────────────────────────
            _wsServer = new WebSocketSharp.Server.WebSocketServer(WsPort, secure: false);
            _wsServer.AddWebSocketService<BridgeBehavior>("/", b => b.Init(FireTouchMessage));
            _wsServer.Start();

            // ── HTTP server (port 8766, HTTP) ─────────────────────────────────
            _httpServer = new HttpServer(HttpPort, secure: false);
            _httpServer.OnGet += HandleHttpGet;
            _httpServer.Start();

            Debug.Log($"[MobileBridge] WS   listening on :{WsPort}");
            Debug.Log($"[MobileBridge] HTTP listening on :{HttpPort}");
        }

        public void Stop()
        {
            try { _wsServer?.Stop(); }   catch { }
            try { _httpServer?.Stop(); } catch { }
        }

        /// <summary>Broadcast a JPEG frame to all connected clients.</summary>
        public Task BroadcastFrameAsync(byte[] jpegBytes)
        {
            _wsServer?.WebSocketServices["/"]?.Sessions.Broadcast(jpegBytes);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
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
                {
                    ServeClientHtml(res);
                }
                else
                {
                    res.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MobileBridge] HTTP error: {ex.Message}");
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

        // ── WebSocket behaviour ────────────────────────────────────────────────

        private sealed class BridgeBehavior : WebSocketBehavior
        {
            private Action<string> _onMessage;

            public void Init(Action<string> onMessage) { _onMessage = onMessage; }

            protected override void OnOpen()
            {
                Debug.Log("[MobileBridge] Client connected.");
            }

            protected override void OnClose(CloseEventArgs e)
            {
                Debug.Log($"[MobileBridge] Client disconnected (code={e.Code}).");
            }

            protected override void OnError(WebSocketSharp.ErrorEventArgs e)
            {
                Debug.LogWarning($"[MobileBridge] WS error: {e.Message}");
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                if (e.IsText) _onMessage?.Invoke(e.Data);
            }
        }
    }
}
