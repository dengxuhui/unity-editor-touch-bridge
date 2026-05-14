using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RuntimeBridge = MobileBridge.MobileBridge;

namespace MobileBridge.Editor
{
    /// <summary>
    /// Editor-only log sink for MobileBridge diagnostics.
    ///
    /// Design:
    ///   - Runtime code emits structured lines via Debug.Log/Warning/Error,
    ///     all prefixed with "[MB]".
    ///   - This class hooks Application.logMessageReceivedThreaded and filters
    ///     only those "[MB]" lines into a rotating log file on disk.
    ///   - One log file per session (Play Mode entry), stored under
    ///     <project>/Logs/MobileBridge/bridge_<yyyyMMdd_HHmmss>.log
    ///   - File stays open for the duration of Play Mode, then flushed/closed.
    ///
    /// No Runtime assembly changes required — boundary is clean.
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeLogger
    {
        // Log prefix that Runtime uses. Must match the constants in Runtime files.
        public const string Prefix = "[MB]";

        private const string PrefKey        = "MobileBridge.LoggingEnabled";
        private const string PrefKeyDebug   = "MobileBridge.ClientDebugOverlay";
        private const string PrefKeyConsole = "MobileBridge.ConsoleLoggingEnabled";

        private static StreamWriter _writer;
        private static string       _currentPath;
        private static readonly object _lock = new object();

        // Cached bool so background threads can read without touching EditorPrefs (main-thread only API).
        private static bool _consoleLoggingCache = true;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Whether diagnostic file logging is enabled (persisted via EditorPrefs).</summary>
        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(PrefKey, false);
            set
            {
                bool prev = IsEnabled;
                EditorPrefs.SetBool(PrefKey, value);
                if (prev == value) return;

                // If Play Mode is active, start/stop logging immediately
                if (!EditorApplication.isPlaying) return;
                if (value)
                    OpenFile();
                else
                    CloseFile();
            }
        }

        /// <summary>Whether the client-side debug overlay (status bar) is shown (persisted via EditorPrefs).</summary>
        public static bool ClientDebugEnabled
        {
            get => EditorPrefs.GetBool(PrefKeyDebug, false);
            set => EditorPrefs.SetBool(PrefKeyDebug, value);
        }

        /// <summary>
        /// Whether MobileBridge Runtime logs are printed to the Unity Console
        /// (persisted via EditorPrefs). Default: true so developers see output
        /// out-of-the-box; users can turn it off to keep their Console clean.
        /// </summary>
        public static bool ConsoleLoggingEnabled
        {
            // Read from the in-memory cache — safe to call from any thread.
            get => _consoleLoggingCache;
            set
            {
                _consoleLoggingCache = value;
                EditorPrefs.SetBool(PrefKeyConsole, value);
                // Keep the Runtime lambda up-to-date (reads the cache field, not EditorPrefs).
                RuntimeBridge.ConsoleLoggingProvider = () => _consoleLoggingCache;
            }
        }

