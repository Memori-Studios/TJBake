using System;
using System.Collections.Generic;
using System.IO;
using MemoriStudios.TJBake.Format;
using Unity.Mathematics;
using UnityEngine;

namespace MemoriStudios.TJBake
{
    /// <summary>Turns a visual folder into engine objects. Every failure is one exception naming the file and the rule; nothing is half built.</summary>
    public static class TJBakeVisualLoader
    {
        public const long MaxAnimBytes = 256L * 1024 * 1024;
        public const long MaxMeshBytes = 64L * 1024 * 1024;
        public const long MaxFolderBytes = 512L * 1024 * 1024;
        public const string ManifestName = "unit.json";
        public const string SkinnedShaderName = "TJBake/Unit";
        public const string RigidShaderName = "Universal Render Pipeline/Lit";

        /// <summary>Reads and validates the manifest only, as a boot-time check does. Returns null and an error on any problem.</summary>
        public static UnitJson ReadManifest(string folder, out string error, bool hostTextures = false)
        {
            error = null;
            try
            {
                string path = Path.Combine(folder, ManifestName);
                if (!File.Exists(path)) throw new GpuAnimFormatException("unit.json is missing");
                UnitJson json = JsonUtility.FromJson<UnitJson>(File.ReadAllText(path));
                if (json == null) throw new GpuAnimFormatException("unit.json did not parse");
                TJBakeValidation.ValidateManifest(json, folder, hostTextures);
                return json;
            }
            catch (Exception e)
            {
                error = $"{folder}: {e.Message}";
                return null;
            }
        }

