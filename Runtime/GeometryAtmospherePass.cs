using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

// Camera-dependent aerial perspective. The sky's planetary LUTs remain independent.
internal sealed class GeometryAtmospherePass : ScriptableRenderPass, IDisposable
{
    const int Width = 32, Depth = 64;
    internal static readonly int RadianceId = Shader.PropertyToID("_PBSkyGeometryRadiance");
    internal static readonly int TransmissionId = Shader.PropertyToID("_PBSkyGeometryTransmission");
    internal static readonly int AvailableId = Shader.PropertyToID("_PBSkyGeometryAvailable");
    static readonly int MultiScatteringId = Shader.PropertyToID("_MultiScatteringLUT");
    static readonly int SourceId = Shader.PropertyToID("_PBSkyGeometryRadianceSource");
    static readonly int SliceId = Shader.PropertyToID("_PBSkyGeometrySlice");
    static readonly int EyeCountId = Shader.PropertyToID("_PBSkyGeometryEyeCount");
    static readonly int CameraSpaceId = Shader.PropertyToID("_PBSkyGeometryCameraSpace");
    static readonly int InvVPId = Shader.PropertyToID("_PBSkyGeometryInvVP");
    static readonly int CameraWSId = Shader.PropertyToID("_PBSkyGeometryCameraWS");
    static readonly int OriginPSId = Shader.PropertyToID("_PBSkyGeometryOriginPS");
    static readonly int UpRadiusId = Shader.PropertyToID("_PBSkyGeometryUpRadius");
    static readonly Matrix4x4[] Matrices = new Matrix4x4[2];
    static readonly Vector4[] Vectors = new Vector4[2];
    readonly ProfilingSampler sampler = new ProfilingSampler("Geometry Atmospheric Scattering LUT");
    readonly ProfilingSampler filterSampler = new ProfilingSampler("Geometry Atmospheric Scattering Filter");
    readonly RenderTargetIdentifier[] attachments = new RenderTargetIdentifier[2];
    internal VisualEnvironment environment;
    internal PhysicallyBasedSky sky;
    // Also allows the validation harness to exercise the fallback on compute-capable hardware.
    internal bool forceRaster;
    ComputeShader compute;
    Material raster;
    RTHandle radiance, transmission, filtered;
    readonly RTHandle[] previousRadiance = new RTHandle[2];
    readonly RTHandle[] previousTransmission = new RTHandle[2];
    bool useCompute;

    struct Settings
    {
        internal int eyes;
        internal bool cameraSpace;
        internal Vector4 airScattering;
        internal Matrix4x4 invVP0, invVP1;
        internal Vector4 camera0, camera1, origin0, origin1, upRadius0, upRadius1;
    }

    internal GeometryAtmospherePass()
    {
        // Resources keeps both generators in player builds without adding scene asset wiring.
        compute = Resources.Load<ComputeShader>("PBSkyGeometryCompute");
        var shader = Resources.Load<Shader>("PBSkyGeometryRaster");
        if (shader != null) raster = CoreUtils.CreateEngineMaterial(shader);
    }

    Settings GetSettings(Camera camera, Matrix4x4 view0, Matrix4x4 projection0,
        Matrix4x4 view1, Matrix4x4 projection1, int eyes)
    {
        var settings = new Settings
        {
            eyes = eyes,
            cameraSpace = environment.renderingSpace.value == VisualEnvironment.RenderingSpace.Camera,
            airScattering = sky.GetAirScatteringCoefficient()
        };
        settings.invVP0 = (GL.GetGPUProjectionMatrix(projection0, true) * view0).inverse;
        settings.invVP1 = (GL.GetGPUProjectionMatrix(projection1, true) * view1).inverse;
        GetOrigin(view0.inverse.GetColumn(3), out settings.camera0, out settings.origin0, out settings.upRadius0);
        GetOrigin(view1.inverse.GetColumn(3), out settings.camera1, out settings.origin1, out settings.upRadius1);
        return settings;
    }

