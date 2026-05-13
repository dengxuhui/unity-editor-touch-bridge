using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MobileBridge.Editor
{
    /// <summary>
    /// Minimal self-contained QR code generator for editor UI.
    ///
    /// Current implementation targets a fixed QR version (V3-L), which is
    /// sufficient for the bridge URL use case (e.g. http://192.168.1.10:8766).
    /// No third-party dependency is required.
    /// </summary>
    public static class QRCodeGenerator
    {
        private const int Version = 3;
        private const int Size = 29; // modules
        private const int DataCodewords = 55; // version 3, level L
        private const int EccCodewords = 15;  // version 3, level L
        private const int QuietZoneModules = 4;

        private static Texture2D _cachedTexture;
        private static string _cachedText;
        private static int _cachedSize;

        public static Texture2D Generate(string text, int pixelSize = 128)
        {
            if (string.IsNullOrEmpty(text))
                text = "http://127.0.0.1:8766";

            if (_cachedTexture != null && _cachedText == text && _cachedSize == pixelSize)
                return _cachedTexture;

            byte[] payload = BuildPayload(text);
            byte[] ecc = ReedSolomonCompute(payload, EccCodewords);

            var codewords = new byte[payload.Length + ecc.Length];
            Buffer.BlockCopy(payload, 0, codewords, 0, payload.Length);
            Buffer.BlockCopy(ecc, 0, codewords, payload.Length, ecc.Length);

            bool[,] modules = BuildMatrix(codewords);
            _cachedTexture = RenderMatrix(modules, pixelSize);
            _cachedTexture.hideFlags = HideFlags.HideAndDontSave;
            _cachedText = text;
            _cachedSize = pixelSize;
            return _cachedTexture;
        }

        public static void Cleanup()
        {
            if (_cachedTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_cachedTexture);
                _cachedTexture = null;
            }
            _cachedText = null;
            _cachedSize = 0;
        }

        private static byte[] BuildPayload(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var bits = new List<bool>(512);

            AppendBits(bits, 0b0100, 4); // Byte mode
            AppendBits(bits, bytes.Length, 8); // Char count for V1-9 Byte mode
            for (int i = 0; i < bytes.Length; i++)
                AppendBits(bits, bytes[i], 8);

            int capacityBits = DataCodewords * 8;
            if (bits.Count > capacityBits)
                throw new InvalidOperationException(
                    $"[MobileBridge] QR payload too long for V{Version}-L: {bytes.Length} bytes");

            int terminator = Math.Min(4, capacityBits - bits.Count);
            AppendBits(bits, 0, terminator);

            while ((bits.Count & 7) != 0)
                bits.Add(false);

            var data = new List<byte>(DataCodewords);
            for (int i = 0; i < bits.Count; i += 8)
            {
                int val = 0;
                for (int j = 0; j < 8; j++)
                {
                    val <<= 1;
                    if (bits[i + j]) val |= 1;
                }
                data.Add((byte)val);
            }

            bool toggle = true;
            while (data.Count < DataCodewords)
            {
                data.Add(toggle ? (byte)0xEC : (byte)0x11);
                toggle = !toggle;
            }

            return data.ToArray();
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        private static bool[,] BuildMatrix(byte[] codewords)
        {
            var modules = new bool[Size, Size];
            var isFunction = new bool[Size, Size];

            DrawFinder(modules, isFunction, 0, 0);
            DrawFinder(modules, isFunction, Size - 7, 0);
            DrawFinder(modules, isFunction, 0, Size - 7);
            DrawAlignment(modules, isFunction, 22, 22); // V3 alignment center
            DrawTiming(modules, isFunction);
            ReserveFormatAreas(modules, isFunction);
            SetFunction(modules, isFunction, 8, Size - 8, true); // dark module

            PlaceData(modules, isFunction, codewords, mask: 0);
            DrawFormatBits(modules, isFunction, eclBits: 1, mask: 0); // L=01
            return modules;
        }

        private static void DrawFinder(bool[,] modules, bool[,] isFunction, int x, int y)
        {
            for (int dy = -1; dy <= 7; dy++)
            {
                for (int dx = -1; dx <= 7; dx++)
                {
                    int xx = x + dx;
                    int yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= Size || yy >= Size) continue;

                    bool dark =
                        dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                        (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                         (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                    SetFunction(modules, isFunction, xx, yy, dark);
                }
            }
        }

        private static void DrawAlignment(bool[,] modules, bool[,] isFunction, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                    if (isFunction[x, y]) continue;

                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    bool dark = dist != 1;
                    SetFunction(modules, isFunction, x, y, dark);
                }
            }
        }

        private static void DrawTiming(bool[,] modules, bool[,] isFunction)
        {
            for (int i = 8; i < Size - 8; i++)
            {
                bool dark = (i & 1) == 0;
                if (!isFunction[i, 6]) SetFunction(modules, isFunction, i, 6, dark);
                if (!isFunction[6, i]) SetFunction(modules, isFunction, 6, i, dark);
            }
        }

        private static void ReserveFormatAreas(bool[,] modules, bool[,] isFunction)
        {
            for (int i = 0; i <= 8; i++)
            {
                if (i == 6) continue;
                SetFunction(modules, isFunction, 8, i, false);
                SetFunction(modules, isFunction, i, 8, false);
            }

            for (int i = 0; i < 8; i++)
                SetFunction(modules, isFunction, Size - 1 - i, 8, false);

            for (int i = 0; i < 7; i++)
                SetFunction(modules, isFunction, 8, Size - 1 - i, false);
        }

        private static void PlaceData(bool[,] modules, bool[,] isFunction, byte[] codewords, int mask)
        {
            int bitIndex = 0;
            int totalBits = codewords.Length * 8;

            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right--;

                for (int vert = 0; vert < Size; vert++)
                {
                    int y = (((right + 1) & 2) == 0) ? (Size - 1 - vert) : vert;
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        if (isFunction[x, y]) continue;

                        bool bit = false;
                        if (bitIndex < totalBits)
                        {
                            int cw = codewords[bitIndex >> 3];
                            bit = ((cw >> (7 - (bitIndex & 7))) & 1) != 0;
                            bitIndex++;
                        }

                        if (ApplyMask(mask, x, y))
                            bit = !bit;

                        modules[x, y] = bit;
                    }
                }
            }
        }

        private static bool ApplyMask(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return ((x + y) & 1) == 0;
                default: return false;
            }
        }

        private static void DrawFormatBits(bool[,] modules, bool[,] isFunction, int eclBits, int mask)
        {
            int data = (eclBits << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++)
                SetFunction(modules, isFunction, 8, i, GetBit(bits, i));
            SetFunction(modules, isFunction, 8, 7, GetBit(bits, 6));
            SetFunction(modules, isFunction, 8, 8, GetBit(bits, 7));
            SetFunction(modules, isFunction, 7, 8, GetBit(bits, 8));
            for (int i = 9; i < 15; i++)
                SetFunction(modules, isFunction, 14 - i, 8, GetBit(bits, i));

            for (int i = 0; i < 8; i++)
                SetFunction(modules, isFunction, Size - 1 - i, 8, GetBit(bits, i));
            for (int i = 8; i < 15; i++)
                SetFunction(modules, isFunction, 8, Size - 15 + i, GetBit(bits, i));
        }

        private static bool GetBit(int val, int i) => ((val >> i) & 1) != 0;

        private static void SetFunction(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
        {
            modules[x, y] = dark;
            isFunction[x, y] = true;
        }

        private static byte[] ReedSolomonCompute(byte[] data, int degree)
        {
            byte[] gen = ReedSolomonGenerator(degree);
            var result = new byte[degree];

            for (int i = 0; i < data.Length; i++)
            {
                int factor = data[i] ^ result[0];
                for (int j = 0; j < degree - 1; j++)
                    result[j] = result[j + 1];
                result[degree - 1] = 0;

                for (int j = 0; j < degree; j++)
                    result[j] = (byte)(result[j] ^ GfMultiply(gen[j], factor));
            }
            return result;
        }

        private static byte[] ReedSolomonGenerator(int degree)
        {
            var gen = new byte[degree];
            gen[degree - 1] = 1;
            int root = 1;

            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    gen[j] = (byte)GfMultiply(gen[j] & 0xFF, root);
                    if (j + 1 < degree)
                        gen[j] ^= gen[j + 1];
                }
                root = GfMultiply(root, 0x02);
            }
            return gen;
        }

        private static int GfMultiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ (((z >> 7) & 1) * 0x11D);
                if (((y >> i) & 1) != 0)
                    z ^= x;
            }
            return z & 0xFF;
        }

        private static Texture2D RenderMatrix(bool[,] modules, int pixelSize)
        {
            int moduleCount = Size + QuietZoneModules * 2;
            int ppm = Mathf.Max(1, pixelSize / moduleCount);
            int qrPixels = Size * ppm;
            int quiet = QuietZoneModules * ppm;
            int used = qrPixels + quiet * 2;
            int offset = (pixelSize - used) / 2;
            int start = offset + quiet;

            var tex = new Texture2D(pixelSize, pixelSize, TextureFormat.RGBA32, false);
            var pixels = new Color32[pixelSize * pixelSize];
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 black = new Color32(0, 0, 0, 255);

            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = white;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!modules[x, y]) continue;

                    int px = start + x * ppm;
                    int py = start + (Size - 1 - y) * ppm;
                    for (int yy = 0; yy < ppm; yy++)
                    {
                        int row = (py + yy) * pixelSize;
                        for (int xx = 0; xx < ppm; xx++)
                            pixels[row + px + xx] = black;
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}
