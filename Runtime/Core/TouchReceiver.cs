using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.InputSystem.LowLevel;

namespace MobileBridge
{
    /// <summary>
    /// Subscribes to touch messages via injected delegates, parses JSON touch events,
    /// and injects them into Unity Input System via a virtual Touchscreen device.
    ///
    /// Design notes:
    /// - EnhancedTouchSupport.Enable() is called once and never disabled, so it
    ///   cannot interfere with user project code that also uses EnhancedTouch.
    /// - QueueStateEvent requires Allocator.Temp (main thread only). Messages
    ///   arrive on a thread-pool thread, so we enqueue raw normalised coordinates
    ///   into a ConcurrentQueue and drain on the main thread via Tick(), called
    ///   from MobileBridge.Update().
    /// - Coordinate conversion (NormalizedToUnity) is intentionally deferred to
    ///   Tick() so that Screen.width / Screen.height are only accessed from the
    ///   main thread.
    ///
    /// Dependencies are injected as delegates so this class compiles in the
    /// Runtime assembly without any reference to WebSocketServer (Editor-only).
    /// </summary>
    public sealed class TouchReceiver
    {
#if UNITY_EDITOR
        // subscribe(handler)   — called in Start()
        // unsubscribe(handler) — called in Stop()
        private readonly Action<Action<string>> _subscribe;
        private readonly Action<Action<string>> _unsubscribe;
#endif

        private Touchscreen _touchDevice;
        private bool _deviceOwned;

        // Thread-safe queue: WS thread produces, main thread consumes.
        private readonly ConcurrentQueue<PendingTouch> _pending =
            new ConcurrentQueue<PendingTouch>();

        // Main-thread-only: caches the screen-space start position per touchId
        // so TouchState.startPosition is correct throughout a touch lifetime.
        private readonly Dictionary<int, Vector2> _startPositions =
            new Dictionary<int, Vector2>();

#if UNITY_EDITOR
        public TouchReceiver(Action<Action<string>> subscribe,
                             Action<Action<string>> unsubscribe)
        {
            _subscribe   = subscribe;
            _unsubscribe = unsubscribe;
        }
#else
        public TouchReceiver() { }
#endif

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public void Start()
        {
            // EnhancedTouchSupport.Enable() is idempotent — safe to call even if
            // user code already called it. We intentionally never call Disable()
            // to avoid inadvertently breaking user code that depends on it.
            EnhancedTouchSupport.Enable();

            // Add a named virtual device so it is clearly identifiable in the
            // Input Debugger and never confused with a real hardware touchscreen.
            _touchDevice = InputSystem.AddDevice<Touchscreen>("MobileBridgeTouch");
            _deviceOwned = true;

#if UNITY_EDITOR
            _subscribe?.Invoke(HandleMessage);
#endif

            MobileBridge.MBLog("[TouchReceiver] Started — virtual Touchscreen device added, EnhancedTouchSupport enabled.");
        }

        public void Stop()
        {
#if UNITY_EDITOR
            _unsubscribe?.Invoke(HandleMessage);
#endif

            if (_deviceOwned && _touchDevice != null && _touchDevice.added)
                InputSystem.RemoveDevice(_touchDevice);

            _touchDevice = null;
            _deviceOwned = false;
            _startPositions.Clear();

            // Drain any queued events that arrived between Stop() being called
            // and the WS thread noticing the unsubscription.
            while (_pending.TryDequeue(out _)) { }

            MobileBridge.MBLog("[TouchReceiver] Stopped — virtual Touchscreen device removed.");
        }

        // ── Main-thread drain ──────────────────────────────────────────────────

        /// <summary>
        /// Must be called every frame from the main thread (MonoBehaviour.Update).
        /// Converts normalised coordinates and injects TouchState events.
        /// </summary>
        public void Tick()
        {
            if (_touchDevice == null) return;

            while (_pending.TryDequeue(out var t))
            {
                Vector2 pos = CoordinateMapper.NormalizedToUnity(t.nx, t.ny);

                // Maintain per-touch start position.
                if (t.phase == UnityEngine.InputSystem.TouchPhase.Began)
                {
                    _startPositions[t.touchId] = pos;
                }

                _startPositions.TryGetValue(t.touchId, out Vector2 startPos);

                if (t.phase == UnityEngine.InputSystem.TouchPhase.Ended ||
                    t.phase == UnityEngine.InputSystem.TouchPhase.Canceled)
                {
                    _startPositions.Remove(t.touchId);
                }

                InputSystem.QueueStateEvent(_touchDevice, new TouchState
                {
                    touchId                = t.touchId,
                    phase                  = t.phase,
                    position               = pos,
                    startPosition          = startPos,
                    // The touch with the lowest id (1-based) is designated primary.
                    // InputSystemUIInputModule and EnhancedTouch both rely on this
                    // flag to drive pointer/drag events on UI elements.
                    isPrimaryTouch         = (t.touchId == 1),
                    isTap                  = false,
                    tapCount               = 0,
                    // A non-zero radius prevents some Input System versions from
                    // silently discarding the event.
                    radius                 = Vector2.one,
                });
            }
        }

        // ── WS thread handler ──────────────────────────────────────────────────

        // Called on the WS thread-pool thread — must NOT touch Unity APIs except
        // ConcurrentQueue and Debug.Log (which is thread-safe).
        private void HandleMessage(string json)
        {
            TouchMessage msg;
            try
            {
                msg = JsonUtility.FromJson<TouchMessage>(json);
            }
            catch (Exception ex)
            {
                MobileBridge.MBLogWarning($"[TouchReceiver] JSON parse error: {ex.Message}  raw={json}");
                return;
            }

            if (msg.type != "touch")
            {
                MobileBridge.MBLogWarning($"[TouchReceiver] Unexpected message type: '{msg.type}'  raw={json}");
                return;
            }

            if (msg.touches == null || msg.touches.Length == 0)
            {
                MobileBridge.MBLogWarning($"[TouchReceiver] touches is null/empty. eventType='{msg.eventType}'  raw={json}");
                return;
            }

            UnityEngine.InputSystem.TouchPhase phase = ParsePhase(msg.eventType);

#if MOBILE_BRIDGE_DEBUG
            MobileBridge.MBLog($"[TouchReceiver] {msg.eventType} phase={phase} count={msg.touches.Length}");
#endif

            foreach (var t in msg.touches)
            {
                _pending.Enqueue(new PendingTouch
                {
                    touchId = t.id + 1,  // Input System uses 1-based touch IDs
                    phase   = phase,
                    nx      = t.nx,
                    ny      = t.ny,
                });
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

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
            public int                                 touchId;
            public UnityEngine.InputSystem.TouchPhase  phase;
            public float                               nx;
            public float                               ny;
        }

        [Serializable]
        private class TouchMessage
        {
            public string       type;
            public string       eventType;   // matches client.html JSON field "eventType"
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
