using System.Collections.Generic;
using System.IO;
using MemoriStudios.TJBake.Format;

namespace MemoriStudios.TJBake
{
    /// <summary>The rules a unit.json must satisfy, shared by the baker before writing and by the loader before building.</summary>
    public static class TJBakeValidation
    {
        public static readonly string[] Roles = { "prop", "bow", "sword", "shield", "saddle" };
        public const int UnitSlotCount = 13;
        public const int RangedSlotCount = 15;
        public const int RiderSlotCount = 3;

        public static TJBakePropRole ParseRole(string role) => role switch
        {
            "bow" => TJBakePropRole.Bow,
            "sword" => TJBakePropRole.Sword,
            "shield" => TJBakePropRole.Shield,
            "saddle" => TJBakePropRole.Saddle,
            _ => TJBakePropRole.Prop,
        };

        public static string RoleName(TJBakePropRole role) => role switch
        {
            TJBakePropRole.Bow => "bow",
            TJBakePropRole.Sword => "sword",
            TJBakePropRole.Shield => "shield",
            TJBakePropRole.Saddle => "saddle",
            _ => "prop",
        };

        /// <summary>Throws <see cref="GpuAnimFormatException"/> naming the first rule the manifest breaks. Files are checked to exist, not parsed.</summary>
        public static void ValidateManifest(UnitJson json, string folder)
        {
            if (json.format != 1) throw new GpuAnimFormatException($"unit.json format {json.format} is not 1");
            bool rider = json.kind == "rider";
            if (!rider && json.kind != "unit") throw new GpuAnimFormatException($"unit.json kind '{json.kind}' is not unit or rider");
            if (json.frameRate != 24 && json.frameRate != 30 && json.frameRate != 60) throw new GpuAnimFormatException($"frameRate {json.frameRate} is not 24, 30 or 60");
            if (json.skeleton == null || json.skeleton.boneCount < 1 || json.skeleton.boneCount > AnimBin.MaxBones) throw new GpuAnimFormatException("skeleton.boneCount is outside 1 to 5461");
            if (json.slots == null || json.slots.Count == 0) throw new GpuAnimFormatException("slots is empty");
            int required = rider ? RiderSlotCount : UnitSlotCount;
            if (json.slots.Count < required) throw new GpuAnimFormatException($"{json.slots.Count} slots, at least {required} needed");
            for (int i = 0; i < json.slots.Count; i++)
            {
                SlotJson s = json.slots[i];
                if (s.id != i) throw new GpuAnimFormatException($"slot ids must be contiguous from 0; entry {i} has id {s.id}");
                if (s.loop && s.returnToIdle) throw new GpuAnimFormatException($"slot {i} ({s.name}) is both loop and returnToIdle");
                if (s.count < 2) throw new GpuAnimFormatException($"slot {i} ({s.name}) has fewer than 2 frames");
            }
            if (json.materials == null || json.materials.Count == 0) throw new GpuAnimFormatException("materials is empty");
            if (json.variants == null || json.variants.Count < 1 || json.variants.Count > 3) throw new GpuAnimFormatException("variants must hold 1 to 3 entries");
            int lodCount = json.variants[0].lods?.Count ?? 0;
            if (lodCount < 1 || lodCount > 3) throw new GpuAnimFormatException("each variant needs 1 to 3 LODs");
            if (rider && lodCount != 1) throw new GpuAnimFormatException("a rider has exactly one LOD");
            if ((json.lodSwitch?.Count ?? 0) != lodCount - 1) throw new GpuAnimFormatException($"lodSwitch needs {lodCount - 1} entries for {lodCount} LODs");
            for (int i = 0; i < json.lodSwitch.Count; i++)
            {
                float h = json.lodSwitch[i];
                if (h <= 0f || h >= 1f || (i > 0 && h >= json.lodSwitch[i - 1])) throw new GpuAnimFormatException("lodSwitch values must be descending fractions in (0, 1)");
            }
            var anchorSet = new HashSet<string>(json.anchors ?? new List<string>());
            if (anchorSet.Count != (json.anchors?.Count ?? 0)) throw new GpuAnimFormatException("anchor names repeat");
            foreach (VariantJson v in json.variants)
            {
                if ((v.lods?.Count ?? 0) != lodCount) throw new GpuAnimFormatException("every variant must have the same LOD count");
                foreach (LodJson lod in v.lods)
                {
                    if (lod.meshes == null || lod.meshes.Count == 0) throw new GpuAnimFormatException("a LOD lists no mesh");
                    foreach (MeshRefJson mr in lod.meshes) CheckRef(folder, mr.file, mr.material, json.materials.Count);
                }
                var roleCounts = new Dictionary<string, int>();
                bool hasSaddle = false;
                foreach (AttachmentJson a in v.attachments ?? new List<AttachmentJson>())
                {
                    CheckRef(folder, a.file, a.material, json.materials.Count);
                    if (!anchorSet.Contains(a.anchor)) throw new GpuAnimFormatException($"attachment {a.file} names unknown anchor '{a.anchor}'");
                    if (System.Array.IndexOf(Roles, a.role) < 0) throw new GpuAnimFormatException($"attachment {a.file} has unknown role '{a.role}'");
                    if (a.role != "prop")
                    {
                        roleCounts.TryGetValue(a.role, out int n);
                        if (n >= 1) throw new GpuAnimFormatException($"more than one attachment has role '{a.role}'");
                        roleCounts[a.role] = n + 1;
                        if (a.role == "saddle") hasSaddle = true;
                    }
                }
                if (!rider && !string.IsNullOrEmpty(json.rider) && !hasSaddle) throw new GpuAnimFormatException("a unit with a rider needs a saddle attachment on every variant");
            }
            foreach (MaterialJson m in json.materials)
            {
                if (string.IsNullOrEmpty(m.baseColor)) throw new GpuAnimFormatException($"material '{m.name}' has no baseColor texture");
                CheckFile(folder, m.baseColor);
                if (!string.IsNullOrEmpty(m.normal)) CheckFile(folder, m.normal);
                if (!string.IsNullOrEmpty(m.emission)) CheckFile(folder, m.emission);
            }
            if (!rider && !string.IsNullOrEmpty(json.rider) && !File.Exists(Path.Combine(folder, json.rider, TJBakeVisualLoader.ManifestName))) throw new GpuAnimFormatException("rider folder has no unit.json");
            CheckFile(folder, "anim.bin");
            if (anchorSet.Count > 0) CheckFile(folder, "anchors.bin");
        }

        private static void CheckRef(string folder, string file, int material, int materialCount)
        {
            CheckFile(folder, file);
            if (material < 0 || material >= materialCount) throw new GpuAnimFormatException($"{file} uses material {material} of {materialCount}");
        }

        private static void CheckFile(string folder, string file)
        {
            if (string.IsNullOrEmpty(file) || file.Contains("..") || file.Contains('/') || file.Contains('\\')) throw new GpuAnimFormatException($"'{file}' is not a plain file name in the folder");
            if (!File.Exists(Path.Combine(folder, file))) throw new GpuAnimFormatException($"{file} is missing");
        }
    }
}
