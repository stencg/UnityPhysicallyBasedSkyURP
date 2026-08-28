using UnityEngine.Rendering;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;

/// <summary>
/// PBSky textures available to later render passes in the current camera frame.
/// </summary>
public sealed class PBSkyFrameResources : ContextItem
{
    public TextureHandle skyViewLut = TextureHandle.nullHandle;
    public TextureHandle multiScatteringLut = TextureHandle.nullHandle;
    public TextureHandle airSingleScattering = TextureHandle.nullHandle;
    public TextureHandle aerosolSingleScattering = TextureHandle.nullHandle;
    public TextureHandle multipleScattering = TextureHandle.nullHandle;
    public TextureHandle groundIrradiance = TextureHandle.nullHandle;
    public TextureHandle fogSky = TextureHandle.nullHandle;

    public override void Reset()
    {
        skyViewLut = TextureHandle.nullHandle;
        multiScatteringLut = TextureHandle.nullHandle;
        airSingleScattering = TextureHandle.nullHandle;
        aerosolSingleScattering = TextureHandle.nullHandle;
        multipleScattering = TextureHandle.nullHandle;
        groundIrradiance = TextureHandle.nullHandle;
        fogSky = TextureHandle.nullHandle;
    }
}
#endif