        static BridgeLogger()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Load persisted value into the cache on the main thread (static ctor is always main thread).
            _consoleLoggingCache = EditorPrefs.GetBool(PrefKeyConsole, true);
            // Inject providers so Runtime can read prefs without depending on Editor assembly.
            // The Console provider reads _consoleLoggingCache — safe from any thread.
            RuntimeBridge.ClientDebugOverlayProvider = () => ClientDebugEnabled;
            RuntimeBridge.ConsoleLoggingProvider     = () => _consoleLoggingCache;
        }

        // ── Play-mode lifecycle ────────────────────────────────────────────────

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (IsEnabled) OpenFile();
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    CloseFile();
                    break;
            }
        }

        // ── File management ────────────────────────────────────────────────────

        private static void OpenFile()
        {
            try
            {
                string dir = Path.Combine(
                    Path.GetDirectoryName(Application.dataPath) ?? ".",
                    "Logs", "MobileBridge");
                Directory.CreateDirectory(dir);

                string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentPath = Path.Combine(dir, $"bridge_{ts}.log");

                _writer = new StreamWriter(_currentPath, append: false, encoding: Encoding.UTF8)
                {
                    AutoFlush = true,   // flush every line so file is readable while running
                };

                Application.logMessageReceivedThreaded += OnLogMessage;

                _writer.WriteLine($"# MobileBridge diagnostic log");
                _writer.WriteLine($"# Session start : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _writer.WriteLine($"# Unity version : {Application.unityVersion}");
                _writer.WriteLine($"# Platform      : {Application.platform}");
                _writer.WriteLine($"# File          : {_currentPath}");
                _writer.WriteLine($"# ─────────────────────────────────────────────────────────");
                _writer.WriteLine();

                Debug.Log($"[MB][BridgeLogger] Log file opened: {_currentPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MobileBridge] BridgeLogger failed to open log file: {ex.Message}");
            }
        }

        private static void CloseFile()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;

            lock (_lock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine();
                    _writer.WriteLine($"# Session end: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _writer.Flush();
                    _writer.Close();
                }
                catch { /* best-effort */ }
                finally
                {
                    _writer = null;
                }
            }

            if (_currentPath != null)
                Debug.Log($"[MobileBridge] BridgeLogger closed. Log saved to: {_currentPath}");
        }

        // ── Log message handler (called on ANY thread) ─────────────────────────

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // Only capture lines from MobileBridge Runtime
            if (string.IsNullOrEmpty(condition) || !condition.Contains(Prefix))
                return;

            lock (_lock)
            {
                if (_writer == null) return;
                try
                {
                    string level = type switch
                    {
                        LogType.Warning => "WARN ",
                        LogType.Error   => "ERROR",
                        LogType.Exception => "EXCPT",
                        _               => "INFO ",
                    };

                    string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    _writer.WriteLine($"{ts} [{level}] {condition}");

                    // For warnings/errors include the first line of stack trace
                    if ((type == LogType.Error || type == LogType.Exception)
                        && !string.IsNullOrEmpty(stackTrace))
                    {
                        // Indent each stack line for readability
                        foreach (var line in stackTrace.Split('\n'))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length == 0) continue;
                            _writer.WriteLine($"          ↳ {trimmed}");
                        }
                    }
                }
                catch { /* never crash the game for logging */ }
            }
        }

        // ── Editor menu helper ─────────────────────────────────────────────────

        [MenuItem("Window/Mobile Bridge/Enable Diagnostic Logging", false, 200)]
        private static void ToggleLogging()
        {
            IsEnabled = !IsEnabled;
            Debug.Log($"[MobileBridge] Diagnostic logging {(IsEnabled ? "enabled" : "disabled")}.");
        }

        [MenuItem("Window/Mobile Bridge/Enable Diagnostic Logging", true)]
        private static bool ToggleLoggingValidate()
        {
            Menu.SetChecked("Window/Mobile Bridge/Enable Diagnostic Logging", IsEnabled);
            return true;
        }

        [MenuItem("Window/Mobile Bridge/Enable Console Logging", false, 201)]
        private static void ToggleConsoleLogging()
        {
            ConsoleLoggingEnabled = !ConsoleLoggingEnabled;
        }

        [MenuItem("Window/Mobile Bridge/Enable Console Logging", true)]
        private static bool ToggleConsoleLoggingValidate()
        {
            Menu.SetChecked("Window/Mobile Bridge/Enable Console Logging", ConsoleLoggingEnabled);
            return true;
        }

        [MenuItem("Window/Mobile Bridge/Show Client Debug Overlay", false, 210)]
        private static void ToggleClientDebug()
        {
            bool next = !ClientDebugEnabled;
            ClientDebugEnabled = next;
            // Push cfg message to all connected clients immediately (best-effort)
            RuntimeBridge.Instance?.BroadcastConfigMessage(next);
        }

        [MenuItem("Window/Mobile Bridge/Show Client Debug Overlay", true)]
        private static bool ToggleClientDebugValidate()
        {
            Menu.SetChecked("Window/Mobile Bridge/Show Client Debug Overlay", ClientDebugEnabled);
            return true;
        }

        [MenuItem("Window/Mobile Bridge/Open Log Folder", false, 220)]
        private static void OpenLogFolder()
        {
            string dir = Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Logs", "MobileBridge");
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        /// <summary>Returns the path of the log file for the current session, or null.</summary>
        public static string CurrentLogPath => _currentPath;
    }
}
