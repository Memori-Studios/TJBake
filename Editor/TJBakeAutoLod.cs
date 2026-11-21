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
        /// <summary>
        /// The mesh with about <paramref name="keep"/> of its triangles (0 to 1). Corners that share a place are joined first,
        /// so a mesh split at every hard edge and atlas patch simplifies as one surface. Each new triangle then takes its
        /// colour from the source triangle beside it that lies nearest and faces most alike: one UV for its three corners,
        /// as a low-poly atlas paints a face one patch. Each corner takes the source vertex at its place whose normal is
        /// closest to that triangle's, so hard and soft edges stay as they were.
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

            var made = new Dictionary<(int, float2), int>();
            var picked = new List<(int vertex, float2 uv)>();
            var result = new int[simple.Length];
            for (int t = 0; t < simple.Length; t += 3)
            {
                int source3 = Nearest(source, joined, trianglesAt, simple, t);
                float2 uv = float2.zero;
                float3 normal = float3.zero;
                for (int k = 0; k < 3; k++)
                {
                    int v = source.Indices[source3 + k];
                    if (source.Uv0 != null) uv += source.Uv0[v] / 3f;
                    if (source.Normals != null) normal += source.Normals[v];
                }
                for (int k = 0; k < 3; k++)
                {
                    int v = Facing(source, verticesAt[simple[t + k]], normal);
                    if (!made.TryGetValue((v, uv), out int to))
                    {
                        to = picked.Count;
                        made[(v, uv)] = to;
                        picked.Add((v, uv));
                    }
                    result[t + k] = to;
                }
            }
            return Pick(source, picked, result);
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

        private static MeshBinData Pick(MeshBinData source, List<(int vertex, float2 uv)> picked, int[] indices)
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
                if (mesh.Normals != null) mesh.Normals[i] = source.Normals[v];
                if (mesh.Tangents != null) mesh.Tangents[i] = source.Tangents[v];
                if (mesh.Uv0 != null) mesh.Uv0[i] = picked[i].uv;
                if (mesh.BoneIndices != null) mesh.BoneIndices[i] = source.BoneIndices[v];
                if (mesh.BoneWeights != null) mesh.BoneWeights[i] = source.BoneWeights[v];
            }
            return mesh;
        }
    }
}
