using System;
using System.Collections.Generic;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    public sealed class TJBakeParameter
    {
        public string Name = "";
        public AnimatorControllerParameterType Type = AnimatorControllerParameterType.Float;
        public float Float;
        public int Int;
        public bool Bool;
    }

    public sealed class TJBakeSlotRequest
    {
        public string Name = "";
        // Sets the frame count and the return point; it is also what plays when no state is named.
        public AnimationClip Clip;
        public bool Loop;
        public bool ReturnToIdle;
        // Negative means: take the clip's ReturnToIdle event, else the request default.
        public float ReturnAt = -1f;
        // Optional: pose through the root's Animator by playing this base-layer state with these parameters, so extra
        // layers (a hand holding a prop) and blend trees bake the way they play.
        public string StateName = "";
        public List<TJBakeParameter> Parameters = new();
    }

    public sealed class TJBakeAnchorRequest
    {
        public string Name = "";
        public Transform Transform;
    }

    public sealed class TJBakeAttachmentRequest
    {
        public string Name = "";
        // Every MeshFilter under the holder is baked in the holder's space. A holder without a mesh still becomes an entity.
        public Transform Holder;
        public string Anchor = "";
        public TJBakePropRole Role = TJBakePropRole.Prop;
        public int LodMask = 0b11;
        // Written to the manifest for the host game.
        public string Tag = "";
    }

    /// <summary>A further look for the same rig: its own meshes and props on the first variant's skeleton and clips.</summary>
    public sealed class TJBakeVariantRequest
    {
        // An instance of a rig whose bones carry the same names as the request's root.
        public GameObject Root;
        public List<TJBakeAttachmentRequest> Attachments = new();
    }

    /// <summary>Everything one bake needs. The caller owns the instantiated objects it hands over.</summary>
    public sealed class TJBakeRequest
    {
        public string UnitName = "";
        public bool IsRider;
        public string OutputFolder = "";
        public int Fps = 30;
        // Baked into the matrices so the visual's root stays at scale 1.
        public float RootScale = 1f;
        // Baked in the same way, applied after the scale, so a rider can sit above its saddle point.
        public Vector3 RootOffset = Vector3.zero;
        public float DefaultReturnAt = 0.9f;
        public string BakerName = "TJBake";
        // An instance (scene or preview scene) of the rigged prefab; its active SkinnedMeshRenderers form LOD0.
        public GameObject Root;
        // Instances of lower-detail rigs sharing the skeleton by bone name, one per extra LOD.
        public List<GameObject> LodRoots = new();
        public List<float> LodSwitch = new();
        // Above 0 and with no LodRoots: bakes a LOD1 of every variant's body meshes with about this share of their triangles,
        // shown below AutoLodSwitch of the screen's height. Mesh for mesh, so a host can swap each mesh for its far one.
        public float AutoLodKeep;
        public float AutoLodSwitch = 0.1f;
        // Above 0 with an automatic LOD1: also bakes an automatic LOD2 of each variant's body meshes with at most about this
        // many vertices among them, shown below AutoLod2Switch of the screen's height. Its bones that AutoLod2Folds names
        // skin with their nearest kept parent, and each vertex keeps its two strongest bones, so it skins with fewer reads.
        public int AutoLod2Vertices;
        public float AutoLod2Switch = 0.03f;
        public Func<string, bool> AutoLod2Folds;
        public List<TJBakeSlotRequest> Slots = new();
        public List<TJBakeAnchorRequest> Anchors = new();
        public List<TJBakeAttachmentRequest> Attachments = new();
        // Up to three more variants; they share the skeleton, clips and anchors of this one.
        public List<TJBakeVariantRequest> ExtraVariants = new();
        public TJBakeRequest Rider;
        // When set and it returns a name, the manifest names the texture asset instead of copying it out as a PNG.
        public Func<Texture2D, string> TextureReference;
        // A rig with no skinned mesh (an invisible clock that only times its slots) bakes a one-millimetre triangle instead of failing.
        public bool PlaceholderWhenNoMesh;
        // Optional: called with the current step and the fraction of this bake done (0 to 1); return true to cancel.
        // A cancel throws OperationCanceledException, possibly after some files are written to OutputFolder.
        public Func<string, float, bool> Progress;
    }
}
