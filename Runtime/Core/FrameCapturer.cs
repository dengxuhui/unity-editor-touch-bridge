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
    /// Three-step pipeline (SPEC §4.2):
    ///   ① URPCaptureFeature.Execute  → AsyncGPUReadback (non-blocking, main thread)
    ///   ② OnReadbackComplete callback → JPEG encode + EnqueueJpeg (main thread)
    ///   ③ This class (background thread) → WebSocket send
    ///
    /// JPEG encoding is done in step ② on the main thread using Unity's built-in
    /// ImageConversion to avoid cross-platform System.Drawing dependencies.
    /// Moving encoding to a worker thread (P3 optimisation) requires a pure-C#
    /// JPEG encoder or a native plugin.
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
        /// Called from the main thread (AsyncGPUReadback callback).
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
