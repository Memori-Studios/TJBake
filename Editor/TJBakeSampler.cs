using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MemoriStudios.TJBake.Format;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    public sealed class TJBakeSlotRequest
    {
        public string Name = "";
        public AnimationClip Clip;
        public bool Loop;
        public bool ReturnToIdle;
        // Negative means: take the first animation event on the clip, else the request default.
        public float ReturnAt = -1f;
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
        public float DefaultReturnAt = 0.9f;
        public string BakerName = "TJBake";
        // An instance (scene or preview scene) of the rigged prefab; its active SkinnedMeshRenderers form LOD0.
        public GameObject Root;
        // Instances of lower-detail rigs sharing the skeleton by bone name, one per extra LOD.
        public List<GameObject> LodRoots = new();
        public List<float> LodSwitch = new();
        public List<TJBakeSlotRequest> Slots = new();
        public List<TJBakeAnchorRequest> Anchors = new();
        public List<TJBakeAttachmentRequest> Attachments = new();
        public TJBakeRequest Rider;
    }

    /// <summary>Samples a rig's clips into the TJBake file format: bone matrices per frame, anchor matrices, meshes, textures and the manifest.</summary>
    public static class TJBakeSampler
    {
        #region Entry
        public static UnitJson Bake(TJBakeRequest r, StringBuilder report)
        {
            if (r.Root == null) throw new ArgumentException("the request has no root instance");
            if (string.IsNullOrEmpty(r.OutputFolder)) throw new ArgumentException("the request has no output folder");
            Directory.CreateDirectory(r.OutputFolder);
            UnitJson json = BakeOne(r, report);
            if (r.Rider != null)
            {
                r.Rider.IsRider = true;
                r.Rider.OutputFolder = Path.Combine(r.OutputFolder, "rider");
                Directory.CreateDirectory(r.Rider.OutputFolder);
                report.AppendLine("rider:");
                UnitJson riderJson = BakeOne(r.Rider, report);
                File.WriteAllText(Path.Combine(r.Rider.OutputFolder, TJBakeVisualLoader.ManifestName), JsonUtility.ToJson(riderJson, true));
                json.rider = "rider/";
            }
            File.WriteAllText(Path.Combine(r.OutputFolder, TJBakeVisualLoader.ManifestName), JsonUtility.ToJson(json, true));
            TJBakeValidation.ValidateManifest(json, r.OutputFolder);
            return json;
        }
        #endregion

        #region Skeleton and bodies
        private sealed class Skeleton
        {
            public readonly List<Transform> Bones = new();
            public readonly List<Matrix4x4> BindPoses = new();
            private readonly Dictionary<Transform, int> _byTransform = new();
            private readonly Dictionary<string, int> _byName = new();

            public int Add(Transform bone, Matrix4x4 bindPose)
            {
                if (_byTransform.TryGetValue(bone, out int i)) return i;
                i = Bones.Count;
                Bones.Add(bone);
                BindPoses.Add(bindPose);
                _byTransform[bone] = i;
                if (!_byName.ContainsKey(bone.name)) _byName[bone.name] = i;
                return i;
            }

            public bool TryByName(string name, out int index) => _byName.TryGetValue(name, out index);
        }

        private sealed class BodyPart
        {
            public MeshBinData Mesh;
            public Material Material;
            public int Lod;
            public string File;
            // LOD meshes carry their own bind poses, so skinning uses them instead of LOD0's.
            public Matrix4x4[] BindPoses;
            public int[] BoneMap;
        }

        private static List<BodyPart> ReadBodies(GameObject root, Skeleton skeleton, int lod, bool matchByName)
        {
            var parts = new List<BodyPart>();
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || smr.bones == null || smr.bones.Length == 0) continue;
                var map = new int[smr.bones.Length];
                var bind = new Matrix4x4[smr.bones.Length];
                for (int b = 0; b < smr.bones.Length; b++)
                {
                    Transform bone = smr.bones[b];
                    bind[b] = b < mesh.bindposes.Length ? mesh.bindposes[b] : Matrix4x4.identity;
                    if (bone == null) { map[b] = 0; continue; }
                    if (matchByName)
                    {
                        if (!skeleton.TryByName(bone.name, out map[b])) throw new Exception($"LOD{lod} bone '{bone.name}' is not in the LOD0 skeleton");
                    }
                    else map[b] = skeleton.Add(bone, bind[b]);
                }

                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector4[] tangents = mesh.tangents;
                Vector2[] uv = mesh.uv;
                var perVertex = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                var boneIndices = new uint4[vertices.Length];
                var boneWeights = new float4[vertices.Length];
                var localIndices = new int4[vertices.Length];
                int cursor = 0;
                for (int v = 0; v < vertices.Length; v++)
                {
                    int n = perVertex[v];
                    var idx = new uint4(0, 0, 0, 0);
                    var local = new int4(0, 0, 0, 0);
                    var w = new float4(0, 0, 0, 0);
                    // Unity stores each vertex's influences by descending weight, so the first four are the strongest.
                    for (int k = 0; k < n && k < 4; k++)
                    {
                        BoneWeight1 bw = weights[cursor + k];
                        idx[k] = (uint)map[bw.boneIndex];
                        local[k] = bw.boneIndex;
                        w[k] = bw.weight;
                    }
                    cursor += n;
                    float sum = w.x + w.y + w.z + w.w;
                    boneIndices[v] = idx;
                    localIndices[v] = local;
                    boneWeights[v] = sum > 0f ? w / sum : new float4(1, 0, 0, 0);
                }
                perVertex.Dispose();
                weights.Dispose();

                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    int[] tris = mesh.GetTriangles(s);
                    if (tris.Length == 0) continue;
                    var data = new MeshBinData
                    {
                        Positions = ToFloat3(vertices),
                        Normals = normals != null && normals.Length == vertices.Length ? ToFloat3(normals) : null,
                        Tangents = tangents != null && tangents.Length == vertices.Length ? ToFloat4(tangents) : null,
                        Uv0 = uv != null && uv.Length == vertices.Length ? ToFloat2(uv) : null,
                        BoneIndices = boneIndices,
                        BoneWeights = boneWeights,
                        Indices = tris,
                    };
                    Material material = smr.sharedMaterials != null && s < smr.sharedMaterials.Length ? smr.sharedMaterials[s] : smr.sharedMaterial;
                    parts.Add(new BodyPart { Mesh = data, Material = material, Lod = lod, BindPoses = bind, BoneMap = map });
                }
            }
            if (parts.Count == 0) throw new Exception($"LOD{lod} has no active skinned mesh");
            return parts;
        }
        #endregion

        #region Attachments
        private sealed class PropPart
        {
            public MeshBinData Mesh;
            public Material Material;
            public TJBakeAttachmentRequest Request;
            public string File;
        }

        private static List<PropPart> ReadProps(TJBakeRequest r, StringBuilder report)
        {
            var props = new List<PropPart>();
            foreach (TJBakeAttachmentRequest a in r.Attachments)
            {
                if (a.Holder == null) throw new Exception($"attachment '{a.Name}' has no holder transform");
                MeshFilter[] filters = a.Holder.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length == 0)
                {
                    if (a.Role == TJBakePropRole.Prop) { report.AppendLine($"  {a.Name}: no mesh and no role, skipped"); continue; }
                    // A saddle or hidden hand point carries no mesh; the entity still has to exist for the host's set-up.
                    var tiny = new[] { new float3(0, 0, 0), new float3(0.001f, 0, 0), new float3(0, 0.001f, 0) };
                    props.Add(new PropPart
                    {
                        Mesh = new MeshBinData { Positions = tiny, Normals = new[] { new float3(0, 1, 0), new float3(0, 1, 0), new float3(0, 1, 0) }, Indices = new[] { 0, 1, 2 }, BoundsMin = new float3(-0.01f), BoundsMax = new float3(0.01f) },
                        Material = null, Request = a,
                    });
                    report.AppendLine($"  {a.Name}: {TJBakeValidation.RoleName(a.Role)} without a mesh, placeholder written");
                    continue;
                }
                foreach (MeshFilter mf in filters)
                {
                    Mesh mesh = mf.sharedMesh;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mesh == null || mr == null) continue;
                    Matrix4x4 local = a.Holder.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    Vector3[] vertices = mesh.vertices;
                    Vector3[] normals = mesh.normals;
                    Vector4[] tangents = mesh.tangents;
                    var pos = new float3[vertices.Length];
                    var nrm = normals != null && normals.Length == vertices.Length ? new float3[vertices.Length] : null;
                    var tan = tangents != null && tangents.Length == vertices.Length ? new float4[vertices.Length] : null;
                    var min = new float3(float.MaxValue); var max = new float3(float.MinValue);
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        pos[i] = local.MultiplyPoint3x4(vertices[i]);
                        min = math.min(min, pos[i]); max = math.max(max, pos[i]);
                        if (nrm != null) nrm[i] = math.normalize((float3)local.MultiplyVector(normals[i]));
                        if (tan != null) { float3 t = math.normalize((float3)local.MultiplyVector(tangents[i])); tan[i] = new float4(t, tangents[i].w); }
                    }
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        int[] tris = mesh.GetTriangles(s);
                        if (tris.Length == 0) continue;
                        props.Add(new PropPart
                        {
                            Mesh = new MeshBinData { Positions = pos, Normals = nrm, Tangents = tan, Uv0 = ToFloat2(mesh.uv), Indices = tris, BoundsMin = min, BoundsMax = max },
                            Material = mr.sharedMaterials != null && s < mr.sharedMaterials.Length ? mr.sharedMaterials[s] : mr.sharedMaterial,
                            Request = a,
                        });
                    }
                }
            }
            return props;
        }
        #endregion

        #region Sampling
        private static UnitJson BakeOne(TJBakeRequest r, StringBuilder report)
        {
            int required = r.IsRider ? TJBakeValidation.RiderSlotCount : TJBakeValidation.UnitSlotCount;
            if (r.Slots.Count < required) throw new Exception($"{r.Slots.Count} slots, {required} needed");
            for (int i = 0; i < r.Slots.Count; i++) if (r.Slots[i].Clip == null) throw new Exception($"slot {i} ({r.Slots[i].Name}) has no clip");
            if (r.LodRoots.Count > 2) throw new Exception("at most 3 LODs");
            if (r.LodSwitch.Count != r.LodRoots.Count) throw new Exception($"lodSwitch needs {r.LodRoots.Count} entries for {r.LodRoots.Count + 1} LODs");

            var skeleton = new Skeleton();
            var bodies = ReadBodies(r.Root, skeleton, 0, false);
            for (int l = 0; l < r.LodRoots.Count; l++) bodies.AddRange(ReadBodies(r.LodRoots[l], skeleton, l + 1, true));
            List<PropPart> props = ReadProps(r, report);
            var anchorNames = new List<string>();
            foreach (TJBakeAnchorRequest a in r.Anchors)
            {
                if (string.IsNullOrEmpty(a.Name) || anchorNames.Contains(a.Name)) throw new Exception($"anchor name '{a.Name}' is empty or repeated");
                anchorNames.Add(a.Name);
            }
            foreach (PropPart p in props) if (!anchorNames.Contains(p.Request.Anchor)) throw new Exception($"attachment '{p.Request.Name}' names unknown anchor '{p.Request.Anchor}'");

            var json = new UnitJson { kind = r.IsRider ? "rider" : "unit", unitName = r.IsRider ? "" : r.UnitName, baker = r.BakerName, frameRate = r.Fps };
            json.skeleton.boneCount = skeleton.Bones.Count;
            foreach (Transform b in skeleton.Bones) json.skeleton.names.Add(b.name);
            json.anchors.AddRange(anchorNames);
            json.lodSwitch.AddRange(r.LodSwitch);

            // Frame table.
            int totalFrames = 0;
            var frameCounts = new int[r.Slots.Count];
            for (int i = 0; i < r.Slots.Count; i++)
            {
                TJBakeSlotRequest s = r.Slots[i];
                AnimationClip clip = s.Clip;
                int count = Mathf.Max(2, s.Loop ? Mathf.RoundToInt(clip.length * r.Fps) : Mathf.RoundToInt(clip.length * r.Fps) + 1);
                frameCounts[i] = count;
                float returnAt = s.ReturnAt >= 0f ? s.ReturnAt : ReturnAtFromEvents(clip, r.DefaultReturnAt);
                json.slots.Add(new SlotJson { id = i, name = s.Name, start = totalFrames, count = count, loop = s.Loop, returnToIdle = s.ReturnToIdle, returnAt = s.ReturnToIdle ? returnAt : 0.9f });
                totalFrames += count;
            }
            if (totalFrames > AnimBin.MaxFrames) throw new Exception($"{totalFrames} frames in total, the limit is {AnimBin.MaxFrames}");
            if (skeleton.Bones.Count > AnimBin.MaxBones) throw new Exception($"{skeleton.Bones.Count} bones, the limit is {AnimBin.MaxBones}");

            // Sample every frame: skinning matrices, anchor matrices, and posed vertices for the bounds.
            int boneCount = skeleton.Bones.Count;
            var matrices = new float3x4[totalFrames * boneCount];
            var anchorMatrices = new float3x4[totalFrames * anchorNames.Count];
            var boneWorld = new Matrix4x4[boneCount];
            var skinLod0 = new Matrix4x4[boneCount];
            var bodyMin = new float3[bodies.Count]; var bodyMax = new float3[bodies.Count];
            for (int b = 0; b < bodies.Count; b++) { bodyMin[b] = new float3(float.MaxValue); bodyMax[b] = new float3(float.MinValue); }
            Matrix4x4 scale = Matrix4x4.Scale(new Vector3(r.RootScale, r.RootScale, r.RootScale));
            Matrix4x4 rootInv = r.Root.transform.worldToLocalMatrix;
            var lodRootInv = new Matrix4x4[r.LodRoots.Count];
            for (int l = 0; l < r.LodRoots.Count; l++) lodRootInv[l] = r.LodRoots[l].transform.worldToLocalMatrix;
            int frame = 0;
            for (int s = 0; s < r.Slots.Count; s++)
            {
                AnimationClip clip = r.Slots[s].Clip;
                bool loop = r.Slots[s].Loop;
                for (int f = 0; f < frameCounts[s]; f++, frame++)
                {
                    float t = loop ? (f / (float)r.Fps) % Mathf.Max(clip.length, 0.0001f) : Mathf.Min(f / (float)r.Fps, clip.length);
                    clip.SampleAnimation(r.Root, t);
                    foreach (GameObject lodRoot in r.LodRoots) clip.SampleAnimation(lodRoot, t);
                    for (int b = 0; b < boneCount; b++)
                    {
                        boneWorld[b] = scale * rootInv * skeleton.Bones[b].localToWorldMatrix;
                        skinLod0[b] = boneWorld[b] * skeleton.BindPoses[b];
                        matrices[frame * boneCount + b] = Rows(skinLod0[b]);
                    }
                    for (int a = 0; a < r.Anchors.Count; a++)
                    {
                        Matrix4x4 m = r.Anchors[a].Transform != null ? scale * rootInv * r.Anchors[a].Transform.localToWorldMatrix : scale;
                        anchorMatrices[frame * anchorNames.Count + a] = Rows(m);
                    }
                    for (int b = 0; b < bodies.Count; b++) Union(bodies[b], skinLod0, ref bodyMin[b], ref bodyMax[b]);
                }
            }
            AnimBin.Write(Path.Combine(r.OutputFolder, "anim.bin"), boneCount, totalFrames, matrices);
            if (anchorNames.Count > 0) AnchorsBin.Write(Path.Combine(r.OutputFolder, "anchors.bin"), anchorNames.Count, totalFrames, anchorMatrices);

            // A LOD mesh skinned with LOD0's matrices needs its vertices moved from its own bind space into LOD0's.
            foreach (BodyPart part in bodies) if (part.Lod > 0) RebindToLod0(part, skeleton);

            // Materials, meshes, props, manifest.
            var materialIndex = new Dictionary<Material, int>();
            var lods = new List<LodJson>();
            for (int l = 0; l <= r.LodRoots.Count; l++) lods.Add(new LodJson());
            for (int b = 0; b < bodies.Count; b++)
            {
                BodyPart part = bodies[b];
                float3 pad = 0.05f * (bodyMax[b] - bodyMin[b]);
                part.Mesh.BoundsMin = bodyMin[b] - pad;
                part.Mesh.BoundsMax = bodyMax[b] + pad;
                part.File = $"body_{b}.mesh.bin";
                MeshBin.Write(Path.Combine(r.OutputFolder, part.File), part.Mesh);
                lods[part.Lod].meshes.Add(new MeshRefJson { file = part.File, material = MaterialSlot(part.Material, materialIndex, json, r.OutputFolder, report) });
            }
            var variant = new VariantJson();
            variant.lods.AddRange(lods);
            for (int p = 0; p < props.Count; p++)
            {
                PropPart part = props[p];
                part.File = $"attach_{p}.mesh.bin";
                MeshBin.Write(Path.Combine(r.OutputFolder, part.File), part.Mesh);
                var lodList = new List<int>();
                for (int l = 0; l < 8; l++) if ((part.Request.LodMask & (1 << l)) != 0) lodList.Add(l);
                variant.attachments.Add(new AttachmentJson
                {
                    file = part.File, material = MaterialSlot(part.Material, materialIndex, json, r.OutputFolder, report),
                    anchor = part.Request.Anchor, role = TJBakeValidation.RoleName(part.Request.Role), lods = lodList,
                });
            }
            json.variants.Add(variant);
            report.AppendLine($"{r.UnitName}{(r.IsRider ? " rider" : "")}: {boneCount} bones, {totalFrames} frames, {bodies.Count} body meshes over {lods.Count} LOD(s), {props.Count} props, {json.materials.Count} materials, scale {r.RootScale}");
            return json;
        }

        private static float ReturnAtFromEvents(AnimationClip clip, float fallback)
        {
            // Clips also carry hit and sound events earlier in the swing; only an event named for the return counts.
            if (clip.length <= 0f) return fallback;
            foreach (AnimationEvent e in AnimationUtility.GetAnimationEvents(clip))
            {
                bool isReturn = e.stringParameter.IndexOf("ReturnToIdle", StringComparison.OrdinalIgnoreCase) >= 0
                    || e.functionName.IndexOf("ReturnToIdle", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isReturn) return Mathf.Clamp(e.time / clip.length, 0.5f, 1f);
            }
            return fallback;
        }

        // The skinning matrix maps LOD0 bind space to the pose; a LOD mesh's own bind pose differs, so fold the difference into its vertices.
        private static void RebindToLod0(BodyPart part, Skeleton skeleton)
        {
            var fix = new Matrix4x4[part.BindPoses.Length];
            for (int b = 0; b < fix.Length; b++) fix[b] = skeleton.BindPoses[part.BoneMap[b]].inverse * part.BindPoses[b];
            MeshBinData m = part.Mesh;
            for (int v = 0; v < m.Positions.Length; v++)
            {
                float4 w = m.BoneWeights[v];
                // The strongest influence decides; the bind poses of one rig differ only by rigid offsets.
                int local = LocalBone(part, v, w);
                Matrix4x4 f = fix[local];
                m.Positions[v] = f.MultiplyPoint3x4(m.Positions[v]);
                if (m.Normals != null) m.Normals[v] = math.normalize((float3)f.MultiplyVector(m.Normals[v]));
                if (m.Tangents != null) { float3 t = math.normalize((float3)f.MultiplyVector(m.Tangents[v].xyz)); m.Tangents[v] = new float4(t, m.Tangents[v].w); }
            }
        }

        private static int LocalBone(BodyPart part, int v, float4 w)
        {
            uint4 g = part.Mesh.BoneIndices[v];
            int best = 0; float bestW = w.x;
            if (w.y > bestW) { best = 1; bestW = w.y; }
            if (w.z > bestW) { best = 2; bestW = w.z; }
            if (w.w > bestW) { best = 3; }
            uint global = best == 0 ? g.x : best == 1 ? g.y : best == 2 ? g.z : g.w;
            for (int b = 0; b < part.BoneMap.Length; b++) if (part.BoneMap[b] == (int)global) return b;
            return 0;
        }

        private static void Union(BodyPart part, Matrix4x4[] skin, ref float3 min, ref float3 max)
        {
            MeshBinData mesh = part.Mesh;
            // A few thousand vertices per frame are enough for a bounds box; the 5% pad covers the rest.
            int step = math.max(1, mesh.Positions.Length / 4000);
            for (int v = 0; v < mesh.Positions.Length; v += step)
            {
                uint4 idx = mesh.BoneIndices[v]; float4 w = mesh.BoneWeights[v];
                Vector3 p = mesh.Positions[v];
                Vector3 posed = Vector3.zero;
                if (w.x > 0f) posed += w.x * skin[(int)idx.x].MultiplyPoint3x4(p);
                if (w.y > 0f) posed += w.y * skin[(int)idx.y].MultiplyPoint3x4(p);
                if (w.z > 0f) posed += w.z * skin[(int)idx.z].MultiplyPoint3x4(p);
                if (w.w > 0f) posed += w.w * skin[(int)idx.w].MultiplyPoint3x4(p);
                min = math.min(min, (float3)posed); max = math.max(max, (float3)posed);
            }
        }

        private static float3x4 Rows(Matrix4x4 m) => new float3x4(
            new float3(m.m00, m.m10, m.m20), new float3(m.m01, m.m11, m.m21), new float3(m.m02, m.m12, m.m22), new float3(m.m03, m.m13, m.m23));
        #endregion

        #region Materials and textures
        private static int MaterialSlot(Material material, Dictionary<Material, int> index, UnitJson json, string folder, StringBuilder report)
        {
            if (material == null) material = new Material(Shader.Find(TJBakeVisualLoader.RigidShaderName));
            if (index.TryGetValue(material, out int i)) return i;
            i = json.materials.Count;
            index[material] = i;
            string baseName = $"mat_{i}_base.png";
            Texture2D baseMap = FindBaseMap(material);
            if (baseMap == null) WriteSolidPng(Path.Combine(folder, baseName), material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white);
            else ExportPng(baseMap, Path.Combine(folder, baseName));
            Color tint = material.HasProperty("_BaseColor") && baseMap != null ? material.GetColor("_BaseColor") : Color.white;
            var entry = new MaterialJson
            {
                name = material.name, baseColor = baseName, tint = new[] { tint.r, tint.g, tint.b, tint.a },
                smoothness = material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness") : 0.2f,
                transparent = material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f,
            };
            Texture2D normal = material.HasProperty("_BumpMap") ? material.GetTexture("_BumpMap") as Texture2D : null;
            if (normal != null && material.IsKeywordEnabled("_NORMALMAP")) { entry.normal = $"mat_{i}_normal.png"; ExportPng(normal, Path.Combine(folder, entry.normal)); }
            Texture2D emission = material.HasProperty("_EmissionMap") ? material.GetTexture("_EmissionMap") as Texture2D : null;
            if (emission != null && material.IsKeywordEnabled("_EMISSION")) { entry.emission = $"mat_{i}_emission.png"; ExportPng(emission, Path.Combine(folder, entry.emission)); }
            json.materials.Add(entry);
            report.AppendLine($"  material {i}: {material.name} base={(baseMap != null ? baseMap.name : "solid")}");
            return i;
        }

        public static Texture2D FindBaseMap(Material material)
        {
            foreach (string name in new[] { "_BaseMap", "_MainTex", "_Base", "_Albedo", "_Texture" })
                if (material.HasProperty(name) && material.GetTexture(name) is Texture2D t) return t;
            foreach (string name in material.GetTexturePropertyNames())
            {
                string lower = name.ToLowerInvariant();
                if (lower.Contains("normal") || lower.Contains("bump") || lower.Contains("emission") || lower.Contains("metallic") || lower.Contains("bone") || lower.Contains("lightmap") || lower.Contains("shadowmask")) continue;
                if (material.GetTexture(name) is Texture2D t) return t;
            }
            return null;
        }

        private static void ExportPng(Texture2D texture, string path)
        {
            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { File.Copy(assetPath, path, true); return; }
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, rt);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            readable.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(path, readable.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(readable);
        }

        private static void WriteSolidPng(string path, Color color)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = color;
            tex.SetPixels(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
        #endregion

        private static float3[] ToFloat3(Vector3[] a) { var r = new float3[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        private static float4[] ToFloat4(Vector4[] a) { var r = new float4[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
        private static float2[] ToFloat2(Vector2[] a) { if (a == null || a.Length == 0) return null; var r = new float2[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = a[i]; return r; }
    }
}
