using System;
using System.IO;
using MemoriStudios.TJBake.Format;
using NUnit.Framework;
using Unity.Mathematics;

namespace MemoriStudios.TJBake.Tests
{
    /// <summary>The binary files round-trip, and a damaged file is refused with a message instead of read past its end.</summary>
    public class TJBakeFormatTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tjbake-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Test]
        public void AnimBin_RoundTripsWithinHalfPrecision()
        {
            const int bones = 3, frames = 4;
            var m = new float3x4[bones * frames];
            for (int i = 0; i < m.Length; i++)
                m[i] = new float3x4(new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1), new float3(i * 0.25f, -i * 0.5f, 1.5f));
            string path = Path.Combine(_dir, "anim.bin");
            AnimBin.Write(path, bones, frames, m);
            byte[] texels = AnimBin.Read(File.ReadAllBytes(path), out int readBones, out int readFrames);
            Assert.That(readBones, Is.EqualTo(bones));
            Assert.That(readFrames, Is.EqualTo(frames));
            Assert.That(texels.Length, Is.EqualTo(bones * frames * AnimBin.BytesPerBone));
            // Row 0 of the last matrix: m00 m01 m02 m03, where m03 is the translation x.
            int last = (m.Length - 1) * AnimBin.BytesPerBone;
            float m03 = math.f16tof32(BitConverter.ToUInt16(texels, last + 6));
            Assert.That(m03, Is.EqualTo(m[m.Length - 1].c3.x).Within(1e-3f));
        }

        [Test]
        public void AnimBin_RefusesATruncatedFile()
        {
            string path = Path.Combine(_dir, "anim.bin");
            AnimBin.Write(path, 2, 3, new float3x4[6]);
            byte[] bytes = File.ReadAllBytes(path);
            Array.Resize(ref bytes, bytes.Length - 5);
            Assert.Throws<GpuAnimFormatException>(() => AnimBin.Read(bytes, out _, out _));
        }

        [Test]
        public void AnchorsBin_RoundTripsExactly()
        {
            var m = new float3x4[2 * 3];
            for (int i = 0; i < m.Length; i++) m[i] = new float3x4(new float3(i, 1, 2), new float3(3, i, 5), new float3(6, 7, i), new float3(0.1f * i, 0.2f, 0.3f));
            string path = Path.Combine(_dir, "anchors.bin");
            AnchorsBin.Write(path, 2, 3, m);
            float3x4[] back = AnchorsBin.Read(File.ReadAllBytes(path), out int anchors, out int frames);
            Assert.That(anchors, Is.EqualTo(2));
            Assert.That(frames, Is.EqualTo(3));
            for (int i = 0; i < m.Length; i++) Assert.That(back[i].Equals(m[i]), $"matrix {i}");
        }

        [Test]
        public void MeshBin_RoundTripsASkinnedMesh()
        {
            var data = new MeshBinData
            {
                Positions = new[] { new float3(0, 0, 0), new float3(1, 0, 0), new float3(0, 1, 0), new float3(0, 0, 1) },
                Normals = new[] { new float3(0, 1, 0), new float3(0, 1, 0), new float3(0, 1, 0), new float3(0, 1, 0) },
                Uv0 = new[] { new float2(0, 0), new float2(1, 0), new float2(0, 1), new float2(1, 1) },
                BoneIndices = new[] { new uint4(0, 1, 0, 0), new uint4(1, 0, 0, 0), new uint4(0, 0, 0, 0), new uint4(1, 1, 0, 0) },
                BoneWeights = new[] { new float4(0.5f, 0.5f, 0, 0), new float4(1, 0, 0, 0), new float4(1, 0, 0, 0), new float4(0.75f, 0.25f, 0, 0) },
                Indices = new[] { 0, 1, 2, 0, 2, 3 },
                BoundsMin = new float3(-1), BoundsMax = new float3(2),
            };
            string path = Path.Combine(_dir, "body.mesh.bin");
            MeshBin.Write(path, data);
            MeshBinData back = MeshBin.Read(File.ReadAllBytes(path), "body.mesh.bin", 2);
            Assert.That(back.Skinned, Is.True);
            Assert.That(back.Positions, Is.EqualTo(data.Positions));
            Assert.That(back.Indices, Is.EqualTo(data.Indices));
            Assert.That(back.BoneIndices, Is.EqualTo(data.BoneIndices));
            Assert.That(back.BoneWeights, Is.EqualTo(data.BoneWeights));
            Assert.That(back.BoundsMax, Is.EqualTo(data.BoundsMax));
        }

        [Test]
        public void MeshBin_RefusesABoneOutsideTheSkeleton()
        {
            var data = new MeshBinData
            {
                Positions = new[] { float3.zero, new float3(1, 0, 0), new float3(0, 1, 0) },
                BoneIndices = new[] { new uint4(0), new uint4(0), new uint4(5, 0, 0, 0) },
                BoneWeights = new[] { new float4(1, 0, 0, 0), new float4(1, 0, 0, 0), new float4(1, 0, 0, 0) },
                Indices = new[] { 0, 1, 2 },
            };
            string path = Path.Combine(_dir, "bad.mesh.bin");
            MeshBin.Write(path, data);
            var e = Assert.Throws<GpuAnimFormatException>(() => MeshBin.Read(File.ReadAllBytes(path), "bad.mesh.bin", 2));
            StringAssert.Contains("bone 5", e.Message);
        }

        [Test]
        public void MeshBin_RefusesAnIndexPastTheVertices()
        {
            var data = new MeshBinData { Positions = new[] { float3.zero, new float3(1, 0, 0), new float3(0, 1, 0) }, Indices = new[] { 0, 1, 7 } };
            string path = Path.Combine(_dir, "bad.mesh.bin");
            MeshBin.Write(path, data);
            Assert.Throws<GpuAnimFormatException>(() => MeshBin.Read(File.ReadAllBytes(path), "bad.mesh.bin", 1));
        }
    }
}
