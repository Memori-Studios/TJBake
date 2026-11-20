using System;
using System.Collections.Generic;
using System.IO;
using MemoriStudios.TJBake.Format;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

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
            bool tangents = json.materials.Exists(m => !string.IsNullOrEmpty(m.normal));
            var meshes = new Dictionary<string, Mesh>();
            foreach (VariantJson v in json.variants)
            {
                var variant = new TJBakeVariant();
                foreach (LodJson lod in v.lods)
                {
                    var lodData = new TJBakeLod();
                    foreach (MeshRefJson mr in lod.meshes)
                    {
                        Mesh mesh = LoadMesh(folder, mr.file, boneCount, true, tangents, meshes, owned, out Bounds bounds);
                        lodData.Meshes.Add(new TJBakeMeshRef { Mesh = mesh, MaterialIndex = mr.material, Bounds = bounds });
                    }
                    variant.Lods.Add(lodData);
                }
                foreach (AttachmentJson a in v.attachments)
                {
                    Mesh mesh = LoadMesh(folder, a.file, boneCount, false, tangents, meshes, owned, out Bounds bounds);
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
            Texture2D tex = TJBakeTextureLease.Take(bytes, srgb, () => DecodeTexture(file, bytes, srgb), owned);
            cache[file] = tex;
            return tex;
        }

        private static Texture2D DecodeTexture(string file, byte[] bytes, bool srgb)
        {
            if (file.EndsWith(TexBin.Extension, StringComparison.OrdinalIgnoreCase))
            {
                byte[] blocks = TexBin.Read(bytes, file, out int width, out int height, out int mipCount);
                if (!SystemInfo.SupportsTextureFormat(TextureFormat.BC7)) throw new GpuAnimFormatException($"{file} is BC7, which this graphics device can't sample");
                var compressed = new Texture2D(width, height, TextureFormat.BC7, mipCount, !srgb) { name = file };
                compressed.LoadRawTextureData(blocks);
                compressed.Apply(false, true);
                return compressed;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, !srgb) { name = file };
            string problem = !tex.LoadImage(bytes, true) ? $"{file} is not a PNG the engine can read"
                : tex.width > 4096 || tex.height > 4096 ? $"{file} is larger than 4096 on a side" : null;
            if (problem == null) return tex;
            DestroyNow(tex);
            throw new GpuAnimFormatException(problem);
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

        private static Mesh LoadMesh(string folder, string file, int boneCount, bool skinnedExpected, bool tangents, Dictionary<string, Mesh> cache, List<UnityEngine.Object> owned, out Bounds bounds)
        {
            if (cache.TryGetValue(file, out Mesh cached)) { bounds = cached.bounds; return cached; }
            MeshBinData d = MeshBin.Read(ReadFile(folder, file, MaxMeshBytes), file, boneCount);
            if (d.Skinned != skinnedExpected) throw new GpuAnimFormatException($"{file} is {(d.Skinned ? "skinned" : "rigid")}, expected {(skinnedExpected ? "skinned" : "rigid")}");
            Mesh mesh = ToMesh(d, file, tangents);
            bounds = mesh.bounds;
            Own(owned, mesh);
            cache[file] = mesh;
            return mesh;
        }

        /// <summary>Destroys an owned object now in edit mode, at the frame's end in Play.</summary>
        internal static void DestroyNow(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        // Owners destroy these explicitly; the unload sweep on every state change must not free them first.
        private static void Own(List<UnityEngine.Object> owned, UnityEngine.Object o)
        {
            o.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            owned.Add(o);
        }
        #endregion

        #region Packed meshes
        // Half floats hold about three digits: past these sizes a unit's shape or a texel would visibly move.
        private const float HalfPositionLimit = 32f, HalfUvLimit = 2f, HalfIndexLimit = 2048f;

        /// <summary>Builds a Mesh from file data, packed small: half positions, UVs and bone indices; 8-bit normals, weights
        /// and smoothed normals. Bone indices ride in UV3, weights in UV4 and smoothed normals in UV5 for the TJBake/Unit
        /// shader. Tangents are kept only when <paramref name="tangents"/> asks for them, for a normal map.</summary>
        public static Mesh ToMesh(MeshBinData d, string name, bool tangents)
        {
            int count = d.Positions.Length;
            float3[] normals = d.Normals;
            float4[] tangentData = tangents ? d.Tangents : null;
            if (normals == null || (tangents && tangentData == null)) Recalculate(d, tangents, ref normals, ref tangentData);

            var mesh = new Mesh { name = name, indexFormat = count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            var layout = new List<VertexAttributeDescriptor>
            {
                MaxAbs(d.Positions) <= HalfPositionLimit
                    ? new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 4)
                    : new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            };
            if (tangentData != null) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm8, 4));
            if (d.Uv0 != null) layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord0, MaxAbs(d.Uv0) <= HalfUvLimit ? VertexAttributeFormat.Float16 : VertexAttributeFormat.Float32, 2));
            if (d.Skinned)
            {
                layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord3, MaxIndex(d.BoneIndices) < HalfIndexLimit ? VertexAttributeFormat.Float16 : VertexAttributeFormat.Float32, 4));
                layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.UNorm8, 4));
                layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord5, VertexAttributeFormat.SNorm8, 4));
            }
            mesh.SetVertexBufferParams(count, layout.ToArray());

            var bytes = new byte[count * mesh.GetVertexBufferStride(0)];
            Pack(bytes, mesh, VertexAttribute.Position, i => new float4(d.Positions[i], 1f));
            Pack(bytes, mesh, VertexAttribute.Normal, i => new float4(normals[i], 0f));
            Pack(bytes, mesh, VertexAttribute.Tangent, i => tangentData[i]);
            Pack(bytes, mesh, VertexAttribute.TexCoord0, i => new float4(d.Uv0[i], 0f, 0f));
            if (d.Skinned)
            {
                float3[] smooth = SmoothNormals(d.Positions, normals);
                Pack(bytes, mesh, VertexAttribute.TexCoord3, i => d.BoneIndices[i]);
                Pack(bytes, mesh, VertexAttribute.TexCoord4, i => WholeWeights(d.BoneWeights[i]));
                Pack(bytes, mesh, VertexAttribute.TexCoord5, i => new float4(smooth[i], 0f));
            }
            mesh.SetVertexBufferData(bytes, 0, 0, bytes.Length);
            SetSubmeshes(mesh, d);
            var bounds = new Bounds();
            bounds.SetMinMax(d.BoundsMin, d.BoundsMax);
            mesh.bounds = bounds;
            mesh.UploadMeshData(true);
            return mesh;
        }

        // Normals and tangents the file leaves out, from the engine's own recalculation on a plain float copy.
        private static void Recalculate(MeshBinData d, bool tangents, ref float3[] normals, ref float4[] tangentData)
        {
            var temp = new Mesh { indexFormat = IndexFormat.UInt32 };
            var v3 = new Vector3[d.Positions.Length];
            for (int i = 0; i < v3.Length; i++) v3[i] = d.Positions[i];
            temp.SetVertices(v3);
            if (normals != null)
            {
                for (int i = 0; i < v3.Length; i++) v3[i] = normals[i];
                temp.SetNormals(v3);
            }
            if (d.Uv0 != null)
            {
                var uv = new Vector2[d.Uv0.Length];
                for (int i = 0; i < uv.Length; i++) uv[i] = d.Uv0[i];
                temp.SetUVs(0, uv);
            }
            SetSubmeshes(temp, d);
            if (normals == null)
            {
                temp.RecalculateNormals();
                Vector3[] n = temp.normals;
                normals = new float3[n.Length];
                for (int i = 0; i < n.Length; i++) normals[i] = n[i];
            }
            if (tangents && tangentData == null)
            {
                temp.RecalculateTangents();
                Vector4[] t = temp.tangents;
                tangentData = new float4[t.Length];
                for (int i = 0; i < t.Length; i++) tangentData[i] = t[i];
            }
            DestroyNow(temp);
        }

        private static void SetSubmeshes(Mesh mesh, MeshBinData d)
        {
            mesh.subMeshCount = d.Submeshes.Length;
            for (int s = 0; s < d.Submeshes.Length; s++)
            {
                (int start, int count) = d.Submeshes[s];
                var sub = new int[count];
                Array.Copy(d.Indices, start, sub, 0, count);
                mesh.SetTriangles(sub, s, false);
            }
        }

        // Writes one attribute of every vertex in the format the layout gave it; an attribute the layout left out writes nothing.
        private static void Pack(byte[] bytes, Mesh mesh, VertexAttribute attribute, Func<int, float4> value)
        {
            if (!mesh.HasVertexAttribute(attribute)) return;
            int stride = mesh.GetVertexBufferStride(0), dim = mesh.GetVertexAttributeDimension(attribute), count = mesh.vertexCount;
            VertexAttributeFormat format = mesh.GetVertexAttributeFormat(attribute);
            for (int v = 0, at = mesh.GetVertexAttributeOffset(attribute); v < count; v++, at += stride)
            {
                float4 x = value(v);
                for (int c = 0; c < dim; c++)
                {
                    switch (format)
                    {
                        case VertexAttributeFormat.Float32:
                            int bits = math.asint(x[c]);
                            for (int b = 0; b < 4; b++) bytes[at + c * 4 + b] = (byte)(bits >> (8 * b));
                            break;
                        case VertexAttributeFormat.Float16:
                            uint half = math.f32tof16(x[c]);
                            bytes[at + c * 2] = (byte)half;
                            bytes[at + c * 2 + 1] = (byte)(half >> 8);
                            break;
                        case VertexAttributeFormat.SNorm8: bytes[at + c] = (byte)(sbyte)math.round(math.clamp(x[c], -1f, 1f) * 127f); break;
                        case VertexAttributeFormat.UNorm8: bytes[at + c] = (byte)math.round(math.saturate(x[c]) * 255f); break;
                        default: throw new NotSupportedException($"no packer for {format}");
                    }
                }
            }
        }

        // Weights in whole 255ths that still add up to one, so 8-bit weights never pull a vertex toward the bone origin.
        private static float4 WholeWeights(float4 w)
        {
            float sum = math.csum(w);
            if (sum <= 0f) return float4.zero;
            float4 q = math.round(w / sum * 255f);
            int largest = 0;
            for (int c = 1; c < 4; c++) if (w[c] > w[largest]) largest = c;
            q[largest] += 255f - math.csum(q);
            return q / 255f;
        }

        // Each vertex's normal averaged with every other at the same spot, for TJBake/Unit's outline: a hard-edged mesh
        // pushed out along its own normals would split apart at every edge.
        private static float3[] SmoothNormals(float3[] vertices, float3[] normals)
        {
            var sums = new Dictionary<float3, float3>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                sums.TryGetValue(vertices[i], out float3 sum);
                sums[vertices[i]] = sum + normals[i];
            }
            var smooth = new float3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) smooth[i] = math.normalizesafe(sums[vertices[i]]);
            return smooth;
        }

        private static float MaxAbs(float3[] a) { float m = 0f; foreach (float3 x in a) m = math.max(m, math.cmax(math.abs(x))); return m; }
        private static float MaxAbs(float2[] a) { float m = 0f; foreach (float2 x in a) m = math.max(m, math.cmax(math.abs(x))); return m; }
        private static uint MaxIndex(uint4[] a) { uint m = 0; foreach (uint4 x in a) m = math.max(m, math.cmax(x)); return m; }
        #endregion
    }
}
