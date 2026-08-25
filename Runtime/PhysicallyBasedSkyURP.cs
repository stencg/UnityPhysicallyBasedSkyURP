using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using Unity.Mathematics;
using static Unity.Mathematics.math;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

/// <summary>
/// A renderer feature that adds physically based sky and precomputed atmospheric scattering support to the URP volume.
/// </summary>
[DisallowMultipleRendererFeature("Physically Based Sky URP")]
[Tooltip("Add this Renderer Feature to support visual environment override in URP Volume.")]
[HelpURL("https://github.com/stencg/UnityPhysicallyBasedSkyURP/tree/main")]
public class PhysicallyBasedSkyURP : ScriptableRendererFeature
{
    public enum PrecomputationQualityMode
    {
        [InspectorName("High")]
        [Tooltip("Generates full resolution look-up tables.")]
        High = 0,

        [InspectorName("Low")]
        [Tooltip("Generates half resolution look-up tables.")]
        Low = 1,
    }

    private Material m_PbrSkyMaterial;
    private Material m_PbrSkyLUTMaterial;

    [Header("Setup")]
    [Tooltip("The shader of physically based sky.")]
    [SerializeField] private Shader m_Shader;
    [Tooltip("The precomputation shader of physically based sky.")]
    [SerializeField] private Shader m_LutShader;

    [Header("Performance")]
    [Tooltip("The precomputation quality of physically based sky.")]
    [SerializeField] private PrecomputationQualityMode m_Precomputation = PrecomputationQualityMode.High;
    [Tooltip("Smooths fog only where opaque geometry meets the sky. Reduces aliased fog lines at distant geometry silhouettes. Active Fog requires a camera depth texture.")]
    [SerializeField] private bool m_FogDepthEdgeAntialiasing = false;

    private bool isShaderMismatchLogPrinted;
    private int lastSkyType = int.MinValue;
    private VisualEnvironment.SkyAmbientMode lastSkyAmbientMode;
    
    private CelestialBodyData m_CelestialBodyData = new CelestialBodyData();

    private PBSkyPrePass m_PBSkyPrePass;
    private SkyViewLUTPass m_SkyViewLUTPass;
    private AtmosphericScatteringPass m_AtmosphericScatteringPass;
    private AmbientProbePass m_AmbientProbePass;
    private PBSkyPostPass m_PBSkyPostPass;
    private StaticFogSkyCache m_StaticFogSkyCache;

    [Header("Sky")]
    [Tooltip("The fallback sky material when physically based sky is disabled.")]
    [SerializeField] private Material m_FallbackSkyMaterial;

    [Header("Volumetric Clouds")]
    [Tooltip("[Optional] The material of volumetric clouds used when updating sky reflection.")]
    [SerializeField] private Material m_VolumetricCloudsMaterial;

    private const string k_PbrSkyShaderName = "Hidden/Skybox/PhysicallyBasedSky";
    private const string k_PbrSkyLutShaderName = "Hidden/Sky/PhysicallyBasedSkyPrecomputation";

    private const string k_CloudsShaderName = "Hidden/Sky/VolumetricClouds";
    private const string k_PbrSkyMaterialName = "Physically Based Sky";
    private const string k_DynamicAmbientProbeKeywordName = "VISUAL_ENVIRONMENT_DYNAMIC_SKY";
    private const string k_AtmosphericScatteringLowResolutionKeywordName = "ATMOSPHERIC_SCATTERING_LOW_RES";
    private const string k_FogDepthEdgeAntialiasingKeywordName = "_FOG_DEPTH_EDGE_ANTIALIASING";

    /// <summary>
    /// Get the skybox material of physically based sky.
    /// </summary>
    /// <value>
    /// The material of physically based sky.
    /// </value>
    public Material PBRSkyMaterial
    {
        get { return m_PbrSkyMaterial; }
    }

    /// <summary>
    /// Get or set the fallback sky material when physically based sky is disabled.
    /// </summary>
    /// <value>
    /// The material of fallback sky shader.
    /// </value>
    public Material FallbackSkyMaterial
    {
        get { return m_FallbackSkyMaterial; }
        set { m_FallbackSkyMaterial = value; }
    }

    /// <summary>
    /// Get or set the material of volumetric clouds shader.
    /// </summary>
    /// <value>
    /// [Optional] The material of "Hidden/Sky/VolumetricClouds" shader used when updating sky reflection.
    /// </value>
    public Material CloudsMaterial
    {
        get { return m_VolumetricCloudsMaterial; }
        set { m_VolumetricCloudsMaterial = value; ValidateCloudsMaterial(); }
    }

    /// <summary>
    /// Get or set the shader of physically based sky.
    /// </summary>
    /// <value>
    /// The shader of physically based sky.
    /// </value>
    public Shader PBSkyShader
    {
        get { return m_Shader; }
        set { m_Shader = value; }
    }

    /// <summary>
    /// Get or set the precomputation shader of physically based sky.
    /// </summary>
    /// <value>
    /// The precomputation shader of physically based sky.
    /// </value>
    public Shader PBSkyLutShader
    {
        get { return m_LutShader; }
        set { m_LutShader = value; }
    }

    /// <summary>
    /// Get or set the precomputation quality of physically based sky.
    /// </summary>
    /// <value>
    /// The precomputation quality of physically based sky.
    /// </value>
    public PrecomputationQualityMode PrecomputationQuality
    {
        get { return m_Precomputation; }
        set { m_Precomputation = value; }
    }

    /// <summary>
    /// Gets or sets a value indicating whether fog is anti-aliased at opaque/sky depth edges.
    /// </summary>
    public bool FogDepthEdgeAntialiasing
    {
        get { return m_FogDepthEdgeAntialiasing; }
        set { m_FogDepthEdgeAntialiasing = value; }
    }

    /// <summary>
    /// Evaluates the physically based sky ambient probe for the current sun and camera position.
    /// </summary>
    public static SphericalHarmonicsL2 EvaluateAmbientProbe(PhysicallyBasedSky pbrSky, VisualEnvironment visualEnvironment, Light mainLight, Vector3 cameraPosition)
    {
        if (pbrSky == null || visualEnvironment == null || mainLight == null)
            return new SphericalHarmonicsL2();

        float3 positionPS = float3(cameraPosition) - visualEnvironment.GetPlanetCenterRadius(cameraPosition).xyz;
        float3 sunAttenuation = PBSkyPrePass.EvaluateSunColorAttenuation(pbrSky, visualEnvironment, positionPS, -mainLight.transform.forward);

        Color color = mainLight.color.linear * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white);
        float3 mainLightColor = float3(color.r, color.g, color.b) * mainLight.intensity * sunAttenuation;

    #if URP_PHYSICAL_LIGHT
        bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;
        mainLightColor = isPhysicalLight ? mainLightColor * rcp(PI) : mainLightColor;
    #endif

