using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Miasma
{
    /// <summary>
    /// Volumetric fog that fills the space. Each frame it marches along every pixel's view ray (at half resolution)
    /// through height fog broken up by drifting noise and any <see cref="FogVolume"/>s, gathering the light the fog
    /// scatters towards the camera from the main light (in and out of its shadows, which makes shafts), the other
    /// lights and the ambient light, and how much of what lies behind still shows through. That is then laid over the
    /// opaque frame, before transparent objects and post-processing (so a print or grade after it sees the fog too).
    ///
    /// Add it to the renderer (Renderer > Add Renderer Feature > Miasma Fog) and add a Miasma Fog override to a Volume.
    /// </summary>
    [DisallowMultipleRendererFeature("Miasma Fog")]
    public sealed class MiasmaFeature : ScriptableRendererFeature
    {
        [Tooltip("Samples along each ray. More = smoother fog and sharper shafts, slower.")]
        [Range(8, 128)] public int steps = 40;

        [Tooltip("March at half resolution (much faster; the fog is soft anyway).")]
        public bool halfResolution = true;

        [Tooltip("Light the fog with additional lights (torches, lanterns). Costs the most with many lights.")]
        public bool otherLights = true;

        [Tooltip("Fog in the Scene view too.")]
        public bool sceneView = true;

        [SerializeField, HideInInspector] Shader shader;

        Material material;
        FogPass pass;

        public override void Create()
        {
            if (shader == null) shader = Shader.Find("Hidden/Miasma");
            pass = new FogPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType type = renderingData.cameraData.cameraType;
            if (type != CameraType.Game && !(sceneView && type == CameraType.SceneView)) return;
            if (shader == null) return;
            if (material == null) material = CoreUtils.CreateEngineMaterial(shader);

            MiasmaFog fog = VolumeManager.instance.stack.GetComponent<MiasmaFog>();
            bool any = fog != null && fog.IsActive();
            if (!any)
            {
                foreach (FogVolume v in FogVolume.All)
                    if (v.density > 0f) { any = true; break; }
            }
            if (!any) return;

            pass.Setup(material, fog, this);
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
            material = null;
        }

        static class Ids
        {
            public static readonly int Density = Shader.PropertyToID("_MiasmaDensity");
            public static readonly int Noise = Shader.PropertyToID("_MiasmaNoise");
            public static readonly int Light = Shader.PropertyToID("_MiasmaLight");
            public static readonly int Albedo = Shader.PropertyToID("_MiasmaAlbedo");
            public static readonly int Range = Shader.PropertyToID("_MiasmaRange");
            public static readonly int VolumeCount = Shader.PropertyToID("_MiasmaVolumeCount");
            public static readonly int VolumeMatrices = Shader.PropertyToID("_MiasmaVolumeMatrices");
            public static readonly int VolumeParams = Shader.PropertyToID("_MiasmaVolumeParams");
            public static readonly int Fog = Shader.PropertyToID("_MiasmaFogTexture");
            public static readonly int BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");
        }

        sealed class FogPass : ScriptableRenderPass
        {
            Material material;
            MiasmaFog fog;
            MiasmaFeature feature;
            readonly MaterialPropertyBlock block = new();
            readonly Matrix4x4[] matrices = new Matrix4x4[FogVolume.Max];
            readonly Vector4[] volumeParams = new Vector4[FogVolume.Max];
            readonly List<FogVolume> nearest = new();

            class MarchData
            {
                public Material material;
                public MaterialPropertyBlock block;
                public TextureHandle depth;
            }

            class CompositeData
            {
                public Material material;
                public MaterialPropertyBlock block;
                public TextureHandle fog;
            }

            public FogPass() => profilingSampler = new ProfilingSampler("Miasma Fog");

            public void Setup(Material material, MiasmaFog fog, MiasmaFeature feature)
            {
                this.material = material;
                this.fog = fog;
                this.feature = feature;
            }

            void Fill(Camera camera)
            {
                float density = fog != null ? fog.density.value : 0f;
                block.SetVector(Ids.Density, new Vector4(density, fog != null ? fog.baseHeight.value : 0f,
                    fog != null ? fog.heightFalloff.value : 0f, fog != null ? fog.noise.value : 0f));
                Vector3 wind = fog != null ? fog.wind.value : Vector3.zero;
                block.SetVector(Ids.Noise, new Vector4(1f / Mathf.Max(0.1f, fog != null ? fog.noiseSize.value : 8f), wind.x, wind.y, wind.z));
                block.SetVector(Ids.Light, new Vector4(fog != null ? fog.forwardScattering.value : 0f,
                    fog != null ? fog.mainLight.value : 1f, fog != null && feature.otherLights ? fog.otherLights.value : 0f,
                    fog != null ? fog.ambient.value : 1f));
                Color albedo = fog != null ? fog.albedo.value : Color.white;
                block.SetVector(Ids.Albedo, new Vector4(albedo.r, albedo.g, albedo.b, 0f));
                block.SetVector(Ids.Range, new Vector4(fog != null ? fog.distance.value : 80f, feature.steps, 0f, 0f));

                // The nearest volumes to the camera.
                nearest.Clear();
                foreach (FogVolume v in FogVolume.All)
                    if (v != null && v.isActiveAndEnabled && v.density != 0f) nearest.Add(v);
                if (nearest.Count > FogVolume.Max)
                {
                    Vector3 eye = camera.transform.position;
                    nearest.Sort((a, b) => (a.transform.position - eye).sqrMagnitude.CompareTo((b.transform.position - eye).sqrMagnitude));
                }
                int count = Mathf.Min(nearest.Count, FogVolume.Max);
                for (int i = 0; i < FogVolume.Max; i++)
                {
                    if (i < count)
                    {
                        FogVolume v = nearest[i];
                        matrices[i] = v.transform.worldToLocalMatrix;
                        volumeParams[i] = new Vector4(v.density, Mathf.Max(0.0001f, v.edge), v.noise, 0f);
                    }
                    else
                    {
                        matrices[i] = Matrix4x4.identity;
                        volumeParams[i] = Vector4.zero;
                    }
                }
                block.SetFloat(Ids.VolumeCount, count);
                block.SetMatrixArray(Ids.VolumeMatrices, matrices);
                block.SetVectorArray(Ids.VolumeParams, volumeParams);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid()) return;

                Fill(cameraData.camera);

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                if (feature.halfResolution)
                {
                    desc.width = Mathf.Max(1, desc.width / 2);
                    desc.height = Mathf.Max(1, desc.height / 2);
                }
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.msaaSamples = 1;
                TextureHandle fogTexture = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MiasmaFog", false, FilterMode.Bilinear);

                using (var builder = renderGraph.AddRasterRenderPass<MarchData>("Miasma March", out MarchData data, profilingSampler))
                {
                    data.material = material;
                    data.block = block;
                    data.depth = resources.cameraDepthTexture;
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    if (resources.mainShadowsTexture.IsValid()) builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                    if (resources.additionalShadowsTexture.IsValid()) builder.UseTexture(resources.additionalShadowsTexture, AccessFlags.Read);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachment(fogTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc((MarchData d, RasterGraphContext context) =>
                    {
                        d.block.SetVector(Ids.BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                        context.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3, 1, d.block);
                    });
                }

                using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("Miasma Composite", out CompositeData data, profilingSampler))
                {
                    data.material = material;
                    data.block = block;
                    data.fog = fogTexture;
                    builder.UseTexture(fogTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderFunc((CompositeData d, RasterGraphContext context) =>
                    {
                        d.block.SetTexture(Ids.Fog, (RTHandle)d.fog);
                        d.block.SetVector(Ids.BlitScaleBias, new Vector4(1f, 1f, 0f, 0f));
                        context.cmd.DrawProcedural(Matrix4x4.identity, d.material, 1, MeshTopology.Triangles, 3, 1, d.block);
                    });
                }
            }
        }
    }
}