    void GetOrigin(Vector3 cameraPosition, out Vector4 cameraWS, out Vector4 originPS, out Vector4 upRadius)
    {
        var planet = environment.GetPlanetCenterRadius(cameraPosition);
        Vector3 offset = cameraPosition - new Vector3(planet.x, planet.y, planet.z);
        double length = Math.Sqrt((double)offset.x * offset.x + (double)offset.y * offset.y + (double)offset.z * offset.z);
        Vector3 up = length > 0 ? offset / (float)length : Vector3.up;
        float r = (float)Math.Max(length, planet.w + 1.0);
        cameraWS = new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, 1);
        Vector3 lifted = up * r;
        originPS = new Vector4(lifted.x, lifted.y, lifted.z, 0);
        upRadius = new Vector4(up.x, up.y, up.z, r);
    }

    void Allocate(int eyes)
    {
        // Raster accumulation stores every prefix. Prefer float storage so rounding does not
        // systematically erase small extinction steps at short distances.
        var format = SupportsFloatLookup() ? GraphicsFormat.R32G32B32A32_SFloat : GraphicsFormat.R16G16B16A16_SFloat;
        useCompute = !forceRaster && compute != null && SystemInfo.supportsComputeShaders &&
            SystemInfo.IsFormatSupported(format,
#if UNITY_6000_0_OR_NEWER
                GraphicsFormatUsage.LoadStore);
#else
                FormatUsage.LoadStore);
#endif
        var desc = new RenderTextureDescriptor(Width * eyes, Width, format, 0)
        {
            dimension = TextureDimension.Tex3D, volumeDepth = Depth, msaaSamples = 1,
            enableRandomWrite = useCompute, useMipMap = false, autoGenerateMips = false
        };
        Reallocate(ref radiance, desc, FilterMode.Trilinear, "PBSky Geometry Radiance");
        Reallocate(ref transmission, desc, FilterMode.Trilinear, "PBSky Geometry Transmission");
        Reallocate(ref filtered, desc, FilterMode.Trilinear, "PBSky Geometry Filtered Radiance");
        if (!useCompute)
        {
            desc.dimension = TextureDimension.Tex2D;
            desc.volumeDepth = 1;
            for (int i = 0; i < 2; i++)
            {
                Reallocate(ref previousRadiance[i], desc, FilterMode.Point, "PBSky Geometry Radiance Slice " + i);
                Reallocate(ref previousTransmission[i], desc, FilterMode.Point, "PBSky Geometry Transmission Slice " + i);
            }
        }
#if UNITY_EDITOR
        if (!useCompute && raster != null)
            for (int pass = 0; pass < 2; pass++)
                if (!UnityEditor.ShaderUtil.IsPassCompiled(raster, pass))
                    UnityEditor.ShaderUtil.CompilePass(raster, pass, forceSync: true);
#endif
    }

    static bool SupportsFloatLookup()
    {
        var format = GraphicsFormat.R32G32B32A32_SFloat;
#if UNITY_6000_0_OR_NEWER
        return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render | GraphicsFormatUsage.Sample | GraphicsFormatUsage.Linear) &&
            (!SystemInfo.supportsComputeShaders || SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore));
#else
        return SystemInfo.IsFormatSupported(format, FormatUsage.Render) &&
            SystemInfo.IsFormatSupported(format, FormatUsage.Sample) && SystemInfo.IsFormatSupported(format, FormatUsage.Linear) &&
            (!SystemInfo.supportsComputeShaders || SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore));
#endif
    }

    static void Reallocate(ref RTHandle handle, RenderTextureDescriptor desc, FilterMode filter, string name)
    {
#if UNITY_6000_0_OR_NEWER
        RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, filter, TextureWrapMode.Clamp, name: name);
#else
        RenderingUtils.ReAllocateIfNeeded(ref handle, desc, filter, TextureWrapMode.Clamp, name: name);
