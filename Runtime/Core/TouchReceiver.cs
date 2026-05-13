using System;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace MobileBridge
{
    /// <summary>
    /// Subscribes to WebSocketServer.OnTouchMessage, parses JSON touch events,
    /// and injects them into Unity Input System via a virtual Touchscreen device.
    ///
    /// NOTE: QueueStateEvent internally allocates Allocator.Temp, which is only
    /// allowed on the main thread. WebSocket messages arrive on a thread-pool thread,
    /// so we enqueue parsed data into a ConcurrentQueue and drain it on the main
    /// thread via Tick(), called from MobileBridge.Update().
    /// </summary>
    public sealed class TouchReceiver
    {
        private readonly WebSocketServer _server;
        private Touchscreen _touchDevice;

        // Thread-safe queue: WS thread produces, main thread consumes.
        private readonly ConcurrentQueue<PendingTouch> _pending =
            new ConcurrentQueue<PendingTouch>();

        public TouchReceiver(WebSocketServer server)
        {
            _server = server;
        }

        public void Start()
        {
            _touchDevice = InputSystem.GetDevice<Touchscreen>()
                           ?? InputSystem.AddDevice<Touchscreen>("MobileBridgeTouch");
            _server.OnTouchMessage += HandleMessage;
        }

        public void Stop()
        {
            _server.OnTouchMessage -= HandleMessage;
        }

        /// <summary>
        /// Must be called every frame from the main thread (MonoBehaviour.Update).
        /// Drains the pending queue and calls QueueStateEvent on the main thread.
        /// </summary>
        public void Tick()
        {
            if (_touchDevice == null) return;

            while (_pending.TryDequeue(out var t))
            {
                InputSystem.QueueStateEvent(_touchDevice, new TouchState
                {
                    touchId  = t.touchId,
                    phase    = t.phase,
                    position = t.position,
                });
            }
        }

        // Called on the WS thread-pool thread — must NOT call QueueStateEvent here.
        private void HandleMessage(string json)
        {
            TouchMessage msg;
            try { msg = JsonUtility.FromJson<TouchMessage>(json); }
            catch { return; }

            if (msg.type != "touch" || msg.touches == null) return;

            UnityEngine.InputSystem.TouchPhase phase = ParsePhase(msg.@event);
            foreach (var t in msg.touches)
            {
                Vector2 pos = CoordinateMapper.NormalizedToUnity(t.nx, t.ny);

                _pending.Enqueue(new PendingTouch
                {
                    touchId  = t.id + 1,   // Input System uses 1-based touch IDs
                    phase    = phase,
                    position = pos,
                });
            }
        }

        private static UnityEngine.InputSystem.TouchPhase ParsePhase(string evt)
        {
            switch (evt)
            {
                case "began":     return UnityEngine.InputSystem.TouchPhase.Began;
                case "moved":     return UnityEngine.InputSystem.TouchPhase.Moved;
                case "ended":     return UnityEngine.InputSystem.TouchPhase.Ended;
                case "cancelled": return UnityEngine.InputSystem.TouchPhase.Canceled;
                default:          return UnityEngine.InputSystem.TouchPhase.Stationary;
            }
        }

        // ── Internal structures ────────────────────────────────────────────────

        private struct PendingTouch
        {
            public int                                  touchId;
            public UnityEngine.InputSystem.TouchPhase   phase;
            public Vector2                              position;
        }

        [Serializable]
        private class TouchMessage
        {
            public string       type;
            public string       @event;
            public TouchPoint[] touches;
        }

        [Serializable]
        private class TouchPoint
        {
            public int   id;
            public float nx;
            public float ny;
        }
    }
}
