using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MobileBridge.Editor
{
    public sealed class SetupWizard : EditorWindow
    {
        [MenuItem("Window/Mobile Bridge/Setup Wizard")]
        public static void ShowWindow()
        {
            var win = GetWindow<SetupWizard>(true, "Mobile Bridge — Setup Wizard");
            win.minSize = new Vector2(480, 360);
        }

        private enum Step { CheckURP, AddFeature, Done }

        private Step   _step = Step.CheckURP;
        private string _log  = "";

        private UniversalRenderPipelineAsset _urpAsset;
        private ScriptableRendererData       _rendererData;

        // ── GUI ────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Mobile Bridge — First-Time Setup", EditorStyles.boldLabel);
            GUILayout.Space(8);

            switch (_step)
            {
                case Step.CheckURP:    DrawCheckUrp();    break;
                case Step.AddFeature:  DrawAddFeature();  break;
                case Step.Done:        DrawDone();        break;
            }

            if (!string.IsNullOrEmpty(_log))
            {
                GUILayout.Space(8);
                EditorGUILayout.HelpBox(_log, MessageType.Info);
            }
        }

        // ── Steps ──────────────────────────────────────────────────────────────

        private void DrawCheckUrp()
        {
            EditorGUILayout.LabelField("Step 1 — Verify URP", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This package requires the Universal Render Pipeline (URP 14.0+).\n" +
                "Make sure your project uses a URP Renderer Asset.", MessageType.Info);

            _urpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;

            if (_urpAsset == null)
            {
                EditorGUILayout.HelpBox(
                    "No URP asset found as the default render pipeline.\n" +
                    "Go to Project Settings > Graphics and assign a Universal Render Pipeline Asset.",
                    MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox($"URP asset found: {_urpAsset.name}", MessageType.None);
                if (GUILayout.Button("Next →")) _step = Step.AddFeature;
            }
        }

        private void DrawAddFeature()
        {
            EditorGUILayout.LabelField("Step 2 — Add URPCaptureFeature", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The URPCaptureFeature must be added to your URP Renderer Asset's " +
                "Renderer Features list.", MessageType.Info);

            _rendererData = (ScriptableRendererData)EditorGUILayout.ObjectField(
                "Renderer Asset", _rendererData,
                typeof(ScriptableRendererData), false);

            if (_rendererData == null)
                TryAutoDetectRendererData();

            using (new EditorGUI.DisabledScope(_rendererData == null))
            {
                if (GUILayout.Button("Add URPCaptureFeature"))
                    AddCaptureFeature();
            }

            GUILayout.Space(4);
            if (GUILayout.Button("Skip (already added)"))
                _step = Step.Done;
        }

        private void DrawDone()
        {
            EditorGUILayout.LabelField("Setup Complete!", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "You're all set.\n\n" +
                "1. Enter Play Mode in Unity.\n" +
                "2. Open Window > Mobile Bridge > Control Panel.\n" +
                "3. Click ▶ Start and scan the link with your phone.",
                MessageType.None);

            if (GUILayout.Button("Open Control Panel"))
            {
                MobileBridgeWindow.ShowWindow();
                Close();
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void TryAutoDetectRendererData()
        {
            if (_urpAsset == null) return;

            var so = new SerializedObject(_urpAsset);
            var prop = so.FindProperty("m_RendererDataList");
            if (prop == null || prop.arraySize == 0) return;

            _rendererData = prop.GetArrayElementAtIndex(0).objectReferenceValue
                            as ScriptableRendererData;
        }

        private void AddCaptureFeature()
        {
            if (_rendererData == null) return;

            foreach (var f in _rendererData.rendererFeatures)
            {
                if (f is URPCaptureFeature)
                {
                    _log = "URPCaptureFeature is already in the renderer.";
                    _step = Step.Done;
                    return;
                }
            }

            var feature = ScriptableObject.CreateInstance<URPCaptureFeature>();
            feature.name = "MobileBridge Capture";

            Undo.RecordObject(_rendererData, "Add MobileBridge Capture Feature");
            AssetDatabase.AddObjectToAsset(feature, _rendererData);
            _rendererData.rendererFeatures.Add(feature);
            EditorUtility.SetDirty(_rendererData);
            AssetDatabase.SaveAssets();

            _log = "URPCaptureFeature added successfully.";
            _step = Step.Done;
        }
    }
}
