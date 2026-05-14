using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
#if MOBILE_BRIDGE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace MobileBridge
{
    /// <summary>
    /// Subscribes to touch messages via injected delegates, parses JSON touch events,
    /// and injects them into the input system(s) present in the project.
    ///
    /// New Input System path (when com.unity.inputsystem >= 1.7.0 is installed):
    ///   Injects via a virtual Touchscreen device using QueueStateEvent.
    ///   Guarded by MOBILE_BRIDGE_INPUT_SYSTEM (defined via asmdef versionDefines).
    ///
    /// Legacy Input Manager path (always compiled):
    ///   Maintains a per-frame Touch list exposed via LegacyTouches, consumed by
    ///   LegacyTouchInput which is wired as StandaloneInputModule.inputOverride.
    ///   Touches persist as Stationary across frames so drag/hold gestures work
    ///   (the WS client never sends stationary events).
    ///
    /// QueueStateEvent requires main-thread access. Messages arrive on the WS
    /// thread-pool thread, so raw normalised coordinates are enqueued into a
    /// ConcurrentQueue and drained on the main thread via Tick().
    /// </summary>
    public sealed class TouchReceiver
    {
#if UNITY_EDITOR
        // subscribe(handler)   — called in Start()
        // unsubscribe(handler) — called in Stop()
        private readonly Action<Action<string>> _subscribe;
        private readonly Action<Action<string>> _unsubscribe;
#endif

#if MOBILE_BRIDGE_INPUT_SYSTEM
        private Touchscreen _touchDevice;
        private bool        _deviceOwned;

        // Caches screen-space start position per touchId so TouchState.startPosition
        // is correct throughout a touch lifetime.
        private readonly Dictionary<int, Vector2> _startPositions = new Dictionary<int, Vector2>();
#endif

        // Thread-safe queue: WS thread produces, main thread consumes.
        private readonly ConcurrentQueue<PendingTouch> _pending = new ConcurrentQueue<PendingTouch>();

        // ── Legacy Input Manager state ─────────────────────────────────────────
        // StandaloneInputModule.inputOverride reads LegacyTouches each frame.
        // Touches must persist across frames (Stationary) until Ended/Cancelled,
        // because the WS client only sends began/moved/ended — not stationary.
        private struct LegacyEntry
        {
            public Vector2              position;
            public Vector2              delta;
            public UnityEngine.TouchPhase phase;
            public bool                 endedThisFrame;
        }
        private readonly Dictionary<int, LegacyEntry> _legacyActive     = new Dictionary<int, LegacyEntry>();
        private readonly List<int>                     _legacyRemoveNext = new List<int>();
        private readonly List<int>                     _legacyKeys       = new List<int>(); // avoids dict-enum alloc
        private readonly List<UnityEngine.Touch>       _legacyTouches    = new List<UnityEngine.Touch>(10);

        public IReadOnlyList<UnityEngine.Touch> LegacyTouches => _legacyTouches;

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
#if MOBILE_BRIDGE_INPUT_SYSTEM
            // EnhancedTouchSupport.Enable() is idempotent — safe to call even if
            // user code already called it. We intentionally never call Disable()
            // to avoid inadvertently breaking user code that depends on it.
            EnhancedTouchSupport.Enable();

            // Add a named virtual device so it is clearly identifiable in the
            // Input Debugger and never confused with a real hardware touchscreen.
            _touchDevice = InputSystem.AddDevice<Touchscreen>("MobileBridgeTouch");
            _deviceOwned = true;
#endif

#if UNITY_EDITOR
            _subscribe?.Invoke(HandleMessage);
#endif

            MobileBridge.MBLog("[TouchReceiver] Started."
#if MOBILE_BRIDGE_INPUT_SYSTEM
                + " Virtual Touchscreen device added, EnhancedTouchSupport enabled."
#endif
            );
        }

        public void Stop()
        {
#if UNITY_EDITOR
            _unsubscribe?.Invoke(HandleMessage);
#endif

#if MOBILE_BRIDGE_INPUT_SYSTEM
            if (_deviceOwned && _touchDevice != null && _touchDevice.added)
                InputSystem.RemoveDevice(_touchDevice);

            _touchDevice = null;
            _deviceOwned = false;
            _startPositions.Clear();
#endif

            _legacyActive.Clear();
            _legacyRemoveNext.Clear();
            _legacyTouches.Clear();

            // Drain any queued events that arrived between Stop() being called
            // and the WS thread noticing the unsubscription.
            while (_pending.TryDequeue(out _)) { }

            MobileBridge.MBLog("[TouchReceiver] Stopped.");
        }

        // ── Main-thread drain ──────────────────────────────────────────────────

        /// <summary>
        /// Must be called every frame from the main thread (MonoBehaviour.Update).
        /// Converts normalised coordinates, injects New IS TouchState events (when
        /// MOBILE_BRIDGE_INPUT_SYSTEM is defined), and maintains the Legacy touch
        /// list read by LegacyTouchInput.
        /// </summary>
        public void Tick()
        {
            // ── Legacy: expire touches that ended last frame ───────────────────
            foreach (int id in _legacyRemoveNext)
                _legacyActive.Remove(id);
            _legacyRemoveNext.Clear();

            // ── Legacy: mark all still-active touches as Stationary ───────────
            // (the WS client never sends stationary events; we synthesise them)
            _legacyKeys.Clear();
            _legacyKeys.AddRange(_legacyActive.Keys);
            foreach (int id in _legacyKeys)
            {
                var e = _legacyActive[id];
                e.delta = Vector2.zero;
                e.phase = UnityEngine.TouchPhase.Stationary;
                _legacyActive[id] = e;
            }

            // ── Drain incoming queue ──────────────────────────────────────────
            while (_pending.TryDequeue(out var t))
            {
                Vector2 pos = CoordinateMapper.NormalizedToUnity(t.nx, t.ny);

#if MOBILE_BRIDGE_INPUT_SYSTEM
                if (_touchDevice != null)
                {
                    var isPhase = ToISPhase(t.phase);

                    if (isPhase == UnityEngine.InputSystem.TouchPhase.Began)
                        _startPositions[t.touchId] = pos;

                    _startPositions.TryGetValue(t.touchId, out Vector2 startPos);

                    if (isPhase == UnityEngine.InputSystem.TouchPhase.Ended ||
                        isPhase == UnityEngine.InputSystem.TouchPhase.Canceled)
                        _startPositions.Remove(t.touchId);

                    InputSystem.QueueStateEvent(_touchDevice, new TouchState
                    {
                        touchId       = t.touchId,
                        phase         = isPhase,
                        position      = pos,
                        startPosition = startPos,
                        // The touch with the lowest id (1-based) is designated primary.
                        // InputSystemUIInputModule and EnhancedTouch both rely on this
                        // flag to drive pointer/drag events on UI elements.
                        isPrimaryTouch = (t.touchId == 1),
                        isTap          = false,
                        tapCount       = 0,
                        // A non-zero radius prevents some Input System versions from
                        // silently discarding the event.
                        radius         = Vector2.one,
                    });
                }
#endif

                // ── Legacy path ───────────────────────────────────────────────
                _legacyActive.TryGetValue(t.touchId, out LegacyEntry prev);
                bool isEnd = t.phase == UnityEngine.TouchPhase.Ended ||
                             t.phase == UnityEngine.TouchPhase.Canceled;
                _legacyActive[t.touchId] = new LegacyEntry
                {
                    position       = pos,
                    delta          = pos - prev.position,
                    phase          = t.phase,
                    endedThisFrame = isEnd,
                };
                if (isEnd) _legacyRemoveNext.Add(t.touchId);
            }

            // ── Build legacy output list (read by LegacyTouchInput) ───────────
            _legacyTouches.Clear();
            foreach (var kvp in _legacyActive)
            {
                _legacyTouches.Add(new UnityEngine.Touch
                {
                    fingerId                = kvp.Key - 1, // back to 0-based for legacy API
                    position                = kvp.Value.position,
                    rawPosition             = kvp.Value.position,
                    deltaPosition           = kvp.Value.delta,
                    deltaTime               = Time.deltaTime,
                    tapCount                = 1,
                    phase                   = kvp.Value.phase,
                    pressure                = 1f,
                    maximumPossiblePressure = 1f,
                    radius                  = 1f,
                    radiusVariance          = 0f,
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

            UnityEngine.TouchPhase phase = ParsePhase(msg.eventType);

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

        private static UnityEngine.TouchPhase ParsePhase(string evt)
        {
            switch (evt)
            {
                case "began":     return UnityEngine.TouchPhase.Began;
                case "moved":     return UnityEngine.TouchPhase.Moved;
                case "ended":     return UnityEngine.TouchPhase.Ended;
                case "cancelled": return UnityEngine.TouchPhase.Canceled;
                default:          return UnityEngine.TouchPhase.Stationary;
            }
        }

#if MOBILE_BRIDGE_INPUT_SYSTEM
        private static UnityEngine.InputSystem.TouchPhase ToISPhase(UnityEngine.TouchPhase p)
        {
            switch (p)
            {
                case UnityEngine.TouchPhase.Began:    return UnityEngine.InputSystem.TouchPhase.Began;
                case UnityEngine.TouchPhase.Moved:    return UnityEngine.InputSystem.TouchPhase.Moved;
                case UnityEngine.TouchPhase.Ended:    return UnityEngine.InputSystem.TouchPhase.Ended;
                case UnityEngine.TouchPhase.Canceled: return UnityEngine.InputSystem.TouchPhase.Canceled;
                default:                              return UnityEngine.InputSystem.TouchPhase.Stationary;
            }
        }
#endif

        // ── Internal structures ────────────────────────────────────────────────

        private struct PendingTouch
        {
            public int                    touchId;
            public UnityEngine.TouchPhase phase; // legacy phase, always available without IS package
            public float                  nx;
            public float                  ny;
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