        return PBSkyPrePass.EvaluateAmbientProbe(new SphericalHarmonicsL2(), pbrSky, mainLight.transform.forward, mainLightColor);
    }

    public struct CelestialBodyData
    {
        public Vector3 color;
        public float radius;

        public Vector3 forward;
        public float distanceFromCamera;
        public Vector3 right;
        public float angularRadius;       // Units: radians
        public Vector3 up;
        public int type;                  // 0: star, 1: moon

        public Vector3 surfaceColor;
        public float earthshine;

        public Vector4 surfaceTextureScaleOffset; // -1 if unused (TODO: 16 bit)

        public Vector3 sunDirection;
        public float flareCosInner;

        //public Vector2 phaseAngleSinCos;
        public float flareCosOuter;
        public float flareSize;           // Units: radians

        public Vector3 flareColor;
        public float flareFalloff;

        //public Vector3 padding;
        //public int shadowIndex;
    };

    public override void Create()
    {
        var stack = VolumeManager.instance.stack;
        PhysicallyBasedSky pbrSkyVolume = stack.GetComponent<PhysicallyBasedSky>();
        VisualEnvironment visualEnvVolume = stack.GetComponent<VisualEnvironment>();

        // Validate sky shaders
        bool shadersValid = true;
        if (m_Shader != Shader.Find(k_PbrSkyShaderName))
        {
    #if UNITY_EDITOR || DEBUG
            if (!isShaderMismatchLogPrinted)
            {
                Debug.LogErrorFormat("Physically Based Sky URP: Skybox shader is not {0}.", k_PbrSkyShaderName);
                isShaderMismatchLogPrinted = true;
            }
    #endif
            shadersValid = false;
        }

        if (m_LutShader != Shader.Find(k_PbrSkyLutShaderName))
        {
    #if UNITY_EDITOR || DEBUG
            if (!isShaderMismatchLogPrinted)
            {
                Debug.LogErrorFormat("Physically Based Sky URP: LUT shader is not {0}.", k_PbrSkyLutShaderName);
                isShaderMismatchLogPrinted = true;
            }
    #endif
            shadersValid = false;
        }

        if (!shadersValid) return;
        isShaderMismatchLogPrinted = false;

        // Cleanup settings when disabled
        if (!isActive)
        {
            bool isCustomSkyType = visualEnvVolume != null && visualEnvVolume.IsActive() && visualEnvVolume.skyType.value == (int)VisualEnvironment.SkyType.Custom && visualEnvVolume.customSkyMaterial.value != null;

            SetSkybox(isCustomSkyType ? visualEnvVolume.customSkyMaterial.value : m_FallbackSkyMaterial, false);
            RenderSettings.customReflectionTexture = null;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;

            Shader.DisableKeyword(k_DynamicAmbientProbeKeywordName);

        #if UNITY_EDITOR
            // Update ambient probe
            if (RenderSettings.skybox != null)
            {
                DynamicGI.UpdateEnvironment();
            }
        #endif
            return;
        }

        // Initialize sky materials
        m_PbrSkyMaterial = CoreUtils.CreateEngineMaterial(m_Shader);
        m_PbrSkyLUTMaterial = CoreUtils.CreateEngineMaterial(m_LutShader);
        m_PbrSkyMaterial.name = k_PbrSkyMaterialName;

        // Initialize render passes
        m_StaticFogSkyCache ??= new StaticFogSkyCache();
        m_StaticFogSkyCache.copyMaterial = m_PbrSkyLUTMaterial;

        m_PBSkyPrePass ??= new PBSkyPrePass(m_PbrSkyMaterial, m_CelestialBodyData)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses
        };

        m_PBSkyPrePass.material = m_PbrSkyMaterial;
        m_PBSkyPrePass.lutMaterial = m_PbrSkyLUTMaterial;

        m_SkyViewLUTPass ??= new SkyViewLUTPass(m_PbrSkyLUTMaterial, ref m_CelestialBodyData)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses
        };

        m_SkyViewLUTPass.lutMaterial = m_PbrSkyLUTMaterial;

        m_AtmosphericScatteringPass ??= new AtmosphericScatteringPass(m_PbrSkyLUTMaterial, m_StaticFogSkyCache)
        {
            // Scatter opaque geometry before clouds are composited. Volumetric clouds apply
            // atmospheric scattering separately in their combine pass using cloud depth.
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents - 1
        };

        m_AtmosphericScatteringPass.lutMaterial = m_PbrSkyLUTMaterial;
        m_AtmosphericScatteringPass.staticFogSkyCache = m_StaticFogSkyCache;

        m_AmbientProbePass ??= new AmbientProbePass(m_VolumetricCloudsMaterial)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses
        };

        m_PBSkyPostPass ??= new PBSkyPostPass()
        {
            renderPassEvent = RenderPassEvent.AfterRendering
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Do not add render passes if any error occurs.
        bool shouldDisable = isShaderMismatchLogPrinted || m_PbrSkyMaterial == null || m_PbrSkyLUTMaterial == null;

        shouldDisable |= renderingData.cameraData.camera == null;

        shouldDisable |= renderingData.cameraData.camera.cameraType == CameraType.Preview;

        if (shouldDisable)
            return;

        var stack = VolumeManager.instance.stack;
        PhysicallyBasedSky pbrSkyVolume = stack.GetComponent<PhysicallyBasedSky>();
        VisualEnvironment visualEnvVolume = stack.GetComponent<VisualEnvironment>();
        Fog fogVolume = stack.GetComponent<Fog>();

        m_StaticFogSkyCache.dynamicEnvironmentTexture = m_AmbientProbePass.environmentTexture;

        const int physicallyBased = (int)VisualEnvironment.SkyType.PhysicallyBased;
        bool isPbrSky = pbrSkyVolume != null && visualEnvVolume != null && visualEnvVolume.IsActive() && visualEnvVolume.skyType.value == physicallyBased;

        {
            bool halfResolutionLuts = m_Precomputation == PrecomputationQualityMode.Low;

            m_PBSkyPrePass.pbrSky = pbrSkyVolume;
            m_SkyViewLUTPass.pbrSky = pbrSkyVolume;
            m_AtmosphericScatteringPass.pbrSky = pbrSkyVolume;
            m_AtmosphericScatteringPass.fogDepthEdgeAntialiasing = m_FogDepthEdgeAntialiasing;

            m_PBSkyPrePass.visualEnvironment = visualEnvVolume;
            m_SkyViewLUTPass.visualEnvironment = visualEnvVolume;
            m_AtmosphericScatteringPass.visualEnvironment = visualEnvVolume;

            m_PBSkyPrePass.fog = fogVolume;
            m_AtmosphericScatteringPass.fog = fogVolume;

            m_SkyViewLUTPass.halfResolutionLuts = halfResolutionLuts;

            if (isPbrSky)
                CoreUtils.SetKeyword(m_PbrSkyMaterial, k_AtmosphericScatteringLowResolutionKeywordName, halfResolutionLuts);
            CoreUtils.SetKeyword(m_PbrSkyLUTMaterial, k_AtmosphericScatteringLowResolutionKeywordName, halfResolutionLuts);

            bool hasFog = isPbrSky && pbrSkyVolume.atmosphericScattering.value || (fogVolume != null && fogVolume.IsActive());

        #if UNITY_EDITOR
            bool isEditingPrefab = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;
            bool isSceneViewFocused = UnityEditor.SceneView.lastActiveSceneView != null && UnityEditor.SceneView.lastActiveSceneView.hasFocus;
            // Disable atmospheric scattering and fog when entering prefab mode.
            hasFog &= !(isEditingPrefab && isSceneViewFocused);
        #endif

            if (isPbrSky)
            {
                renderer.EnqueuePass(m_PBSkyPrePass);

                m_SkyViewLUTPass.celestialBodyData = m_PBSkyPrePass.celestialBodyData;

                renderer.EnqueuePass(m_SkyViewLUTPass);
            }

            if (hasFog && renderingData.cameraData.camera.cameraType != CameraType.Reflection)
            {
                m_AtmosphericScatteringPass.ConfigureInput(ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(m_AtmosphericScatteringPass);
            }
            
            renderer.EnqueuePass(m_PBSkyPostPass);
        }

        if (visualEnvVolume.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic && renderingData.cameraData.camera.cameraType != CameraType.Reflection && RenderSettings.skybox != null)
        {
            m_AmbientProbePass.visualEnvironment = visualEnvVolume;
            m_AmbientProbePass.cloudsMaterial = ValidateCloudsMaterial();
            m_AmbientProbePass.isPbrSky = isPbrSky;
            Shader.EnableKeyword(k_DynamicAmbientProbeKeywordName);
            renderer.EnqueuePass(m_AmbientProbePass);
        }
        else
        {
            Shader.DisableKeyword(k_DynamicAmbientProbeKeywordName);
        }

        UpdateSkySettings(isPbrSky, visualEnvVolume);
    }

    protected override void Dispose(bool disposing)
    {
        if (m_PBSkyPrePass != null)
            m_PBSkyPrePass.Dispose();

        if (m_SkyViewLUTPass != null)
            m_SkyViewLUTPass.Dispose();

        if (m_AtmosphericScatteringPass != null)
            m_AtmosphericScatteringPass.Dispose();

        if (m_AmbientProbePass != null)
            m_AmbientProbePass.Dispose();

        if (m_StaticFogSkyCache != null)
        {
            m_StaticFogSkyCache.Dispose();
            m_StaticFogSkyCache = null;
        }

        if (m_PBSkyPostPass != null)
            m_PBSkyPostPass.Dispose();

        if (m_PbrSkyMaterial != null)
            CoreUtils.Destroy(m_PbrSkyMaterial);

        if (m_PbrSkyLUTMaterial != null)
            CoreUtils.Destroy(m_PbrSkyLUTMaterial);
    }

    private Material ValidateCloudsMaterial()
    {
        return m_VolumetricCloudsMaterial != null && m_VolumetricCloudsMaterial.shader == Shader.Find(k_CloudsShaderName)
            ? m_VolumetricCloudsMaterial
            : null;
    }

    private static void SetSkybox(Material material, bool markSceneDirty)
    {
        if (RenderSettings.skybox == material)
            return;

        RenderSettings.skybox = material;

    #if UNITY_EDITOR
        if (markSceneDirty && !Application.isPlaying)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isLoaded)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    #endif
    }

    private void UpdateSkySettings(bool isPbrSky, VisualEnvironment visualEnvVolume)
    {
        bool isCustomSky = visualEnvVolume.skyType.value == (int)VisualEnvironment.SkyType.Custom;
        bool isCustomSkyValid = visualEnvVolume.customSkyMaterial.value != null;

        bool isDynamicSky = visualEnvVolume.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic;

        bool isInitialSkyUpdate = lastSkyType == int.MinValue;
        bool isSkyTypeChanged = lastSkyType != visualEnvVolume.skyType.value;
        bool isAmbientModeChanged = lastSkyAmbientMode != visualEnvVolume.skyAmbientMode.value;
        
        // Reset the sky reflection texture
        if (!isDynamicSky && isAmbientModeChanged)
        {
            RenderSettings.customReflectionTexture = null;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        }

        // Update the sky material
        Material skyMaterial = isPbrSky
            ? m_PbrSkyMaterial
            : isCustomSky && isCustomSkyValid
            ? visualEnvVolume.customSkyMaterial.value
            : isSkyTypeChanged // executes once only
            ? m_FallbackSkyMaterial
            : RenderSettings.skybox;
        SetSkybox(skyMaterial, isSkyTypeChanged && !isInitialSkyUpdate && !isPbrSky);

    #if UNITY_EDITOR
        // Re-bake the sky ambient probe
        // A static PBR sky is assigned at runtime, before it has rendered. Preserve the
        // scene's baked ambient probe instead of replacing it with stale sky material data.
        if (isSkyTypeChanged && !isPbrSky && RenderSettings.skybox != null)
        {
            DynamicGI.UpdateEnvironment();
        }
    #endif  

        lastSkyType = visualEnvVolume.skyType.value;
        lastSkyAmbientMode = visualEnvVolume.skyAmbientMode.value;
    }

    /// <summary>
    /// Keeps an immutable copy of the active scene's baked environment reflection for static mip
    /// fog. Unity may replace or update ReflectionProbe.defaultTexture in place while scenes or
    /// lighting data are loading, so camera passes must not sample it directly.
    /// </summary>
    private sealed class StaticFogSkyCache : IDisposable
    {
        private const string profilerTag = "Update Static Fog Environment";
        private const int k_CopyMaterialPass = 6;
        private const int k_CubemapFaceCount = 6;
        private const int k_MaxSnapshotResolution = 128;
        private static readonly Vector4 k_FullscreenScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        private static readonly int _FogSkyCopySource = Shader.PropertyToID("_FogSkyCopySource");
        private static readonly int _FogSkyCopySourceHDR = Shader.PropertyToID("_FogSkyCopySource_HDR");
        private static readonly int _FogSkyCopyMip = Shader.PropertyToID("_FogSkyCopyMip");
        private static readonly int _FogSkyCopyFace = Shader.PropertyToID("_FogSkyCopyFace");

        internal Material copyMaterial;
        internal Texture dynamicEnvironmentTexture;

        private sealed class SnapshotSlot : IDisposable
        {
            internal RTHandle handle;
            internal int mipCount;
            internal int resolution;
            internal int sourceMipOffset;

            internal bool Allocate(int sourceResolution, int sourceMipCount, string name)
            {
                resolution = sourceResolution;
                sourceMipOffset = 0;
                while (resolution > k_MaxSnapshotResolution)
                {
                    resolution = Mathf.Max(1, resolution >> 1);
                    sourceMipOffset++;
                }

                RenderTextureDescriptor desc = new RenderTextureDescriptor(resolution, resolution)
                {
                    msaaSamples = 1,
                    useMipMap = true,
                    autoGenerateMips = false,
                    dimension = TextureDimension.Cube,
                    graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32,
                    depthStencilFormat = GraphicsFormat.None,
                    depthBufferBits = 0,
                    useDynamicScale = false
                };

                RenderingUtils.ReAllocateHandleIfNeeded(ref handle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: name);
                if (handle == null || handle.rt == null || !handle.rt.IsCreated())
                    return false;

                int availableSourceMipCount = sourceMipCount - sourceMipOffset;
                mipCount = Mathf.Min(availableSourceMipCount, handle.rt.mipmapCount);
                return mipCount > 1;
            }

            public void Dispose()
            {
                handle?.Release();
                handle = null;
                mipCount = 0;
                resolution = 0;
                sourceMipOffset = 0;
            }
        }

        internal readonly struct Snapshot
        {
            internal readonly RTHandle handle;
            internal readonly int mipCount;
            internal readonly int generation;

            internal Snapshot(RTHandle handle, int mipCount, int generation)
            {
                this.handle = handle;
                this.mipCount = mipCount;
                this.generation = generation;
            }

            internal bool IsValid => handle != null && handle.rt != null && handle.rt.IsCreated() && mipCount > 1;
        }

        private readonly struct SourceFingerprint : IEquatable<SourceFingerprint>
        {
            internal readonly int sceneHandle;
            internal readonly int lightingRevision;
            internal readonly int textureInstanceId;
            internal readonly int width;
            internal readonly int height;
            internal readonly int mipCount;
            internal readonly GraphicsFormat graphicsFormat;
            internal readonly uint updateCount;
            internal readonly Vector4 hdrDecodeValues;

            internal SourceFingerprint(int sceneHandle, int lightingRevision, Texture texture, Vector4 hdrDecodeValues)
            {
                this.sceneHandle = sceneHandle;
                this.lightingRevision = lightingRevision;
                textureInstanceId = texture.GetHashCode();
                width = texture.width;
                height = texture.height;
                mipCount = texture.mipmapCount;
                graphicsFormat = texture.graphicsFormat;
                updateCount = texture.updateCount;
                this.hdrDecodeValues = hdrDecodeValues;
            }

            public bool Equals(SourceFingerprint other)
            {
                return sceneHandle == other.sceneHandle
                    && lightingRevision == other.lightingRevision
                    && HasSameTextureContent(other);
            }

            internal bool HasSameTextureContent(SourceFingerprint other)
            {
                return textureInstanceId == other.textureInstanceId
                    && width == other.width
                    && height == other.height
                    && mipCount == other.mipCount
                    && graphicsFormat == other.graphicsFormat
                    && updateCount == other.updateCount
                    && hdrDecodeValues == other.hdrDecodeValues;
            }
        }

        private readonly SnapshotSlot[] m_Slots = { new SnapshotSlot(), new SnapshotSlot() };
        private int m_ActiveSlot = -1;
        private int m_PendingSlot = -1;
        private int m_PendingFrame = -1;
        private int m_Generation;
        private int m_SceneHandle = int.MinValue;
        private int m_LightingRevision;
        private bool m_HasActiveFingerprint;
        private bool m_HasPendingFingerprint;
        private bool m_HasCandidateFingerprint;
        private bool m_HasRejectedFingerprint;
        private bool m_BakeInProgress;
        private bool m_LightingDataCleared;
        private int m_CandidateFirstFrame;
        private SourceFingerprint m_ActiveFingerprint;
        private SourceFingerprint m_PendingFingerprint;
        private SourceFingerprint m_CandidateFingerprint;
        private SourceFingerprint m_RejectedFingerprint;
        private RTHandle m_PendingSourceHandle;

        internal StaticFogSkyCache()
        {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;

        #if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            UnityEditor.Lightmapping.bakeStarted += OnBakeStarted;
            UnityEditor.Lightmapping.bakeCompleted += OnBakeCompleted;
            UnityEditor.Lightmapping.lightingDataUpdated += OnLightingDataUpdated;
            UnityEditor.Lightmapping.lightingDataCleared += OnLightingDataCleared;
        #endif
        }

        internal Snapshot GetSnapshot(CommandBuffer cmd)
        {
            RefreshSceneState();
            CompletePendingUpdate();

            if (TryGetStableSource(out Texture source, out SourceFingerprint fingerprint, out Vector4 hdrDecodeValues))
                ScheduleUpdate(cmd, source, fingerprint, hdrDecodeValues);

            return GetActiveSnapshot();
        }

    #if UNITY_6000_0_OR_NEWER
        private class CopyPassData
        {
            internal Material material;
            internal Texture source;
            internal RTHandle destination;
            internal int mipCount;
            internal int resolution;
            internal int sourceMipOffset;
            internal Vector4 hdrDecodeValues;
        }

        internal Snapshot GetSnapshot(RenderGraph renderGraph)
        {
            RefreshSceneState();
            CompletePendingUpdate();

            if (TryGetStableSource(out Texture source, out SourceFingerprint fingerprint, out Vector4 hdrDecodeValues))
                ScheduleUpdate(renderGraph, source, fingerprint, hdrDecodeValues);

            return GetActiveSnapshot();
        }

        private void ScheduleUpdate(RenderGraph renderGraph, Texture source, SourceFingerprint fingerprint, Vector4 hdrDecodeValues)
        {
            if (!PreparePendingSlot(source, fingerprint, out SnapshotSlot slot))
                return;

            m_PendingSourceHandle = RTHandles.Alloc(source);
            TextureHandle sourceHandle = renderGraph.ImportTexture(m_PendingSourceHandle);
            TextureHandle destinationHandle = renderGraph.ImportTexture(slot.handle);

            using (var builder = renderGraph.AddUnsafePass<CopyPassData>(profilerTag, out var passData))
            {
                passData.material = copyMaterial;
                passData.source = source;
                passData.destination = slot.handle;
                passData.mipCount = slot.mipCount;
                passData.resolution = slot.resolution;
                passData.sourceMipOffset = slot.sourceMipOffset;
                passData.hdrDecodeValues = hdrDecodeValues;

                builder.UseTexture(sourceHandle, AccessFlags.Read);
                builder.UseTexture(destinationHandle, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((CopyPassData data, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    CopyEnvironment(cmd, data.material, data.source, data.destination, data.mipCount, data.resolution, data.sourceMipOffset, data.hdrDecodeValues);
                });
            }

            MarkUpdatePending(fingerprint);
        }
    #endif

        private void ScheduleUpdate(CommandBuffer cmd, Texture source, SourceFingerprint fingerprint, Vector4 hdrDecodeValues)
        {
            if (!PreparePendingSlot(source, fingerprint, out SnapshotSlot slot))
                return;

            CopyEnvironment(cmd, copyMaterial, source, slot.handle, slot.mipCount, slot.resolution, slot.sourceMipOffset, hdrDecodeValues);
            MarkUpdatePending(fingerprint);
        }

        private bool PreparePendingSlot(Texture source, SourceFingerprint fingerprint, out SnapshotSlot slot)
        {
            slot = null;
            if (m_HasPendingFingerprint || copyMaterial == null || copyMaterial.passCount <= k_CopyMaterialPass)
                return false;

            int slotIndex = m_ActiveSlot == 0 ? 1 : 0;
            slot = m_Slots[slotIndex];
            if (!slot.Allocate(source.width, source.mipmapCount, $"Static Fog Environment {slotIndex}"))
                return false;

            m_PendingSlot = slotIndex;
            m_PendingFingerprint = fingerprint;
            return true;
        }

        private void MarkUpdatePending(SourceFingerprint fingerprint)
        {
            m_PendingFingerprint = fingerprint;
            m_HasPendingFingerprint = true;
            m_PendingFrame = Time.renderedFrameCount;
        }

        private static void CopyEnvironment(CommandBuffer cmd, Material material, Texture source, RTHandle destination, int mipCount, int resolution, int sourceMipOffset, Vector4 hdrDecodeValues)
        {
            cmd.SetGlobalTexture(_FogSkyCopySource, source);
            cmd.SetGlobalVector(_FogSkyCopySourceHDR, hdrDecodeValues);

            for (int mip = 0; mip < mipCount; mip++)
            {
                cmd.SetGlobalFloat(_FogSkyCopyMip, sourceMipOffset + mip);
                int mipResolution = Mathf.Max(1, resolution >> mip);

                for (int face = 0; face < k_CubemapFaceCount; face++)
                {
                    cmd.SetGlobalInteger(_FogSkyCopyFace, face);
                    CoreUtils.SetRenderTarget(cmd, destination, ClearFlag.None, mip, (CubemapFace)face);
                    cmd.SetViewport(new Rect(0.0f, 0.0f, mipResolution, mipResolution));
                    Blitter.BlitTexture(cmd, k_FullscreenScaleBias, material, k_CopyMaterialPass);
                }
            }
        }

        private Snapshot GetActiveSnapshot()
        {
            if (m_ActiveSlot < 0)
                return default;

            SnapshotSlot slot = m_Slots[m_ActiveSlot];
            return new Snapshot(slot.handle, slot.mipCount, m_Generation);
        }

        private void RefreshSceneState()
        {
            SetActiveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        }

        private void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene previousScene, UnityEngine.SceneManagement.Scene newScene)
        {
            SetActiveScene(newScene);
        }

        private void SetActiveScene(UnityEngine.SceneManagement.Scene scene)
        {
            // Scene.GetHashCode maps to the loaded scene's unique handle on both the int-handle
            // Unity 6.0 API and the SceneHandle API introduced in later Unity 6 releases.
            int sceneHandle = scene.GetHashCode();
            if (sceneHandle == m_SceneHandle)
                return;

            // An additive scene becoming active changes the global RenderSettings/default
            // reflection. Reject the newest source from the previous active scene so Unity cannot
            // briefly republish it as the new scene's environment while lighting data settles.
            RejectCurrentSource();

            m_SceneHandle = sceneHandle;
            m_LightingRevision++;
            m_LightingDataCleared = false;
            ReleaseSnapshots();
        }

        private void RejectCurrentSource()
        {
            if (m_HasPendingFingerprint)
                m_RejectedFingerprint = m_PendingFingerprint;
            else if (m_HasCandidateFingerprint)
                m_RejectedFingerprint = m_CandidateFingerprint;
            else if (m_HasActiveFingerprint)
                m_RejectedFingerprint = m_ActiveFingerprint;
            else
            {
                m_HasRejectedFingerprint = false;
                return;
            }

            m_HasRejectedFingerprint = true;
        }

        private void CompletePendingUpdate()
        {
            if (!m_HasPendingFingerprint || Time.renderedFrameCount <= m_PendingFrame)
                return;

            bool sourceStillMatches = TryCaptureSource(out _, out SourceFingerprint fingerprint, out _)
                && fingerprint.Equals(m_PendingFingerprint)
                && !IsRejected(fingerprint);

            if (sourceStillMatches)
            {
                m_ActiveSlot = m_PendingSlot;
                m_ActiveFingerprint = m_PendingFingerprint;
                m_HasActiveFingerprint = true;
                m_Generation++;
            }

            m_HasPendingFingerprint = false;
            m_PendingSlot = -1;
            m_PendingFrame = -1;
            m_PendingSourceHandle?.Release();
            m_PendingSourceHandle = null;
        }

        private bool TryGetStableSource(out Texture source, out SourceFingerprint fingerprint, out Vector4 hdrDecodeValues)
        {
            source = null;
            fingerprint = default;
            hdrDecodeValues = default;

            if (IsBakeRunning() || m_LightingDataCleared
                || !TryCaptureSource(out source, out fingerprint, out hdrDecodeValues) || IsRejected(fingerprint))
                return false;

            if (m_HasActiveFingerprint && fingerprint.Equals(m_ActiveFingerprint))
                return false;

            if (m_HasPendingFingerprint && fingerprint.Equals(m_PendingFingerprint))
                return false;

            if (!m_HasCandidateFingerprint || !fingerprint.Equals(m_CandidateFingerprint))
            {
                m_CandidateFingerprint = fingerprint;
                m_HasCandidateFingerprint = true;
                m_CandidateFirstFrame = Time.renderedFrameCount;
                return false;
            }

            return Time.renderedFrameCount > m_CandidateFirstFrame;
        }

        private bool TryCaptureSource(out Texture source, out SourceFingerprint fingerprint, out Vector4 hdrDecodeValues)
        {
            source = ReflectionProbe.defaultTexture;
            hdrDecodeValues = ReflectionProbe.defaultTextureHDRDecodeValues;
            fingerprint = default;

            if (source == null || source == dynamicEnvironmentTexture || source.dimension != TextureDimension.Cube
                || source.width <= 1 || source.height != source.width || source.mipmapCount <= 1
                || source.graphicsFormat == GraphicsFormat.None)
                return false;

            if (source is RenderTexture renderTexture && !renderTexture.IsCreated())
                return false;

            fingerprint = new SourceFingerprint(m_SceneHandle, m_LightingRevision, source, hdrDecodeValues);
            return true;
        }

        private bool IsRejected(SourceFingerprint fingerprint)
        {
            return m_HasRejectedFingerprint && fingerprint.HasSameTextureContent(m_RejectedFingerprint);
        }

        private bool IsBakeRunning()
        {
        #if UNITY_EDITOR
            return m_BakeInProgress || UnityEditor.Lightmapping.isRunning;
        #else
            return false;
        #endif
        }

    #if UNITY_EDITOR
        private void OnBakeStarted()
        {
            m_BakeInProgress = true;
        }

        private void OnBakeCompleted()
        {
            m_BakeInProgress = false;
            m_LightingRevision++;
            m_HasCandidateFingerprint = false;
            m_HasRejectedFingerprint = false;
            m_LightingDataCleared = false;
        }

        private void OnLightingDataUpdated()
        {
            m_LightingRevision++;
            m_HasCandidateFingerprint = false;
            m_LightingDataCleared = false;
        }

        private void OnLightingDataCleared()
        {
            RejectCurrentSource();

            m_LightingRevision++;
            m_LightingDataCleared = true;
            ReleaseSnapshots();
        }
    #endif

        private void ReleaseSnapshots()
        {
            m_Slots[0].Dispose();
            m_Slots[1].Dispose();
            m_PendingSourceHandle?.Release();
            m_PendingSourceHandle = null;
            m_ActiveSlot = -1;
            m_PendingSlot = -1;
            m_PendingFrame = -1;
            m_HasActiveFingerprint = false;
            m_HasPendingFingerprint = false;
            m_HasCandidateFingerprint = false;
        }

        public void Dispose()
        {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;

        #if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            UnityEditor.Lightmapping.bakeStarted -= OnBakeStarted;
            UnityEditor.Lightmapping.bakeCompleted -= OnBakeCompleted;
            UnityEditor.Lightmapping.lightingDataUpdated -= OnLightingDataUpdated;
            UnityEditor.Lightmapping.lightingDataCleared -= OnLightingDataCleared;
        #endif

            ReleaseSnapshots();
        }
    }

    /// <summary>
    /// This pass updates the global shader properties of physically based sky.
    /// </summary>
    private class PBSkyPrePass : ScriptableRenderPass
    {
        private const string profilerTag = "Setup Physically Based Sky";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;
        public VisualEnvironment visualEnvironment;
        public Fog fog;

        public CelestialBodyData celestialBodyData;

        public Material material;
        public Material lutMaterial;

        private static readonly int _AtmosphericRadius = Shader.PropertyToID("_AtmosphericRadius");
        private static readonly int _AerosolAnisotropy = Shader.PropertyToID("_AerosolAnisotropy");
        private static readonly int _AerosolPhasePartConstant = Shader.PropertyToID("_AerosolPhasePartConstant");
        private static readonly int _AerosolSeaLevelExtinction = Shader.PropertyToID("_AerosolSeaLevelExtinction");
        private static readonly int _AirDensityFalloff = Shader.PropertyToID("_AirDensityFalloff");
        private static readonly int _AirScaleHeight = Shader.PropertyToID("_AirScaleHeight");
        private static readonly int _AerosolDensityFalloff = Shader.PropertyToID("_AerosolDensityFalloff");
        private static readonly int _AerosolScaleHeight = Shader.PropertyToID("_AerosolScaleHeight");
        private static readonly int _OzoneScaleOffset = Shader.PropertyToID("_OzoneScaleOffset");
        private static readonly int _OzoneLayerStart = Shader.PropertyToID("_OzoneLayerStart");
        private static readonly int _OzoneLayerEnd = Shader.PropertyToID("_OzoneLayerEnd");
        private static readonly int _AirSeaLevelExtinction = Shader.PropertyToID("_AirSeaLevelExtinction");
        private static readonly int _AirSeaLevelScattering = Shader.PropertyToID("_AirSeaLevelScattering");
        private static readonly int _AerosolSeaLevelScattering = Shader.PropertyToID("_AerosolSeaLevelScattering");
        private static readonly int _OzoneSeaLevelExtinction = Shader.PropertyToID("_OzoneSeaLevelExtinction");
        private static readonly int _GroundAlbedo_PlanetRadius = Shader.PropertyToID("_GroundAlbedo_PlanetRadius");
        private static readonly int _HorizonTint = Shader.PropertyToID("_HorizonTint");
        private static readonly int _ZenithTint = Shader.PropertyToID("_ZenithTint");
        private static readonly int _IntensityMultiplier = Shader.PropertyToID("_IntensityMultiplier");
        private static readonly int _ColorSaturation = Shader.PropertyToID("_ColorSaturation");
        private static readonly int _AlphaSaturation = Shader.PropertyToID("_AlphaSaturation");
        private static readonly int _AlphaMultiplier = Shader.PropertyToID("_AlphaMultiplier");
        private static readonly int _HorizonZenithShiftPower = Shader.PropertyToID("_HorizonZenithShiftPower");
        private static readonly int _HorizonZenithShiftScale = Shader.PropertyToID("_HorizonZenithShiftScale");
        private static readonly int _CelestialLightCount = Shader.PropertyToID("_CelestialLightCount");
        private static readonly int _CelestialBodyCount = Shader.PropertyToID("_CelestialBodyCount");
        private static readonly int _AtmosphericDepth = Shader.PropertyToID("_AtmosphericDepth");
        private static readonly int _RcpAtmosphericDepth = Shader.PropertyToID("_RcpAtmosphericDepth");
        private static readonly int _CelestialLightExposure = Shader.PropertyToID("_CelestialLightExposure");

        private static readonly int _DisableSunDisk = Shader.PropertyToID("_DisableSunDisk");

        private static readonly int _GroundAlbedoTexture = Shader.PropertyToID("_GroundAlbedoTexture");

        private static readonly int _GroundEmissionTexture = Shader.PropertyToID("_GroundEmissionTexture");
        private static readonly int _GroundEmissionMultiplier = Shader.PropertyToID("_GroundEmissionMultiplier");

        private static readonly int _SpaceEmissionTexture = Shader.PropertyToID("_SpaceEmissionTexture");
        private static readonly int _SpaceEmissionMultiplier = Shader.PropertyToID("_SpaceEmissionMultiplier");

        private static readonly int _PlanetRotation = Shader.PropertyToID("_PlanetRotation");
        private static readonly int _SpaceRotation = Shader.PropertyToID("_SpaceRotation");

        private static readonly int _PlanetCenterRadius = Shader.PropertyToID("_PlanetCenterRadius");
        private static readonly int _PlanetUpAltitude = Shader.PropertyToID("_PlanetUpAltitude");

        private static readonly int _PBRSkyCameraPosPS = Shader.PropertyToID("_PBRSkyCameraPosPS");

        private static readonly int _CelestialBody_Color = Shader.PropertyToID("_CelestialBody_Color");
        private static readonly int _CelestialBody_Radius = Shader.PropertyToID("_CelestialBody_Radius");
        private static readonly int _CelestialBody_Forward = Shader.PropertyToID("_CelestialBody_Forward");
        private static readonly int _CelestialBody_DistanceFromCamera = Shader.PropertyToID("_CelestialBody_DistanceFromCamera");
        private static readonly int _CelestialBody_Right = Shader.PropertyToID("_CelestialBody_Right");
        private static readonly int _CelestialBody_AngularRadius = Shader.PropertyToID("_CelestialBody_AngularRadius");
        private static readonly int _CelestialBody_Up = Shader.PropertyToID("_CelestialBody_Up");
        private static readonly int _CelestialBody_Type = Shader.PropertyToID("_CelestialBody_Type");
        private static readonly int _CelestialBody_SurfaceColor = Shader.PropertyToID("_CelestialBody_SurfaceColor");
        private static readonly int _CelestialBody_Earthshine = Shader.PropertyToID("_CelestialBody_Earthshine");
        private static readonly int _CelestialBody_SurfaceTextureScaleOffset = Shader.PropertyToID("_CelestialBody_SurfaceTextureScaleOffset");
        private static readonly int _CelestialBody_SunDirection = Shader.PropertyToID("_CelestialBody_SunDirection");
        private static readonly int _CelestialBody_FlareCosInner = Shader.PropertyToID("_CelestialBody_FlareCosInner");
        private static readonly int _CelestialBody_FlareCosOuter = Shader.PropertyToID("_CelestialBody_FlareCosOuter");
        private static readonly int _CelestialBody_FlareSize = Shader.PropertyToID("_CelestialBody_FlareSize");
        private static readonly int _CelestialBody_FlareColor = Shader.PropertyToID("_CelestialBody_FlareColor");
        private static readonly int _CelestialBody_FlareFalloff = Shader.PropertyToID("_CelestialBody_FlareFalloff");

        private static readonly int _MainLightColor = Shader.PropertyToID("_MainLightColor");
        private static readonly int _EnableAtmosphericScattering = Shader.PropertyToID("_EnableAtmosphericScattering");

        private const string PHYSICALLY_BASED_SKY = "PHYSICALLY_BASED_SKY";
        private const string LOCAL_SKY = "LOCAL_SKY";
        private const string SKY_NOT_BAKING = "SKY_NOT_BAKING";
        private const string GROUND_ALBEDO_TEXTURE = "_GROUND_ALBEDO_TEXTURE";
        private const string GROUND_EMISSION_TEXTURE = "_GROUND_EMISSION_TEXTURE";
        private const string SPACE_EMISSION_TEXTURE = "_SPACE_EMISSION_TEXTURE";

        private SphericalHarmonicsL2 ambientProbe = new SphericalHarmonicsL2();
        private bool staticAmbientProbeInitialized;
        private string staticAmbientProbeScenePath;

        private const int fibonacciSamplesCount = 64;
        private static readonly float3[] fibonacciSamples = new float3[] {
            new float3(-0.000000f, -1.000000f, -0.000000f),
            new float3(0.184319f, -0.968254f, 0.168851f),
            new float3(-0.030656f, -0.936508f, -0.349304f),
            new float3(-0.259145f, -0.904762f, 0.338009f),
            new float3(0.480237f, -0.873016f, -0.084947f),
            new float3(-0.456147f, -0.841270f, -0.290163f),
            new float3(0.152410f, -0.809524f, 0.566959f),
            new float3(0.289698f, -0.777778f, -0.557796f),
            new float3(-0.625504f, -0.746032f, 0.228433f),
            new float3(0.646907f, -0.714286f, 0.267034f),
            new float3(-0.309767f, -0.682540f, -0.661955f),
            new float3(-0.227233f, -0.650794f, 0.724454f),
            new float3(0.679497f, -0.619048f, -0.393782f),
            new float3(-0.790490f, -0.587302f, -0.173787f),
            new float3(0.478208f, -0.555556f, 0.680202f),
            new float3(0.109470f, -0.523810f, -0.844772f),
            new float3(-0.665672f, -0.492063f, 0.561029f),
            new float3(0.886996f, -0.460317f, 0.036680f),
            new float3(-0.640433f, -0.428571f, -0.637316f),
            new float3(0.042399f, -0.396825f, 0.916914f),
            new float3(0.596485f, -0.365079f, -0.714788f),
            new float3(-0.934389f, -0.333333f, 0.125721f),
            new float3(0.782638f, -0.301587f, 0.544539f),
            new float3(-0.211340f, -0.269841f, -0.939426f),
            new float3(-0.482883f, -0.238095f, 0.842695f),
            new float3(0.932189f, -0.206349f, -0.297394f),
            new float3(-0.893846f, -0.174603f, -0.412980f),
            new float3(0.382101f, -0.142857f, 0.913012f),
            new float3(0.336357f, -0.111111f, -0.935157f),
            new float3(-0.882397f, -0.079365f, 0.463763f),
            new float3(0.965873f, -0.047619f, 0.254601f),
            new float3(-0.540770f, -0.015873f, -0.841021f),
            new float3(-0.169355f, 0.015873f, 0.985427f),
            new float3(0.789726f, 0.047619f, -0.611608f),
            new float3(-0.993442f, 0.079365f, -0.082305f),
            new float3(0.674874f, 0.111111f, 0.729520f),
            new float3(-0.004828f, 0.142857f, -0.989732f),
            new float3(-0.661564f, 0.174603f, 0.729278f),
            new float3(0.974303f, 0.206349f, -0.090298f),
            new float3(-0.773652f, 0.238095f, -0.587174f),
            new float3(0.172344f, 0.269841f, 0.947356f),
            new float3(0.507807f, 0.301587f, -0.806956f),
            new float3(-0.909280f, 0.333333f, 0.249196f),
            new float3(0.828277f, 0.365079f, 0.425058f),
            new float3(-0.319075f, 0.396825f, -0.860651f),
            new float3(-0.340661f, 0.428571f, 0.836825f),
            new float3(0.802224f, 0.460317f, -0.380189f),
            new float3(-0.831918f, 0.492063f, -0.256489f),
            new float3(0.430707f, 0.523810f, 0.734925f),
            new float3(0.174571f, 0.555556f, -0.812947f),
            new float3(-0.659835f, 0.587302f, 0.468716f),
            new float3(0.779324f, 0.619048f, 0.097126f),
            new float3(-0.492126f, 0.650794f, -0.578169f),
            new float3(-0.026634f, 0.682540f, 0.730363f),
            new float3(0.491237f, 0.714286f, -0.498480f),
            new float3(-0.665042f, 0.746032f, 0.034004f),
            new float3(0.484541f, 0.777778f, 0.400352f),
            new float3(-0.081124f, 0.809524f, -0.581455f),
            new float3(-0.306594f, 0.841270f, 0.445270f),
            new float3(0.475272f, 0.873016f, -0.109359f),
            new float3(-0.370575f, 0.904762f, -0.209953f),
            new float3(0.108202f, 0.936508f, 0.333534f),
            new float3(0.103734f, 0.968254f, -0.227428f),
            new float3(-0.000000f, 1.000000f, 0.000000f)
        };

        public PBSkyPrePass(Material material, CelestialBodyData celestialBodyData)
        {
            this.material = material;
            this.celestialBodyData = celestialBodyData;
        }

        #region Non Render Graph Pass
// Unity 6.4 removed the compatibility-mode ScriptableRenderPass callbacks and
// target configuration APIs. Use the Render Graph implementation below there.
#if !UNITY_6000_4_OR_NEWER
        // Passing the final sun color to the Execute() method
        private float3 mainLightColor;

        private Light GetMainLight(LightData lightData)
        {
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex != -1)
            {
                VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
                Light light = shadowLight.light;
                if ((light.shadows != LightShadows.None || RenderSettings.sun != null && !RenderSettings.sun.isActiveAndEnabled) && shadowLight.lightType == LightType.Directional)
                    return light;
            }

            return RenderSettings.sun;
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            Light mainLight = GetMainLight(renderingData.lightData);

            if (mainLight != null)
            {
                float3 sunAttenuation = EvaluateSunColorAttenuation(float3(camera.transform.position) - visualEnvironment.GetPlanetCenterRadius(camera.transform.position).xyz, -mainLight.transform.forward);

                Color color = mainLight.color.linear * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white);
                mainLightColor = float3(color.r, color.g, color.b) * mainLight.intensity * sunAttenuation;

            #if URP_PHYSICAL_LIGHT
                bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;

                mainLightColor = isPhysicalLight ? mainLightColor * rcp(PI) : mainLightColor;
            #endif
            }

            UpdateMaterialProperties(mainLight, camera, material);
            lutMaterial.CopyPropertiesFromMaterial(material);

            UpdateAmbientProbe(mainLight, camera, mainLightColor);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                bool isReflectionCamera = renderingData.cameraData.camera.cameraType == CameraType.Reflection;
                cmd.SetGlobalFloat(_DisableSunDisk, isReflectionCamera ? 1.0f : 0.0f);
                cmd.SetGlobalVector(_MainLightColor, float4(mainLightColor, 0.0f));
                cmd.EnableShaderKeyword(PHYSICALLY_BASED_SKY);
                cmd.EnableShaderKeyword(SKY_NOT_BAKING);
                cmd.SetGlobalFloat(_EnableAtmosphericScattering, pbrSky.atmosphericScattering.value ? 1.0f : 0.0f);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
#endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass

        private Light GetMainLight(UniversalLightData lightData)
        {
            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex != -1)
            {
                VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
                Light light = shadowLight.light;
                if ((light.shadows != LightShadows.None || RenderSettings.sun != null && !RenderSettings.sun.isActiveAndEnabled) && shadowLight.lightType == LightType.Directional)
                    return light;
            }

            return RenderSettings.sun;
        }

        private class PassData
        {
            internal Vector3 mainLightColor;
            internal bool enableAtmosphericScattering;
            internal bool isReflectionCamera;
            internal CameraAtmosphereData cameraAtmosphereData;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            cmd.SetGlobalFloat(_DisableSunDisk, data.isReflectionCamera ? 1.0f : 0.0f);
            cmd.SetGlobalVector(_MainLightColor, data.mainLightColor);
            cmd.EnableShaderKeyword(PHYSICALLY_BASED_SKY);
            cmd.EnableShaderKeyword(SKY_NOT_BAKING);
            cmd.SetGlobalFloat(_EnableAtmosphericScattering, data.enableAtmosphericScattering ? 1.0f : 0.0f);
            cmd.SetGlobalVector(_PBRSkyCameraPosPS, data.cameraAtmosphereData.cameraPositionPS);
            cmd.SetGlobalVector(_PlanetCenterRadius, data.cameraAtmosphereData.planetCenterRadius);
            cmd.SetGlobalVector(_PlanetUpAltitude, data.cameraAtmosphereData.planetUpAltitude);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                Light mainLight = GetMainLight(lightData);
                Camera camera = cameraData.camera;

                float3 mainLightColor = 0.0f;
                if (mainLight != null)
                {
                    float3 sunAttenuation = EvaluateSunColorAttenuation(float3(camera.transform.position) - visualEnvironment.GetPlanetCenterRadius(camera.transform.position).xyz, -mainLight.transform.forward);

                    Color color = mainLight.color.linear * (mainLight.useColorTemperature ? Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : Color.white);
                    mainLightColor = float3(color.r, color.g, color.b) * mainLight.intensity * sunAttenuation;

                #if URP_PHYSICAL_LIGHT
                    bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;

                    mainLightColor = isPhysicalLight ? mainLightColor * rcp(PI) : mainLightColor;
                #endif
                }

                UpdateMaterialProperties(mainLight, camera, material);
                lutMaterial.CopyPropertiesFromMaterial(material);

                UpdateAmbientProbe(mainLight, camera, mainLightColor);

                passData.mainLightColor = mainLightColor;
                passData.enableAtmosphericScattering = pbrSky.atmosphericScattering.value;
                passData.isReflectionCamera = cameraData.camera.cameraType == CameraType.Reflection;
                passData.cameraAtmosphereData = GetCameraAtmosphereData(camera);

                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }

        private void UpdateAmbientProbe(Light mainLight, Camera camera, float3 mainLightColor)
        {
            if (mainLight == null)
                return;

            if (visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic)
            {
                staticAmbientProbeInitialized = false;
                ambientProbe = EvaluateAmbientProbe(ambientProbe, pbrSky, mainLight.transform.forward, mainLightColor);
                RenderSettings.ambientProbe = ambientProbe;
                return;
            }

            if (camera.cameraType != CameraType.Game)
                return;

            string scenePath = camera.gameObject.scene.path;
            if (!staticAmbientProbeInitialized || staticAmbientProbeScenePath != scenePath)
            {
                ambientProbe = EvaluateAmbientProbe(ambientProbe, pbrSky, mainLight.transform.forward, mainLightColor);
                staticAmbientProbeInitialized = true;
                staticAmbientProbeScenePath = scenePath;
            }

            // Entering Play Mode and loading lighting data can restore the serialized baked
            // probe. Re-publish the cached PBR probe so static ambient remains deterministic.
            if (!AmbientProbesEqual(RenderSettings.ambientProbe, ambientProbe))
                RenderSettings.ambientProbe = ambientProbe;
        }

        private static bool AmbientProbesEqual(SphericalHarmonicsL2 lhs, SphericalHarmonicsL2 rhs)
        {
            for (int rgb = 0; rgb < 3; rgb++)
                for (int coefficient = 0; coefficient < 9; coefficient++)
                    if (lhs[rgb, coefficient] != rhs[rgb, coefficient])
                        return false;

            return true;
        }

        internal static SphericalHarmonicsL2 EvaluateAmbientProbe(SphericalHarmonicsL2 ambientProbe, PhysicallyBasedSky pbrSky, float3 lightDirection, float3 lightColor)
        {
            ambientProbe.Clear();

            float weightOverPdf = 4.0f * PI * rcp(fibonacciSamplesCount);
            for (int i = 0; i < fibonacciSamplesCount; i++)
            {
                float3 V = fibonacciSamples[i];

                pbrSky.RenderSky(-lightDirection, lightColor, V, out float3 skyColor, out _);

                Color color = new Color(skyColor.x, skyColor.y, skyColor.z);
                ambientProbe.AddDirectionalLight(V, color, weightOverPdf);
            }
            return ambientProbe;
        }

        internal struct CameraAtmosphereData
        {
            internal Vector4 cameraPositionPS;
            internal Vector4 planetCenterRadius;
            internal Vector4 planetUpAltitude;
        }

        private CameraAtmosphereData GetCameraAtmosphereData(Camera camera)
        {
            Vector3 cameraPosition = camera.transform.position;
            float4 planetCenterRadius = visualEnvironment.GetPlanetCenterRadius(cameraPosition);
            float planetRadius = planetCenterRadius.w;
            Vector3 planetCenter = planetCenterRadius.xyz;
            Vector3 planetUp = (cameraPosition - planetCenter).normalized;
            float cameraAltitude = Vector3.Dot(cameraPosition - (planetUp * planetRadius + planetCenter), planetUp);
            Vector3 cameraPositionPS = cameraPosition - planetCenter;

            if (cameraAltitude < 1.0f)
                cameraPositionPS -= (cameraAltitude - 1.0f) * planetUp;

            return new CameraAtmosphereData
            {
                cameraPositionPS = cameraPositionPS,
                planetCenterRadius = planetCenterRadius,
                planetUpAltitude = new Vector4(planetUp.x, planetUp.y, planetUp.z, cameraAltitude)
            };
        }

        private void UpdateMaterialProperties(Light mainLight, Camera camera, Material material)
        {
            CameraAtmosphereData cameraAtmosphereData = GetCameraAtmosphereData(camera);
            float4 planetCenterRadius = cameraAtmosphereData.planetCenterRadius;

            float R = planetCenterRadius.w;
            float D = pbrSky.GetMaximumAltitude();
            float airH = pbrSky.GetAirScaleHeight();
            float aerH = pbrSky.GetAerosolScaleHeight();
            float aerA = pbrSky.aerosolAnisotropy.value;
            float ozoS = pbrSky.GetOzoneLayerMinimumAltitude();
            float ozoW = pbrSky.GetOzoneLayerWidth();

            float skyIntensityMultiplier = pbrSky.GetIntensityFromSettings();

            float2 expParams = ComputeExponentialInterpolationParams(pbrSky.horizonZenithShift.value);

            material.SetFloat(_AtmosphericDepth, D);
            Shader.SetGlobalFloat(_RcpAtmosphericDepth, 1.0f / D);
            Shader.SetGlobalFloat(_AtmosphericRadius, R + D);
            Shader.SetGlobalFloat(_AerosolAnisotropy, aerA);
            Shader.SetGlobalFloat(_AerosolPhasePartConstant, CornetteShanksPhasePartConstant(aerA));

            Shader.SetGlobalFloat(_AirDensityFalloff, 1.0f / airH);
            Shader.SetGlobalFloat(_AirScaleHeight, airH);
            Shader.SetGlobalFloat(_AerosolDensityFalloff, 1.0f / aerH);
            Shader.SetGlobalFloat(_AerosolScaleHeight, aerH);

            Shader.SetGlobalVector(_AirSeaLevelExtinction, pbrSky.GetAirExtinctionCoefficient());
            Shader.SetGlobalFloat(_AerosolSeaLevelExtinction, pbrSky.GetAerosolExtinctionCoefficient());

            material.SetVector(_AirSeaLevelScattering, pbrSky.GetAirScatteringCoefficient());
            Shader.SetGlobalFloat(_IntensityMultiplier, skyIntensityMultiplier);

            Shader.SetGlobalVector(_AerosolSeaLevelScattering, pbrSky.GetAerosolScatteringCoefficient());
            Shader.SetGlobalFloat(_ColorSaturation, pbrSky.colorSaturation.value);

            Shader.SetGlobalVector(_OzoneSeaLevelExtinction, pbrSky.GetOzoneExtinctionCoefficient());
            Shader.SetGlobalVector(_OzoneScaleOffset, new Vector2(2.0f / ozoW, -2.0f * ozoS / ozoW - 1.0f));
            Shader.SetGlobalFloat(_OzoneLayerStart, R + ozoS);
            Shader.SetGlobalFloat(_OzoneLayerEnd, R + ozoS + ozoW);

            material.SetVector(_GroundAlbedo_PlanetRadius, new Vector4(pbrSky.groundTint.value.r, pbrSky.groundTint.value.g, pbrSky.groundTint.value.b, R));
            Shader.SetGlobalFloat(_AlphaSaturation, pbrSky.alphaSaturation.value);

            Shader.SetGlobalFloat(_AlphaMultiplier, pbrSky.alphaMultiplier.value);

            Shader.SetGlobalVector(_HorizonTint, new Vector3(pbrSky.horizonTint.value.r, pbrSky.horizonTint.value.g, pbrSky.horizonTint.value.b));
            Shader.SetGlobalFloat(_HorizonZenithShiftPower, expParams.x);

            Shader.SetGlobalVector(_ZenithTint, new Vector3(pbrSky.zenithTint.value.r, pbrSky.zenithTint.value.g, pbrSky.zenithTint.value.b));
            Shader.SetGlobalFloat(_HorizonZenithShiftScale, expParams.y);

            Shader.SetGlobalVector(_PBRSkyCameraPosPS, cameraAtmosphereData.cameraPositionPS);
            Shader.SetGlobalVector(_PlanetCenterRadius, planetCenterRadius);
            Shader.SetGlobalVector(_PlanetUpAltitude, cameraAtmosphereData.planetUpAltitude);

            var renderingSpace = visualEnvironment.renderingSpace.value;
            CoreUtils.SetKeyword(material, LOCAL_SKY, renderingSpace == VisualEnvironment.RenderingSpace.World);

            // Precomputation is done, shading is next.
            Quaternion planetRotation = Quaternion.Euler(pbrSky.planetRotation.value.x,
                pbrSky.planetRotation.value.y,
                pbrSky.planetRotation.value.z);

            Quaternion spaceRotation = Quaternion.Euler(pbrSky.spaceRotation.value.x,
                pbrSky.spaceRotation.value.y,
                pbrSky.spaceRotation.value.z);

            var planetRotationMatrix = Matrix4x4.Rotate(planetRotation);
            planetRotationMatrix[0] *= -1;
            planetRotationMatrix[1] *= -1;
            planetRotationMatrix[2] *= -1;

            CoreUtils.SetKeyword(material, GROUND_ALBEDO_TEXTURE, pbrSky.groundColorTexture.value != null);
            material.SetTexture(_GroundAlbedoTexture, pbrSky.groundColorTexture.value);

            CoreUtils.SetKeyword(material, GROUND_EMISSION_TEXTURE, pbrSky.groundEmissionTexture.value != null);
            material.SetTexture(_GroundEmissionTexture, pbrSky.groundEmissionTexture.value);
            material.SetFloat(_GroundEmissionMultiplier, pbrSky.groundEmissionMultiplier.value);

            CoreUtils.SetKeyword(material, SPACE_EMISSION_TEXTURE, pbrSky.spaceEmissionTexture.value != null);
            material.SetTexture(_SpaceEmissionTexture, pbrSky.spaceEmissionTexture.value);
            material.SetFloat(_SpaceEmissionMultiplier, pbrSky.spaceEmissionMultiplier.value);

            material.SetMatrix(_PlanetRotation, planetRotationMatrix);
            material.SetMatrix(_SpaceRotation, Matrix4x4.Rotate(spaceRotation));

            if (mainLight != null)
            {
                // Celestial Body Data
                material.SetInt(_CelestialLightCount, 1);
                material.SetInt(_CelestialBodyCount, 1);
                material.SetFloat(_CelestialLightExposure, 1.0f);

                const float distanceFromCamera = 1.5e+11f;
                const float angularDiameter = 0.5f;
                var angularRadius = angularDiameter * 0.5f * Mathf.Deg2Rad;
                var flareSize = Mathf.Max(2.0f * Mathf.Deg2Rad, 5.960464478e-8f);
                var flareCosInner = Mathf.Cos(angularRadius);
                float rcpSolidAngle = 1.0f / (Mathf.PI * 2.0f * (1 - flareCosInner));

            #if URP_PHYSICAL_LIGHT
                var color = mainLight.color.linear * mainLight.intensity;

                bool isPhysicalLight = mainLight.GetComponent<AdditionalLightData>() != null;
                color = isPhysicalLight ? color : color * PI;
            #else
                var color = mainLight.color.linear * mainLight.intensity * PI;
            #endif

                color = mainLight.useColorTemperature ? color * Mathf.CorrelatedColorTemperatureToRGB(mainLight.colorTemperature) : color;
                var surfaceColor = Vector4.one;
                var flareColor = Vector4.one;

                surfaceColor *= rcpSolidAngle;
                flareColor *= rcpSolidAngle;

                celestialBodyData.color = float3(color.r, color.g, color.b);

                const float lightingUnitsMultiplier = 50.0f;
                color *= rcp(lightingUnitsMultiplier); // avoid potential precision issues

                surfaceColor = Vector4.Scale(color, surfaceColor);
                flareColor = Vector4.Scale(color, flareColor);

                celestialBodyData.forward = mainLight.transform.forward;
                celestialBodyData.distanceFromCamera = distanceFromCamera;
                celestialBodyData.right = mainLight.transform.right.normalized;
                celestialBodyData.angularRadius = angularRadius;
                celestialBodyData.radius = Mathf.Tan(angularRadius) * distanceFromCamera;
                celestialBodyData.up = mainLight.transform.up.normalized;
                celestialBodyData.type = 0; // sun
                celestialBodyData.surfaceColor = surfaceColor;
                celestialBodyData.earthshine = 1.0f * 0.01f;  // earth reflects about 0.01% of sun light
                celestialBodyData.surfaceTextureScaleOffset = Vector4.zero;
                celestialBodyData.sunDirection = mainLight != null ? mainLight.transform.forward : Vector3.forward;

                // Flare
                celestialBodyData.flareSize = flareSize;
                celestialBodyData.flareFalloff = 4.0f;

                celestialBodyData.flareCosInner = flareCosInner;
                celestialBodyData.flareCosOuter = Mathf.Cos(angularRadius + flareSize);

                celestialBodyData.flareColor = flareColor;

                Shader.SetGlobalVector(_CelestialBody_Color, celestialBodyData.color);
                Shader.SetGlobalVector(_CelestialBody_Forward, celestialBodyData.forward);
                material.SetFloat(_CelestialBody_DistanceFromCamera, celestialBodyData.distanceFromCamera);
                material.SetVector(_CelestialBody_Right, celestialBodyData.right);
                material.SetFloat(_CelestialBody_AngularRadius, celestialBodyData.angularRadius);
                material.SetFloat(_CelestialBody_Radius, celestialBodyData.radius);
                material.SetVector(_CelestialBody_Up, celestialBodyData.up);
                material.SetInt(_CelestialBody_Type, celestialBodyData.type);
                material.SetVector(_CelestialBody_SurfaceColor, celestialBodyData.surfaceColor);
                material.SetFloat(_CelestialBody_Earthshine, celestialBodyData.earthshine);
                material.SetVector(_CelestialBody_SurfaceTextureScaleOffset, celestialBodyData.surfaceTextureScaleOffset);
                material.SetVector(_CelestialBody_SunDirection, celestialBodyData.sunDirection);
                material.SetFloat(_CelestialBody_FlareCosInner, celestialBodyData.flareCosInner);
                material.SetFloat(_CelestialBody_FlareCosOuter, celestialBodyData.flareCosOuter);
                material.SetFloat(_CelestialBody_FlareSize, celestialBodyData.flareSize);
                material.SetVector(_CelestialBody_FlareColor, celestialBodyData.flareColor);
                material.SetFloat(_CelestialBody_FlareFalloff, celestialBodyData.flareFalloff);
            }
        }

        static float CornetteShanksPhasePartConstant(float anisotropy)
        {
            float g = anisotropy;

            return (3.0f / (8.0f * Mathf.PI)) * (1.0f - g * g) / (2.0f + g * g);
        }

        static float2 ComputeExponentialInterpolationParams(float k)
        {
            if (k == 0) k = 1e-6f; // Avoid the numerical explosion around 0

            // Remap t: (exp(10 k t) - 1) / (exp(10 k) - 1) = exp(x t) y - y.
            float x = 10 * k;
            float y = 1 / (exp(x) - 1);

            return float2(x, y);
        }

        static float3 TransmittanceFromOpticalDepth(float3 opticalDepth)
        {
            return exp(-opticalDepth);
        }

        static float Avg3(float a, float b, float c)
        {
            return (a + b + c) * 0.33333333f;
        }

        static float3 Desaturate(float3 value, float3 saturation)
        {
            // Saturation = Colorfulness / Brightness.
            // https://munsell.com/color-blog/difference-chroma-saturation/
            float mean = Avg3(value.x, value.y, value.z);
            float3 dev = value - mean;

            return mean + dev * saturation;
        }

        float3 EvaluateSunColorAttenuation(float3 positionPS, float3 sunDirection, bool estimatePenumbra = false)
        {
            return EvaluateSunColorAttenuation(pbrSky, visualEnvironment, positionPS, sunDirection, estimatePenumbra);
        }

        internal static float3 EvaluateSunColorAttenuation(PhysicallyBasedSky pbrSky, VisualEnvironment visualEnvironment, float3 positionPS, float3 sunDirection, bool estimatePenumbra = false)
        {
            float r = length(positionPS);
            float cosTheta = dot(positionPS, sunDirection) * rcp(r); // Normalize

            // Point can be below horizon due to precision issues
            float R = visualEnvironment.GetPlanetRadius();
            r = max(r, R);
            float cosHoriz = PhysicallyBasedSky.ComputeCosineOfHorizonAngle(r, R);

            if (cosTheta >= cosHoriz) // Above horizon
            {
                float3 oDepth = PhysicallyBasedSky.ComputeAtmosphericOpticalDepth(
                    pbrSky.GetAirScaleHeight(), pbrSky.GetAerosolScaleHeight(), pbrSky.GetAirExtinctionCoefficient(), pbrSky.GetAerosolExtinctionCoefficient(),
                    pbrSky.GetOzoneLayerMinimumAltitude(), pbrSky.GetOzoneLayerWidth(), pbrSky.GetOzoneExtinctionCoefficient(),
                    R, r, cosTheta, true);
                float3 opacity = 1 - TransmittanceFromOpticalDepth(oDepth);
                float penumbra = saturate((cosTheta - cosHoriz) / 0.0019f); // very scientific value
                float3 attenuation = 1 - (Desaturate(opacity, pbrSky.alphaSaturation.value) * pbrSky.alphaMultiplier.value);
                return estimatePenumbra ? attenuation * penumbra : attenuation;
            }
            else
            {
                return 0;
            }
        }
        #endregion
    }

    /// <summary>
    /// This pass updates the precomputation data for physically based sky.
    /// </summary>
    private class SkyViewLUTPass : ScriptableRenderPass
    {
        private const string profilerTag = "Precompute Physically Based Sky";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;
        public VisualEnvironment visualEnvironment;

        public CelestialBodyData celestialBodyData;

        // Store the hash of the parameters each time precomputation is done.
        // If the hash does not match, we must recompute our data.
        private int m_LastPrecomputationParamHash;
        //private int m_LastCelestialBodyDataHash;
        private int m_LastLutDataHash;

        public Material lutMaterial;
        public bool halfResolutionLuts;
        private RTHandle multiScatteringLUTHandle;
        private RTHandle skyViewLUTHandle;
        private RTHandle airSingleScatteringHandle;     // Air SS
        private RTHandle aerosolSingleScatteringHandle; // Aerosol SS
        private RTHandle multipleScatteringHandle;      // Atmosphere MS
        private RTHandle groundIrradianceHandle;
        //private RTHandle atmosphericScatteringLUTHandle;

        private const string _MultiScatteringLUT = "_MultiScatteringLUT";
        private const string _SkyViewLUT = "_SkyViewLUT";
        private const string _AirSingleScatteringTexture = "_InScatteredRadianceTable0";
        private const string _AerosolSingleScatteringTexture = "_InScatteredRadianceTable1";
        private const string _MultipleScatteringTexture = "_InScatteredRadianceTable2";
        private const string _GroundIrradianceTexture = "_GroundIrradianceTable";
        //private const string _AtmosphericScatteringLUT = "_AtmosphericScatteringLUT";

        private const string STEREO_INSTANCING_ON = "STEREO_INSTANCING_ON";

        // Match the texture naming in HDRP
        private static readonly int airSingleScatteringTexture = Shader.PropertyToID("_AirSingleScatteringTexture");
        private static readonly int aerosolSingleScatteringTexture = Shader.PropertyToID("_AerosolSingleScatteringTexture");
        private static readonly int multipleScatteringTexture = Shader.PropertyToID("_MultipleScatteringTexture");
        private static readonly int groundIrradianceTexture = Shader.PropertyToID("_GroundIrradianceTexture");
        //private static readonly int atmosphericScatteringLUT = Shader.PropertyToID(_AtmosphericScatteringLUT);
        private static readonly int multiScatteringLUT = Shader.PropertyToID(_MultiScatteringLUT);
        private static readonly int skyViewLUT = Shader.PropertyToID(_SkyViewLUT);


        private static readonly int PBSky_TableCoord_Z = Shader.PropertyToID("PBSky_TableCoord_Z");

        public const int k_GroundIrradianceTableSize = 256;
        public const int k_InScatteredRadianceTableSizeX = 128; // <N, V>
        public const int k_InScatteredRadianceTableSizeY = 32;  // height
        public const int k_InScatteredRadianceTableSizeZ = 16;  // AzimuthAngle(L) w.r.t. the view vector
        public const int k_InScatteredRadianceTableSizeW = 64;  // <N, L>,

        public const int k_MultiScatteringLutWidth = 32;
        public const int k_MultiScatteringLutHeight = 32;

        public const int k_SkyViewLutWidth = 256;
        public const int k_SkyViewLutHeight = 144;

        public const int k_AtmosphericScatteringLutWidth = 32;
        public const int k_AtmosphericScatteringLutHeight = 32;
        public const int k_AtmosphericScatteringLutDepth = 64;

        private readonly RenderTargetIdentifier[] lutHandles = new RenderTargetIdentifier[3];
        //private readonly RenderTargetIdentifier[] sliceHandles = new RenderTargetIdentifier[2];

        private static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        public SkyViewLUTPass(Material material, ref CelestialBodyData celestialBodyData)
        {
            lutMaterial = material;
            this.celestialBodyData = celestialBodyData;
        }

        #region Non Render Graph Pass
// Unity 6.4 removed the compatibility-mode ScriptableRenderPass callbacks and
// target configuration APIs. Use the Render Graph implementation below there.
#if !UNITY_6000_4_OR_NEWER
        bool lutDataChanged;
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            desc.dimension = TextureDimension.Tex2D;
            desc.useDynamicScale = false;

            desc.width = k_MultiScatteringLutWidth;
            desc.height = k_MultiScatteringLutHeight;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref multiScatteringLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultiScatteringLUT);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref multiScatteringLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultiScatteringLUT);
        #endif

            desc.width = k_SkyViewLutWidth;
            desc.height = k_SkyViewLutHeight;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref skyViewLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _SkyViewLUT);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref skyViewLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _SkyViewLUT);
        #endif

            desc.width = k_GroundIrradianceTableSize;
            desc.height = 1;
        #if UNITY_6000_0_OR_NEWER
            lutDataChanged = RenderingUtils.ReAllocateHandleIfNeeded(ref groundIrradianceHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _GroundIrradianceTexture);
        #else
            lutDataChanged = RenderingUtils.ReAllocateIfNeeded(ref groundIrradianceHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _GroundIrradianceTexture);
        #endif

            // Switched Y and Z dimension to reduce draw calls.
            desc.memoryless = RenderTextureMemoryless.None;
            desc.dimension = TextureDimension.Tex3D;
            desc.width = halfResolutionLuts ? k_InScatteredRadianceTableSizeX / 2 : k_InScatteredRadianceTableSizeX;
            desc.height = halfResolutionLuts ? (k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW) / 2 : k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW;
            desc.volumeDepth = halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
        #if UNITY_6000_0_OR_NEWER
            lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref airSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AirSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref aerosolSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AerosolSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref multipleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultipleScatteringTexture);
        #else
            lutDataChanged |= RenderingUtils.ReAllocateIfNeeded(ref airSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AirSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateIfNeeded(ref aerosolSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AerosolSingleScatteringTexture);
            lutDataChanged |= RenderingUtils.ReAllocateIfNeeded(ref multipleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultipleScatteringTexture);
        #endif

            // Unused
            /*
            desc.width = k_AtmosphericScatteringLutWidth;
            desc.height = k_AtmosphericScatteringLutHeight;
            desc.volumeDepth = k_AtmosphericScatteringLutDepth;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref atmosphericScatteringLUTHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _AtmosphericScatteringLUT);
            RenderingUtils.ReAllocateHandleIfNeeded(ref skyTransmittanceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittance");
        #else
            RenderingUtils.ReAllocateIfNeeded(ref atmosphericScatteringLUTHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: _AtmosphericScatteringLUT);
            RenderingUtils.ReAllocateIfNeeded(ref skyTransmittanceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittance");
        #endif

            desc.dimension = TextureDimension.Tex2D;
            desc.volumeDepth = 1;
        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref atmosphericScatteringSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_AtmosphericScatteringSlice");
            RenderingUtils.ReAllocateHandleIfNeeded(ref skyTransmittanceSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittanceSlice");
        #else
            RenderingUtils.ReAllocateIfNeeded(ref atmosphericScatteringSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_AtmosphericScatteringSlice");
            RenderingUtils.ReAllocateIfNeeded(ref skyTransmittanceSliceHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SkyTransmittanceSlice");
        #endif
            */

            lutDataChanged |= HasLutDataChanged();
            m_LastPrecomputationParamHash = lutDataChanged ? 0 : m_LastPrecomputationParamHash;
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            bool precomputationChanged = HasPrecomputationDataChanged() || lutDataChanged;
            //bool celestialBodyDataChanged = HasCelestialBodyDataChanged() || lutDataChanged;

            bool cameraSpaceSky = visualEnvironment.renderingSpace.value == VisualEnvironment.RenderingSpace.Camera;
            bool isStereoEnabled = renderingData.cameraData.camera.stereoEnabled;

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                if (isStereoEnabled)
                    cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

                if (precomputationChanged)
                {
                    Blitter.BlitCameraTexture(cmd, multiScatteringLUTHandle, multiScatteringLUTHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, lutMaterial, pass: 1);
                }

                lutMaterial.SetTexture(multiScatteringLUT, multiScatteringLUTHandle);

                if (cameraSpaceSky)
                {
                    Blitter.BlitCameraTexture(cmd, skyViewLUTHandle, skyViewLUTHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, lutMaterial, pass: 0);
                }

                cmd.SetGlobalTexture(skyViewLUT, skyViewLUTHandle);

                if (precomputationChanged)
                {
                    // InScattered Radiance LUTs
                    lutHandles[0] = airSingleScatteringHandle;
                    lutHandles[1] = aerosolSingleScatteringHandle;
                    lutHandles[2] = multipleScatteringHandle;

                    int slices = halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
                    for (int slice = 0; slice < slices; ++slice)
                    {
                        cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                        cmd.SetRenderTarget(lutHandles, airSingleScatteringHandle, 0, CubemapFace.Unknown, slice);

                        Blitter.BlitTexture(cmd, airSingleScatteringHandle, m_ScaleBias, lutMaterial, pass: 2);
                    }

                    if (!cameraSpaceSky)
                    {
                        Blitter.BlitCameraTexture(cmd, groundIrradianceHandle, groundIrradianceHandle, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, lutMaterial, pass: 3);
                    }
                }

                // Unused
                /*
                if (precomputedAtmosphericScattering)
                {
                    for (int slice = 0; slice < k_AtmosphericScatteringLutDepth; ++slice)
                    {
                        sliceHandles[0] = atmosphericScatteringLUTHandle;
                        sliceHandles[1] = skyTransmittanceHandle;

                        cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                        cmd.SetRenderTarget(sliceHandles, atmosphericScatteringLUTHandle, 0, CubemapFace.Unknown, slice);

                        Blitter.BlitTexture(cmd, atmosphericScatteringLUTHandle, m_ScaleBias, lutMaterial, pass: 5);

                        cmd.CopyTexture(atmosphericScatteringLUTHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, atmosphericScatteringSliceHandle, 0, 0, 0, 0);
                        cmd.CopyTexture(skyTransmittanceHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, skyTransmittanceSliceHandle, 0, 0, 0, 0);
                    }
                }

                lutMaterial.SetTexture("_AtmosphericScatteringSlice", atmosphericScatteringSliceHandle);
                lutMaterial.SetTexture("_SkyTransmittanceSlice", skyTransmittanceSliceHandle);

                cmd.SetGlobalTexture(atmosphericScatteringLUT, atmosphericScatteringLUTHandle);
                */

                cmd.SetGlobalTexture(airSingleScatteringTexture, airSingleScatteringHandle);
                cmd.SetGlobalTexture(aerosolSingleScatteringTexture, aerosolSingleScatteringHandle);
                cmd.SetGlobalTexture(multipleScatteringTexture, multipleScatteringHandle);
                cmd.SetGlobalTexture(groundIrradianceTexture, groundIrradianceHandle);

                cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle, renderingData.cameraData.renderer.cameraDepthTargetHandle);

                if (isStereoEnabled)
                    cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
#endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material lutMaterial;

            internal TextureHandle multiScatteringLUTHandle;
            internal TextureHandle skyViewLUTHandle;

            internal TextureHandle airSingleScatteringHandle;
            internal TextureHandle aerosolSingleScatteringHandle;
            internal TextureHandle multipleScatteringHandle;

            internal TextureHandle groundIrradianceHandle;
            //internal TextureHandle atmosphericScatteringLUTHandle;
            //internal TextureHandle skyTransmittanceHandle;
            //internal TextureHandle atmosphericScatteringSliceHandle;
            //internal TextureHandle skyTransmittanceSliceHandle;

            internal RenderTargetIdentifier[] lutHandles;
            //internal RenderTargetIdentifier[] sliceHandles;

            internal bool cameraSpaceSky;
            internal bool precomputedAtmosphericScattering;
            internal bool halfResolutionLuts;

            internal bool precomputationChanged;
            //internal bool celestialBodyDataChanged;

            internal bool isStereoEnabled;
        }

        private static readonly MaterialPropertyBlock s_LutPropertyBlock = new MaterialPropertyBlock();
        private static readonly int blitTextureTexelSize = Shader.PropertyToID("_BlitTexture_TexelSize");
        private static readonly int blitScaleBias = Shader.PropertyToID("_BlitScaleBias");

        static void DrawLut(CommandBuffer cmd, Material material, int pass, int width, int height)
        {
            s_LutPropertyBlock.Clear();
            s_LutPropertyBlock.SetVector(blitTextureTexelSize, new Vector4(1.0f / width, 1.0f / height, width, height));
            s_LutPropertyBlock.SetVector(blitScaleBias, m_ScaleBias);
            cmd.SetViewport(new Rect(0.0f, 0.0f, width, height));
            CoreUtils.DrawFullScreen(cmd, material, s_LutPropertyBlock, pass);
        }

