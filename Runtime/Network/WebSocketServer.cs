using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MobileBridge
{
    /// <summary>
    /// Runs two listeners:
    ///   port 8765 — WebSocket (WS/WSS): binary frame push down, JSON touch up
    ///   port 8766 — HTTP/HTTPS: serves client.html on GET /
    /// </summary>
    public sealed class WebSocketServer : IDisposable
    {
        public const int WsPort   = 8765;
        public const int HttpPort = 8766;

        private HttpListener _wsListener;
        private HttpListener _httpListener;
        private readonly List<WebSocket> _clients = new List<WebSocket>();
        private readonly object _clientsLock = new object();
        private CancellationTokenSource _cts;

        private readonly string _clientHtmlPath;
        private bool _disposed;

        public event Action<string> OnTouchMessage;

        public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

        public WebSocketServer(string clientHtmlPath)
        {
            _clientHtmlPath = clientHtmlPath;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();

            _wsListener = new HttpListener();
            _wsListener.Prefixes.Add($"http://+:{WsPort}/");
            _wsListener.Start();

            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://+:{HttpPort}/");
            _httpListener.Start();

            Task.Run(() => AcceptWsLoop(_cts.Token));
            Task.Run(() => AcceptHttpLoop(_cts.Token));

            Debug.Log($"[MobileBridge] WS  listening on :{WsPort}");
            Debug.Log($"[MobileBridge] HTTP listening on :{HttpPort}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _wsListener?.Stop(); }   catch { }
            try { _httpListener?.Stop(); } catch { }

            lock (_clientsLock)
            {
                foreach (var c in _clients)
                {
                    try { c.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(200); }
                    catch { }
                    c.Dispose();
                }
                _clients.Clear();
            }
        }

        /// <summary>Enqueue a JPEG frame for all connected clients (call from any thread).</summary>
        public async Task BroadcastFrameAsync(byte[] jpegBytes)
        {
            List<WebSocket> snapshot;
            lock (_clientsLock) snapshot = new List<WebSocket>(_clients);

            if (snapshot.Count == 0) return;

            var segment = new ArraySegment<byte>(jpegBytes);
            var toRemove = new List<WebSocket>();

            foreach (var ws in snapshot)
            {
                if (ws.State != WebSocketState.Open) { toRemove.Add(ws); continue; }
                try
                {
                    await ws.SendAsync(segment, WebSocketMessageType.Binary, true, _cts.Token);
                }
                catch
                {
                    toRemove.Add(ws);
                }
            }

            if (toRemove.Count > 0)
                lock (_clientsLock)
                    foreach (var ws in toRemove)
                        _clients.Remove(ws);
        }

        // ── WebSocket accept loop ──────────────────────────────────────────────

        private async Task AcceptWsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _wsListener.GetContextAsync(); }
                catch (Exception) { break; }

                if (ctx.Request.IsWebSocketRequest)
                    _ = Task.Run(() => HandleWsClient(ctx, ct), ct);
                else
                {
                    ctx.Response.StatusCode = 426;
                    ctx.Response.Close();
                }
            }
        }

        private async Task HandleWsClient(HttpListenerContext ctx, CancellationToken ct)
        {
            WebSocket ws;
            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                ws = wsCtx.WebSocket;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MobileBridge] WS handshake failed: {ex.Message}");
                ctx.Response.Close();
                return;
            }

            lock (_clientsLock) _clients.Add(ws);
            Debug.Log($"[MobileBridge] Client connected. Total: {ClientCount}");

            var buf = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                        OnTouchMessage?.Invoke(msg);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.LogWarning($"[MobileBridge] WS recv error: {ex.Message}"); }
            finally
            {
                lock (_clientsLock) _clients.Remove(ws);
                Debug.Log($"[MobileBridge] Client disconnected. Total: {ClientCount}");
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch { }
                ws.Dispose();
            }
        }

        // ── HTTP accept loop ───────────────────────────────────────────────────

        private async Task AcceptHttpLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _httpListener.GetContextAsync(); }
                catch (Exception) { break; }

                _ = Task.Run(() => ServeHttp(ctx));
            }
        }

        private void ServeHttp(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/" || path == "/index.html")
                {
                    if (File.Exists(_clientHtmlPath))
                    {
                        var html = File.ReadAllBytes(_clientHtmlPath);
                        ctx.Response.ContentType       = "text/html; charset=utf-8";
                        ctx.Response.ContentLength64   = html.Length;
                        ctx.Response.OutputStream.Write(html, 0, html.Length);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 503;
                        var msg = Encoding.UTF8.GetBytes("client.html not found");
                        ctx.Response.OutputStream.Write(msg, 0, msg.Length);
                    }
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[MobileBridge] HTTP error: {ex.Message}"); }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }
    }
}
