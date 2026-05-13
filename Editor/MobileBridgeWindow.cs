using System.Net;
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
            win.minSize = new Vector2(320, 480);
        }

        // ── State ──────────────────────────────────────────────────────────────

        private MobileBridge _bridge;
        private bool  _isRunning;
        private int   _clientCount;
        private float _refreshTimer;
        private Texture2D _qrTex;

        // Settings (mirrored to/from MobileBridge component)
        private int   _targetFps    = 30;
        private int   _jpegQuality  = 75;
        private int   _streamWidthIndex  = 1;   // default 960×540
        private int   _streamHeightIndex = 1;

        private static readonly string[] ResLabels = { "640×360", "960×540", "1280×720" };
        private static readonly int[] ResWidths    = { 640, 960, 1280 };
        private static readonly int[] ResHeights   = { 360, 540, 720  };

        // ── EditorWindow lifecycle ─────────────────────────────────────────────

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            _qrTex = QRCodeGenerator.GeneratePlaceholder(128);
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

            _streamWidthIndex = EditorGUILayout.Popup("Resolution",
                _streamWidthIndex, ResLabels);

            _targetFps   = EditorGUILayout.IntSlider("Target FPS", _targetFps, 1, 60);
            _jpegQuality = EditorGUILayout.IntSlider("JPEG Quality", _jpegQuality, 1, 100);

            if (_isRunning && _bridge != null)
            {
                _bridge.targetFps   = _targetFps;
                _bridge.jpegQuality = _jpegQuality;
                _bridge.streamWidth  = ResWidths[_streamWidthIndex];
                _bridge.streamHeight = ResHeights[_streamWidthIndex];
            }
        }

        private void DrawConnection()
        {
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);

            string ip  = GetLocalIP();
            string url = $"http://{ip}:{WebSocketServer.HttpPort}";

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

                    GUILayout.Label("(QR code — P4)", EditorStyles.miniLabel);
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
        }

        // ── Actions ────────────────────────────────────────────────────────────

        private void StartBridge()
        {
            EnsureBridgeComponent();
            if (_bridge == null) return;
            _bridge.targetFps    = _targetFps;
            _bridge.jpegQuality  = _jpegQuality;
            _bridge.streamWidth  = ResWidths[_streamWidthIndex];
            _bridge.streamHeight = ResHeights[_streamWidthIndex];
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

        private static string GetLocalIP()
        {
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                sock.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)sock.LocalEndPoint).Address.ToString();
            }
            catch { return "127.0.0.1"; }
        }
    }
}
