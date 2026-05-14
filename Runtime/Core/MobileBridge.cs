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
        // ── Editor injection points ────────────────────────────────────────────
        // All fields in this block are Editor-only and are bound by the Editor
        // assembly ([InitializeOnLoad] or just before StartBridge). They must
        // NEVER be referenced outside a #if UNITY_EDITOR guard so the Runtime
        // assembly compiles cleanly without any Editor dependency.
#if UNITY_EDITOR
        /// <summary>
        /// Injected by the Editor assembly to provide the current
        /// "show client debug overlay" preference.
        /// </summary>
        public static System.Func<bool> ClientDebugOverlayProvider;

        /// <summary>
        /// Injected by the Editor assembly to indicate whether MobileBridge logs
        /// should be printed to the Unity Console.
        /// </summary>
        public static System.Func<bool> ConsoleLoggingProvider;

        // ── Network delegates (bound by MobileBridgeWindow before StartBridge) ─

        /// <summary>Returns the number of currently connected WebSocket clients.</summary>
        public static System.Func<int> ClientCountProvider;

        /// <summary>Broadcast a binary JPEG frame to all connected clients.</summary>
        public static System.Action<byte[]> BroadcastFrameAction;

        /// <summary>Broadcast a text message to all connected clients.</summary>
        public static System.Action<string> BroadcastTextAction;

        /// <summary>Send a text message to a single session by ID.</summary>
        public static System.Action<string, string> SendTextAction;

        /// <summary>Start the WebSocket + HTTP servers.</summary>
        public static System.Action StartServerAction;

        /// <summary>Stop the WebSocket + HTTP servers.</summary>
        public static System.Action StopServerAction;

        /// <summary>
        /// Register a handler for the OnHelloReceived server event.
        /// Arg: the handler Action&lt;string sessionId&gt;.
        /// </summary>
        public static System.Action<System.Action<string>> RegisterHelloHandler;

        /// <summary>
        /// Register a handler for the OnClientConnected server event.
        /// Arg: the handler Action&lt;string sessionId&gt;.
        /// </summary>
        public static System.Action<System.Action<string>> RegisterConnectHandler;

        /// <summary>
        /// Subscribe a touch message handler.
        /// Used by TouchReceiver.Start().
        /// </summary>
        public static System.Action<System.Action<string>> RegisterTouchHandler;

        /// <summary>
        /// Unsubscribe a touch message handler.
        /// Used by TouchReceiver.Stop().
        /// </summary>
        public static System.Action<System.Action<string>> UnregisterTouchHandler;
