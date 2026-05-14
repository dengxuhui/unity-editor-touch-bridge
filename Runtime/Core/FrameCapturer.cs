using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MobileBridge
{
    /// <summary>
    /// Receives JPEG-encoded frames from the main thread and sends them
    /// to all WebSocket clients via a dedicated background send thread.
    ///
    /// Pipeline:
    ///   ① MobileBridge.CaptureLoop (main thread, WaitForEndOfFrame)
    ///        → ReadPixels + EncodeToJPG
    ///        → EnqueueJpeg
    ///   ② This class (background thread) → broadcastFrame delegate
    ///
    /// Dependencies are injected as delegates so this class compiles in the
    /// Runtime assembly without any reference to WebSocketServer (Editor-only).
    /// </summary>
    public sealed class FrameCapturer
    {
        // Injected by MobileBridge.cs (bound by Editor before StartBridge).
#if UNITY_EDITOR
        private readonly Action<byte[]> _broadcastFrame;
        private readonly Func<int>      _clientCount;
#endif

        // Capacity 2: if the send thread falls behind, drop new frames.
        private readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(2);

        private Thread _sendThread;
        private volatile bool _running;

        // ── Diagnostic counters (written by multiple threads, read for logging) ─
        // All accessed via Interlocked for thread safety.
        private long _totalEnqueued;
        private long _totalDropped;       // TryAdd returned false (queue full)
        private long _totalSendOk;
        private long _totalSendErr;
        private long _totalSendSlowMs;    // accumulated slow-send ms (>300ms)
        private long _slowSendCount;

        // Timestamps (Environment.TickCount, ms) — written by send thread only.
        private long _lastDequeueMs;
        private long _lastSendOkMs;

        // Per-second snapshot (refreshed in SendLoop on send thread)
        private long _statsWindowEnqueued;
        private long _statsWindowSendOk;
        private long _statsWindowDrop;
        private long _statsLastPrintMs;

        // Stall detection: if clients are connected and nothing is sent for this long, warn.
        private const long StallThresholdMs = 2000;

#if UNITY_EDITOR
        public FrameCapturer(Action<byte[]> broadcastFrame, Func<int> clientCount)
        {
            _broadcastFrame = broadcastFrame;
            _clientCount    = clientCount;
        }
#else
        public FrameCapturer() { }
#endif

        public void Start()
        {
            _running = true;
            long now = (long)Environment.TickCount;
            _lastSendOkMs     = now;
            _lastDequeueMs    = now;
            _statsLastPrintMs = now;

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

            // Final summary
            MobileBridge.MBLog($"[MB][FrameCapturer] Session summary — " +
                      $"enqueued={_totalEnqueued} sendOk={_totalSendOk} " +
                      $"dropped={_totalDropped} sendErr={_totalSendErr} " +
                      $"slowSends={_slowSendCount} slowTotalMs={_totalSendSlowMs}");
        }

        /// <summary>
        /// Enqueue an already-encoded JPEG for broadcasting.
        /// Called from the main thread (CaptureLoop coroutine).
        /// </summary>
        public void EnqueueJpeg(byte[] jpegBytes)
        {
            if (!_running || jpegBytes == null || jpegBytes.Length == 0) return;

            if (_queue.TryAdd(jpegBytes))
            {
                Interlocked.Increment(ref _totalEnqueued);
                Interlocked.Increment(ref _statsWindowEnqueued);
            }
            else
            {
                Interlocked.Increment(ref _totalDropped);
                Interlocked.Increment(ref _statsWindowDrop);
#if MOBILE_BRIDGE_DEBUG
                MobileBridge.MBLogWarning("[MB][FrameCapturer] WARN queue_full — frame dropped (send thread lagging)");
#endif
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                byte[] jpeg;
                try
                {
                    jpeg = _queue.Take();
                }
                catch (InvalidOperationException)
                {
                    break; // CompleteAdding() called
                }

                _lastDequeueMs = (long)Environment.TickCount;

#if UNITY_EDITOR
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    _broadcastFrame?.Invoke(jpeg);
                    sw.Stop();

                    long elapsedMs = sw.ElapsedMilliseconds;
                    _lastSendOkMs = (long)Environment.TickCount;
                    Interlocked.Increment(ref _totalSendOk);
                    Interlocked.Increment(ref _statsWindowSendOk);

                    if (elapsedMs > 300)
                    {
                        Interlocked.Increment(ref _slowSendCount);
                        Interlocked.Add(ref _totalSendSlowMs, elapsedMs);
                        MobileBridge.MBLogWarning(
                            $"[MB][FrameCapturer] WARN broadcast_slow — took {elapsedMs}ms " +
                            $"(clients={_clientCount?.Invoke() ?? 0})");
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Interlocked.Increment(ref _totalSendErr);
                    MobileBridge.MBLogWarning(
                        $"[MB][FrameCapturer] WARN send_error — {ex.GetType().Name}: {ex.Message}");
                }
#endif

                // Print per-second stats + stall check
                PrintStatsIfDue();
            }

            MobileBridge.MBLog("[MB][FrameCapturer] Send thread exited.");
        }

        private void PrintStatsIfDue()
        {
            long now = (long)Environment.TickCount;
            if (now - _statsLastPrintMs < 1000) return;

            long windowMs = now - _statsLastPrintMs;
            long enq      = Interlocked.Exchange(ref _statsWindowEnqueued, 0);
            long sent     = Interlocked.Exchange(ref _statsWindowSendOk,   0);
            long drop     = Interlocked.Exchange(ref _statsWindowDrop,     0);
#if UNITY_EDITOR
            int  clients  = _clientCount?.Invoke() ?? 0;
#else
            int  clients  = 0;
#endif
            long queueLen   = _queue.Count;
            long msSinceOk  = now - _lastSendOkMs;

            _statsLastPrintMs = now;

            float enqFps  = enq  * 1000f / windowMs;
            float sentFps = sent * 1000f / windowMs;

            MobileBridge.MBLog(
                $"[MB][FrameCapturer] STATS " +
                $"enqFps={enqFps:F1} sentFps={sentFps:F1} " +
                $"drop1s={drop} queueLen={queueLen} " +
                $"clients={clients} msSinceLastSendOk={msSinceOk} " +
                $"totalSent={_totalSendOk} totalDrop={_totalDropped} totalErr={_totalSendErr}");

            if (clients > 0 && msSinceOk > StallThresholdMs)
            {
                MobileBridge.MBLogWarning(
                    $"[MB][FrameCapturer] WARN send_stalled — " +
                    $"{msSinceOk}ms since last successful send, clients={clients}");
            }
        }
    }
}
