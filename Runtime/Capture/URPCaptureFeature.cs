using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MobileBridge
{
    /// <summary>
    /// Add this to a URP Renderer Asset's Renderer Features list.
    /// Injects a capture pass only when:
    ///   - camera is Base (not Overlay)
    ///   - camera is Game view (not Scene/Preview)
    ///   - MobileBridge.IsActive == true
    /// </summary>
    public sealed class URPCaptureFeature : ScriptableRendererFeature
    {
        private CapturePass _pass;

        public override void Create()
        {
            _pass = new CapturePass { renderPassEvent = RenderPassEvent.AfterRendering };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            ref var camData = ref renderingData.cameraData;
            bool isBase     = camData.renderType == CameraRenderType.Base;
            bool isGameView = camData.cameraType == CameraType.Game;

            if (isBase && isGameView && MobileBridge.IsActive)
            {
                _pass.Setup(renderer.cameraColorTargetHandle);
                renderer.EnqueuePass(_pass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            // no unmanaged resources
        }

        // ── Render pass ────────────────────────────────────────────────────────

        private sealed class CapturePass : ScriptableRenderPass
        {
            private RTHandle _src;
            private float    _lastCaptureTime;

            public void Setup(RTHandle src) { _src = src; }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var bridge = MobileBridge.Instance;
                if (bridge == null || !MobileBridge.IsActive) return;

                float interval = 1f / Mathf.Max(bridge.targetFps, 1);
                if (Time.realtimeSinceStartup - _lastCaptureTime < interval) return;
                _lastCaptureTime = Time.realtimeSinceStartup;

                int dstW = bridge.streamWidth;
                int dstH = bridge.streamHeight;

                // Blit camera color → temp RT at stream resolution
                var captureRt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
                var cmd = CommandBufferPool.Get("MobileBridge.Capture");
                cmd.Blit(_src, captureRt);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                // Async readback from temp RT; release RT in the callback
                AsyncGPUReadback.Request(captureRt, 0, TextureFormat.RGBA32, (req) =>
                {
                    RenderTexture.ReleaseTemporary(captureRt);
                    if (req.hasError || !MobileBridge.IsActive) return;

                    // Copy NativeArray while it is valid (callback scope)
                    var data   = req.GetData<byte>();
                    var pixels = new byte[data.Length];
                    data.CopyTo(pixels);

                    bridge.OnFrameReady(pixels, req.width, req.height);
                });
            }
        }
    }
}