#endif

        // ── Public API ─────────────────────────────────────────────────────────

        public static MobileBridge Instance { get; private set; }

        /// <summary>True while the bridge is started. Read by URPCaptureFeature.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Number of currently connected WebSocket clients.</summary>
        public int ClientCount
        {
            get
            {
#if UNITY_EDITOR
                return ClientCountProvider?.Invoke() ?? 0;
#else
                return 0;
#endif
            }
        }

        // ── Inspector fields ───────────────────────────────────────────────────

        [Header("Streaming")]
        [Range(1, 60)]  public int   targetFps    = 30;
        [Range(1, 100)] public int   jpegQuality  = 75;

        // ── Internal references ────────────────────────────────────────────────

        private FrameCapturer _capturer;
        private TouchReceiver _receiver;

        // Reusable texture for ReadPixels
        private Texture2D _readbackTex;
        private float     _lastCaptureTime;

        // ── Capture diagnostics ────────────────────────────────────────────────
        private int   _captureCountThisSecond;
        private int   _captureSkipNoClientThisSecond;
        private int   _captureSkipThrottleThisSecond;
        private float _lastStatsTime;
        private long  _totalCaptureBytes;
        private int   _totalCaptureFrames;

        // Stall detection: clients connected but nothing enqueued
        private const float CaptureStallThresholdSec = 2f;
        private float _lastEnqueueTime = -1f;

        private static string ClientHtmlPath
        {
            get
            {
#if UNITY_EDITOR
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

        /// <summary>Emit a log line only when console logging is enabled.</summary>
        internal static void MBLog(string msg)
        {
#if UNITY_EDITOR
            if (ConsoleLoggingProvider != null && !ConsoleLoggingProvider()) return;
#endif
            Debug.Log(msg);
        }

        /// <summary>Emit a warning line only when console logging is enabled.</summary>
        internal static void MBLogWarning(string msg)
        {
#if UNITY_EDITOR
            if (ConsoleLoggingProvider != null && !ConsoleLoggingProvider()) return;
#endif
            Debug.LogWarning(msg);
        }

        public void StartBridge()
        {
            if (IsActive)
            {
                MBLogWarning("[MB][MobileBridge] Already running.");
                return;
            }

#if UNITY_EDITOR
            // Start the servers first so sessions/events are available
            StartServerAction?.Invoke();

            // Subscribe to session events before any client can connect
            RegisterHelloHandler?.Invoke(sessionId =>
            {
                bool show = ClientDebugOverlayProvider?.Invoke() ?? false;
                string cfg = $"{{\"type\":\"cfg\",\"showDebug\":{(show ? "true" : "false")}}}";
                MBLog($"[MB][MobileBridge] hello_recv id={sessionId} → sending cfg showDebug={show}");
                SendTextAction?.Invoke(sessionId, cfg);
            });

            RegisterConnectHandler?.Invoke(sessionId =>
            {
                bool show = ClientDebugOverlayProvider?.Invoke() ?? false;
                string cfg = $"{{\"type\":\"cfg\",\"showDebug\":{(show ? "true" : "false")}}}";
                MBLog($"[MB][MobileBridge] client_connected id={sessionId} → sending cfg showDebug={show}");
                SendTextAction?.Invoke(sessionId, cfg);
            });

            _capturer = new FrameCapturer(BroadcastFrameAction, ClientCountProvider);
            _receiver = new TouchReceiver(RegisterTouchHandler, UnregisterTouchHandler);
#else
            _capturer = new FrameCapturer();
            _receiver = new TouchReceiver();
#endif

            _capturer.Start();
            _receiver.Start();

            IsActive = true;
            _lastStatsTime   = Time.realtimeSinceStartup;
            _lastEnqueueTime = Time.realtimeSinceStartup;
            StartCoroutine(CaptureLoop());

            MBLog($"[MB][MobileBridge] Started — targetFps={targetFps} jpegQuality={jpegQuality}");
        }

        public void StopBridge()
        {
            if (!IsActive) return;
            IsActive = false;

            _receiver?.Stop();
            _capturer?.Stop();

#if UNITY_EDITOR
            StopServerAction?.Invoke();
#endif

            _receiver = null;
            _capturer = null;

            MBLog($"[MB][MobileBridge] Stopped. totalFrames={_totalCaptureFrames} " +
                       $"totalBytes={_totalCaptureBytes}");
        }

        /// <summary>
        /// Broadcast a config message to all connected clients.
        /// Called by the Editor menu to push settings without requiring a reconnect.
        /// </summary>
        public void BroadcastConfigMessage(bool showDebug)
        {
            MBLog($"[MB][MobileBridge] BroadcastConfigMessage showDebug={showDebug} clients={ClientCount}");
#if UNITY_EDITOR
            BroadcastTextAction?.Invoke($"{{\"type\":\"cfg\",\"showDebug\":{(showDebug ? "true" : "false")}}}");
#endif
        }

        // ── MonoBehaviour update ───────────────────────────────────────────────

        private void Update()
        {
            _receiver?.Tick();
        }

        // ── Frame capture loop ─────────────────────────────────────────────────

        private IEnumerator CaptureLoop()
        {
            var waitEof  = new WaitForEndOfFrame();
            float interval = 1f / Mathf.Max(targetFps, 1);

            while (IsActive)
            {
                int clients = ClientCount;

                if (clients == 0)
                {
                    _captureSkipNoClientThisSecond++;
                    PrintStatsIfDue();
                    yield return null;
                    continue;
                }

                float now = Time.realtimeSinceStartup;
                if (now - _lastCaptureTime < interval)
                {
                    _captureSkipThrottleThisSecond++;
                    PrintStatsIfDue();
                    yield return null;
                    continue;
                }

                // Stall detection: clients connected but we haven't enqueued for a while
                if (_lastEnqueueTime >= 0 &&
                    now - _lastEnqueueTime > CaptureStallThresholdSec)
                {
                    MBLogWarning(
                        $"[MB][MobileBridge] WARN capture_stall — " +
                        $"{(now - _lastEnqueueTime) * 1000f:F0}ms since last enqueue, clients={clients}");
                    _lastEnqueueTime = now;
                }

                yield return waitEof;

                if (!IsActive) yield break;

                interval = 1f / Mathf.Max(targetFps, 1);
                _lastCaptureTime = Time.realtimeSinceStartup;

                int screenW = Screen.width;
                int screenH = Screen.height;

                if (_readbackTex == null ||
                    _readbackTex.width  != screenW ||
                    _readbackTex.height != screenH)
                {
                    if (_readbackTex != null)
                    {
                        MBLog($"[MB][MobileBridge] Texture resized {_readbackTex.width}x{_readbackTex.height} → {screenW}x{screenH}");
                        Destroy(_readbackTex);
                    }
                    _readbackTex = new Texture2D(screenW, screenH, TextureFormat.RGB24, false);
                    MBLog($"[MB][MobileBridge] Readback texture created {screenW}x{screenH}");
                }

                _readbackTex.ReadPixels(new Rect(0, 0, screenW, screenH), 0, 0, false);
                _readbackTex.Apply(false);

                byte[] jpeg = _readbackTex.EncodeToJPG(jpegQuality);

                _capturer?.EnqueueJpeg(jpeg);
                _lastEnqueueTime = Time.realtimeSinceStartup;

                _captureCountThisSecond++;
                _totalCaptureFrames++;
                _totalCaptureBytes += jpeg.Length;

                PrintStatsIfDue();
            }
        }

        private void PrintStatsIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastStatsTime < 1f) return;

            float windowSec = now - _lastStatsTime;
            float capFps    = _captureCountThisSecond / windowSec;
            int   clients   = ClientCount;
            long  avgBytes  = _totalCaptureFrames > 0
                ? _totalCaptureBytes / _totalCaptureFrames : 0;

            MBLog(
                $"[MB][MobileBridge] STATS " +
                $"capFps={capFps:F1} clients={clients} " +
                $"skipNoClient={_captureSkipNoClientThisSecond} " +
                $"skipThrottle={_captureSkipThrottleThisSecond} " +
                $"avgJpegBytes={avgBytes} " +
                $"totalFrames={_totalCaptureFrames}");

            _captureCountThisSecond            = 0;
            _captureSkipNoClientThisSecond     = 0;
            _captureSkipThrottleThisSecond     = 0;
            _lastStatsTime = now;
        }

        // ── Legacy callback (no longer used) ──────────────────────────────────

        [System.Obsolete("Frame capture is now driven by the internal CaptureLoop coroutine. This method is no longer called.")]
        public void OnFrameReady(byte[] rgba32, int width, int height)
        {
            // no-op
        }
    }
}
