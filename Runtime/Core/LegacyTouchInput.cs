using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MobileBridge
{
    // Injected as StandaloneInputModule.inputOverride by MobileBridge.WireUpLegacyInput().
    // Feeds bridged touches into the EventSystem without touching UnityEngine.Input directly.
    [AddComponentMenu("")]
    internal sealed class LegacyTouchInput : BaseInput
    {
        private readonly List<Touch> _touches = new List<Touch>(10);

        internal void SetTouches(IReadOnlyList<Touch> touches)
        {
            _touches.Clear();
            for (int i = 0; i < touches.Count; i++)
                _touches.Add(touches[i]);
        }

        public override int touchCount => _touches.Count;
        public override bool touchSupported => true;

        public override Touch GetTouch(int index)
        {
            if ((uint)index >= (uint)_touches.Count) return default;
            return _touches[index];
        }

        // Mirror primary touch as mouse so StandaloneInputModule's mouse fallback path
        // (scroll, drag on non-touch UI) still gets a sensible position.
        public override Vector2 mousePosition =>
            _touches.Count > 0 ? _touches[0].position : base.mousePosition;

        public override bool GetMouseButtonDown(int button) =>
            button == 0 && _touches.Count > 0 && _touches[0].phase == TouchPhase.Began;

        public override bool GetMouseButtonUp(int button) =>
            button == 0 && _touches.Count > 0 &&
            (_touches[0].phase == TouchPhase.Ended || _touches[0].phase == TouchPhase.Canceled);

        public override bool GetMouseButton(int button) =>
            button == 0 && _touches.Count > 0 &&
            _touches[0].phase != TouchPhase.Ended && _touches[0].phase != TouchPhase.Canceled;
    }
}
