using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Miasma
{
    /// <summary>
    /// Volumetric fog that fills the space, worked out on a froxel grid: the view frustum is cut into a small 3D grid
    /// (x and y follow the screen, z slices grow with distance). Once per froxel, a compute shader works out how much
    /// fog is there (height fog broken into drifting patches, plus <see cref="FogVolume"/>s) and the light it scatters
    /// towards the camera (the main light in and out of its shadows, which makes shafts; other lights; the ambient
    /// light), blended with last frame's so the fog stays smooth. A second pass adds it up front to back, and each pixel
    /// then just looks up the fog at its distance. Beyond the grid the height fog carries on, worked out exactly, so
    /// distant things and the sky keep fading out.
    ///
    /// The fog is laid over the opaque frame before transparent objects and post-processing (so a grade or print after
    /// it sees the fog too).
    ///
    /// Add it to the renderer (Renderer > Add Renderer Feature > Miasma Feature) and add a Miasma Fog override to a
    /// Volume.
    /// </summary>
    [DisallowMultipleRendererFeature("Miasma Fog")]
    public sealed class MiasmaFeature : ScriptableRendererFeature
    {
        public enum Quality
        {
            [Tooltip("96 x 54 x 48 froxels")] Low,
            [Tooltip("160 x 90 x 64 froxels")] Medium,
            [Tooltip("240 x 135 x 96 froxels")] High,
        }

        public enum Layer
        {
            [Tooltip("Painted over the finished frame (after post-processing and any print) as watercolour washes.")] Painted,
            [Tooltip("Blended into the scene before transparents and post-processing, like real fog.")] InScene,
        }

        [Tooltip("Where the fog goes: painted over the finished frame as watercolour, or into the scene like real fog.")]
        public Layer layer = Layer.Painted;

        public enum Method
        {
            [Tooltip("Each pixel's view ray is marched through the fog (at half resolution). Hides the background fully and evenly; costs more with more steps.")] Marched,
            [Tooltip("Fog worked out once per cell of a 3D grid and looked up per pixel. Cheaper, softer, smoothed over frames.")] Froxels,
        }

        [Tooltip("How the fog is worked out.")]
        public Method method = Method.Marched;

        [Tooltip("Marched: samples along each ray. More = smoother fog and sharper shafts, slower.")]
        [Range(8, 128)] public int steps = 24;

        [Tooltip("Marched: march at half resolution (much faster; the fog is soft anyway).")]
        public bool halfResolution = true;

        [Tooltip("Froxels: size of the grid. Higher = sharper shafts and edges, slower.")]
        public Quality quality = Quality.Medium;

        [Tooltip("How much of last frame's fog is kept (0 = none, flickers; 0.9 = smooth, slow to follow fast changes).")]
        [Range(0f, 0.97f)] public float smoothing = 0.9f;

        [Tooltip("Light the fog with additional lights (torches, lanterns). The 32 brightest are used.")]
        public bool otherLights = true;

        [Tooltip("Fog in the Scene view too.")]
        public bool sceneView = true;

        [Tooltip("Share how much of each pixel shows through the fog (global texture _ChromoVisibility, with _ChromoVisibilityOn = 1), so a print effect such as Chromolithograph can fade its lines and texture in the fog.")]
        public bool shareVisibility = true;

        [SerializeField, HideInInspector] Shader shader;
        [SerializeField, HideInInspector] ComputeShader compute;

        Material material;
        FogPass pass;
        PaintPass paint;

        public override void Create()
        {
            if (shader == null) shader = Shader.Find("Hidden/Miasma");
            if (compute == null) compute = Resources.Load<ComputeShader>("MiasmaFroxels");
            pass?.Release();
            pass = new FogPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
            // After post-processing and anything else at that point (a print) that comes earlier in the renderer's list.
            paint = new PaintPass { renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing + 1 };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType type = renderingData.cameraData.cameraType;
            if (type != CameraType.Game && !(sceneView && type == CameraType.SceneView)) return;
            if (shader == null || compute == null || !SystemInfo.supportsComputeShaders) return;
            if (material == null) material = CoreUtils.CreateEngineMaterial(shader);

            MiasmaFog fog = VolumeManager.instance.stack.GetComponent<MiasmaFog>();
            bool any = fog != null && fog.IsActive();
            if (!any)
            {
                foreach (FogVolume v in FogVolume.All)
                    if (v.density > 0f) { any = true; break; }
            }
            if (!any) return;

            pass.Setup(material, compute, fog, this);
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(pass);
            if (layer == Layer.Painted)
            {
                paint.Setup(material, fog);
                renderer.EnqueuePass(paint);
            }
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Release();
            CoreUtils.Destroy(material);
            material = null;
        }

        static class Ids
        {
            public static readonly int Injected = Shader.PropertyToID("_MiasmaInjected");
            public static readonly int History = Shader.PropertyToID("_MiasmaHistory");
            public static readonly int Integrated = Shader.PropertyToID("_MiasmaIntegrated");
            public static readonly int InjectedRead = Shader.PropertyToID("_MiasmaInjectedRead");
            public static readonly int IntegratedRead = Shader.PropertyToID("_MiasmaIntegratedRead");
            public static readonly int Grid = Shader.PropertyToID("_MiasmaGrid");
            public static readonly int Slices = Shader.PropertyToID("_MiasmaSlices");
            public static readonly int InvViewProj = Shader.PropertyToID("_MiasmaInvViewProj");
            public static readonly int PrevViewProj = Shader.PropertyToID("_MiasmaPrevViewProj");
            public static readonly int Camera = Shader.PropertyToID("_MiasmaCamera");
            public static readonly int PrevCamera = Shader.PropertyToID("_MiasmaPrevCamera");
            public static readonly int Density = Shader.PropertyToID("_MiasmaDensity");
            public static readonly int Noise = Shader.PropertyToID("_MiasmaNoise");
            public static readonly int Time = Shader.PropertyToID("_MiasmaTime");
            public static readonly int Light = Shader.PropertyToID("_MiasmaLight");
            public static readonly int Albedo = Shader.PropertyToID("_MiasmaAlbedo");
            public static readonly int MainDirection = Shader.PropertyToID("_MiasmaMainDirection");
            public static readonly int MainColour = Shader.PropertyToID("_MiasmaMainColour");
            public static readonly int Ambient = Shader.PropertyToID("_MiasmaAmbient");
            public static readonly int LightCount = Shader.PropertyToID("_MiasmaLightCount");
            public static readonly int LightPositions = Shader.PropertyToID("_MiasmaLightPositions");
            public static readonly int LightColours = Shader.PropertyToID("_MiasmaLightColours");
            public static readonly int LightSpots = Shader.PropertyToID("_MiasmaLightSpots");
            public static readonly int LightSpotAngles = Shader.PropertyToID("_MiasmaLightSpotAngles");
            public static readonly int VolumeCount = Shader.PropertyToID("_MiasmaVolumeCount");
            public static readonly int VolumeMatrices = Shader.PropertyToID("_MiasmaVolumeMatrices");
            public static readonly int VolumeParams = Shader.PropertyToID("_MiasmaVolumeParams");
            public static readonly int BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
            public static readonly int Visibility = Shader.PropertyToID("_ChromoVisibility");
            public static readonly int Layer = Shader.PropertyToID("_MiasmaLayer");
            public static readonly int Paint = Shader.PropertyToID("_MiasmaPaint");
            public static readonly int Paint2 = Shader.PropertyToID("_MiasmaPaint2");
            public static readonly int Pigment = Shader.PropertyToID("_MiasmaPigment");
            public static readonly int Morph = Shader.PropertyToID("_MiasmaMorph");
            public static readonly int MethodId = Shader.PropertyToID("_MiasmaMethod");
            public static readonly int Marched = Shader.PropertyToID("_MiasmaMarched");
            public static readonly int Range = Shader.PropertyToID("_MiasmaRange");
            public static readonly int VisibilityOn = Shader.PropertyToID("_ChromoVisibilityOn");
        }

        const int MaxLights = 32;
        const float Near = 0.3f;

        /// <summary>Per camera: last frame's froxels (to blend with) and where the camera was.</summary>
        sealed class CameraState
        {
            public readonly RTHandle[] history = new RTHandle[2];
            public int current;
            public Vector3Int size;
            public Matrix4x4 prevViewProj;
            public Vector3 prevPosition;
            public float prevFar;
            public bool valid;
            public int frame;

            public void Release()
            {
                for (int i = 0; i < 2; i++)
                {
                    history[i]?.Release();
                    history[i] = null;
                }
                valid = false;
            }
        }

        /// <summary>The fog layer, handed from the fog pass to the paint pass within a frame.</summary>
        sealed class LayerData : ContextItem
        {
            public TextureHandle layer;
            public bool valid;

            public override void Reset()
            {
                layer = TextureHandle.nullHandle;
                valid = false;
            }
        }

        /// <summary>Paints the fog layer over the finished frame as watercolour.</summary>
        sealed class PaintPass : ScriptableRenderPass
        {
            Material material;
            MiasmaFog fog;
            readonly MaterialPropertyBlock block = new();

            class PaintData
            {
                public Material material;
                public MaterialPropertyBlock block;
                public TextureHandle layer;
            }

            float morphPhase, morphTime;

            public PaintPass() => profilingSampler = new ProfilingSampler("Miasma Paint");

            public void Setup(Material material, MiasmaFog fog)
            {
                this.material = material;
                this.fog = fog;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<LayerData>()) return;
                LayerData layerData = frameData.Get<LayerData>();
                if (!layerData.valid) return;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer) return;

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                block.SetVector(Ids.Paint, fog != null
                    ? new Vector4(fog.washSteps.value, fog.blooms.value, fog.granulation.value, fog.wetEdges.value)
                    : new Vector4(0.5f, 0.6f, 0.5f, 0.5f));
                block.SetVector(Ids.Paint2, new Vector4(fog != null ? fog.pigmentSize.value : 1f, fog != null ? fog.paintExposure.value : 1f,
                    1f / Mathf.Max(1, desc.width), 1f / Mathf.Max(1, desc.height)));
                // Morph phase: time over seconds per shape, so changing the speed only changes how fast it moves on.
                float seconds = fog != null ? fog.bloomMorphSeconds.value : 40f;
                float now = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
                if (seconds > 0f) morphPhase += Mathf.Clamp(now - morphTime, 0f, 0.5f) / seconds;
                morphTime = now;
                block.SetVector(Ids.Morph, new Vector4(morphPhase % 4096f, 0f, 0f, 0f));
                Color pigment = fog != null ? fog.pigment.value : Color.clear;
                block.SetVector(Ids.Pigment, new Vector4(pigment.r, pigment.g, pigment.b, pigment.a));

                using (var builder = renderGraph.AddRasterRenderPass<PaintData>("Miasma Paint", out PaintData data, profilingSampler))
                {
                    data.material = material;
                    data.block = block;
                    data.layer = layerData.layer;
                    builder.UseTexture(layerData.layer, AccessFlags.Read);
                    if (resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderFunc((PaintData d, RasterGraphContext context) =>
                    {
                        d.block.SetTexture(Ids.Layer, (RTHandle)d.layer);
                        d.block.SetVector(Ids.BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                        context.cmd.DrawProcedural(Matrix4x4.identity, d.material, 2, MeshTopology.Triangles, 3, 1, d.block);
                    });
                }
            }
        }

        sealed class FogPass : ScriptableRenderPass
        {
            Material material;
            ComputeShader compute;
            MiasmaFog fog;
            MiasmaFeature feature;
            int injectKernel = -1, integrateKernel = -1;

            readonly Dictionary<Camera, CameraState> states = new();
            readonly MaterialPropertyBlock block = new();
            readonly Matrix4x4[] volumeMatrices = new Matrix4x4[FogVolume.Max];
            readonly Vector4[] volumeParams = new Vector4[FogVolume.Max];
            readonly Vector4[] lightPositions = new Vector4[MaxLights];
            readonly Vector4[] lightColours = new Vector4[MaxLights];
            readonly Vector4[] lightSpots = new Vector4[MaxLights];
            readonly Vector4[] lightSpotAngles = new Vector4[MaxLights];
            readonly List<FogVolume> nearest = new();
            readonly List<(float brightness, int index)> lightOrder = new();
            readonly Vector3[] shDirections = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            readonly Color[] shResults = new Color[6];

            // Shared values, copied into each pass's data.
            Vector4 grid, slices, density, noise, time, light, albedo, mainDirection, mainColour, ambient, cameraParams, prevCameraParams;
            Matrix4x4 invViewProj, prevViewProj;
            int lightCount, volumeCount;

            class InjectData
            {
                public FogPass owner;
                public TextureHandle injected, history;
                public Vector3Int size;
            }

            class IntegrateData
            {
                public FogPass owner;
                public TextureHandle injected, integrated;
                public Vector3Int size;
            }

            class ApplyData
            {
                public Material material;
                public MaterialPropertyBlock block;
                public TextureHandle integrated;
                public bool share;
                public bool marched;
                public int shaderPass;
            }

            class MarchData
            {
                public Material material;
                public MaterialPropertyBlock block;
            }

            public FogPass() => profilingSampler = new ProfilingSampler("Miasma Fog");

            public void Setup(Material material, ComputeShader compute, MiasmaFog fog, MiasmaFeature feature)
            {
                this.material = material;
                this.fog = fog;
                this.feature = feature;
                if (this.compute != compute)
                {
                    this.compute = compute;
                    injectKernel = compute.FindKernel("Inject");
                    integrateKernel = compute.FindKernel("Integrate");
                }
            }

            public void Release()
            {
                foreach (var s in states.Values) s.Release();
                states.Clear();
            }

            static Vector3Int GridSize(Quality quality, float aspect)
            {
                int h = quality switch { Quality.Low => 54, Quality.High => 135, _ => 90 };
                int d = quality switch { Quality.Low => 48, Quality.High => 96, _ => 64 };
                int w = Mathf.Clamp(Mathf.RoundToInt(h * aspect), 8, 512);
                return new Vector3Int(w, h, d);
            }

            CameraState StateFor(Camera camera, Vector3Int size)
            {
                if (states.Count > 16)
                {
                    var dead = new List<Camera>();
                    foreach (var kv in states) if (kv.Key == null) { kv.Value.Release(); dead.Add(kv.Key); }
                    foreach (var c in dead) states.Remove(c);
                }
                if (!states.TryGetValue(camera, out var state))
                {
                    state = new CameraState();
                    states[camera] = state;
                }
                if (state.size != size || state.history[0] == null)
                {
                    state.Release();
                    for (int i = 0; i < 2; i++)
                    {
                        state.history[i] = RTHandles.Alloc(size.x, size.y, slices: size.z, dimension: TextureDimension.Tex3D,
                            colorFormat: GraphicsFormat.R16G16B16A16_SFloat, filterMode: FilterMode.Bilinear,
                            wrapMode: TextureWrapMode.Clamp, enableRandomWrite: true, name: "_MiasmaHistory" + i);
                    }
                    state.size = size;
                    state.valid = false;
                }
                return state;
            }

            static float Halton(int index, int b)
            {
                float f = 1f, r = 0f;
                index += 1;
                while (index > 0)
                {
                    f /= b;
                    r += f * (index % b);
                    index /= b;
                }
                return r;
            }

            void Fill(UniversalCameraData cameraData, UniversalLightData lightData, bool shadowsAvailable, CameraState state)
            {
                Camera camera = cameraData.camera;
                Vector3Int size = state.size;
                float far = fog != null ? fog.distance.value : 100f;
                grid = new Vector4(size.x, size.y, size.z, state.frame);
                slices = new Vector4(Near, far, Mathf.Log(far / Near), Halton(state.frame & 1023, 2));

                Matrix4x4 viewProj = cameraData.GetProjectionMatrix() * cameraData.GetViewMatrix(); // OpenGL style
                invViewProj = viewProj.inverse;
                Vector3 position = camera.transform.position;
                bool cut = !state.valid || (position - state.prevPosition).sqrMagnitude > 25f || !Mathf.Approximately(far, state.prevFar);
                float historyWeight = cut ? 0f : feature.smoothing;
                cameraParams = new Vector4(position.x, position.y, position.z, historyWeight);
                prevViewProj = state.valid ? state.prevViewProj : viewProj;
                prevCameraParams = state.valid ? (Vector4)state.prevPosition : (Vector4)position;

                density = new Vector4(fog != null ? fog.density.value : 0f, fog != null ? fog.baseHeight.value : 0f,
                    fog != null ? fog.heightFalloff.value : 0f, fog != null ? fog.noise.value : 0f);
                Vector3 wind = fog != null ? fog.wind.value : Vector3.zero;
                noise = new Vector4(1f / Mathf.Max(0.1f, fog != null ? fog.noiseSize.value : 8f), wind.x, wind.y, wind.z);
                time = new Vector4(Application.isPlaying ? UnityEngine.Time.time : UnityEngine.Time.realtimeSinceStartup, 0f, 0f, 0f);
                light = new Vector4(fog != null ? fog.forwardScattering.value : 0f, fog != null ? fog.mainLight.value : 1f,
                    fog != null && feature.otherLights ? fog.otherLights.value : 0f, fog != null ? fog.ambient.value : 1f);
                Color a = fog != null ? fog.albedo.value : Color.white;
                // Cimmerian: the fog swallows light instead of glowing with it (scatters less, hides the same).
                float keep = 1f - (fog != null ? fog.cimmerian.value : 0f);
                albedo = new Vector4(a.r * keep, a.g * keep, a.b * keep, 0f);

                // Ambient: the ambient probe, averaged over every direction.
                RenderSettings.ambientProbe.Evaluate(shDirections, shResults);
                Color sum = Color.black;
                foreach (Color c in shResults) sum += c;
                sum /= shResults.Length;
                ambient = new Vector4(Mathf.Max(0f, sum.r), Mathf.Max(0f, sum.g), Mathf.Max(0f, sum.b), 0f);

                // Lights.
                NativeArray<VisibleLight> visible = lightData.visibleLights;
                int mainIndex = lightData.mainLightIndex;
                mainColour = Vector4.zero;
                mainDirection = new Vector4(0f, 1f, 0f, 0f);
                if (mainIndex >= 0 && mainIndex < visible.Length)
                {
                    VisibleLight main = visible[mainIndex];
                    Vector3 towards = -(Vector3)main.localToWorldMatrix.GetColumn(2);
                    bool shadows = shadowsAvailable && main.light != null && main.light.shadows != LightShadows.None;
                    mainDirection = new Vector4(towards.x, towards.y, towards.z, shadows ? 1f : 0f);
                    Color c = main.finalColor;
                    mainColour = new Vector4(c.r, c.g, c.b, 0f);
                }

                lightOrder.Clear();
                if (feature.otherLights)
                {
                    for (int i = 0; i < visible.Length; i++)
                    {
                        if (i == mainIndex) continue;
                        VisibleLight v = visible[i];
                        if (v.lightType != LightType.Point && v.lightType != LightType.Spot) continue;
                        // How much it can light the fog near the camera: bright, wide lights close by first.
                        Color c = v.finalColor;
                        Vector3 lp = v.localToWorldMatrix.GetColumn(3);
                        float r = Mathf.Max(0.01f, v.range);
                        float d2 = Mathf.Max((lp - position).sqrMagnitude - r * r, 1f);
                        lightOrder.Add((c.maxColorComponent * r * r / d2, i));
                    }
                    lightOrder.Sort((x, y) => y.brightness.CompareTo(x.brightness));
                }
                lightCount = Mathf.Min(lightOrder.Count, MaxLights);
                // Past the limit, the weakest kept lights fade towards the first one left out, so a light never pops
                // in or out of the fog as the camera moves.
                float lightCut = lightOrder.Count > MaxLights ? lightOrder[MaxLights].brightness : 0f;
                for (int n = 0; n < lightCount; n++)
                {
                    VisibleLight v = visible[lightOrder[n].index];
                    Vector3 p = v.localToWorldMatrix.GetColumn(3);
                    float range = Mathf.Max(0.01f, v.range);
                    lightPositions[n] = new Vector4(p.x, p.y, p.z, 1f / (range * range));
                    Color c = v.finalColor;
                    if (lightCut > 0f) c *= Mathf.Clamp01((lightOrder[n].brightness - lightCut) / lightCut);
                    bool spot = v.lightType == LightType.Spot;
                    lightColours[n] = new Vector4(c.r, c.g, c.b, spot ? 1f : 0f);
                    Vector3 forward = v.localToWorldMatrix.GetColumn(2);
                    lightSpots[n] = new Vector4(forward.x, forward.y, forward.z, 0f);
                    if (spot)
                    {
                        float outer = Mathf.Cos(Mathf.Deg2Rad * v.spotAngle * 0.5f);
                        float innerAngle = v.light != null ? v.light.innerSpotAngle : v.spotAngle * 0.8f;
                        float inner = Mathf.Cos(Mathf.Deg2Rad * innerAngle * 0.5f);
                        float scale = 1f / Mathf.Max(0.001f, inner - outer);
                        lightSpotAngles[n] = new Vector4(scale, -outer * scale, 0f, 0f);
                    }
                    else
                    {
                        lightSpotAngles[n] = Vector4.zero;
                    }
                }

                // The nearest fog volumes.
                nearest.Clear();
                foreach (FogVolume v in FogVolume.All)
                    if (v != null && v.isActiveAndEnabled && v.density != 0f) nearest.Add(v);
                if (nearest.Count > FogVolume.Max)
                    nearest.Sort((x, y) => (x.transform.position - position).sqrMagnitude.CompareTo((y.transform.position - position).sqrMagnitude));
                volumeCount = Mathf.Min(nearest.Count, FogVolume.Max);
                for (int i = 0; i < FogVolume.Max; i++)
                {
                    if (i < volumeCount)
                    {
                        volumeMatrices[i] = nearest[i].transform.worldToLocalMatrix;
                        volumeParams[i] = new Vector4(nearest[i].density, Mathf.Max(0.0001f, nearest[i].edge), nearest[i].noise, 0f);
                    }
                    else
                    {
                        volumeMatrices[i] = Matrix4x4.identity;
                        volumeParams[i] = Vector4.zero;
                    }
                }

                // For the next frame.
                state.prevViewProj = viewProj;
                state.prevPosition = position;
                state.prevFar = far;
                state.valid = true;
                state.frame++;

                block.SetVector(Ids.Grid, grid);
                block.SetVector(Ids.Slices, slices);
                block.SetVector(Ids.Density, density);
                block.SetVector(Ids.Light, light);
                block.SetVector(Ids.Albedo, albedo);
                block.SetVector(Ids.MainDirection, mainDirection);
                block.SetVector(Ids.MainColour, mainColour);
                block.SetVector(Ids.Ambient, ambient);
                block.SetVector(Ids.Noise, noise);
                block.SetVector(Ids.Range, new Vector4(slices.y, feature.steps, state.frame & 63, 0f));
                block.SetFloat(Ids.VolumeCount, volumeCount);
                block.SetMatrixArray(Ids.VolumeMatrices, volumeMatrices);
                block.SetVectorArray(Ids.VolumeParams, volumeParams);
            }

            void SetComputeParams(ComputeCommandBuffer cmd, int kernel)
            {
                cmd.SetComputeVectorParam(compute, Ids.Grid, grid);
                cmd.SetComputeVectorParam(compute, Ids.Slices, slices);
                cmd.SetComputeMatrixParam(compute, Ids.InvViewProj, invViewProj);
                cmd.SetComputeMatrixParam(compute, Ids.PrevViewProj, prevViewProj);
                cmd.SetComputeVectorParam(compute, Ids.Camera, cameraParams);
                cmd.SetComputeVectorParam(compute, Ids.PrevCamera, prevCameraParams);
                cmd.SetComputeVectorParam(compute, Ids.Density, density);
                cmd.SetComputeVectorParam(compute, Ids.Noise, noise);
                cmd.SetComputeVectorParam(compute, Ids.Time, time);
                cmd.SetComputeVectorParam(compute, Ids.Light, light);
                cmd.SetComputeVectorParam(compute, Ids.Albedo, albedo);
                cmd.SetComputeVectorParam(compute, Ids.MainDirection, mainDirection);
                cmd.SetComputeVectorParam(compute, Ids.MainColour, mainColour);
                cmd.SetComputeVectorParam(compute, Ids.Ambient, ambient);
                cmd.SetComputeFloatParam(compute, Ids.LightCount, lightCount);
                cmd.SetComputeVectorArrayParam(compute, Ids.LightPositions, lightPositions);
                cmd.SetComputeVectorArrayParam(compute, Ids.LightColours, lightColours);
                cmd.SetComputeVectorArrayParam(compute, Ids.LightSpots, lightSpots);
                cmd.SetComputeVectorArrayParam(compute, Ids.LightSpotAngles, lightSpotAngles);
                cmd.SetComputeFloatParam(compute, Ids.VolumeCount, volumeCount);
                cmd.SetComputeMatrixArrayParam(compute, Ids.VolumeMatrices, volumeMatrices);
                cmd.SetComputeVectorArrayParam(compute, Ids.VolumeParams, volumeParams);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid()) return;

                Camera camera = cameraData.camera;
                float aspect = cameraData.cameraTargetDescriptor.width / (float)Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
                Vector3Int size = GridSize(feature.quality, aspect);
                CameraState state = StateFor(camera, size);
                bool shadowsAvailable = resources.mainShadowsTexture.IsValid();
                Fill(cameraData, lightData, shadowsAvailable, state);

                bool marched = feature.method == Method.Marched;
                TextureHandle source;
                if (marched)
                {
                    RenderTextureDescriptor marchDesc = cameraData.cameraTargetDescriptor;
                    if (feature.halfResolution)
                    {
                        marchDesc.width = Mathf.Max(1, marchDesc.width / 2);
                        marchDesc.height = Mathf.Max(1, marchDesc.height / 2);
                    }
                    marchDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                    marchDesc.depthStencilFormat = GraphicsFormat.None;
                    marchDesc.msaaSamples = 1;
                    source = UniversalRenderer.CreateRenderGraphTexture(renderGraph, marchDesc, "_MiasmaMarched", false, FilterMode.Bilinear);
                    block.SetVector(Ids.MethodId, new Vector4(1f, 0f, 1f / marchDesc.width, 1f / marchDesc.height));

                    using (var builder = renderGraph.AddRasterRenderPass<MarchData>("Miasma March", out MarchData data, profilingSampler))
                    {
                        data.material = material;
                        data.block = block;
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                        if (shadowsAvailable) builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                        if (resources.additionalShadowsTexture.IsValid()) builder.UseTexture(resources.additionalShadowsTexture, AccessFlags.Read);
                        builder.UseAllGlobalTextures(true);
                        builder.SetRenderAttachment(source, 0, AccessFlags.Write);
                        builder.SetRenderFunc((MarchData d, RasterGraphContext context) =>
                        {
                            d.block.SetVector(Ids.BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                            context.cmd.DrawProcedural(Matrix4x4.identity, d.material, 3, MeshTopology.Triangles, 3, 1, d.block);
                        });
                    }
                }
                else
                {
                block.SetVector(Ids.MethodId, Vector4.zero);
                int previous = state.current;
                state.current = 1 - state.current;
                TextureHandle injected = renderGraph.ImportTexture(state.history[state.current]);
                TextureHandle history = renderGraph.ImportTexture(state.history[previous]);

                var integratedDesc = new TextureDesc(size.x, size.y)
                {
                    dimension = TextureDimension.Tex3D,
                    slices = size.z,
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "_MiasmaIntegrated",
                };
                TextureHandle integrated = renderGraph.CreateTexture(integratedDesc);

                using (var builder = renderGraph.AddComputePass<InjectData>("Miasma Inject", out InjectData data, profilingSampler))
                {
                    data.owner = this;
                    data.injected = injected;
                    data.history = history;
                    data.size = size;
                    builder.UseTexture(injected, AccessFlags.Write);
                    builder.UseTexture(history, AccessFlags.Read);
                    if (shadowsAvailable) builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((InjectData d, ComputeGraphContext context) =>
                    {
                        FogPass o = d.owner;
                        o.SetComputeParams(context.cmd, o.injectKernel);
                        context.cmd.SetComputeTextureParam(o.compute, o.injectKernel, Ids.Injected, d.injected);
                        context.cmd.SetComputeTextureParam(o.compute, o.injectKernel, Ids.History, d.history);
                        context.cmd.DispatchCompute(o.compute, o.injectKernel,
                            (d.size.x + 7) / 8, (d.size.y + 7) / 8, d.size.z);
                    });
                }

                using (var builder = renderGraph.AddComputePass<IntegrateData>("Miasma Integrate", out IntegrateData data, profilingSampler))
                {
                    data.owner = this;
                    data.injected = injected;
                    data.integrated = integrated;
                    data.size = size;
                    builder.UseTexture(injected, AccessFlags.Read);
                    builder.UseTexture(integrated, AccessFlags.Write);
                    builder.SetRenderFunc((IntegrateData d, ComputeGraphContext context) =>
                    {
                        FogPass o = d.owner;
                        o.SetComputeParams(context.cmd, o.integrateKernel);
                        context.cmd.SetComputeTextureParam(o.compute, o.integrateKernel, Ids.InjectedRead, d.injected);
                        context.cmd.SetComputeTextureParam(o.compute, o.integrateKernel, Ids.Integrated, d.integrated);
                        context.cmd.DispatchCompute(o.compute, o.integrateKernel, (d.size.x + 7) / 8, (d.size.y + 7) / 8, 1);
                    });
                }
                source = integrated;
                }

                // How much of each pixel shows through the fog, for a print effect after us.
                RenderTextureDescriptor visDesc = cameraData.cameraTargetDescriptor;
                visDesc.graphicsFormat = GraphicsFormat.R8_UNorm;
                visDesc.depthStencilFormat = GraphicsFormat.None;
                visDesc.msaaSamples = 1;
                TextureHandle visibility = UniversalRenderer.CreateRenderGraphTexture(renderGraph, visDesc, "_ChromoVisibility", false);

                // Painted: the fog goes to its own layer, painted over the finished frame later.
                bool painted = feature.layer == Layer.Painted;
                TextureHandle target = resources.activeColorTexture;
                if (painted)
                {
                    RenderTextureDescriptor layerDesc = cameraData.cameraTargetDescriptor;
                    layerDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                    layerDesc.depthStencilFormat = GraphicsFormat.None;
                    layerDesc.msaaSamples = 1;
                    target = UniversalRenderer.CreateRenderGraphTexture(renderGraph, layerDesc, "_MiasmaLayer", false, FilterMode.Bilinear);
                    LayerData layerData = frameData.GetOrCreate<LayerData>();
                    layerData.layer = target;
                    layerData.valid = true;
                }

                using (var builder = renderGraph.AddRasterRenderPass<ApplyData>("Miasma Apply", out ApplyData data, profilingSampler))
                {
                    data.material = material;
                    data.block = block;
                    data.integrated = source;
                    data.marched = marched;
                    data.share = feature.shareVisibility;
                    data.shaderPass = painted ? 1 : 0;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(target, 0, painted ? AccessFlags.Write : AccessFlags.ReadWrite);
                    builder.SetRenderAttachment(visibility, 1, AccessFlags.Write);
                    if (data.share)
                    {
                        builder.SetGlobalTextureAfterPass(visibility, Ids.Visibility);
                        builder.AllowGlobalStateModification(true);
                    }
                    builder.SetRenderFunc((ApplyData d, RasterGraphContext context) =>
                    {
                        d.block.SetTexture(d.marched ? Ids.Marched : Ids.IntegratedRead, (RTHandle)d.integrated);
                        d.block.SetVector(Ids.BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                        context.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.shaderPass, MeshTopology.Triangles, 3, 1, d.block);
                        if (d.share) context.cmd.SetGlobalFloat(Ids.VisibilityOn, 1f);
                    });
                }
            }
        }
    }
}
