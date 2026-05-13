using UnityEngine;

namespace MobileBridge
{
    public static class CoordinateMapper
    {
        private static float _effectiveWidth  = 1f;
        private static float _effectiveHeight = 1f;
        private static float _offsetX = 0f;
        private static float _offsetY = 0f;

        /// <summary>
        /// Call once when Game View layout is known.
        /// offsetX/Y and width/height are all in normalized [0,1] space.
        /// </summary>
        public static void SetEffectiveArea(float offsetX, float offsetY, float width, float height)
        {
            _offsetX = offsetX;
            _offsetY = offsetY;
            _effectiveWidth  = Mathf.Max(width,  0.001f);
            _effectiveHeight = Mathf.Max(height, 0.001f);
        }

        /// <summary>
        /// Convert normalized touch coordinates [0,1] to Unity screen-space pixels.
        /// Handles Y-axis flip (phone: top-left origin, Unity: bottom-left origin)
        /// and Windows DPI scaling.
        /// </summary>
        public static Vector2 NormalizedToUnity(float nx, float ny)
        {
            float ex = nx * _effectiveWidth  + _offsetX;
            float ey = ny * _effectiveHeight + _offsetY;

            float unityX =        ex  * Screen.width;
            float unityY = (1f - ey) * Screen.height;

#if UNITY_EDITOR_WIN
            float dpiScale = Screen.dpi > 0f ? Screen.dpi / 96f : 1f;
            unityX /= dpiScale;
            unityY /= dpiScale;
#endif

            return new Vector2(unityX, unityY);
        }
    }
}
