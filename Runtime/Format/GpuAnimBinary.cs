using System;
using System.IO;
using Unity.Mathematics;

namespace MemoriStudios.TJBake.Format
{
    /// <summary>Thrown for any file that breaks the format; the message names the rule.</summary>
    public sealed class GpuAnimFormatException : Exception
    {
        public GpuAnimFormatException(string message) : base(message) { }
    }

    /// <summary>anim.bin: "TJBA", one row per frame, 3 half4 rows per bone. Readers bounds-check every count against the file.</summary>
    public static class AnimBin
    {
        public const int HeaderSize = 32;
        public const int BytesPerBone = 24;
        public const int MaxBones = 5461;
        public const int MaxFrames = 16384;
        private static readonly byte[] Magic = { (byte)'T', (byte)'J', (byte)'B', (byte)'A' };

        /// <summary>Returns the raw RGBAHalf texel bytes (width boneCount * 3, height frameCount) ready for LoadRawTextureData.</summary>
        public static byte[] Read(byte[] file, out int boneCount, out int frameCount)
        {
            if (file.Length < HeaderSize) throw new GpuAnimFormatException("anim.bin is shorter than its header");
            CheckMagic(file, Magic, "anim.bin");
            uint version = BitConverter.ToUInt32(file, 4);
            if (version != 1) throw new GpuAnimFormatException($"anim.bin version {version} is not 1");
            boneCount = checked((int)BitConverter.ToUInt32(file, 8));
            frameCount = checked((int)BitConverter.ToUInt32(file, 12));
            if (boneCount < 1 || boneCount > MaxBones) throw new GpuAnimFormatException($"anim.bin boneCount {boneCount} is outside 1 to {MaxBones}");
            if (frameCount < 2 || frameCount > MaxFrames) throw new GpuAnimFormatException($"anim.bin frameCount {frameCount} is outside 2 to {MaxFrames}");
            long expected = HeaderSize + (long)frameCount * boneCount * BytesPerBone;
            if (file.Length != expected) throw new GpuAnimFormatException($"anim.bin is {file.Length} bytes, expected {expected}");
            var data = new byte[file.Length - HeaderSize];
            Buffer.BlockCopy(file, HeaderSize, data, 0, data.Length);
            return data;
        }

        /// <summary>matrices: frame-major, boneCount per frame, each the top three rows of the skinning matrix.</summary>
        public static void Write(string path, int boneCount, int frameCount, float3x4[] matrices)
        {
            if (matrices.Length != boneCount * frameCount) throw new ArgumentException("matrix count does not match boneCount * frameCount");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write(Magic);
            w.Write(1u);
            w.Write((uint)boneCount);
            w.Write((uint)frameCount);
            w.Write(new byte[HeaderSize - 16]);
            foreach (float3x4 m in matrices)
            {
                WriteHalfRow(w, m.c0.x, m.c1.x, m.c2.x, m.c3.x);
                WriteHalfRow(w, m.c0.y, m.c1.y, m.c2.y, m.c3.y);
                WriteHalfRow(w, m.c0.z, m.c1.z, m.c2.z, m.c3.z);
            }
        }

        private static void WriteHalfRow(BinaryWriter w, float a, float b, float c, float d)
        {
            // f32tof16 returns the 16 bits in a uint; the file stores two bytes per value.
            w.Write((ushort)math.f32tof16(a)); w.Write((ushort)math.f32tof16(b)); w.Write((ushort)math.f32tof16(c)); w.Write((ushort)math.f32tof16(d));
        }

        internal static void CheckMagic(byte[] file, byte[] magic, string name)
        {
            for (int i = 0; i < 4; i++)
                if (file[i] != magic[i]) throw new GpuAnimFormatException($"{name} does not start with {System.Text.Encoding.ASCII.GetString(magic)}");
        }
    }