#if UNITY_EDITOR
        static void EnsureLutPassesCompiled(Material material)
        {
            // Precomputation only runs when its inputs change. If async shader compilation is
            // still in progress on a cold editor or platform start, placeholder draws would make
            // the persistent LUTs black with no later reason to regenerate them.
            for (int pass = 0; pass <= 3; pass++)
            {
                if (!UnityEditor.ShaderUtil.IsPassCompiled(material, pass))
                    UnityEditor.ShaderUtil.CompilePass(material, pass, forceSync: true);
            }
        }
#endif

        static void ExecuteMultiScatteringPass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            cmd.SetRenderTarget(data.multiScatteringLUTHandle);
            DrawLut(cmd, data.lutMaterial, 1, k_MultiScatteringLutWidth, k_MultiScatteringLutHeight);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        static void ExecuteLutGenerationPass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            data.lutMaterial.SetTexture(multiScatteringLUT, data.multiScatteringLUTHandle);

            if (data.cameraSpaceSky)
            {
                cmd.SetRenderTarget(data.skyViewLUTHandle);
                DrawLut(cmd, data.lutMaterial, 0, k_SkyViewLutWidth, k_SkyViewLutHeight);
            }

            if (data.precomputationChanged)
            {
                // InScattered Radiance LUTs
                data.lutHandles[0] = data.airSingleScatteringHandle;
                data.lutHandles[1] = data.aerosolSingleScatteringHandle;
                data.lutHandles[2] = data.multipleScatteringHandle;

                int slices = data.halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
                for (int slice = 0; slice < slices; ++slice)
                {
                    cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                    cmd.SetRenderTarget(data.lutHandles, data.airSingleScatteringHandle, 0, CubemapFace.Unknown, slice);
                    DrawLut(cmd, data.lutMaterial, 2,
                        data.halfResolutionLuts ? k_InScatteredRadianceTableSizeX / 2 : k_InScatteredRadianceTableSizeX,
                        data.halfResolutionLuts ? (k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW) / 2 : k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW);
                }
            }

            // Unused
            /*
            if (data.precomputedAtmosphericScattering)
            {
                for (int slice = 0; slice < k_AtmosphericScatteringLutDepth; ++slice)
                {
                    data.sliceHandles[0] = data.atmosphericScatteringLUTHandle;
                    data.sliceHandles[1] = data.skyTransmittanceHandle;

                    cmd.SetGlobalInteger(PBSky_TableCoord_Z, slice);
                    cmd.SetRenderTarget(data.sliceHandles, data.atmosphericScatteringLUTHandle, 0, CubemapFace.Unknown, slice);

                    Blitter.BlitTexture(cmd, data.atmosphericScatteringLUTHandle, m_ScaleBias, data.lutMaterial, pass: 5);

                    cmd.CopyTexture(data.atmosphericScatteringLUTHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, data.atmosphericScatteringSliceHandle, 0, 0, 0, 0);
                    cmd.CopyTexture(data.skyTransmittanceHandle, slice, 0, 0, 0, k_AtmosphericScatteringLutWidth, k_AtmosphericScatteringLutHeight, data.skyTransmittanceSliceHandle, 0, 0, 0, 0);
                }
            }

            data.lutMaterial.SetTexture("_AtmosphericScatteringSlice", data.atmosphericScatteringSliceHandle);
            data.lutMaterial.SetTexture("_SkyTransmittanceSlice", data.skyTransmittanceSliceHandle);

            cmd.SetGlobalTexture(atmosphericScatteringLUT, data.atmosphericScatteringLUTHandle);
            */

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        static void ExecuteGroundIrradiancePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            cmd.SetGlobalTexture(airSingleScatteringTexture, data.airSingleScatteringHandle);
            cmd.SetGlobalTexture(aerosolSingleScatteringTexture, data.aerosolSingleScatteringHandle);
            cmd.SetGlobalTexture(multipleScatteringTexture, data.multipleScatteringHandle);

            cmd.SetRenderTarget(data.groundIrradianceHandle);
            DrawLut(cmd, data.lutMaterial, 3, k_GroundIrradianceTableSize, 1);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        static void ExecutePublishLutsPass(PassData data, UnsafeGraphContext context)
        {
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            bool lutDataChanged;

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.useMipMap = false;
                desc.autoGenerateMips = false;
                desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.dimension = TextureDimension.Tex2D;
                desc.useDynamicScale = false;

                desc.width = k_MultiScatteringLutWidth;
                desc.height = k_MultiScatteringLutHeight;
                RenderingUtils.ReAllocateHandleIfNeeded(ref multiScatteringLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultiScatteringLUT);
                TextureHandle multiScatteringLUTTextureHandle = renderGraph.ImportTexture(multiScatteringLUTHandle);

                desc.width = k_SkyViewLutWidth;
                desc.height = k_SkyViewLutHeight;
                RenderingUtils.ReAllocateHandleIfNeeded(ref skyViewLUTHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _SkyViewLUT);
                TextureHandle skyViewLUTTextureHandle = renderGraph.ImportTexture(skyViewLUTHandle);

                desc.width = k_GroundIrradianceTableSize;
                desc.height = 1;
                lutDataChanged = RenderingUtils.ReAllocateHandleIfNeeded(ref groundIrradianceHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _GroundIrradianceTexture);
                TextureHandle groundIrradianceTextureHandle = renderGraph.ImportTexture(groundIrradianceHandle);

                // Switched Y and Z dimension to reduce draw calls.
                desc.dimension = TextureDimension.Tex3D;
                desc.width = halfResolutionLuts ? k_InScatteredRadianceTableSizeX / 2 : k_InScatteredRadianceTableSizeX;
                desc.height = halfResolutionLuts ? (k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW) / 2 : k_InScatteredRadianceTableSizeZ * k_InScatteredRadianceTableSizeW;
                desc.volumeDepth = halfResolutionLuts ? k_InScatteredRadianceTableSizeY / 2 : k_InScatteredRadianceTableSizeY;
                lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref airSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AirSingleScatteringTexture);
                TextureHandle airSingleScatteringTextureHandle = renderGraph.ImportTexture(airSingleScatteringHandle);

                lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref aerosolSingleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _AerosolSingleScatteringTexture);
                TextureHandle aerosolSingleScatteringTextureHandle = renderGraph.ImportTexture(aerosolSingleScatteringHandle);

                lutDataChanged |= RenderingUtils.ReAllocateHandleIfNeeded(ref multipleScatteringHandle, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: _MultipleScatteringTexture);
                TextureHandle multipleScatteringTextureHandle = renderGraph.ImportTexture(multipleScatteringHandle);

            lutDataChanged |= HasLutDataChanged();
            if (lutDataChanged)
                m_LastPrecomputationParamHash = 0;
            bool precomputationChanged = HasPrecomputationDataChanged() || lutDataChanged;

