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
                // Skip capture entirely when no client is connected — avoids
                // wasting CPU/GPU on ReadPixels + JPEG encode with nobody to receive.
                if (ClientCount == 0)
                {
                    yield return null;
                    continue;
                }

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

                int screenW = Screen.width;
                int screenH = Screen.height;

                // Capture at full Game View resolution (1:1, no downscale).
                // The stream resolution always matches the Game View to avoid
                // meaningless up/down-sampling.
                int capW = screenW;
                int capH = screenH;

                // Lazy-allocate / resize reusable texture to the effective capture size.
                if (_readbackTex == null ||
                    _readbackTex.width  != capW ||
                    _readbackTex.height != capH)
                {
                    if (_readbackTex != null) Destroy(_readbackTex);
                    _readbackTex = new Texture2D(capW, capH, TextureFormat.RGB24, false);
                }

                // Capture the full screen rect (no letterbox offset needed since
                // capW/capH always equal screenW/screenH).
                _readbackTex.ReadPixels(new Rect(0, 0, capW, capH), 0, 0, false);
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
