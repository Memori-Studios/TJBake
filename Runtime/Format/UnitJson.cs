using System;
using System.Collections.Generic;

namespace MemoriStudios.TJBake.Format
{
    /// <summary>unit.json as JsonUtility reads and writes it. Field names are the file format; see unit-json-schema.md.</summary>
    [Serializable]
    public sealed class UnitJson
    {
        public int format = 1;
        public string kind = "unit";
        public string unitName = "";
        public string baker = "";
        public SkeletonJson skeleton = new();
        public int frameRate = 30;
        public List<SlotJson> slots = new();
        public List<string> anchors = new();
        public List<MaterialJson> materials = new();
        public List<float> lodSwitch = new();
        public List<VariantJson> variants = new();
        public string rider = "";
    }

    [Serializable]
    public sealed class SkeletonJson
    {
        public int boneCount;
        public List<string> names = new();
    }

    [Serializable]
    public sealed class SlotJson
    {
        public int id;
        public string name = "";
        public int start;
        public int count;
        public bool loop;
        public bool returnToIdle;
        public float returnAt = 0.9f;
    }

    [Serializable]
    public sealed class MaterialJson
    {
        public string name = "";
        public string baseColor = "";
        public string normal = "";
        public string emission = "";
        public float[] tint = { 1f, 1f, 1f, 1f };
        public float smoothness = 0.2f;
        public bool transparent;
    }

    [Serializable]
    public sealed class VariantJson
    {
        public List<LodJson> lods = new();
        public List<AttachmentJson> attachments = new();
    }

    [Serializable]
    public sealed class LodJson
    {
        public List<MeshRefJson> meshes = new();
    }

    [Serializable]
    public sealed class MeshRefJson
    {
        public string file = "";
        public int material;
    }

    [Serializable]
    public sealed class AttachmentJson
    {
        public string file = "";
        public int material;
        public string anchor = "";
        public string role = "prop";
        public List<int> lods = new() { 0, 1 };
    }
}