        /// <summary>Loads the whole folder. The caller owns every object it adds to <paramref name="owned"/> and destroys them together.</summary>
        /// <param name="resolveTexture">Loads a texture the manifest names with <see cref="TJBakeValidation.HostTexturePrefix"/>; the host keeps ownership. Null refuses such names.</param>
        public static TJBakeVisual Load(string folder, List<UnityEngine.Object> owned, bool isRider = false, Func<string, Texture2D> resolveTexture = null)
        {
            string manifestPath = Path.Combine(folder, ManifestName);
            if (!File.Exists(manifestPath)) throw new GpuAnimFormatException("unit.json is missing");
            UnitJson json = JsonUtility.FromJson<UnitJson>(File.ReadAllText(manifestPath));
            if (json == null) throw new GpuAnimFormatException("unit.json did not parse");
            TJBakeValidation.ValidateManifest(json, folder, resolveTexture != null);
            if (isRider != (json.kind == "rider")) throw new GpuAnimFormatException($"unit.json kind is '{json.kind}', expected '{(isRider ? "rider" : "unit")}'");
            CheckFolderSize(folder);

            var visual = new TJBakeVisual { Name = string.IsNullOrEmpty(json.unitName) ? Path.GetFileName(folder) : json.unitName, Kind = json.kind, Fps = json.frameRate };

            byte[] animFile = ReadFile(folder, "anim.bin", MaxAnimBytes);
            byte[] texels = AnimBin.Read(animFile, out int boneCount, out int frameCount);
            if (boneCount != json.skeleton.boneCount) throw new GpuAnimFormatException($"anim.bin has {boneCount} bones, unit.json says {json.skeleton.boneCount}");
            visual.BoneCount = boneCount;
            visual.FrameCount = frameCount;
            foreach (SlotJson s in json.slots)
            {
                if (s.start < 0 || s.count < 2 || s.start + s.count > frameCount) throw new GpuAnimFormatException($"slot {s.id} ({s.name}) frames {s.start}+{s.count} fall outside anim.bin's {frameCount}");
                visual.Slots.Add(new TJBakeSlot { Name = s.name, Start = s.start, Count = s.count, Fps = json.frameRate, Loop = s.loop, ReturnToIdle = s.returnToIdle, ReturnAt = math.clamp(s.returnAt, 0.5f, 1f) });
            }
            var tex = new Texture2D(boneCount * 3, frameCount, TextureFormat.RGBAHalf, false, true)
            {
                name = visual.Name + "_bones", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            tex.LoadRawTextureData(texels);
            tex.Apply(false, true);
            Own(owned, tex);
            visual.BoneTexture = tex;

            visual.AnchorNames.AddRange(json.anchors);
            if (json.anchors.Count > 0)
            {
                byte[] anchorFile = ReadFile(folder, "anchors.bin", MaxAnimBytes);
                visual.AnchorMatrices = AnchorsBin.Read(anchorFile, out int anchorCount, out int anchorFrames);
                if (anchorCount != json.anchors.Count) throw new GpuAnimFormatException($"anchors.bin has {anchorCount} anchors, unit.json lists {json.anchors.Count}");
                if (anchorFrames != frameCount) throw new GpuAnimFormatException($"anchors.bin has {anchorFrames} frames, anim.bin {frameCount}");
            }

            visual.SkinnedMaterials = new Material[json.materials.Count];
            visual.RigidMaterials = new Material[json.materials.Count];
            var textures = new Dictionary<string, Texture2D>();
            for (int i = 0; i < json.materials.Count; i++)
            {
                MaterialJson m = json.materials[i];
                Texture2D baseMap = LoadTexture(folder, m.baseColor, true, textures, owned, resolveTexture);
                Texture2D normalMap = LoadTexture(folder, m.normal, false, textures, owned, resolveTexture);
                Texture2D emissionMap = LoadTexture(folder, m.emission, true, textures, owned, resolveTexture);
                visual.SkinnedMaterials[i] = MakeMaterial(SkinnedShaderName, m, baseMap, normalMap, emissionMap, tex, owned);
                visual.RigidMaterials[i] = MakeMaterial(RigidShaderName, m, baseMap, normalMap, emissionMap, null, owned);
            }

            visual.LodSwitch = json.lodSwitch.ToArray();
            var meshes = new Dictionary<string, Mesh>();
            foreach (VariantJson v in json.variants)
            {
                var variant = new TJBakeVariant();
                foreach (LodJson lod in v.lods)
                {
                    var lodData = new TJBakeLod();
                    foreach (MeshRefJson mr in lod.meshes)
                    {
                        Mesh mesh = LoadMesh(folder, mr.file, boneCount, true, meshes, owned, out Bounds bounds);
                        lodData.Meshes.Add(new TJBakeMeshRef { Mesh = mesh, MaterialIndex = mr.material, Bounds = bounds });
                    }
                    variant.Lods.Add(lodData);
                }
                foreach (AttachmentJson a in v.attachments)
                {
                    Mesh mesh = LoadMesh(folder, a.file, boneCount, false, meshes, owned, out Bounds bounds);
                    int mask = 0;
                    foreach (int l in a.lods) if (l >= 0 && l < 8) mask |= 1 << l;
                    variant.Attachments.Add(new TJBakeAttachment
                    {
                        Mesh = mesh, MaterialIndex = a.material, AnchorIndex = json.anchors.IndexOf(a.anchor), Role = TJBakeValidation.ParseRole(a.role),
                        LodMask = mask == 0 ? 0b11 : mask, Bounds = bounds, Tag = a.tag ?? "",
                    });
                }
                visual.Variants.Add(variant);
            }

            if (!isRider && !string.IsNullOrEmpty(json.rider))
                visual.Rider = Load(Path.Combine(folder, json.rider.TrimEnd('/', '\\')), owned, true, resolveTexture);
            return visual;
        }

        #region Objects
        private static void CheckFolderSize(string folder)
        {
            long total = 0;
            foreach (string f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) total += new FileInfo(f).Length;
            if (total > MaxFolderBytes) throw new GpuAnimFormatException($"folder is {total / (1024 * 1024)} MB, the limit is {MaxFolderBytes / (1024 * 1024)} MB");
        }

        private static byte[] ReadFile(string folder, string file, long limit)
        {
            string path = Path.Combine(folder, file);
            var info = new FileInfo(path);
            if (!info.Exists) throw new GpuAnimFormatException($"{file} is missing");
            if (info.Length > limit) throw new GpuAnimFormatException($"{file} is {info.Length / (1024 * 1024)} MB, the limit is {limit / (1024 * 1024)} MB");
            return File.ReadAllBytes(path);
        }

        private static Texture2D LoadTexture(string folder, string file, bool srgb, Dictionary<string, Texture2D> cache, List<UnityEngine.Object> owned, Func<string, Texture2D> resolveTexture)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (cache.TryGetValue(file, out Texture2D cached)) return cached;
            if (file.StartsWith(TJBakeValidation.HostTexturePrefix))
            {
                Texture2D hosted = resolveTexture?.Invoke(file.Substring(TJBakeValidation.HostTexturePrefix.Length));
                if (hosted == null) throw new GpuAnimFormatException($"the host did not supply texture '{file}'");
                cache[file] = hosted;
                return hosted;
            }
            byte[] bytes = ReadFile(folder, file, MaxMeshBytes);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, !srgb) { name = file };
            if (!tex.LoadImage(bytes, true)) throw new GpuAnimFormatException($"{file} is not a PNG the engine can read");
            if (tex.width > 4096 || tex.height > 4096) throw new GpuAnimFormatException($"{file} is larger than 4096 on a side");
            Own(owned, tex);
            cache[file] = tex;
            return tex;
        }

