using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MobileBridge
{
    /// <summary>
    /// URP ScriptableRendererFeature — must remain in the Renderer Asset to avoid
    /// missing-reference errors in existing projects.
    ///
    /// Frame capture is now performed by MobileBridge.CaptureLoop() via
    /// WaitForEndOfFrame + ReadPixels, which captures the complete screen including
    /// Screen Space Overlay canvases. This feature no longer enqueues any render
    /// passes and can safely remain on the Renderer Asset without side-effects.
    ///
    /// Background: AsyncGPUReadback inside a ScriptableRenderPass fires before Unity
    /// composites Canvas Overlay UI onto the backbuffer, so it cannot capture 2D UI
    /// in Overlay mode. WaitForEndOfFrame is the only point after full compositing.
    /// </summary>
    public sealed class URPCaptureFeature : ScriptableRendererFeature
    {
        public override void Create() { }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) { }

        protected override void Dispose(bool disposing) { }
    }
}
