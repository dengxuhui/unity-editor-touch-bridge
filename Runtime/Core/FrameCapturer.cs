using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace MobileBridge
{
    /// <summary>
    /// Receives JPEG-encoded frames from the main thread and sends them
    /// to all WebSocket clients via a dedicated background send thread.
    ///
    /// Pipeline (current implementation):
    ///   ① MobileBridge.CaptureLoop (main thread, WaitForEndOfFrame)
    ///        → ReadPixels (sync GPU readback, ~1–3 ms at stream resolution)
    ///        → EncodeToJPG
    ///        → EnqueueJpeg
    ///   ② This class (background thread) → WebSocket broadcast
    ///
    /// WaitForEndOfFrame is used instead of AsyncGPUReadback so that
    /// Screen Space Overlay canvases are included in the captured frame.
    /// AsyncGPUReadback fires inside the URP render pass, before Overlay UI
    /// is composited, which caused 2D UI to be invisible in the stream.
    ///
    /// Trade-off: ReadPixels + EncodeToJPG run on the main thread (~3–8 ms).
    /// For a P3 optimisation, encoding could be moved to a worker thread using
    /// a pure-C# JPEG encoder or a native plugin.
    /// </summary>
    public sealed class FrameCapturer
    {
        private readonly WebSocketServer _server;

        // Capacity 2: if the send thread falls behind, drop new frames rather than accumulate.
        private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(2);

        private Thread _sendThread;
        private volatile bool _running;

        public FrameCapturer(WebSocketServer server)
        {
            _server = server;
        }

        public void Start()
        {
            _running = true;
            _sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "MobileBridge.Send"
            };
            _sendThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _queue.CompleteAdding();
            _sendThread?.Join(2000);
        }

        /// <summary>
        /// Enqueue an already-encoded JPEG for broadcasting.
        /// Called from the main thread (CaptureLoop coroutine).
        /// </summary>
        public void EnqueueJpeg(byte[] jpegBytes)
        {
            if (!_running || jpegBytes == null || jpegBytes.Length == 0) return;
            _queue.TryAdd(jpegBytes);   // silently drops if queue is full (back-pressure)
        }

        private void SendLoop()
        {
            while (_running)
            {
                byte[] jpeg;
                try { jpeg = _queue.Take(); }
                catch (InvalidOperationException) { break; }

                try
                {
                    _server.BroadcastFrameAsync(jpeg).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MobileBridge] Frame send error: {ex.Message}");
                }
            }
        }
    }
}