        private static Material MakeMaterial(string shaderName, MaterialJson m, Texture2D baseMap, Texture2D normalMap, Texture2D emissionMap, Texture2D boneTex, List<UnityEngine.Object> owned)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new GpuAnimFormatException($"shader {shaderName} is not in this build");
            var mat = new Material(shader) { name = m.name };
            mat.SetTexture("_BaseMap", baseMap);
            var tint = m.tint != null && m.tint.Length == 4 ? new Color(m.tint[0], m.tint[1], m.tint[2], m.tint[3]) : Color.white;
            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Smoothness", math.saturate(m.smoothness));
            if (normalMap != null) { mat.SetTexture("_BumpMap", normalMap); mat.EnableKeyword("_NORMALMAP"); }
            if (emissionMap != null)
            {
                var glow = m.emissionColor != null && m.emissionColor.Length == 4 ? new Color(m.emissionColor[0], m.emissionColor[1], m.emissionColor[2], m.emissionColor[3]) : Color.white;
                mat.SetTexture("_EmissionMap", emissionMap); mat.SetColor("_EmissionColor", glow); mat.EnableKeyword("_EMISSION");
            }
            if (boneTex != null) mat.SetTexture("_BoneTex", boneTex);
            if (m.transparent)
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            Own(owned, mat);
            return mat;
        }

        private static Mesh LoadMesh(string folder, string file, int boneCount, bool skinnedExpected, Dictionary<string, Mesh> cache, List<UnityEngine.Object> owned, out Bounds bounds)
        {
            if (cache.TryGetValue(file, out Mesh cached)) { bounds = cached.bounds; return cached; }
            MeshBinData d = MeshBin.Read(ReadFile(folder, file, MaxMeshBytes), file, boneCount);
            if (d.Skinned != skinnedExpected) throw new GpuAnimFormatException($"{file} is {(d.Skinned ? "skinned" : "rigid")}, expected {(skinnedExpected ? "skinned" : "rigid")}");
            Mesh mesh = ToMesh(d, file);
            bounds = mesh.bounds;
            Own(owned, mesh);
            cache[file] = mesh;
            return mesh;
        }

        /// <summary>Builds a Mesh from file data. Bone indices ride in UV3 and weights in UV4 for the TJBake/Unit shader.</summary>
        // Owners destroy these explicitly; the unload sweep on every state change must not free them first.
        private static void Own(List<UnityEngine.Object> owned, UnityEngine.Object o)
        {
            o.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            owned.Add(o);
        }

        public static Mesh ToMesh(MeshBinData d, string name)
        {
            var mesh = new Mesh { name = name, indexFormat = d.Positions.Length > ushort.MaxValue ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(ToVector3(d.Positions));
            if (d.Normals != null) mesh.SetNormals(ToVector3(d.Normals));
            if (d.Tangents != null) mesh.SetTangents(ToVector4(d.Tangents));
            if (d.Uv0 != null) { var uv = new Vector2[d.Uv0.Length]; for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(d.Uv0[i].x, d.Uv0[i].y); mesh.SetUVs(0, uv); }
            if (d.Skinned)
            {
                var idx = new Vector4[d.BoneIndices.Length];
                for (int i = 0; i < idx.Length; i++) idx[i] = new Vector4(d.BoneIndices[i].x, d.BoneIndices[i].y, d.BoneIndices[i].z, d.BoneIndices[i].w);
                mesh.SetUVs(3, idx);
                mesh.SetUVs(4, ToVector4(d.BoneWeights));
            }
            mesh.subMeshCount = d.Submeshes.Length;
            for (int s = 0; s < d.Submeshes.Length; s++)
            {
                (int start, int count) = d.Submeshes[s];
                var sub = new int[count];
                Array.Copy(d.Indices, start, sub, 0, count);
                mesh.SetTriangles(sub, s, false);
            }
            if (d.Normals == null) mesh.RecalculateNormals();
            if (d.Tangents == null) mesh.RecalculateTangents();
            var bounds = new Bounds();
            bounds.SetMinMax(d.BoundsMin, d.BoundsMax);
            mesh.bounds = bounds;
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Vector3[] ToVector3(float3[] a) { var r = new Vector3[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        private static Vector4[] ToVector4(float4[] a) { var r = new Vector4[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        #endregion
    }
}
