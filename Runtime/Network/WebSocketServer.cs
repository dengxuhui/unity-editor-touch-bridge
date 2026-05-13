using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace MobileBridge
{
    /// <summary>
    /// Runs two TLS-enabled servers:
    ///   port 8765 — WebSocket (WSS): binary frame push down, JSON touch up
    ///   port 8766 — HTTPS static file server: serves client.html on GET /
    ///                                          serves cert as DER on GET /cert
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
            var cert = CertificateHelper.Load();
            if (cert == null)
                throw new InvalidOperationException(
                    "[MobileBridge] No TLS certificate found. " +
                    "Please generate one first via Window > Mobile Bridge > Generate Certificate.");

            // ── WebSocket server (port 8765, WSS) ─────────────────────────────
            _wsServer = new WebSocketSharp.Server.WebSocketServer(WsPort, secure: true);
            ConfigureSsl(_wsServer.SslConfiguration, cert);
            _wsServer.AddWebSocketService<BridgeBehavior>("/", b => b.Init(FireTouchMessage));
            _wsServer.Start();

            // ── HTTP server (port 8766, HTTPS) ────────────────────────────────
            _httpServer = new HttpServer(HttpPort, secure: true);
            ConfigureSsl(_httpServer.SslConfiguration, cert);
            _httpServer.OnGet += HandleHttpGet;
            _httpServer.Start();

            Debug.Log($"[MobileBridge] WSS  listening on :{WsPort}");
            Debug.Log($"[MobileBridge] HTTPS listening on :{HttpPort}");
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

        // ── SSL configuration helper ───────────────────────────────────────────

        private static void ConfigureSsl(WebSocketSharp.Net.ServerSslConfiguration ssl, X509Certificate2 cert)
        {
            ssl.ServerCertificate = cert;
            ssl.EnabledSslProtocols =
                System.Security.Authentication.SslProtocols.Tls12;
            // Client does not send a certificate; accept all (iOS user-installed cert)
            ssl.ClientCertificateValidationCallback = (_, __, ___, ____) => true;
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
                else if (path == "/cert")
                {
                    ServeCert(res);
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

        private static void ServeCert(HttpListenerResponse res)
        {
            try
            {
                var cert    = CertificateHelper.Load();
                var derBytes = cert.Export(X509ContentType.Cert); // DER, public key only
                res.ContentType = "application/x-x509-ca-cert";
                res.Headers.Add("Content-Disposition", "attachment; filename=\"mobileBridge.cer\"");
                res.ContentLength64 = derBytes.Length;
                res.OutputStream.Write(derBytes, 0, derBytes.Length);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MobileBridge] /cert error: {ex.Message}");
                res.StatusCode = 500;
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
