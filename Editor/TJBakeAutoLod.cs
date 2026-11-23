using System.Collections.Generic;
using MemoriStudios.TJBake.Format;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>
    /// A lighter copy of a body mesh for a far LOD, made with Unity's Mesh LOD simplifier: the same bones, weights and
    /// atlas, fewer triangles, and only the vertices those triangles use.
    /// </summary>
    public static class TJBakeAutoLod
    {
        // The least a corner's normal may agree with its face, as a cosine, before the corner takes the face's normal.
        private const float MinFacing = 0.5f;

        /// <summary>
        /// The mesh with about <paramref name="keep"/> of its triangles (0 to 1). Corners that share a place are joined first,
        /// so a mesh split at every hard edge and atlas patch simplifies as one surface. Each new triangle then takes its
        /// colour from the source triangle beside it that lies nearest and faces most alike: one UV for its three corners,
        /// as a low-poly atlas paints a face one patch. Each corner takes the source vertex at its place whose normal is
        /// closest to the new triangle's face, so hard and soft edges stay as they were. Where none comes within 60 degrees
        /// of it, the corner takes the face's own normal: a normal turned from the camera on a face toward it lights the
        /// look's distance rim on the far crowd as bright flecks.
        /// </summary>
        public static MeshBinData Simplify(MeshBinData source, float keep)
        {
            int[] place = JoinPlaces(source.Positions, out List<int> firstAt);
            var joined = new Vector3[firstAt.Count];
            for (int p = 0; p < joined.Length; p++) joined[p] = source.Positions[firstAt[p]];
            var indices = new int[source.Indices.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = place[source.Indices[i]];

            int[] simple = SimplifiedIndices(joined, indices, keep);

            // Every source vertex and every source triangle at each place.
            var verticesAt = new List<int>[joined.Length];
            var trianglesAt = new List<int>[joined.Length];
            for (int i = 0; i < source.Indices.Length; i++)
            {
                int v = source.Indices[i], p = place[v];
                verticesAt[p] ??= new List<int>();
                if (!verticesAt[p].Contains(v)) verticesAt[p].Add(v);
                (trianglesAt[p] ??= new List<int>()).Add(i / 3 * 3);
            }

            var made = new Dictionary<(int, float2, float3), int>();
            var picked = new List<(int vertex, float2 uv, float3 normal)>();
            var result = new int[simple.Length];
            for (int t = 0; t < simple.Length; t += 3)
            {
                int source3 = Nearest(source, joined, trianglesAt, simple, t);
                float2 uv = float2.zero;
                if (source.Uv0 != null)
                    for (int k = 0; k < 3; k++) uv += source.Uv0[source.Indices[source3 + k]] / 3f;
                float3 face = math.normalizesafe(math.cross(joined[simple[t + 1]] - joined[simple[t]], joined[simple[t + 2]] - joined[simple[t]]));
                for (int k = 0; k < 3; k++)
                {
                    int v = Facing(source, verticesAt[simple[t + k]], face);
                    float3 normal = source.Normals == null ? float3.zero : math.dot(source.Normals[v], face) >= MinFacing ? source.Normals[v] : face;
                    if (!made.TryGetValue((v, uv, normal), out int to))
                    {
                        to = picked.Count;
                        made[(v, uv, normal)] = to;
                        picked.Add((v, uv, normal));
                    }
                    result[t + k] = to;
                }
            }
            return Pick(source, picked, result);
        }

        /// <summary>
        /// Lighter copies of one look's meshes with at most about <paramref name="maxVertices"/> among them: each starts at
        /// <paramref name="keep"/> of its triangles, then the one with the most vertices halves its share again until they
        /// fit or none gets lighter.
        /// </summary>
        public static MeshBinData[] SimplifyTo(IReadOnlyList<MeshBinData> sources, float keep, int maxVertices)
        {
            var made = new MeshBinData[sources.Count];
            var keeps = new float[sources.Count];
            var spent = new bool[sources.Count];
            int total = 0;
            for (int i = 0; i < made.Length; i++)
            {
                keeps[i] = keep;
                made[i] = Simplify(sources[i], keep);
                total += made[i].Positions.Length;
            }
            while (total > maxVertices)
            {
                int most = -1;
                for (int i = 0; i < made.Length; i++)
                    if (!spent[i] && (most < 0 || made[i].Positions.Length > made[most].Positions.Length)) most = i;
                if (most < 0) break;
                keeps[most] *= 0.5f;
                MeshBinData lighter = Simplify(sources[most], keeps[most]);
                if (lighter.Indices.Length >= made[most].Indices.Length)
                {
                    spent[most] = true;
                    continue;
                }
                total += lighter.Positions.Length - made[most].Positions.Length;
                made[most] = lighter;
            }
            return made;
        }

        /// <summary>
        /// Moves each vertex's weights onto the bones <paramref name="into"/> maps them to, joins weights that land on one
        /// bone and keeps the two strongest, so a far mesh skins with fewer bone reads.
        /// </summary>
        public static void Fold(MeshBinData mesh, int[] into)
        {
            if (mesh.BoneIndices == null) return;
            for (int v = 0; v < mesh.BoneIndices.Length; v++)
            {
                uint4 idx = mesh.BoneIndices[v];
                float4 w = mesh.BoneWeights[v];
                uint b0 = (uint)into[(int)idx.x], b1 = 0;
                float w0 = 0f, w1 = 0f;
                for (int k = 0; k < 4; k++)
                {
                    if (w[k] <= 0f) continue;
                    uint bone = (uint)into[(int)idx[k]];
                    // The joined weight of this bone across all four slots, counted once at its first slot.
                    bool seen = false;
                    float sum = 0f;
                    for (int j = 0; j < 4; j++)
                    {
                        if (w[j] <= 0f || (uint)into[(int)idx[j]] != bone) continue;
                        if (j < k) seen = true;
                        sum += w[j];
                    }
                    if (seen) continue;
                    if (sum > w0)
                    {
                        b1 = b0; w1 = w0;
                        b0 = bone; w0 = sum;
                    }
                    else if (sum > w1)
                    {
                        b1 = bone; w1 = sum;
                    }
                }
                float total = w0 + w1;
                mesh.BoneIndices[v] = new uint4(b0, b1, 0, 0);
                mesh.BoneWeights[v] = total > 0f ? new float4(w0 / total, w1 / total, 0f, 0f) : new float4(1f, 0f, 0f, 0f);
            }
        }

        // Each vertex's place: vertices at the same position share one, numbered in order of first use.
        private static int[] JoinPlaces(float3[] positions, out List<int> firstAt)
        {
            firstAt = new List<int>();
            var byPosition = new Dictionary<float3, int>();
            var place = new int[positions.Length];
            for (int v = 0; v < positions.Length; v++)
            {
                if (!byPosition.TryGetValue(positions[v], out int p))
                {
                    p = firstAt.Count;
                    byPosition[positions[v]] = p;
                    firstAt.Add(v);
                }
                place[v] = p;
            }
            return place;
        }

        // The deepest Mesh LOD level whose triangles are still at least the kept share, as indices into the vertices.
        private static int[] SimplifiedIndices(Vector3[] vertices, int[] indices, float keep)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                mesh.vertices = vertices;
                mesh.SetIndices(indices, MeshTopology.Triangles, 0);
                MeshLodUtility.GenerateMeshLods(mesh, -1);
                int want = (int)(indices.Length * keep);
                MeshLodRange chosen = mesh.GetLod(0, 0);
                for (int l = 1; l < mesh.lodCount; l++)
                {
                    MeshLodRange range = mesh.GetLod(0, l);
                    if (range.indexCount < want) break;
                    chosen = range;
                }
                using Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(mesh);
                NativeArray<int> all = data[0].GetIndexData<int>();
                var result = new int[chosen.indexCount];
                NativeArray<int>.Copy(all, (int)chosen.indexStart, result, 0, (int)chosen.indexCount);
                return result;
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        // The source triangle at one of the new triangle's corners that faces most alike, less how far its middle lies
        // from the new one's in the new triangle's own size, as the index of its first corner.
        private static int Nearest(MeshBinData source, Vector3[] joined, List<int>[] trianglesAt, int[] simple, int t)
        {
            float3 a = joined[simple[t]], b = joined[simple[t + 1]], c = joined[simple[t + 2]];
            float3 face = math.normalizesafe(math.cross(b - a, c - a));
            float3 middle = (a + b + c) / 3f;
            float size = math.max(1e-6f, (math.distance(a, b) + math.distance(b, c) + math.distance(c, a)) / 3f);
            int best = trianglesAt[simple[t]][0];
            float bestScore = float.MinValue;
            for (int k = 0; k < 3; k++)
                foreach (int s in trianglesAt[simple[t + k]])
                {
                    float3 sa = source.Positions[source.Indices[s]], sb = source.Positions[source.Indices[s + 1]], sc = source.Positions[source.Indices[s + 2]];
                    float score = math.dot(math.normalizesafe(math.cross(sb - sa, sc - sa)), face) - math.distance((sa + sb + sc) / 3f, middle) / size;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = s;
                    }
                }
            return best;
        }

        // The candidate whose normal is closest to the given one; the first one when the mesh has no normals.
        private static int Facing(MeshBinData source, List<int> candidates, float3 normal)
        {
            int best = candidates[0];
            if (source.Normals == null) return best;
            float bestDot = float.MinValue;
            foreach (int v in candidates)
            {
                float dot = math.dot(source.Normals[v], normal);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = v;
                }
            }
            return best;
        }

        private static MeshBinData Pick(MeshBinData source, List<(int vertex, float2 uv, float3 normal)> picked, int[] indices)
        {
            int n = picked.Count;
            var mesh = new MeshBinData
            {
                Positions = new float3[n],
                Normals = source.Normals != null ? new float3[n] : null,
                Tangents = source.Tangents != null ? new float4[n] : null,
                Uv0 = source.Uv0 != null ? new float2[n] : null,
                BoneIndices = source.BoneIndices != null ? new uint4[n] : null,
                BoneWeights = source.BoneWeights != null ? new float4[n] : null,
                Indices = indices,
            };
            for (int i = 0; i < n; i++)
            {
                int v = picked[i].vertex;
                mesh.Positions[i] = source.Positions[v];
                if (mesh.Normals != null) mesh.Normals[i] = picked[i].normal;
                if (mesh.Tangents != null) mesh.Tangents[i] = source.Tangents[v];
                if (mesh.Uv0 != null) mesh.Uv0[i] = picked[i].uv;
                if (mesh.BoneIndices != null) mesh.BoneIndices[i] = source.BoneIndices[v];
                if (mesh.BoneWeights != null) mesh.BoneWeights[i] = source.BoneWeights[v];
            }
            return mesh;
        }
    }
}
