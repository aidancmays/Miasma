using System.Collections.Generic;
using UnityEngine;

namespace Miasma
{
    /// <summary>
    /// A box of extra fog (or, with a negative density, a clearing) on top of the Miasma Fog settings: mist pooled in a
    /// hollow, a fog bank, a crypt full of it. The box is this object's transform (a unit cube scaled, turned and moved
    /// with it); its edges fade over Edge.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Miasma/Fog Volume")]
    public sealed class FogVolume : MonoBehaviour
    {
        /// <summary>At most this many volumes are drawn (the nearest to the camera).</summary>
        public const int Max = 16;

        [Tooltip("Fog added inside the box, per metre (negative thins the fog out).")]
        public float density = 0.05f;

        [Tooltip("How far in from the box's sides the fog fades in, as a fraction of its size (0 = hard edges, 0.5 = a soft blob).")]
        [Range(0f, 0.5f)] public float edge = 0.2f;

        [Tooltip("1 = the box's fog drifts with the noise like the rest; 0 = even.")]
        [Range(0f, 1f)] public float noise = 1f;

        static readonly List<FogVolume> all = new();

        /// <summary>Every enabled volume.</summary>
        public static IReadOnlyList<FogVolume> All => all;

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.7f, 0.75f, 0.8f, 0.6f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one * (1f - 2f * edge));
        }
    }
}
