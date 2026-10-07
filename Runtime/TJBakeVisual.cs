using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MemoriStudios.TJBake
{
    /// <summary>What an attachment is for. The host game maps roles to its own behaviour; the baker only records them.</summary>
    public enum TJBakePropRole : byte { Prop = 0, Bow = 1, Sword = 2, Shield = 3, Saddle = 4 }

    /// <summary>One clip slot: a frame range in the matrix texture and how it plays.</summary>
    public sealed class TJBakeSlot
    {
        public string Name = "";
        // First row of the clip in the matrix texture.
        public int Start;
        public int Count;
        public float Fps;
        public bool Loop;
        public bool ReturnToIdle;
        // Fraction of the clip at which a returning slot hands back to idle.
        public float ReturnAt = 0.9f;
    }

    /// <summary>A loaded visual: engine objects the caller owns and destroys together.</summary>
    public sealed class TJBakeVisual
    {
        public string Name = "";
        public string Kind = "unit";
        public int BoneCount;
        public int FrameCount;
        public float Fps;
        public List<TJBakeSlot> Slots = new();
        // RGBAHalf, width BoneCount * 3, height FrameCount, point sampled.
        public Texture2D BoneTexture;
        public List<string> AnchorNames = new();
        // Frame-major: index = frame * AnchorCount + anchor. Empty when there are no anchors.
        public float3x4[] AnchorMatrices = System.Array.Empty<float3x4>();
        public int AnchorCount => AnchorNames.Count;
        // Skinned (TJBake/Unit with _BoneTex) and rigid (URP Lit) material per unit.json material entry.
        public Material[] SkinnedMaterials = System.Array.Empty<Material>();
        public Material[] RigidMaterials = System.Array.Empty<Material>();
        public List<TJBakeVariant> Variants = new();
        // Screen-height fractions below which the next LOD shows; one fewer than the LOD count.
        public float[] LodSwitch = System.Array.Empty<float>();
        public TJBakeVisual Rider;
    }

    public sealed class TJBakeVariant
    {
        public List<TJBakeLod> Lods = new();
        public List<TJBakeAttachment> Attachments = new();
    }

    public sealed class TJBakeLod
    {
        public List<TJBakeMeshRef> Meshes = new();
    }

    public sealed class TJBakeMeshRef
    {
        public Mesh Mesh;
        public int MaterialIndex;
        // Animation-inclusive, in model space.
        public Bounds Bounds;
    }

    public sealed class TJBakeAttachment
    {
        public Mesh Mesh;
        public int MaterialIndex;
        public int AnchorIndex;
        public TJBakePropRole Role;
        // Bit i set = shown at LOD i.
        public int LodMask = 0b11;
        public Bounds Bounds;
    }
}