#endif
    }

    static void SetSettings(CommandBuffer cmd, Settings s)
    {
        // This coefficient was previously material-local on the sky LUT generator. The
        // geometry generators also need it: otherwise they attenuate blue light without
        // adding the corresponding Rayleigh in-scattering.
        cmd.SetGlobalVector("_AirSeaLevelScattering", s.airScattering);
        cmd.SetGlobalInt(EyeCountId, s.eyes);
        cmd.SetGlobalInt(CameraSpaceId, s.cameraSpace ? 1 : 0);
        Matrices[0] = s.invVP0; Matrices[1] = s.invVP1;
        cmd.SetGlobalMatrixArray(InvVPId, Matrices);
        Vectors[0] = s.camera0; Vectors[1] = s.camera1; cmd.SetGlobalVectorArray(CameraWSId, Vectors);
        Vectors[0] = s.origin0; Vectors[1] = s.origin1; cmd.SetGlobalVectorArray(OriginPSId, Vectors);
        Vectors[0] = s.upRadius0; Vectors[1] = s.upRadius1; cmd.SetGlobalVectorArray(UpRadiusId, Vectors);
    }

    void Generate(CommandBuffer cmd, Settings settings, RenderTargetIdentifier multiScattering)
    {
        SetSettings(cmd, settings);
        if (useCompute)
        {
            int kernel = compute.FindKernel("Generate");
            cmd.SetComputeTextureParam(compute, kernel, MultiScatteringId, multiScattering);
            cmd.SetComputeTextureParam(compute, kernel, "_PBSkyGeometryRadianceRW", radiance);
            cmd.SetComputeTextureParam(compute, kernel, "_PBSkyGeometryTransmissionRW", transmission);
            cmd.DispatchCompute(compute, kernel, 4 * settings.eyes, 4, 1);
        }
        else
        {
            cmd.SetGlobalTexture(MultiScatteringId, multiScattering);
            for (int slice = 0; slice < Depth; slice++)
            {
                int write = slice & 1, read = 1 - write;
                cmd.SetGlobalInt(SliceId, slice);
                cmd.SetGlobalTexture("_PBSkyGeometryPreviousRadiance", previousRadiance[read]);
                cmd.SetGlobalTexture("_PBSkyGeometryPreviousTransmission", previousTransmission[read]);
                attachments[0] = previousRadiance[write]; attachments[1] = previousTransmission[write];
                cmd.SetRenderTarget(attachments, BuiltinRenderTextureType.None);
                cmd.SetViewport(new Rect(0, 0, Width * settings.eyes, Width));
                CoreUtils.DrawFullScreen(cmd, raster, shaderPassId: 0);
                cmd.CopyTexture(previousRadiance[write], 0, 0, radiance, slice, 0);
                cmd.CopyTexture(previousTransmission[write], 0, 0, transmission, slice, 0);
            }
        }
    }

    void Filter(CommandBuffer cmd, Settings settings)
    {
        SetSettings(cmd, settings);
        if (useCompute)
        {
            int kernel = compute.FindKernel("Filter");
            cmd.SetComputeTextureParam(compute, kernel, SourceId, radiance);
            cmd.SetComputeTextureParam(compute, kernel, "_PBSkyGeometryFilteredRW", filtered);
            cmd.DispatchCompute(compute, kernel, 4 * settings.eyes, 4, Depth);
        }
        else
        {
            cmd.SetGlobalTexture(SourceId, radiance);
            for (int slice = 0; slice < Depth; slice++)
            {
                cmd.SetGlobalInt(SliceId, slice);
                cmd.SetRenderTarget(filtered, 0, CubemapFace.Unknown, slice);
                cmd.SetViewport(new Rect(0, 0, Width * settings.eyes, Width));
                CoreUtils.DrawFullScreen(cmd, raster, shaderPassId: 1);
            }
        }
        cmd.SetGlobalInt(AvailableId, 1);
    }

#if !UNITY_6000_4_OR_NEWER
#if UNITY_6000_0_OR_NEWER
    [Obsolete]
