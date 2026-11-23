using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Miasma
{
    /// <summary>
    /// Volume override with every fog setting. Put it on a global Volume for the default fog and on local Volumes for
    /// areas with their own; from code, add it to a runtime profile (see the README). Fog is off while Density is 0
    /// and no Fog Volume adds any.
    /// </summary>
    [Serializable]
    [VolumeComponentMenu("Miasma/Fog")]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public sealed class MiasmaFog : VolumeComponent
    {
        [Header("Density")]
        [Tooltip("How thick the fog is at and below Base Height, per metre. 0.01 is a light haze, 0.05 a thick night fog, 0.15 hard to see through.")]
        public MinFloatParameter density = new(0f, 0f);

        [Tooltip("World height the fog sits on. Above it the fog thins out.")]
        public FloatParameter baseHeight = new(0f);

        [Tooltip("How quickly the fog thins with height above Base Height. 0 = the same at every height; 0.5 = mostly gone a few metres up (ground fog).")]
        public MinFloatParameter heightFalloff = new(0.15f, 0f);

        [Header("Shape")]
        [Tooltip("How much drifting noise breaks the fog into patches and wisps (0 = even).")]
        public ClampedFloatParameter noise = new(0.6f, 0f, 1f);

        [Tooltip("Size of the patches, in metres.")]
        public MinFloatParameter noiseSize = new(8f, 0.1f);

        [Tooltip("Which way and how fast the fog drifts, in metres per second.")]
        public Vector3Parameter wind = new(new Vector3(0.4f, 0f, 0.15f));

        [Header("Light")]
        [Tooltip("Colour of the fog itself (what it does to the light passing through it).")]
        public ColorParameter albedo = new(new Color(0.9f, 0.9f, 0.9f), false, false, true);

        [Tooltip("How much the fog glows towards a light you look towards (0 = the same from every side; 0.6 = strong halos and shafts).")]
        public ClampedFloatParameter forwardScattering = new(0.45f, -0.9f, 0.9f);

        [Tooltip("Strength of the main light (sun or moon) in the fog. It respects the light's shadows, which gives shafts.")]
        public MinFloatParameter mainLight = new(1f, 0f);

        [Tooltip("Strength of other lights (torches, lanterns) in the fog.")]
        public MinFloatParameter otherLights = new(1f, 0f);

        [Tooltip("Strength of the ambient light in the fog.")]
        public MinFloatParameter ambient = new(1f, 0f);

        [Header("Range")]
        [Tooltip("How far the fog is marched, in metres. Things further away get the fog of this distance.")]
        public MinFloatParameter distance = new(80f, 1f);

        /// <summary>True when this override makes any fog.</summary>
        public bool IsActive() => density.value > 0f;
    }
}