    /// <summary>anchors.bin: "TJBK", one row per frame, a full-float 3x4 per anchor.</summary>
    public static class AnchorsBin
    {
        public const int HeaderSize = 32;
        public const int BytesPerAnchor = 48;
        private static readonly byte[] Magic = { (byte)'T', (byte)'J', (byte)'B', (byte)'K' };

        public static float3x4[] Read(byte[] file, out int anchorCount, out int frameCount)
        {
            if (file.Length < HeaderSize) throw new GpuAnimFormatException("anchors.bin is shorter than its header");
            AnimBin.CheckMagic(file, Magic, "anchors.bin");
            uint version = BitConverter.ToUInt32(file, 4);
            if (version != 1) throw new GpuAnimFormatException($"anchors.bin version {version} is not 1");
            anchorCount = checked((int)BitConverter.ToUInt32(file, 8));
            frameCount = checked((int)BitConverter.ToUInt32(file, 12));
            if (anchorCount < 0 || anchorCount > 256) throw new GpuAnimFormatException($"anchors.bin anchorCount {anchorCount} is outside 0 to 256");
            if (frameCount < 0 || frameCount > AnimBin.MaxFrames) throw new GpuAnimFormatException($"anchors.bin frameCount {frameCount} is outside 0 to {AnimBin.MaxFrames}");
            long expected = HeaderSize + (long)frameCount * anchorCount * BytesPerAnchor;
            if (file.Length != expected) throw new GpuAnimFormatException($"anchors.bin is {file.Length} bytes, expected {expected}");
            var result = new float3x4[frameCount * anchorCount];
            int o = HeaderSize;
            for (int i = 0; i < result.Length; i++)
            {
                float m00 = F(file, o), m01 = F(file, o + 4), m02 = F(file, o + 8), m03 = F(file, o + 12);
                float m10 = F(file, o + 16), m11 = F(file, o + 20), m12 = F(file, o + 24), m13 = F(file, o + 28);
                float m20 = F(file, o + 32), m21 = F(file, o + 36), m22 = F(file, o + 40), m23 = F(file, o + 44);
                result[i] = new float3x4(new float3(m00, m10, m20), new float3(m01, m11, m21), new float3(m02, m12, m22), new float3(m03, m13, m23));
                o += BytesPerAnchor;
            }
            return result;
        }

        public static void Write(string path, int anchorCount, int frameCount, float3x4[] matrices)
        {
            if (matrices.Length != anchorCount * frameCount) throw new ArgumentException("anchor matrix count does not match anchorCount * frameCount");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write(Magic);
            w.Write(1u);
            w.Write((uint)anchorCount);
            w.Write((uint)frameCount);
            w.Write(new byte[HeaderSize - 16]);
            foreach (float3x4 m in matrices)
            {
                w.Write(m.c0.x); w.Write(m.c1.x); w.Write(m.c2.x); w.Write(m.c3.x);
                w.Write(m.c0.y); w.Write(m.c1.y); w.Write(m.c2.y); w.Write(m.c3.y);
                w.Write(m.c0.z); w.Write(m.c1.z); w.Write(m.c2.z); w.Write(m.c3.z);
            }
        }