#if UNITY_EDITOR
            if (precomputationChanged)
                EnsureLutPassesCompiled(lutMaterial);
#endif

            bool cameraSpaceSky = visualEnvironment.renderingSpace.value == VisualEnvironment.RenderingSpace.Camera;

            if (precomputationChanged)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>($"{profilerTag} (Multiple Scattering)", out var passData);
                passData.multiScatteringLUTHandle = multiScatteringLUTTextureHandle;
                passData.lutMaterial = lutMaterial;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;

                builder.UseTexture(passData.multiScatteringLUTHandle, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecuteMultiScatteringPass(data, context));
            }

            // Keep the 3D LUT producer separate from its ground-irradiance consumer so
            // RenderGraph can insert the attachment-write to shader-read transition on Vulkan.
            using (var builder = renderGraph.AddUnsafePass<PassData>($"{profilerTag} (LUT Generation)", out var passData))
            {
                passData.lutHandles = lutHandles;
                passData.multiScatteringLUTHandle = multiScatteringLUTTextureHandle;
                passData.skyViewLUTHandle = skyViewLUTTextureHandle;
                passData.airSingleScatteringHandle = airSingleScatteringTextureHandle;
                passData.aerosolSingleScatteringHandle = aerosolSingleScatteringTextureHandle;
                passData.multipleScatteringHandle = multipleScatteringTextureHandle;
                passData.groundIrradianceHandle = groundIrradianceTextureHandle;

                passData.cameraSpaceSky = cameraSpaceSky;
                passData.precomputedAtmosphericScattering = pbrSky.atmosphericScattering.value;
                passData.halfResolutionLuts = halfResolutionLuts;
                passData.precomputationChanged = precomputationChanged;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;
                passData.lutMaterial = lutMaterial;

                builder.UseTexture(passData.multiScatteringLUTHandle, AccessFlags.Read);
                builder.UseTexture(passData.skyViewLUTHandle, cameraSpaceSky ? AccessFlags.Write : AccessFlags.Read);
                builder.UseTexture(passData.airSingleScatteringHandle, precomputationChanged ? AccessFlags.Write : AccessFlags.Read);
                builder.UseTexture(passData.aerosolSingleScatteringHandle, precomputationChanged ? AccessFlags.Write : AccessFlags.Read);
                builder.UseTexture(passData.multipleScatteringHandle, precomputationChanged ? AccessFlags.Write : AccessFlags.Read);
                builder.UseTexture(passData.groundIrradianceHandle, AccessFlags.Read);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecuteLutGenerationPass(data, context));
            }

            if (precomputationChanged && !cameraSpaceSky)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>($"{profilerTag} (Ground Irradiance)", out var passData);
                passData.airSingleScatteringHandle = airSingleScatteringTextureHandle;
                passData.aerosolSingleScatteringHandle = aerosolSingleScatteringTextureHandle;
                passData.multipleScatteringHandle = multipleScatteringTextureHandle;
                passData.groundIrradianceHandle = groundIrradianceTextureHandle;
                passData.lutMaterial = lutMaterial;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;

                builder.UseTexture(passData.airSingleScatteringHandle, AccessFlags.Read);
                builder.UseTexture(passData.aerosolSingleScatteringHandle, AccessFlags.Read);
                builder.UseTexture(passData.multipleScatteringHandle, AccessFlags.Read);
                builder.UseTexture(passData.groundIrradianceHandle, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecuteGroundIrradiancePass(data, context));
            }

            // URP's skybox renderer list does not declare its global texture reads. End LUT
            // generation with an explicit read-only pass so Vulkan transitions every LUT to a
            // shader-readable layout before the skybox samples the published globals.
            using (var builder = renderGraph.AddUnsafePass<PassData>($"{profilerTag} (Publish LUTs)", out var passData))
            {
                passData.skyViewLUTHandle = skyViewLUTTextureHandle;
                passData.airSingleScatteringHandle = airSingleScatteringTextureHandle;
                passData.aerosolSingleScatteringHandle = aerosolSingleScatteringTextureHandle;
                passData.multipleScatteringHandle = multipleScatteringTextureHandle;
                passData.groundIrradianceHandle = groundIrradianceTextureHandle;

                builder.UseTexture(passData.skyViewLUTHandle, AccessFlags.Read);
                builder.UseTexture(passData.airSingleScatteringHandle, AccessFlags.Read);
                builder.UseTexture(passData.aerosolSingleScatteringHandle, AccessFlags.Read);
                builder.UseTexture(passData.multipleScatteringHandle, AccessFlags.Read);
                builder.UseTexture(passData.groundIrradianceHandle, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(passData.skyViewLUTHandle, skyViewLUT);
                builder.SetGlobalTextureAfterPass(passData.airSingleScatteringHandle, airSingleScatteringTexture);
                builder.SetGlobalTextureAfterPass(passData.aerosolSingleScatteringHandle, aerosolSingleScatteringTexture);
                builder.SetGlobalTextureAfterPass(passData.multipleScatteringHandle, multipleScatteringTexture);
                builder.SetGlobalTextureAfterPass(passData.groundIrradianceHandle, groundIrradianceTexture);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePublishLutsPass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            m_LastPrecomputationParamHash = 0;
            //m_LastCelestialBodyDataHash = 0;
            m_LastLutDataHash = 0;

            multiScatteringLUTHandle?.Release();
            skyViewLUTHandle?.Release();
            airSingleScatteringHandle?.Release();
            aerosolSingleScatteringHandle?.Release();
            multipleScatteringHandle?.Release();
            groundIrradianceHandle?.Release();
            //atmosphericScatteringLUTHandle?.Release();
        }

        /*
        // Computes hash code of light parameters used during sky view lut precomputation
        int GetLightsHash()
        {
            int hash = 13;
            //for (int i = 0; i < s_CelestialLightCount; i++)
            {
                //ref var data = ref celestialBodyData;
                hash = hash * 23 + celestialBodyData.forward.GetHashCode();
                hash = hash * 23 + celestialBodyData.color.GetHashCode();
            }
            return hash;
        }
        */

        // Computes hash code of LUT RTHandles used during sky view lut precomputation
        int GetLutDataHash()
        {
            int hash = 13;
            hash = hash * 23 + airSingleScatteringHandle.GetHashCode();
            hash = hash * 23 + aerosolSingleScatteringHandle.GetHashCode();
            hash = hash * 23 + multipleScatteringHandle.GetHashCode();
            hash = hash * 23 + groundIrradianceHandle.GetHashCode();
            return hash;
        }

        bool HasPrecomputationDataChanged()
        {
            int currPrecomputationParamHash = pbrSky.GetPrecomputationHashCode();
            // Calculate the parameter hash in the Visual Environment override.
            currPrecomputationParamHash = currPrecomputationParamHash * 23 + visualEnvironment.planetRadius.GetHashCode();
            currPrecomputationParamHash = currPrecomputationParamHash * 23 + visualEnvironment.renderingSpace.GetHashCode();
            currPrecomputationParamHash += halfResolutionLuts.GetHashCode();
            if (currPrecomputationParamHash != m_LastPrecomputationParamHash || m_LastPrecomputationParamHash == 0)
            {
                m_LastPrecomputationParamHash = currPrecomputationParamHash;
                return true;
            }
            return false;
        }

        /*
        bool HasCelestialBodyDataChanged()
        {
            int currCelestialBodyDataHash = GetLightsHash();
            if (currCelestialBodyDataHash != m_LastCelestialBodyDataHash || m_LastCelestialBodyDataHash == 0)
            {
                m_LastCelestialBodyDataHash = currCelestialBodyDataHash;
                return true;
            }
            return false;
        }
        */

        bool HasLutDataChanged()
        {
            int currLutDataHash = GetLutDataHash();
            if (currLutDataHash != m_LastLutDataHash || m_LastLutDataHash == 0)
            {
                m_LastLutDataHash = currLutDataHash;
                return true;
            }
            return false;
        }
        #endregion
    }

    /// <summary>
    /// This pass computes atmospheric scattering (PBSky only) or height-based fog.
    /// </summary>
    private class AtmosphericScatteringPass : ScriptableRenderPass
    {
        private const string profilerTag = "Opaque Atmospheric Scattering";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;
        public VisualEnvironment visualEnvironment;
        public Fog fog;
        public bool fogDepthEdgeAntialiasing;

        public Material lutMaterial;
        public StaticFogSkyCache staticFogSkyCache;

        private static readonly int _FogEnabled = Shader.PropertyToID("_FogEnabled");
        private static readonly int _MaxFogDistance = Shader.PropertyToID("_MaxFogDistance");
        private static readonly int _FogColor = Shader.PropertyToID("_FogColor");
        private static readonly int _FogColorMode = Shader.PropertyToID("_FogColorMode");
        private static readonly int _MipFogParameters = Shader.PropertyToID("_MipFogParameters");
        private static readonly int _HeightFogBaseScattering = Shader.PropertyToID("_HeightFogBaseScattering");
        private static readonly int _HeightFogBaseExtinction = Shader.PropertyToID("_HeightFogBaseExtinction");
        private static readonly int _HeightFogBaseHeight = Shader.PropertyToID("_HeightFogBaseHeight");
        private static readonly int _HeightFogExponents = Shader.PropertyToID("_HeightFogExponents");
        private static readonly int _PlanetUpAltitude = Shader.PropertyToID("_PlanetUpAltitude");
        private static readonly int _UnderWaterEnabled = Shader.PropertyToID("_UnderWaterEnabled");
        private static readonly int _FogWaterHeight = Shader.PropertyToID("_FogWaterHeight");
        private static readonly int _FogSHAr = Shader.PropertyToID("_FogSHAr");
        private static readonly int _FogSHAg = Shader.PropertyToID("_FogSHAg");
        private static readonly int _FogSHAb = Shader.PropertyToID("_FogSHAb");
        private static readonly int _FogSHBr = Shader.PropertyToID("_FogSHBr");
        private static readonly int _FogSHBg = Shader.PropertyToID("_FogSHBg");
        private static readonly int _FogSHBb = Shader.PropertyToID("_FogSHBb");
        private static readonly int _FogSHC = Shader.PropertyToID("_FogSHC");
        private static readonly int _FogSkyTexture = Shader.PropertyToID("_FogSkyTexture");
        private static readonly int _FogSkyTextureMipCount = Shader.PropertyToID("_FogSkyTextureMipCount");
        private static readonly int _FogSkySourceMode = Shader.PropertyToID("_FogSkySourceMode");

        private const float k_DynamicFogSky = 0.0f;
        private const float k_StaticFogSky = 1.0f;
        private const float k_AmbientProbeFogSky = 2.0f;

        // "_ScreenSize" that supports dynamic resolution
        private static readonly int _ScreenResolution = Shader.PropertyToID("_ScreenResolution");

        private readonly LocalKeyword m_FogDepthEdgeAntialiasingKeyword;

        public AtmosphericScatteringPass(Material lutMaterial, StaticFogSkyCache staticFogSkyCache)
        {
            this.lutMaterial = lutMaterial;
            this.staticFogSkyCache = staticFogSkyCache;
            m_FogDepthEdgeAntialiasingKeyword = new LocalKeyword(lutMaterial.shader, k_FogDepthEdgeAntialiasingKeywordName);
        }

        #region Non Render Graph Pass
// Unity 6.4 removed the compatibility-mode ScriptableRenderPass callbacks and
// target configuration APIs. Use the Render Graph implementation below there.
#if !UNITY_6000_4_OR_NEWER
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            bool isFogEnabled = fog != null && fog.IsActive();
            if (isFogEnabled)
            {
                StaticFogSkyCache.Snapshot staticFogSky = UsesStaticSkyFog()
                    ? staticFogSkyCache.GetSnapshot(cmd)
                    : default;

                if (staticFogSky.IsValid)
                    cmd.SetGlobalTexture(_FogSkyTexture, staticFogSky.handle);

                SetFogProperties(cmd, GetFogProperties(renderingData.cameraData.camera, staticFogSky));
            }

            cmd.SetKeyword(lutMaterial, m_FogDepthEdgeAntialiasingKeyword, isFogEnabled && fogDepthEdgeAntialiasing);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                bool isFogEnabled = fog != null && fog.IsActive();
                cmd.SetGlobalInteger(_FogEnabled, isFogEnabled ? 1 : 0);

                var cameraColorHandle = renderingData.cameraData.renderer.cameraColorTargetHandle;

                if (cameraColorHandle != null)
                {
                    CalculateActualScreenResolution(cmd, cameraColorHandle);

                    Blitter.BlitCameraTexture(cmd, cameraColorHandle, cameraColorHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, lutMaterial, pass: 4);
                }
                    
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            CommandBufferPool.Release(cmd);
        }
#endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material lutMaterial;

            internal TextureHandle cameraColorHandle;
            internal bool enableFog;
            internal bool fogDepthEdgeAntialiasing;
            internal LocalKeyword fogDepthEdgeAntialiasingKeyword;
            internal FogProperties fogProperties;
            internal Vector2Int screenResolution;
            internal TextureHandle staticFogSkyTexture;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            SetScreenResolution(cmd, data.screenResolution.x, data.screenResolution.y);

            cmd.SetGlobalInteger(_FogEnabled, data.enableFog ? 1 : 0);
            cmd.SetKeyword(data.lutMaterial, data.fogDepthEdgeAntialiasingKeyword, data.enableFog && data.fogDepthEdgeAntialiasing);

            if (data.enableFog)
            {
                if (data.staticFogSkyTexture.IsValid())
                    cmd.SetGlobalTexture(_FogSkyTexture, data.staticFogSkyTexture);

                SetFogProperties(cmd, data.fogProperties);
            }

            Blitter.BlitCameraTexture(cmd, data.cameraColorHandle, data.cameraColorHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, data.lutMaterial, pass: 4);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            bool isFogEnabled = fog != null && fog.IsActive();
            StaticFogSkyCache.Snapshot staticFogSky = isFogEnabled && UsesStaticSkyFog()
                ? staticFogSkyCache.GetSnapshot(renderGraph)
                : default;
            TextureHandle staticFogSkyTexture = staticFogSky.IsValid
                ? renderGraph.ImportTexture(staticFogSky.handle)
                : default;

            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                passData.lutMaterial = lutMaterial;
                passData.cameraColorHandle = resourceData.activeColorTexture;
                passData.enableFog = isFogEnabled;
                passData.fogDepthEdgeAntialiasing = fogDepthEdgeAntialiasing;
                passData.fogDepthEdgeAntialiasingKeyword = m_FogDepthEdgeAntialiasingKeyword;
                passData.fogProperties = isFogEnabled ? GetFogProperties(cameraData.camera, staticFogSky) : default;
                passData.screenResolution = new Vector2Int(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
                passData.staticFogSkyTexture = staticFogSkyTexture;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                if (staticFogSkyTexture.IsValid())
                    builder.UseTexture(staticFogSkyTexture, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);

                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }

        private struct FogProperties
        {
            internal float maxFogDistance;
            internal float fogColorMode;
            internal Vector4 fogColor;
            internal Vector4 mipFogParameters;
            internal Vector4 heightFogBaseScattering;
            internal float heightFogBaseExtinction;
            internal Vector4 planetUpAltitude;
            internal float heightFogBaseHeight;
            internal Vector4 heightFogExponents;
            internal float underWaterEnabled;
            internal float fogWaterHeight;
            internal bool useFogAmbientProbe;
            internal SphericalHarmonicsL2 fogAmbientProbe;
            internal float fogSkySourceMode;
            internal float fogSkyTextureMipCount;
        }

        private FogProperties GetFogProperties(Camera camera, StaticFogSkyCache.Snapshot staticFogSky)
        {
            var cameraPos = camera.transform.position;

            float4 planetCenterRadius = visualEnvironment.GetPlanetCenterRadius(cameraPos);
            float R = planetCenterRadius.w;

            Vector3 planetCenter = planetCenterRadius.xyz;
            var planetPosRWS = planetCenter - cameraPos;

            // This is not very efficient but necessary for precision
            var planetUp = -planetPosRWS.normalized;
            var cameraHeight = Vector3.Dot(cameraPos - (planetUp * R + planetCenter), planetUp);
            Vector4 upAltitude = new Vector4(planetUp.x, planetUp.y, planetUp.z, cameraHeight);

            Color fogColor = (fog.colorMode.value == Fog.FogColorMode.ConstantColor) ? fog.color.value : fog.tint.value;

            // When volumetric fog is disabled, we don't want its color to affect the heightfog. So we pass neutral values here.
            var extinction = ExtinctionFromMeanFreePath(fog.meanFreePath.value);

            float crBaseHeight = fog.baseHeight.value;

            float layerDepth = Mathf.Max(0.01f, fog.maximumHeight.value - fog.baseHeight.value);
            float H = ScaleHeightFromLayerDepth(layerDepth);

            bool usesStaticSky = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Static;
            bool useFogAmbientProbe = fog.colorMode.value == Fog.FogColorMode.SkyColor && usesStaticSky && !staticFogSky.IsValid;

            return new FogProperties
            {
                maxFogDistance = fog.maxFogDistance.value,
                fogColorMode = (float)fog.colorMode.value,
                fogColor = new Vector4(fogColor.r, fogColor.g, fogColor.b, 0.0f),
                mipFogParameters = new Vector4(fog.mipFogNear.value, fog.mipFogFar.value, fog.mipFogMaxMip.value, 0.0f),
                heightFogBaseScattering = Vector4.one * extinction,
                heightFogBaseExtinction = extinction,
                planetUpAltitude = upAltitude,
                heightFogBaseHeight = crBaseHeight - upAltitude.w,
                heightFogExponents = new Vector4(1.0f / H, H, 0.0f, 0.0f),
                underWaterEnabled = fog.underWater.value ? 1.0f : 0.0f,
                fogWaterHeight = fog.waterHeight.value,
                useFogAmbientProbe = useFogAmbientProbe,
                fogAmbientProbe = useFogAmbientProbe ? RenderSettings.ambientProbe : default,
                fogSkySourceMode = usesStaticSky
                    ? staticFogSky.IsValid ? k_StaticFogSky : k_AmbientProbeFogSky
                    : k_DynamicFogSky,
                fogSkyTextureMipCount = staticFogSky.IsValid ? staticFogSky.mipCount : 0.0f
            };
        }

        private bool UsesStaticSkyFog()
        {
            return fog.colorMode.value == Fog.FogColorMode.SkyColor
                && visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Static;
        }

        private static void SetFogProperties(CommandBuffer cmd, FogProperties properties)
        {
            cmd.SetGlobalFloat(_MaxFogDistance, properties.maxFogDistance);
            cmd.SetGlobalFloat(_FogColorMode, properties.fogColorMode);
            cmd.SetGlobalVector(_FogColor, properties.fogColor);
            cmd.SetGlobalVector(_MipFogParameters, properties.mipFogParameters);
            cmd.SetGlobalVector(_HeightFogBaseScattering, properties.heightFogBaseScattering);
            cmd.SetGlobalFloat(_HeightFogBaseExtinction, properties.heightFogBaseExtinction);
            cmd.SetGlobalVector(_PlanetUpAltitude, properties.planetUpAltitude);
            cmd.SetGlobalFloat(_HeightFogBaseHeight, properties.heightFogBaseHeight);
            cmd.SetGlobalVector(_HeightFogExponents, properties.heightFogExponents);
            cmd.SetGlobalFloat(_UnderWaterEnabled, properties.underWaterEnabled);
            cmd.SetGlobalFloat(_FogWaterHeight, properties.fogWaterHeight);
            cmd.SetGlobalFloat(_FogSkySourceMode, properties.fogSkySourceMode);
            cmd.SetGlobalFloat(_FogSkyTextureMipCount, properties.fogSkyTextureMipCount);

            if (properties.useFogAmbientProbe)
                SetFogAmbientProbe(cmd, properties.fogAmbientProbe);
        }

        private static void SetFogAmbientProbe(CommandBuffer cmd, SphericalHarmonicsL2 ambientProbe)
        {
            cmd.SetGlobalVector(_FogSHAr, new Vector4(ambientProbe[0, 3], ambientProbe[0, 1], ambientProbe[0, 2], ambientProbe[0, 0] - ambientProbe[0, 6]));
            cmd.SetGlobalVector(_FogSHAg, new Vector4(ambientProbe[1, 3], ambientProbe[1, 1], ambientProbe[1, 2], ambientProbe[1, 0] - ambientProbe[1, 6]));
            cmd.SetGlobalVector(_FogSHAb, new Vector4(ambientProbe[2, 3], ambientProbe[2, 1], ambientProbe[2, 2], ambientProbe[2, 0] - ambientProbe[2, 6]));
            cmd.SetGlobalVector(_FogSHBr, new Vector4(ambientProbe[0, 4], ambientProbe[0, 5], ambientProbe[0, 6] * 3.0f, ambientProbe[0, 7]));
            cmd.SetGlobalVector(_FogSHBg, new Vector4(ambientProbe[1, 4], ambientProbe[1, 5], ambientProbe[1, 6] * 3.0f, ambientProbe[1, 7]));
            cmd.SetGlobalVector(_FogSHBb, new Vector4(ambientProbe[2, 4], ambientProbe[2, 5], ambientProbe[2, 6] * 3.0f, ambientProbe[2, 7]));
            cmd.SetGlobalVector(_FogSHC, new Vector4(ambientProbe[0, 8], ambientProbe[1, 8], ambientProbe[2, 8], 1.0f));
        }

        static float ExtinctionFromMeanFreePath(float meanFreePath)
        {
            return 1.0f / meanFreePath;
        }

        static float ScaleHeightFromLayerDepth(float d)
        {
            // Exp[-d / H] = 0.001
            // -d / H = Log[0.001]
            // H = d / -Log[0.001]
            return d * 0.144765f;
        }

        static void CalculateActualScreenResolution(CommandBuffer cmd, RTHandle cameraTargetHandle)
        {
            Vector2Int viewportSize = cameraTargetHandle.GetScaledSize(cameraTargetHandle.rtHandleProperties.currentViewportSize);
            SetScreenResolution(cmd, viewportSize.x, viewportSize.y);
        }

        static void SetScreenResolution(CommandBuffer cmd, float width, float height)
        {
            cmd.SetGlobalVector(_ScreenResolution, new Vector4(width, height, 1.0f / width, 1.0f / height));
        }
        #endregion
    }

    /// <summary>
    /// This pass cleans up the global shader properties of physically based sky.
    /// </summary>
    private class PBSkyPostPass : ScriptableRenderPass
    {
        private const string profilerTag = "Cleanup Physically Based Sky";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public PhysicallyBasedSky pbrSky;

        private const string PHYSICALLY_BASED_SKY = "PHYSICALLY_BASED_SKY";
        private const string SKY_NOT_BAKING = "SKY_NOT_BAKING";

        private static readonly int _EnableAtmosphericScattering = Shader.PropertyToID("_EnableAtmosphericScattering");
        private static readonly int _FogEnabled = Shader.PropertyToID("_FogEnabled");
        private static readonly int _SkyTextureMipCounts = Shader.PropertyToID("_SkyTextureMipCounts");

        public PBSkyPostPass()
        {

        }

        #region Non Render Graph Pass
// Unity 6.4 removed the compatibility-mode ScriptableRenderPass callbacks and
// target configuration APIs. Use the Render Graph implementation below there.
#if !UNITY_6000_4_OR_NEWER
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {

        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                cmd.SetGlobalFloat(_EnableAtmosphericScattering, 0.0f);
                cmd.SetGlobalInteger(_FogEnabled, 0);
                cmd.SetGlobalFloat(_SkyTextureMipCounts, 0.0f);
                cmd.DisableShaderKeyword(PHYSICALLY_BASED_SKY);
                cmd.DisableShaderKeyword(SKY_NOT_BAKING);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
#endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass

        private class PassData
        {

        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            cmd.SetGlobalFloat(_EnableAtmosphericScattering, 0.0f);
            cmd.SetGlobalInteger(_FogEnabled, 0);
            cmd.SetGlobalFloat(_SkyTextureMipCounts, 0.0f);
            cmd.DisableShaderKeyword(PHYSICALLY_BASED_SKY);
            cmd.DisableShaderKeyword(SKY_NOT_BAKING);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                builder.AllowGlobalStateModification(true);

                // Assign the ExecutePass function to the render pass delegate, which will be called by the render graph when executing the pass
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {

        }
        #endregion
    }

    /// <summary>
    /// This pass updates the sky and environment reflection.
    /// </summary>
    private class AmbientProbePass : ScriptableRenderPass
    {
        private const string profilerTag = "Update Environment Reflection";
        private readonly ProfilingSampler m_ProfilingSampler = new ProfilingSampler(profilerTag);

        public VisualEnvironment visualEnvironment;
        public Material cloudsMaterial;
        public bool isPbrSky;

        private RTHandle probeColorHandle;
        private RTHandle skyColorHandle;

        internal Texture environmentTexture => probeColorHandle;

        // TODO: expose this property
        private static readonly int reflectionResolution = 128;

        private const string _GlossyEnvironmentCubeMap = "_GlossyEnvironmentCubeMap";
        private const string _SkyTexture = "_SkyTexture";
        private const string k_VolumetricCloudsEnvironmentPassName = "Volumetric Clouds - Update PBSky Environment";
        private const string k_LegacyVolumetricCloudsEnvironmentPassName = "Volumetric Clouds Update Environment";

        private const string VOLUMETRIC_CLOUDS = "VOLUMETRIC_CLOUDS";
        private const string STEREO_INSTANCING_ON = "STEREO_INSTANCING_ON";

        private static readonly int glossyEnvironmentCubeMap = Shader.PropertyToID(_GlossyEnvironmentCubeMap);
        private static readonly int skyTexture = Shader.PropertyToID(_SkyTexture);

        private static readonly int worldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        private static readonly int disableSunDisk = Shader.PropertyToID("_DisableSunDisk");
        //private static readonly int unity_MatrixVP = Shader.PropertyToID("unity_MatrixVP");
        private static readonly int unity_MatrixInvVP = Shader.PropertyToID("unity_MatrixInvVP");
        private static readonly int scaledScreenParams = Shader.PropertyToID("_ScaledScreenParams");
        private static readonly int screenSize = Shader.PropertyToID("_ScreenSize");
        private static readonly int skyTextureMipCounts = Shader.PropertyToID("_SkyTextureMipCounts");

        // Modified from CoreUtils.lookAtList to swap the directions of up and down faces
        private static readonly Matrix4x4 frontView = new Matrix4x4(float4(-1, 0, 0, 0), float4(0, -1, 0, 0), float4(0, 0, -1, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 backView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, -1, 0, 0), float4(0, 0, 1, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 upView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, 0, -1, 0), float4(0, -1, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 downView = new Matrix4x4(float4(1, 0, 0, 0), float4(0, 0, 1, 0), float4(0, 1, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 rightView = new Matrix4x4(float4(0, 0, -1, 0), float4(0, -1, 0, 0), float4(1, 0, 0, 0), float4(0, 0, 0, 1));
        private static readonly Matrix4x4 leftView = new Matrix4x4(float4(0, 0, 1, 0), float4(0, -1, 0, 0), float4(-1, 0, 0, 0), float4(0, 0, 0, 1));

        // Cubemap Order: right, left, up, down, back, front. (+X, -X, +Y, -Y, +Z, -Z)
        private static readonly Matrix4x4[] skyViews = { rightView, leftView, upView, downView, backView, frontView };

    #if UNITY_6000_0_OR_NEWER
        private readonly RendererListHandle[] rendererListHandles = new RendererListHandle[6];
    #endif
        private readonly Matrix4x4[] skyViewMatrices = new Matrix4x4[6];


        private static readonly Vector4 m_ScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

        private static readonly Matrix4x4 skyProjectionMatrix = Matrix4x4.Perspective(90.0f, 1.0f, 0.1f, 10.0f);
        private static readonly Vector4 skyViewScreenParams = new Vector4(reflectionResolution, reflectionResolution, 1.0f + rcp(reflectionResolution), 1.0f + rcp(reflectionResolution));
        private static readonly Vector4 skyViewScreenSize = new Vector4(reflectionResolution, reflectionResolution, rcp(reflectionResolution), rcp(reflectionResolution));

        public AmbientProbePass(Material material)
        {
            cloudsMaterial = material;
        }

        internal static Matrix4x4 GetSkyViewMatrix(int face)
        {
            Matrix4x4 viewMatrix = skyViews[face];
            return viewMatrix * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f));
        }

        private static int GetVolumetricCloudsEnvironmentPass(Material material)
        {
            if (material == null || !Shader.IsKeywordEnabled(VOLUMETRIC_CLOUDS))
                return -1;

            int pass = material.FindPass(k_VolumetricCloudsEnvironmentPassName);
            return pass >= 0 ? pass : material.FindPass(k_LegacyVolumetricCloudsEnvironmentPassName);
        }

        #region Non Render Graph Pass
// Unity 6.4 removed the compatibility-mode ScriptableRenderPass callbacks and
// target configuration APIs. Use the Render Graph implementation below there.
#if !UNITY_6000_4_OR_NEWER
    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.useMipMap = true;
            desc.autoGenerateMips = true;
            desc.width = reflectionResolution;
            desc.height = reflectionResolution;
            desc.dimension = TextureDimension.Cube;
            desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            desc.depthStencilFormat = GraphicsFormat.None;
            desc.depthBufferBits = 0;
            desc.useDynamicScale = false;

            bool hasVolumetricClouds = GetVolumetricCloudsEnvironmentPass(cloudsMaterial) >= 0;

        #if UNITY_6000_0_OR_NEWER
            RenderingUtils.ReAllocateHandleIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _GlossyEnvironmentCubeMap);
            if (hasVolumetricClouds)
                RenderingUtils.ReAllocateHandleIfNeeded(ref skyColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _SkyTexture);
        #else
            RenderingUtils.ReAllocateIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _GlossyEnvironmentCubeMap);
            if (hasVolumetricClouds)
                RenderingUtils.ReAllocateIfNeeded(ref skyColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _SkyTexture);
        #endif

            ConfigureTarget(probeColorHandle, probeColorHandle);
        }

    #if UNITY_6000_0_OR_NEWER
        [Obsolete]
    #endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();

            Camera camera = renderingData.cameraData.camera;
            var desc = renderingData.cameraData.cameraTargetDescriptor;

            bool isStereoEnabled = camera.stereoEnabled;

            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                if (isStereoEnabled)
                    cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

                float2 cameraResolution = float2(desc.width, desc.height);
                Vector3 cameraPositionWS = camera.transform.position;
                Vector4 cameraScreenSize = new Vector4(cameraResolution.x, cameraResolution.y, rcp(cameraResolution.x), rcp(cameraResolution.y));
                Vector4 cameraScreenParams = new Vector4(cameraResolution.x, cameraResolution.y, 1.0f + cameraScreenSize.z, 1.0f + cameraScreenSize.w);
                bool isDynamicAmbientMode = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic;

                Matrix4x4 skyMatrixP = GL.GetGPUProjectionMatrix(skyProjectionMatrix, true);

                cmd.SetGlobalVector(worldSpaceCameraPos, Vector3.zero);
                cmd.SetGlobalFloat(disableSunDisk, 1.0f);

                cmd.SetGlobalVector(scaledScreenParams, skyViewScreenParams);
                cmd.SetGlobalVector(screenSize, skyViewScreenSize);

                int volumetricCloudsEnvironmentPass = GetVolumetricCloudsEnvironmentPass(cloudsMaterial);
                bool hasVolumetricClouds = volumetricCloudsEnvironmentPass >= 0;

                for (int i = 0; i < 6; i++)
                {
                    CoreUtils.SetRenderTarget(cmd, hasVolumetricClouds ? skyColorHandle : probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);

                    //var lookAt = Matrix4x4.LookAt(Vector3.zero, CoreUtils.lookAtList[i], CoreUtils.upVectorList[i]);
                    //Matrix4x4 viewMatrix = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...

                    // Need to scale -1.0 on Z to match what is being done in the camera.worldToCameraMatrix API.
                    Matrix4x4 viewMatrix = GetSkyViewMatrix(i);
                    skyViewMatrices[i] = viewMatrix;

                    Matrix4x4 skyMatrixVP = skyMatrixP * skyViewMatrices[i];

                    // Camera matrices for skybox rendering
                    cmd.SetViewMatrix(skyViewMatrices[i]);
                    //cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                    cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                    if (isPbrSky)
                    {
                        Blitter.BlitTexture(cmd, m_ScaleBias, RenderSettings.skybox, pass: 1);
                    }
                    else
                    {
                        RendererList rendererList = context.CreateSkyboxRendererList(camera, skyProjectionMatrix, skyViewMatrices[i]);
                        cmd.DrawRendererList(rendererList);
                    }
                }

                cmd.SetGlobalTexture(skyTexture, hasVolumetricClouds ? skyColorHandle : probeColorHandle);
                int skyTextureMips = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic ?
                    hasVolumetricClouds ? skyColorHandle.rt.mipmapCount : probeColorHandle.rt.mipmapCount : 0;
                cmd.SetGlobalFloat(skyTextureMipCounts, skyTextureMips);

                if (hasVolumetricClouds)
                {
                    // We split the rendering into 2 loops to avoid calling CopyTexture() multiple times, which can be slow on the GPU side.
                    cmd.CopyTexture(skyColorHandle, probeColorHandle);

                    for (int i = 0; i < 6; i++)
                    {
                        Matrix4x4 skyMatrixVP = skyMatrixP * skyViewMatrices[i];

                        // Camera matrices for skybox rendering
                        cmd.SetViewMatrix(skyViewMatrices[i]);
                        //cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                        cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                        CoreUtils.SetRenderTarget(cmd, probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);
                        Blitter.BlitTexture(cmd, m_ScaleBias, cloudsMaterial, pass: volumetricCloudsEnvironmentPass);
                    }
                }

                cmd.SetGlobalTexture(glossyEnvironmentCubeMap, probeColorHandle);
                RenderSettings.defaultReflectionMode = isDynamicAmbientMode ? DefaultReflectionMode.Custom : RenderSettings.defaultReflectionMode;
                RenderSettings.customReflectionTexture = isDynamicAmbientMode ? probeColorHandle : null;
                cmd.SetGlobalVector(worldSpaceCameraPos, cameraPositionWS);
                cmd.SetGlobalFloat(disableSunDisk, 0.0f);

                Matrix4x4 matrixVP = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;

                // Camera matrices for objects rendering
                cmd.SetViewMatrix(camera.worldToCameraMatrix);
                //cmd.SetGlobalMatrix(unity_MatrixVP, matrixVP);
                cmd.SetGlobalMatrix(unity_MatrixInvVP, matrixVP.inverse);
                cmd.SetGlobalVector(scaledScreenParams, cameraScreenParams);
                cmd.SetGlobalVector(screenSize, cameraScreenSize);

                if (isStereoEnabled)
                    cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            
            CommandBufferPool.Release(cmd);
        }
#endif
        #endregion

    #if UNITY_6000_0_OR_NEWER
        #region Render Graph Pass
        private class PassData
        {
            internal Material cloudsMaterial;

            internal TextureHandle probeColorHandle;
            internal TextureHandle skyColorHandle;

            internal Vector3 cameraPositionWS;
            internal Vector4 cameraScreenParams;
            internal Vector4 cameraScreenSize;
            internal Matrix4x4 worldToCameraMatrix;
            internal Matrix4x4 projectionMatrix;

            internal RendererListHandle[] rendererListHandles;
            internal Matrix4x4[] skyViewMatrices;
            internal Matrix4x4 skyProjectionMatrix;

            internal bool isDynamicAmbientMode;
            internal bool isPbrSky;
            internal bool hasVolumetricClouds;
            internal bool isStereoEnabled;
            internal int volumetricCloudsEnvironmentPass;

            internal int skyTextureMipCounts;
        }

        // This static method is used to execute the pass and passed as the RenderFunc delegate to the RenderGraph render pass
        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

            if (data.isStereoEnabled)
                cmd.DisableShaderKeyword(STEREO_INSTANCING_ON);

            context.cmd.SetGlobalVector(worldSpaceCameraPos, Vector3.zero);
            context.cmd.SetGlobalFloat(disableSunDisk, 1.0f);

            context.cmd.SetGlobalVector(scaledScreenParams, skyViewScreenParams);
            context.cmd.SetGlobalVector(screenSize, skyViewScreenSize);

            Matrix4x4 skyMatrixP = GL.GetGPUProjectionMatrix(data.skyProjectionMatrix, true);

            for (int i = 0; i < 6; i++)
            {
                CoreUtils.SetRenderTarget(cmd, data.hasVolumetricClouds ? data.skyColorHandle : data.probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);

                Matrix4x4 skyMatrixVP = skyMatrixP * data.skyViewMatrices[i];

                // Camera matrices for skybox rendering
                cmd.SetViewMatrix(data.skyViewMatrices[i]);
                //cmd.SetProjectionMatrix(skyMatrixP);
                //context.cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                context.cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);
                
                if (data.isPbrSky)
                {
                    Blitter.BlitTexture(cmd, m_ScaleBias, RenderSettings.skybox, pass: 1);
                }
                else
                {
                    context.cmd.DrawRendererList(data.rendererListHandles[i]);
                }
            }

            cmd.SetGlobalFloat(skyTextureMipCounts, data.skyTextureMipCounts);

            if (data.hasVolumetricClouds)
            {
                // We split the rendering into 2 loops to avoid calling CopyTexture() multiple times, which can be slow on the GPU side.
                cmd.CopyTexture(data.skyColorHandle, data.probeColorHandle);

                for (int i = 0; i < 6; i++)
                {
                    Matrix4x4 skyMatrixVP = skyMatrixP * data.skyViewMatrices[i];
                    // Camera matrices for skybox rendering
                    cmd.SetViewMatrix(data.skyViewMatrices[i]);
                    //cmd.SetProjectionMatrix(skyMatrixP);
                    //context.cmd.SetGlobalMatrix(unity_MatrixVP, skyMatrixVP);
                    context.cmd.SetGlobalMatrix(unity_MatrixInvVP, skyMatrixVP.inverse);

                    CoreUtils.SetRenderTarget(cmd, data.probeColorHandle, ClearFlag.None, 0, (CubemapFace)i);
                    Blitter.BlitTexture(cmd, m_ScaleBias, data.cloudsMaterial, pass: data.volumetricCloudsEnvironmentPass);
                }
            }

            RenderSettings.defaultReflectionMode = data.isDynamicAmbientMode ? DefaultReflectionMode.Custom : RenderSettings.defaultReflectionMode;
            RenderSettings.customReflectionTexture = data.isDynamicAmbientMode ? data.probeColorHandle : null;
            context.cmd.SetGlobalVector(worldSpaceCameraPos, data.cameraPositionWS);
            context.cmd.SetGlobalFloat(disableSunDisk, 0.0f);

            Matrix4x4 matrixVP = GL.GetGPUProjectionMatrix(data.projectionMatrix, true) * data.worldToCameraMatrix;

            // Camera matrices for objects rendering
            cmd.SetViewMatrix(data.worldToCameraMatrix);
            //cmd.SetProjectionMatrix(data.projectionMatrix);
            //context.cmd.SetGlobalMatrix(unity_MatrixVP, matrixVP);
            context.cmd.SetGlobalMatrix(unity_MatrixInvVP, matrixVP.inverse);
            context.cmd.SetGlobalVector(scaledScreenParams, data.cameraScreenParams);
            context.cmd.SetGlobalVector(screenSize, data.cameraScreenSize);

            if (data.isStereoEnabled)
                cmd.EnableShaderKeyword(STEREO_INSTANCING_ON);
        }

        // This is where the renderGraph handle can be accessed.
        // Each ScriptableRenderPass can use the RenderGraph handle to add multiple render passes to the render graph
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // add an unsafe render pass to the render graph, specifying the name and the data type that will be passed to the ExecutePass function
            using (var builder = renderGraph.AddUnsafePass<PassData>(profilerTag, out var passData))
            {
                // UniversalResourceData contains all the texture handles used by the renderer, including the active color and depth textures
                // The active color and depth textures are the main color and depth buffers that the camera renders into
                UniversalRenderingData universalRenderingData = frameData.Get<UniversalRenderingData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                int volumetricCloudsEnvironmentPass = GetVolumetricCloudsEnvironmentPass(cloudsMaterial);
                bool hasVolumetricClouds = volumetricCloudsEnvironmentPass >= 0;

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;

                float2 cameraResolution = float2(desc.width, desc.height);
                
                desc.msaaSamples = 1;
                desc.useMipMap = true;
                desc.autoGenerateMips = true;
                desc.width = reflectionResolution;
                desc.height = reflectionResolution;
                desc.dimension = TextureDimension.Cube;
                desc.graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.depthBufferBits = 0;
                desc.useDynamicScale = false;

                RenderingUtils.ReAllocateHandleIfNeeded(ref probeColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _GlossyEnvironmentCubeMap);
                TextureHandle probeColorTextureHandle = renderGraph.ImportTexture(probeColorHandle);
                passData.probeColorHandle = probeColorTextureHandle;

                if (hasVolumetricClouds)
                {
                    RenderingUtils.ReAllocateHandleIfNeeded(ref skyColorHandle, desc, FilterMode.Trilinear, TextureWrapMode.Clamp, name: _SkyTexture);
                    TextureHandle skyColorTextureHandle = renderGraph.ImportTexture(skyColorHandle);
                    passData.skyColorHandle = skyColorTextureHandle;
                }

                passData.skyTextureMipCounts = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic ?
                    hasVolumetricClouds ? skyColorHandle.rt.mipmapCount : probeColorHandle.rt.mipmapCount : 0;

                passData.cloudsMaterial = cloudsMaterial;

                for (int i = 0; i < 6; i++)
                {
                    //var lookAt = Matrix4x4.LookAt(Vector3.zero, CoreUtils.lookAtList[i], CoreUtils.upVectorList[i]);
                    //Matrix4x4 viewMatrix = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...

                    // Need to scale -1.0 on Z to match what is being done in the camera.worldToCameraMatrix API.
                    Matrix4x4 viewMatrix = GetSkyViewMatrix(i);
                    skyViewMatrices[i] = viewMatrix;
                    rendererListHandles[i] = renderGraph.CreateSkyboxRendererList(cameraData.camera, skyProjectionMatrix, viewMatrix);
                    builder.UseRendererList(rendererListHandles[i]);
                }

                // Fill up the passData with the data needed by the pass
                passData.rendererListHandles = rendererListHandles;
                passData.skyViewMatrices = skyViewMatrices;
                passData.skyProjectionMatrix = skyProjectionMatrix;
                passData.cloudsMaterial = cloudsMaterial;
                passData.cameraPositionWS = cameraData.camera.transform.position;
                passData.cameraScreenSize = new Vector4(cameraResolution.x, cameraResolution.y, rcp(cameraResolution.x), rcp(cameraResolution.y));
                passData.cameraScreenParams = new Vector4(cameraResolution.x, cameraResolution.y, 1.0f + passData.cameraScreenSize.z, 1.0f + passData.cameraScreenSize.w);
                passData.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                passData.projectionMatrix = cameraData.camera.projectionMatrix;
                passData.isDynamicAmbientMode = visualEnvironment.skyAmbientMode.value == VisualEnvironment.SkyAmbientMode.Dynamic;
                passData.isPbrSky = isPbrSky;
                passData.hasVolumetricClouds = hasVolumetricClouds;
                passData.isStereoEnabled = cameraData.camera.stereoEnabled;
                passData.volumetricCloudsEnvironmentPass = volumetricCloudsEnvironmentPass;

                // UnsafePasses don't setup the outputs using UseTextureFragment/UseTextureFragmentDepth, you should specify your writes with UseTexture instead
                // Cloud blending loads the current probe contents before writing the composited result.
                builder.UseTexture(passData.probeColorHandle, AccessFlags.ReadWrite);

                if (hasVolumetricClouds)
                    builder.UseTexture(passData.skyColorHandle, AccessFlags.ReadWrite);

                builder.SetGlobalTextureAfterPass(
                    hasVolumetricClouds ? passData.skyColorHandle : passData.probeColorHandle,
                    skyTexture);
                builder.SetGlobalTextureAfterPass(passData.probeColorHandle, glossyEnvironmentCubeMap);

                // Sky and cloud materials sample LUTs published by earlier RenderGraph passes.
                builder.UseAllGlobalTextures(true);

                // Shader keyword changes are considered as global state modifications
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
        #endregion
    #endif

        #region Shared
        public void Dispose()
        {
            probeColorHandle?.Release();
            skyColorHandle?.Release();
        }

        #endregion
    }

}
