using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

namespace MobileBridge.Editor
{
    public sealed class MobileBridgeWindow : EditorWindow
    {
        [MenuItem("Window/Mobile Bridge/Control Panel")]
        public static void ShowWindow()
        {
            var win = GetWindow<MobileBridgeWindow>("Mobile Bridge");
            win.minSize = new Vector2(320, 520);
        }

        // ── State ──────────────────────────────────────────────────────────────

        private MobileBridge _bridge;
        private bool  _isRunning;
        private int   _clientCount;
        private float _refreshTimer;
        private Texture2D _qrTex;
        private string _currentUrl;

        // Settings (mirrored to/from MobileBridge component)
        private int   _targetFps    = 30;
        private int   _jpegQuality  = 75;

        // Game View resolution (read-only, auto-detected via reflection)
        private int   _gameViewWidth  = 0;
        private int   _gameViewHeight = 0;

        // ── EditorWindow lifecycle ─────────────────────────────────────────────

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            RefreshConnectionVisuals();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            QRCodeGenerator.Cleanup();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                _isRunning = false;
            Repaint();
        }

        private void Update()
        {
            // Refresh status at 2 Hz
            _refreshTimer += 0.5f;
            if (_refreshTimer < 1f) return;
            _refreshTimer = 0f;

            _bridge      = MobileBridge.Instance;
            _isRunning   = MobileBridge.IsActive;
            _clientCount = (_bridge != null && _isRunning) ? _bridge.ClientCount : 0;
            RefreshConnectionVisuals();
            Repaint();
        }