        private static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);
    }

    /// <summary>A mesh as the file holds it. Bone data is present only on a skinned body mesh.</summary>
    public sealed class MeshBinData
    {
        public float3[] Positions = Array.Empty<float3>();
        public float3[] Normals;
        public float4[] Tangents;
        public float2[] Uv0;
        public uint4[] BoneIndices;
        public float4[] BoneWeights;
        public int[] Indices = Array.Empty<int>();
        public (int start, int count)[] Submeshes = Array.Empty<(int, int)>();
        public float3 BoundsMin, BoundsMax;
        public bool Skinned => BoneIndices != null;
    }

    /// <summary>*.mesh.bin: "TJBM", header, submesh table, then the streams the flags announce.</summary>
    public static class MeshBin
    {
        public const int HeaderSize = 48;
        public const int MaxVertices = 1_000_000;
        private static readonly byte[] Magic = { (byte)'T', (byte)'J', (byte)'B', (byte)'M' };
        private const uint HasNormals = 1, HasTangents = 2, HasUv0 = 4, SkinnedFlag = 8, Indices32 = 16;

        public static MeshBinData Read(byte[] file, string name, int boneCount)
        {
            if (file.Length < HeaderSize) throw new GpuAnimFormatException($"{name} is shorter than its header");
            AnimBin.CheckMagic(file, Magic, name);
            uint version = BitConverter.ToUInt32(file, 4);
            if (version != 1) throw new GpuAnimFormatException($"{name} version {version} is not 1");
            int vertexCount = checked((int)BitConverter.ToUInt32(file, 8));
            int indexCount = checked((int)BitConverter.ToUInt32(file, 12));
            uint flags = BitConverter.ToUInt32(file, 16);
            if (vertexCount < 3 || vertexCount > MaxVertices) throw new GpuAnimFormatException($"{name} vertexCount {vertexCount} is outside 3 to {MaxVertices}");
            if (indexCount < 3 || indexCount % 3 != 0 || indexCount > MaxVertices * 6) throw new GpuAnimFormatException($"{name} indexCount {indexCount} is not a multiple of 3 in range");
            var data = new MeshBinData
            {
                BoundsMin = new float3(F(file, 20), F(file, 24), F(file, 28)),
                BoundsMax = new float3(F(file, 32), F(file, 36), F(file, 40)),
            };
            int submeshCount = checked((int)BitConverter.ToUInt32(file, 44));
            if (submeshCount < 1 || submeshCount > 64) throw new GpuAnimFormatException($"{name} submeshCount {submeshCount} is outside 1 to 64");
            int o = HeaderSize;
            Need(file, o, submeshCount * 8, name, "submesh table");
            data.Submeshes = new (int, int)[submeshCount];
            for (int i = 0; i < submeshCount; i++)
            {
                int start = checked((int)BitConverter.ToUInt32(file, o)), count = checked((int)BitConverter.ToUInt32(file, o + 4));
                if (start < 0 || count < 3 || start + count > indexCount) throw new GpuAnimFormatException($"{name} submesh {i} runs past the index buffer");
                data.Submeshes[i] = (start, count);
                o += 8;
            }
            o = Align16(o);
            data.Positions = ReadFloat3s(file, ref o, vertexCount, name, "positions");
            if ((flags & HasNormals) != 0) data.Normals = ReadFloat3s(file, ref o, vertexCount, name, "normals");
            if ((flags & HasTangents) != 0) data.Tangents = ReadFloat4s(file, ref o, vertexCount, name, "tangents");
            if ((flags & HasUv0) != 0)
            {
                o = Align16(o);
                Need(file, o, vertexCount * 8, name, "uv0");
                data.Uv0 = new float2[vertexCount];
                for (int i = 0; i < vertexCount; i++) { data.Uv0[i] = new float2(F(file, o), F(file, o + 4)); o += 8; }
            }
            if ((flags & SkinnedFlag) != 0)
            {
                o = Align16(o);
                Need(file, o, vertexCount * 8, name, "bone indices");
                data.BoneIndices = new uint4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var idx = new uint4(BitConverter.ToUInt16(file, o), BitConverter.ToUInt16(file, o + 2), BitConverter.ToUInt16(file, o + 4), BitConverter.ToUInt16(file, o + 6));
                    if (math.cmax(idx) >= (uint)boneCount) throw new GpuAnimFormatException($"{name} vertex {i} references bone {math.cmax(idx)} of {boneCount}");
                    data.BoneIndices[i] = idx;
                    o += 8;
                }
                data.BoneWeights = ReadFloat4s(file, ref o, vertexCount, name, "bone weights");
            }
            o = Align16(o);
            bool wide = (flags & Indices32) != 0;
            Need(file, o, indexCount * (wide ? 4 : 2), name, "indices");
            data.Indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                int v = wide ? checked((int)BitConverter.ToUInt32(file, o)) : BitConverter.ToUInt16(file, o);
                if (v < 0 || v >= vertexCount) throw new GpuAnimFormatException($"{name} index {i} is {v}, vertexCount {vertexCount}");
                data.Indices[i] = v;
                o += wide ? 4 : 2;
            }
            return data;
        }

        public static void Write(string path, MeshBinData m)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            int vertexCount = m.Positions.Length;
            bool wide = vertexCount > ushort.MaxValue;
            uint flags = (m.Normals != null ? HasNormals : 0) | (m.Tangents != null ? HasTangents : 0) | (m.Uv0 != null ? HasUv0 : 0)
                | (m.Skinned ? SkinnedFlag : 0) | (wide ? Indices32 : 0);
            w.Write(Magic); w.Write(1u); w.Write((uint)vertexCount); w.Write((uint)m.Indices.Length); w.Write(flags);
            w.Write(m.BoundsMin.x); w.Write(m.BoundsMin.y); w.Write(m.BoundsMin.z); w.Write(m.BoundsMax.x); w.Write(m.BoundsMax.y); w.Write(m.BoundsMax.z);
            var submeshes = m.Submeshes.Length == 0 ? new[] { (0, m.Indices.Length) } : m.Submeshes;
            w.Write((uint)submeshes.Length);
            foreach ((int start, int count) in submeshes) { w.Write((uint)start); w.Write((uint)count); }
            Pad16(w);
            foreach (float3 p in m.Positions) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
            if (m.Normals != null) { Pad16(w); foreach (float3 n in m.Normals) { w.Write(n.x); w.Write(n.y); w.Write(n.z); } }
            if (m.Tangents != null) { Pad16(w); foreach (float4 t in m.Tangents) { w.Write(t.x); w.Write(t.y); w.Write(t.z); w.Write(t.w); } }
            if (m.Uv0 != null) { Pad16(w); foreach (float2 uv in m.Uv0) { w.Write(uv.x); w.Write(uv.y); } }
            if (m.Skinned)
            {
                Pad16(w);
                foreach (uint4 i in m.BoneIndices) { w.Write((ushort)i.x); w.Write((ushort)i.y); w.Write((ushort)i.z); w.Write((ushort)i.w); }
                Pad16(w);
                foreach (float4 bw in m.BoneWeights) { w.Write(bw.x); w.Write(bw.y); w.Write(bw.z); w.Write(bw.w); }
            }
            Pad16(w);
            foreach (int i in m.Indices) { if (wide) w.Write((uint)i); else w.Write((ushort)i); }
        }

        private static float3[] ReadFloat3s(byte[] file, ref int o, int count, string name, string what)
        {
            o = Align16(o);
            Need(file, o, count * 12, name, what);
            var r = new float3[count];
            for (int i = 0; i < count; i++) { r[i] = new float3(F(file, o), F(file, o + 4), F(file, o + 8)); o += 12; }
            return r;
        }

        private static float4[] ReadFloat4s(byte[] file, ref int o, int count, string name, string what)
        {
            o = Align16(o);
            Need(file, o, count * 16, name, what);
            var r = new float4[count];
            for (int i = 0; i < count; i++) { r[i] = new float4(F(file, o), F(file, o + 4), F(file, o + 8), F(file, o + 12)); o += 16; }
            return r;
        }

        private static void Need(byte[] file, int offset, long bytes, string name, string what)
        {
            if (offset < 0 || offset + bytes > file.Length) throw new GpuAnimFormatException($"{name} ends inside its {what}");
        }

        private static int Align16(int o) => (o + 15) & ~15;
        private static void Pad16(BinaryWriter w) { while (w.BaseStream.Position % 16 != 0) w.Write((byte)0); }
        private static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);
    }
}
