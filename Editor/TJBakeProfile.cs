using System;
using System.Collections.Generic;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>Everything a bake needs, saved as an asset so a modder sets it up once and re-bakes with one click.</summary>
    [CreateAssetMenu(menuName = "TJBake/Bake Profile", fileName = "New TJBake Profile")]
    public sealed class TJBakeProfile : ScriptableObject
    {
        [Serializable]
        public sealed class Slot
        {
            public string name = "";
            public AnimationClip clip;
            public bool loop;
            public bool returnToIdle;
            [Tooltip("Fraction of the clip at which a returning slot hands back to idle. Negative takes the clip's first animation event, else 0.9.")]
            public float returnAt = -1f;
        }

        [Serializable]
        public sealed class Anchor
        {
            public string name = "";
            [Tooltip("Path of the bone under the source prefab's root, as Transform.Find takes it.")]
            public string bonePath = "";
        }

        [Serializable]
        public sealed class Attachment
        {
            public string name = "";
            [Tooltip("A prefab with MeshFilters, placed at the anchor. Leave empty for a saddle or another role that needs no mesh.")]
            public GameObject prefab;
            public string anchor = "";
            public TJBakePropRole role = TJBakePropRole.Prop;
            public Vector3 position;
            public Vector3 rotation;
            public Vector3 scale = Vector3.one;
            [Tooltip("Bit i set = shown at LOD i.")]
            public int lodMask = 0b11;
        }

        public string unitName = "";
        public bool isRider;
        [Tooltip("The rigged prefab whose active SkinnedMeshRenderers become LOD0.")]
        public GameObject sourcePrefab;
        [Tooltip("Lower-detail rigs that share the source's bone names, LOD1 then LOD2.")]
        public List<GameObject> lodPrefabs = new();
        [Tooltip("Screen-height fraction below which each next LOD shows, one per LOD prefab, descending.")]
        public List<float> lodSwitch = new();
        [Tooltip("Uniform scale baked into the bones, so the visual's own root stays at 1.")]
        public float rootScale = 1f;
        public int frameRate = 30;
        public List<Slot> slots = new();
        public List<Anchor> anchors = new();
        public List<Attachment> attachments = new();
        [Tooltip("A rider profile for a mounted unit. Needs a saddle attachment here.")]
        public TJBakeProfile rider;
        [Tooltip("Folder the bake writes, usually Mods/<YourMod>/unit_visuals/<UnitName>.")]
        public string outputFolder = "";

        /// <summary>Fills the slot list with the default contract's names and kinds, keeping clips already set.</summary>
        public void ResetSlotsToContract()
        {
            string[] names = isRider ? TJBakeContract.RiderSlotNames : TJBakeContract.UnitSlotNames;
            var old = new List<Slot>(slots);
            slots.Clear();
            for (int i = 0; i < names.Length; i++)
            {
                TJBakeContract.SlotKind(i, isRider, out bool loop, out bool returns);
                slots.Add(new Slot { name = names[i], clip = i < old.Count ? old[i].clip : null, loop = loop, returnToIdle = returns, returnAt = i < old.Count ? old[i].returnAt : -1f });
            }
        }
    }
}
