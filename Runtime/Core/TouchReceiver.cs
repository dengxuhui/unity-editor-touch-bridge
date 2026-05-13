using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace MobileBridge
{
    /// <summary>
    /// Subscribes to WebSocketServer.OnTouchMessage, parses JSON touch events,
    /// and injects them into Unity Input System via a virtual Touchscreen device.
    /// InputSystem.QueueStateEvent is thread-safe; this runs on the WS receive thread.
    /// </summary>
    public sealed class TouchReceiver
    {
        private readonly WebSocketServer _server;
        private Touchscreen _touchDevice;

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

                InputSystem.QueueStateEvent(_touchDevice, new TouchState
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

        // ── JSON data structures ───────────────────────────────────────────────

        [Serializable]
        private class TouchMessage
        {
            public string      type;
            // 'event' is a C# keyword so we use the verbatim identifier
            public string      @event;
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
