using System;
using UnityEngine;

namespace MobileBridge.Editor
{
    /// <summary>
    /// Generates a minimal QR code texture for a URL.
    /// This is a placeholder implementation for P0; a full QR encoder is planned for P4.
    /// The texture is a solid gray square with a border, prompting the user to copy the URL.
    /// </summary>
    public static class QRCodeGenerator
    {
        private static Texture2D _placeholder;

        /// <summary>
        /// Returns a placeholder texture. Pass the URL to the Editor window label instead.
        /// Replace with a real QR encoder in P4.
        /// </summary>
        public static Texture2D GeneratePlaceholder(int size = 128)
        {
            if (_placeholder != null && _placeholder.width == size)
                return _placeholder;

            _placeholder = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _placeholder.hideFlags = HideFlags.HideAndDontSave;

            var pixels = new Color32[size * size];
            var border = Color32Extensions.FromHex(0x333333);
            var fill   = Color32Extensions.FromHex(0xEEEEEE);
            var corner = Color32Extensions.FromHex(0x111111);

            int b = Mathf.Max(1, size / 16);    // border thickness
            int cb = Mathf.Max(4, size / 8);    // corner block size

            for (int i = 0; i < pixels.Length; i++)
            {
                int x = i % size;
                int y = i / size;

                bool onBorder = x < b || x >= size - b || y < b || y >= size - b;
                bool inCorner =
                    (x < cb && y < cb) || (x >= size - cb && y < cb) ||
                    (x < cb && y >= size - cb);

                if (inCorner) pixels[i] = corner;
                else if (onBorder) pixels[i] = border;
                else pixels[i] = fill;
            }

            _placeholder.SetPixels32(pixels);
            _placeholder.Apply();
            return _placeholder;
        }

        public static void Cleanup()
        {
            if (_placeholder != null)
            {
                UnityEngine.Object.DestroyImmediate(_placeholder);
                _placeholder = null;
            }
        }

        private static class Color32Extensions
        {
            public static Color32 FromHex(uint hex)
            {
                return new Color32(
                    (byte)((hex >> 16) & 0xFF),
                    (byte)((hex >> 8)  & 0xFF),
                    (byte)( hex        & 0xFF),
                    255);
            }
        }
    }
}