        // ── GUI ────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            GUILayout.Space(4);
            DrawStatus();
            GUILayout.Space(8);
            DrawSettings();
            GUILayout.Space(8);
            DrawConnection();
            GUILayout.Space(8);
            DrawControls();
        }

        private void DrawHeader()
        {
            using var h = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            GUILayout.Label("Unity Editor Touch Bridge", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("v0.1.0", EditorStyles.miniLabel);
        }

        private void DrawStatus()
        {
            string dot   = _isRunning ? "●" : "○";
            Color  color = _isRunning ? Color.green : Color.gray;

            var prev = GUI.color;
            GUI.color = color;
            GUILayout.Label($"{dot}  {(_isRunning ? "Running" : "Stopped")}", EditorStyles.boldLabel);
            GUI.color = prev;
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Stream Settings", EditorStyles.boldLabel);
            using var indent = new EditorGUI.IndentLevelScope(1);

            // Resolution: auto-follow Game View (read-only display)
            RefreshGameViewSize();
            string resLabel = (_gameViewWidth > 0 && _gameViewHeight > 0)
                ? $"{_gameViewWidth} × {_gameViewHeight}  (Game View)"
                : "Unknown (enter Play Mode)";
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField("Resolution", resLabel);

            _targetFps   = EditorGUILayout.IntSlider("Target FPS", _targetFps, 1, 60);
            _jpegQuality = EditorGUILayout.IntSlider("JPEG Quality", _jpegQuality, 1, 100);

            if (_isRunning && _bridge != null)
            {
                _bridge.targetFps   = _targetFps;
                _bridge.jpegQuality = _jpegQuality;
                // streamWidth/streamHeight are driven by Screen.width/height at runtime;
                // no need to push them here.
            }
        }

        /// <summary>
        /// Reads the current Game View size via reflection (internal Unity API).
        /// Falls back to 0×0 if unavailable.
        /// </summary>
        private void RefreshGameViewSize()
        {
            try
            {
                var gameViewType = typeof(UnityEditor.EditorWindow).Assembly
                    .GetType("UnityEditor.GameView");
                if (gameViewType == null) return;

                var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameView == null) return;

                // Unity 2022+: targetSize property returns the current Game View pixel size
                var targetSizeProp = gameViewType.GetProperty(
                    "targetSize",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (targetSizeProp != null)
                {
                    var size = (Vector2)targetSizeProp.GetValue(gameView);
                    _gameViewWidth  = Mathf.RoundToInt(size.x);
                    _gameViewHeight = Mathf.RoundToInt(size.y);
                    return;
                }

                // Fallback: currentGameViewSize (older Unity versions)
                var sizeProp = gameViewType.GetProperty(
                    "currentGameViewSize",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (sizeProp != null)
                {
                    var gvSize = sizeProp.GetValue(gameView);
                    var gvSizeType = gvSize.GetType();
                    var wProp = gvSizeType.GetProperty("width");
                    var hProp = gvSizeType.GetProperty("height");
                    if (wProp != null && hProp != null)
                    {
                        _gameViewWidth  = Mathf.RoundToInt((float)wProp.GetValue(gvSize));
                        _gameViewHeight = Mathf.RoundToInt((float)hProp.GetValue(gvSize));
                    }
                }
            }
            catch
            {
                // Non-critical; silently ignore reflection failures
            }
        }

        private void DrawConnection()
        {
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            string url = _currentUrl ?? BuildBridgeUrl();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_qrTex != null)
                    GUILayout.Label(_qrTex, GUILayout.Width(80), GUILayout.Height(80));

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.SelectableLabel(url, EditorStyles.textField,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));

                    if (GUILayout.Button("Copy Link", GUILayout.Width(100)))
                        GUIUtility.systemCopyBuffer = url;

                    GUILayout.Label("Scan QR to open client", EditorStyles.miniLabel);
                }
            }
        }

        private void DrawControls()
        {
            bool inPlayMode = EditorApplication.isPlaying;

            using (new EditorGUI.DisabledScope(!inPlayMode))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_isRunning))
                    {
                        if (GUILayout.Button("▶  Start"))
                            StartBridge();
                    }
                    using (new EditorGUI.DisabledScope(!_isRunning))
                    {
                        if (GUILayout.Button("■  Stop"))
                            StopBridge();
                    }
                    if (GUILayout.Button("⚙  Setup Wizard", GUILayout.Width(120)))
                        SetupWizard.ShowWindow();
                }
            }

            if (!inPlayMode)
                EditorGUILayout.HelpBox("Enter Play Mode to start the bridge.", MessageType.Info);

            if (inPlayMode && _isRunning)
                EditorGUILayout.HelpBox(
                    "触控生效需要 Game View 保持聚焦。\n" +
                    "请点击 Game View 窗口使其获得焦点，手机触控即可正常注入。\n\n" +
                    "Touch input requires the Game View to have focus.\n" +
                    "Click the Game View window first, then touch events from your phone will work correctly.\n\n" +
                    "画面串流不受此影响，始终正常推送。\n" +
                    "Video streaming is unaffected and always active.",
                    MessageType.Warning);
        }

        // ── Actions ────────────────────────────────────────────────────────────

        private void StartBridge()
        {
            EnsureBridgeComponent();
            if (_bridge == null) return;
            _bridge.targetFps    = _targetFps;
            _bridge.jpegQuality  = _jpegQuality;
            // streamWidth/streamHeight no longer set here;
            // CaptureLoop uses Screen.width/height (Game View size) directly.
            _bridge.StartBridge();
            _isRunning = true;
        }

        private void StopBridge()
        {
            _bridge?.StopBridge();
            _isRunning = false;
        }

        private void EnsureBridgeComponent()
        {
            if (MobileBridge.Instance != null) { _bridge = MobileBridge.Instance; return; }

            var go = new GameObject("[MobileBridge]");
            _bridge = go.AddComponent<MobileBridge>();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the best LAN IP address of this machine.
        /// Priority: private RFC-1918 addresses (192.168/10/172.16-31) found via
        /// NetworkInterface enumeration, which avoids macOS virtual/test interfaces
        /// (e.g. 198.18.x.x used by the Proxyman/Charles performance network range).
        /// Falls back to the UDP-connect trick, then 127.0.0.1.
        /// </summary>
        internal static string GetLocalIP()
        {
            // 1. Enumerate all up, non-loopback, non-virtual interfaces
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    var addr = ua.Address;
                    if (IsPrivateLanAddress(addr))
                        return addr.ToString();
                }
            }

            // 2. UDP-connect fallback (no packets sent)
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                sock.Connect("8.8.8.8", 65530);
                var candidate = ((IPEndPoint)sock.LocalEndPoint).Address;
                if (IsPrivateLanAddress(candidate))
                    return candidate.ToString();
            }
            catch { }

            return "127.0.0.1";
        }

        private static string BuildBridgeUrl()
        {
            return $"http://{GetLocalIP()}:{WebSocketServer.HttpPort}";
        }

        private void RefreshConnectionVisuals()
        {
            string nextUrl = BuildBridgeUrl();
            if (_currentUrl == nextUrl && _qrTex != null)
                return;

            _currentUrl = nextUrl;
            try
            {
                _qrTex = QRCodeGenerator.Generate(_currentUrl, 128);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MobileBridge] QR generation failed: {ex.Message}");
                _qrTex = null;
            }
        }

        /// <summary>Returns true for RFC-1918 private address ranges only.</summary>
        private static bool IsPrivateLanAddress(IPAddress addr)
        {
            var b = addr.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }
    }
}
