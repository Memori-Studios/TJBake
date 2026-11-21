using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>A rigid piece fixed to a bone and baked into a figure, placed where the figure's current pose holds that bone.</summary>
    public sealed class TJBakeProp
    {
        /// <summary>A model's asset path.</summary>
        public string Model;
        public Color Color;

        /// <summary>The asset path of the atlas the piece is drawn on. Empty draws it flat in <see cref="Color"/>.</summary>
        public string Texture;
        public string Bone;

        /// <summary>From the bone, in the figure's space.</summary>
        public Vector3 Offset;

        /// <summary>Euler degrees in the figure's space, such as a pot turned upside down to sit on a head.</summary>
        public Vector3 Turn;
        public float Scale = 1f;
    }

    /// <summary>
    /// Turns a figure's rigid pieces into skinned meshes so they bake with its body, and folds a figure's skinned meshes
    /// into one a material. Every mesh and material made here goes in the caller's owned list.
    /// </summary>
    public static class TJBakeSkinning
    {
        #region Skinning
        /// <summary>
        /// A shown rigid mesh becomes a skinned mesh wholly on its parent bone, so it bakes with the body. The bake keeps
        /// one bind pose a bone, the body's, so the vertices go where that bind pose expects them, whatever pose the rig holds.
        /// </summary>
        public static void Skin(GameObject figure, MeshRenderer rigid, List<Object> owned)
        {
            var filter = rigid.GetComponent<MeshFilter>();
            Transform bone = rigid.transform.parent;
            if (!rigid.enabled || filter == null || filter.sharedMesh == null || bone == null) return;

            Mesh mesh = Object.Instantiate(filter.sharedMesh);
            mesh.name = filter.sharedMesh.name + " (skinned)";
            owned.Add(mesh);
            Matrix4x4 bind = BindPose(figure, bone);
            Matrix4x4 toBind = bind.inverse * bone.worldToLocalMatrix * rigid.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = toBind.MultiplyPoint3x4(vertices[i]);
            for (int i = 0; i < normals.Length; i++) normals[i] = toBind.MultiplyVector(normals[i]).normalized;
            mesh.vertices = vertices;
            mesh.normals = normals;
            var weights = new BoneWeight[vertices.Length];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { bind };
            mesh.RecalculateBounds();

            var skinned = new GameObject(rigid.name + " (skinned)").AddComponent<SkinnedMeshRenderer>();
            skinned.transform.SetParent(figure.transform, false);
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { bone };
            skinned.rootBone = bone;
            skinned.sharedMaterials = rigid.sharedMaterials;
            rigid.enabled = false;
        }

        private static Matrix4x4 BindPose(GameObject figure, Transform bone)
        {
            foreach (SkinnedMeshRenderer body in figure.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                int index = System.Array.IndexOf(body.bones, bone);
                if (index >= 0) return body.sharedMesh.bindposes[index];
            }
            return bone.worldToLocalMatrix * figure.transform.localToWorldMatrix;
        }
        #endregion

        #region Props
        /// <summary>
        /// Skins each piece onto its bone, drawn with one <see cref="TJBakeFigure.Flat"/> material per atlas or colour. Run
        /// it after the rest pose is set, so a piece sits where the standing figure holds its bone.
        /// </summary>
        public static void AddProps(GameObject figure, IEnumerable<TJBakeProp> props, float smoothness, List<Object> owned)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (Transform t in figure.GetComponentsInChildren<Transform>(true)) bones.TryAdd(t.name, t);
            var materials = new Dictionary<string, Material>();
            foreach (TJBakeProp prop in props)
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(prop.Model);
                if (mesh == null) throw new FileNotFoundException($"No model at {prop.Model}.");
                bool textured = !string.IsNullOrEmpty(prop.Texture);
                string key = textured ? prop.Texture : prop.Color.ToString();
                if (!materials.TryGetValue(key, out Material material))
                {
                    materials[key] = material = TJBakeFigure.Flat("Prop", textured ? Color.white : prop.Color, smoothness, owned);
                    if (textured) material.SetTexture("_BaseMap", TJBakeFigure.Load<Texture2D>(prop.Texture));
                }

                Transform bone = bones[prop.Bone];
                var rigid = new GameObject(mesh.name).AddComponent<MeshRenderer>();
                rigid.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                // Every submesh takes the piece's material; a slot left empty draws magenta.
                var slots = new Material[mesh.subMeshCount];
                for (int s = 0; s < slots.Length; s++) slots[s] = material;
                rigid.sharedMaterials = slots;
                rigid.transform.SetParent(bone, false);
                rigid.transform.SetPositionAndRotation(bone.position + figure.transform.TransformVector(prop.Offset), figure.transform.rotation * Quaternion.Euler(prop.Turn));
                rigid.transform.localScale = Vector3.one * prop.Scale;
                Skin(figure, rigid, owned);
            }
        }
        #endregion

        #region Merging
        /// <summary>Materials with the same key draw the same: the same base map and colour.</summary>
        public static string MaterialKey(Material material)
        {
            Texture texture = material.GetTexture("_BaseMap");
            return $"{(texture != null ? texture.name : "")};{material.GetColor("_BaseColor")}";
        }

        /// <summary>Folds the figure's skinned meshes into one a material, so pieces on the body's atlas cost no extra mesh to draw.</summary>
        public static void Merge(GameObject figure, List<Object> owned)
        {
            SkinnedMeshRenderer[] renderers = figure.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            SkinnedMeshRenderer rig = renderers.OrderByDescending(r => r.bones.Length).First();
            var bones = new List<Transform>(rig.bones);
            Matrix4x4[] binds = rig.sharedMesh.bindposes;
            // A mesh bound in another space than the rig's, or holding the rig's bones under it, stays as it is.
            SkinnedMeshRenderer[] joinable = renderers.Where(r => r.transform != figure.transform && r.transform.childCount == 0 && SameBind(r, bones, binds)).ToArray();
            Material[] used = joinable.SelectMany(r => Enumerable.Range(0, r.sharedMesh.subMeshCount).Select(s => SlotMaterial(r, s))).Distinct().ToArray();
            foreach (Material material in used)
            {
                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var tangents = new List<Vector4>();
                var uvs = new List<Vector2>();
                var weights = new List<BoneWeight>();
                var triangles = new List<int>();
                foreach (SkinnedMeshRenderer r in joinable)
                {
                    Mesh mesh = r.sharedMesh;
                    int[] tris = Enumerable.Range(0, mesh.subMeshCount).Where(s => SlotMaterial(r, s) == material).SelectMany(s => mesh.GetTriangles(s)).ToArray();
                    if (tris.Length == 0) continue;
                    int start = vertices.Count;
                    int count = mesh.vertexCount;
                    vertices.AddRange(mesh.vertices);
                    Vector3[] n = mesh.normals;
                    normals.AddRange(n.Length == count ? n : Enumerable.Repeat(Vector3.up, count));
                    Vector4[] t = mesh.tangents;
                    tangents.AddRange(t.Length == count ? t : Enumerable.Repeat(new Vector4(1f, 0f, 0f, 1f), count));
                    Vector2[] uv = mesh.uv;
                    uvs.AddRange(uv.Length == count ? uv : Enumerable.Repeat(Vector2.zero, count));
                    int[] map = System.Array.ConvertAll(r.bones, bones.IndexOf);
                    foreach (BoneWeight w in mesh.boneWeights)
                        weights.Add(new BoneWeight
                        {
                            boneIndex0 = map[w.boneIndex0], weight0 = w.weight0, boneIndex1 = map[w.boneIndex1], weight1 = w.weight1,
                            boneIndex2 = map[w.boneIndex2], weight2 = w.weight2, boneIndex3 = map[w.boneIndex3], weight3 = w.weight3,
                        });
                    triangles.AddRange(tris.Select(i => i + start));
                }
                var joined = new Mesh { name = $"{figure.name} {material.name}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                joined.SetVertices(vertices);
                joined.SetNormals(normals);
                joined.SetTangents(tangents);
                joined.SetUVs(0, uvs);
                joined.SetTriangles(triangles, 0);
                joined.boneWeights = weights.ToArray();
                joined.bindposes = binds;
                joined.RecalculateBounds();
                owned.Add(joined);
                var skinned = new GameObject(joined.name).AddComponent<SkinnedMeshRenderer>();
                skinned.transform.SetParent(figure.transform, false);
                skinned.sharedMesh = joined;
                skinned.bones = bones.ToArray();
                skinned.rootBone = rig.rootBone;
                skinned.sharedMaterial = material;
            }
            foreach (SkinnedMeshRenderer r in joinable) r.gameObject.SetActive(false);
        }

        // The sampler draws a submesh past the material list with the renderer's first material.
        private static Material SlotMaterial(SkinnedMeshRenderer renderer, int submesh)
        {
            Material[] slots = renderer.sharedMaterials;
            return submesh < slots.Length ? slots[submesh] : renderer.sharedMaterial;
        }

        private static bool SameBind(SkinnedMeshRenderer renderer, List<Transform> bones, Matrix4x4[] binds)
        {
            Matrix4x4[] own = renderer.sharedMesh.bindposes;
            for (int b = 0; b < renderer.bones.Length; b++)
            {
                int index = bones.IndexOf(renderer.bones[b]);
                if (index < 0 || b >= own.Length) return false;
                for (int e = 0; e < 16; e++)
                    if (Mathf.Abs(own[b][e] - binds[index][e]) > 1e-4f) return false;
            }
            return true;
        }
        #endregion
    }
}
