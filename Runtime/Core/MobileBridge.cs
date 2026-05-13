using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MobileBridge
{
    /// <summary>
    /// Main entry point. Add this component to any GameObject in the scene,
    /// or let MobileBridgeWindow create it automatically.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class MobileBridge : MonoBehaviour
    {
        // ── Public API ─────────────────────────────────────────────────────────

        public static MobileBridge Instance { get; private set; }

        /// <summary>True while the bridge is started. Read by URPCaptureFeature.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Number of currently connected WebSocket clients.</summary>
        public int ClientCount => _server?.ClientCount ?? 0;

        // ── Inspector fields ───────────────────────────────────────────────────

        [Header("Streaming")]
        [Range(1, 60)]  public int   targetFps    = 30;
        [Range(1, 100)] public int   jpegQuality  = 75;
        [Range(320, 1920)] public int streamWidth  = 960;
        [Range(180, 1080)] public int streamHeight = 540;

        // ── Internal references ────────────────────────────────────────────────

        private WebSocketServer _server;
        private FrameCapturer   _capturer;
        private TouchReceiver   _receiver;

        // Reusable texture for ReadPixels — avoid per-frame alloc
        private Texture2D _readbackTex;
        private float     _lastCaptureTime;

        private static string ClientHtmlPath
        {
            get
            {
#if UNITY_EDITOR
                // Resolve relative to the package root via the project's Packages folder
                string packageRoot = Path.GetFullPath(
                    "Packages/com.dengxuhui.unity-editor-touch-bridge");
                return Path.Combine(packageRoot, "WebClient", "client.html");
#else
                return Path.Combine(Application.streamingAssetsPath, "MobileBridge", "client.html");
#endif
            }
        }

        // ── MonoBehaviour lifecycle ────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            StopBridge();
            if (Instance == this) Instance = null;
            if (_readbackTex != null)
            {
                Destroy(_readbackTex);
                _readbackTex = null;
            }
        }

        // ── Bridge control ─────────────────────────────────────────────────────

        public void StartBridge()
        {
            if (IsActive)
            {
                Debug.LogWarning("[MobileBridge] Already running.");
                return;
            }

            _server   = new WebSocketServer(ClientHtmlPath);
            _capturer = new FrameCapturer(_server);
            _receiver = new TouchReceiver(_server);

            _server.Start();
            _capturer.Start();
            _receiver.Start();

            IsActive = true;
            StartCoroutine(CaptureLoop());

            Debug.Log($"[MobileBridge] Started — WS:{WebSocketServer.WsPort}  HTTP:{WebSocketServer.HttpPort}");
        }

        public void StopBridge()
        {
            if (!IsActive) return;
            IsActive = false;

            // CaptureLoop coroutine checks IsActive and will exit on its own.

            _receiver?.Stop();
            _capturer?.Stop();
            _server?.Stop();
            _server?.Dispose();

            _receiver = null;
            _capturer = null;
            _server   = null;

            Debug.Log("[MobileBridge] Stopped.");
        }

        // ── MonoBehaviour update ───────────────────────────────────────────────

        private void Update()
        {
            // Drain touch events queued by the WS thread onto the main thread,
            // where QueueStateEvent (Allocator.Temp) is permitted.
            _receiver?.Tick();
        }

        // ── Frame capture loop ─────────────────────────────────────────────────

        /// <summary>
        /// Coroutine that captures the complete screen (including Canvas Overlay)
        /// every frame after Unity has finished compositing everything — including
        /// Screen Space Overlay canvases — into the final backbuffer.
        ///
        /// WaitForEndOfFrame is the only point where ReadPixels can see Overlay UI.
        /// AsyncGPUReadback from a ScriptableRenderPass fires before Overlay is drawn,
        /// which is why this approach is required.
        ///
        /// Trade-off: ReadPixels is a synchronous GPU stall (~1-3 ms at stream
        /// resolution). For an Editor-only debug tool this is acceptable.
        /// </summary>
        private IEnumerator CaptureLoop()
        {
            var waitEof = new WaitForEndOfFrame();
            float interval = 1f / Mathf.Max(targetFps, 1);

            while (IsActive)
            {
                // Throttle to targetFps
                float now = Time.realtimeSinceStartup;
                if (now - _lastCaptureTime < interval)
                {
                    yield return null;
                    continue;
                }

                yield return waitEof;   // ← all rendering including Overlay is done here

                if (!IsActive) yield break;

                // Update interval in case targetFps changed at runtime
                interval = 1f / Mathf.Max(targetFps, 1);
                _lastCaptureTime = Time.realtimeSinceStartup;

                // Lazy-allocate / resize reusable texture
                if (_readbackTex == null ||
                    _readbackTex.width != streamWidth ||
                    _readbackTex.height != streamHeight)
                {
                    if (_readbackTex != null) Destroy(_readbackTex);
                    _readbackTex = new Texture2D(streamWidth, streamHeight,
                        TextureFormat.RGB24, false);
                }

                // ReadPixels reads from the screen backbuffer into the texture.
                // The Rect maps the centre of the screen at stream resolution,
                // which handles Game View black bars (letterbox / pillarbox) correctly
                // as long as the stream dimensions match the Game View aspect ratio.
                // For a more robust solution consider reading full screen then scaling.
                int screenW = Screen.width;
                int screenH = Screen.height;
                int srcX    = (screenW - streamWidth)  / 2;
                int srcY    = (screenH - streamHeight) / 2;
                // Clamp to screen bounds to avoid GL errors
                srcX = Mathf.Clamp(srcX, 0, screenW);
                srcY = Mathf.Clamp(srcY, 0, screenH);
                int readW = Mathf.Min(streamWidth,  screenW - srcX);
                int readH = Mathf.Min(streamHeight, screenH - srcY);

                _readbackTex.ReadPixels(new Rect(srcX, srcY, readW, readH), 0, 0, false);
                _readbackTex.Apply(false);

                byte[] jpeg = _readbackTex.EncodeToJPG(jpegQuality);
                _capturer?.EnqueueJpeg(jpeg);
            }
        }

        // ── Legacy callback (kept for API compatibility, no longer used) ────────

        /// <summary>
        /// Previously called by URPCaptureFeature with raw RGBA32 pixels.
        /// Now unused — capture is driven by CaptureLoop coroutine.
        /// Kept to avoid compile errors if any external code references it.
        /// </summary>
        [System.Obsolete("Frame capture is now driven by the internal CaptureLoop coroutine. This method is no longer called.")]
        public void OnFrameReady(byte[] rgba32, int width, int height)
        {
            // no-op
        }
    }
}