#endif
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        var data = renderingData.cameraData;
        int eyes = data.xr.enabled ? data.xr.viewCount : 1;
        Allocate(eyes);
        var settings = GetSettings(data.camera, data.GetViewMatrix(0), data.GetProjectionMatrix(0),
            data.GetViewMatrix(eyes - 1), data.GetProjectionMatrix(eyes - 1), eyes);
        var cmd = CommandBufferPool.Get();
        using (new ProfilingScope(cmd, sampler))
        {
            Generate(cmd, settings, new RenderTargetIdentifier(Shader.GetGlobalTexture(MultiScatteringId)));
            Filter(cmd, settings);
            cmd.SetGlobalTexture(RadianceId, filtered);
            cmd.SetGlobalTexture(TransmissionId, transmission);
            // Raster generation changed the target/viewport; restore it before URP continues.
            cmd.SetRenderTarget(data.renderer.cameraColorTargetHandle, data.renderer.cameraDepthTargetHandle);
            cmd.SetViewport(data.camera.pixelRect);
        }
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
#endif

#if UNITY_6000_0_OR_NEWER
    sealed class PassData
    {
        internal GeometryAtmospherePass pass;
        internal Settings settings;
        internal TextureHandle multiScattering;
    }

    public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
    {
        var camera = frameData.Get<UniversalCameraData>();
        int eyes = camera.xr.enabled ? camera.xr.viewCount : 1;
        Allocate(eyes);
        var settings = GetSettings(camera.camera, camera.GetViewMatrix(0), camera.GetProjectionMatrix(0),
            camera.GetViewMatrix(eyes - 1), camera.GetProjectionMatrix(eyes - 1), eyes);
        var resources = frameData.GetOrCreate<PBSkyFrameResources>();
        var color = graph.ImportTexture(radiance);
        var tr = graph.ImportTexture(transmission);
        var output = graph.ImportTexture(filtered);
        resources.geometryRadiance = output;
        resources.geometryTransmission = tr;
        using (var builder = graph.AddUnsafePass<PassData>("Geometry Atmosphere (Integrate)", out var data, sampler))
        {
            data.pass = this; data.settings = settings; data.multiScattering = resources.multiScatteringLut;
            builder.UseTexture(data.multiScattering, AccessFlags.Read);
            builder.UseTexture(color, AccessFlags.Write);
            builder.UseTexture(tr, AccessFlags.Write);
            if (!useCompute)
                for (int i = 0; i < 2; i++)
                {
                    builder.UseTexture(graph.ImportTexture(previousRadiance[i]), AccessFlags.ReadWrite);
                    builder.UseTexture(graph.ImportTexture(previousTransmission[i]), AccessFlags.ReadWrite);
                }
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) =>
                d.pass.Generate(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd), d.settings, d.multiScattering));
        }
        using (var builder = graph.AddUnsafePass<PassData>("Geometry Atmosphere (Filter)", out var data, filterSampler))
        {
            data.pass = this; data.settings = settings; data.multiScattering = TextureHandle.nullHandle;
            builder.UseTexture(color, AccessFlags.Read);
            builder.UseTexture(output, AccessFlags.Write);
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) =>
                d.pass.Filter(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd), d.settings));
        }
        // Explicit read transition before opaque and transparent consumers use globals.
        using (var builder = graph.AddUnsafePass<PassData>("Geometry Atmosphere (Publish)", out var data))
        {
            data.pass = null; data.settings = default; data.multiScattering = TextureHandle.nullHandle;
            builder.UseTexture(output, AccessFlags.Read);
            builder.UseTexture(tr, AccessFlags.Read);
            builder.SetGlobalTextureAfterPass(output, RadianceId);
            builder.SetGlobalTextureAfterPass(tr, TransmissionId);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) => { });
        }
    }
#endif

    public void Dispose()
    {
        Release(ref radiance); Release(ref transmission); Release(ref filtered);
        for (int i = 0; i < 2; i++) { Release(ref previousRadiance[i]); Release(ref previousTransmission[i]); }
        CoreUtils.Destroy(raster); raster = null;
    }
    static void Release(ref RTHandle handle) { handle?.Release(); handle = null; }
}
