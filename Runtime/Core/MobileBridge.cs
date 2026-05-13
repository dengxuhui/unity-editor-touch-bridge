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
            Debug.Log($"[MobileBridge] Started — WS:{WebSocketServer.WsPort}  HTTP:{WebSocketServer.HttpPort}");
        }

        public void StopBridge()
        {
            if (!IsActive) return;
            IsActive = false;

            _receiver?.Stop();
            _capturer?.Stop();
            _server?.Stop();
            _server?.Dispose();

            _receiver = null;
            _capturer = null;
            _server   = null;

            Debug.Log("[MobileBridge] Stopped.");
        }

        // ── Frame pipeline (called from main thread by URPCaptureFeature) ──────

        /// <summary>
        /// Called by URPCaptureFeature with raw RGBA32 pixels from AsyncGPUReadback.
        /// Encodes to JPEG (main thread, see FrameCapturer for context) then enqueues
        /// for the background send thread.
        /// </summary>
        public void OnFrameReady(byte[] rgba32, int width, int height)
        {
            if (!IsActive || _capturer == null) return;

            // Encode to JPEG using Unity's built-in converter (main thread required).
            // TODO(P3): move encoding to background thread with a pure-C# JPEG encoder.
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(rgba32);
            tex.Apply();
            byte[] jpeg = tex.EncodeToJPG(jpegQuality);
            Destroy(tex);

            _capturer.EnqueueJpeg(jpeg);
        }
    }
}
