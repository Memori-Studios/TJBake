using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MemoriStudios.TJBake.Editor;
using MemoriStudios.TJBake.Format;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace MemoriStudios.TJBake.Tests
{
    /// <summary>
    /// A two-bone rig built in code, with a clip whose answer is known: bone 1 turns 90 degrees about X around the point (0, 1, 0).
    /// The bake must reproduce that pose from the matrix file, and the manifest rules must refuse broken folders.
    /// </summary>
    public class TJBakeSamplerTests
    {
        private string _dir;
        private readonly List<UnityEngine.Object> _objects = new();

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "tjbake-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object o in _objects) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _objects.Clear();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        #region Rig
        private GameObject Rig(out Transform bone1, out AnimationClip clip)
        {
            var root = Track(new GameObject("Rig"));
            var bone0 = new GameObject("Bone0").transform;
            bone0.SetParent(root.transform, false);
            bone1 = new GameObject("Bone1").transform;
            bone1.SetParent(bone0, false);
            bone1.localPosition = new Vector3(0, 1, 0);

            var mesh = Track(new Mesh { name = "Column" });
            mesh.vertices = new[] { new Vector3(-0.1f, 0, 0), new Vector3(0.1f, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 1.5f, 0.1f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 1, weight0 = 1 }, new BoneWeight { boneIndex0 = 1, weight0 = 1 },
            };
            mesh.bindposes = new[] { bone0.worldToLocalMatrix * root.transform.localToWorldMatrix, bone1.worldToLocalMatrix * root.transform.localToWorldMatrix };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var smr = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            smr.transform.SetParent(root.transform, false);
            smr.sharedMesh = mesh;
            smr.bones = new[] { bone0, bone1 };
            smr.rootBone = bone0;

            // One second from rest to 90 degrees about X.
            clip = Track(new AnimationClip { name = "Bend" });
            Quaternion end = Quaternion.AngleAxis(90f, Vector3.right);
            clip.SetCurve("Bone0/Bone1", typeof(Transform), "m_LocalRotation.x", AnimationCurve.Linear(0, 0, 1, end.x));
            clip.SetCurve("Bone0/Bone1", typeof(Transform), "m_LocalRotation.y", AnimationCurve.Linear(0, 0, 1, 0));
            clip.SetCurve("Bone0/Bone1", typeof(Transform), "m_LocalRotation.z", AnimationCurve.Linear(0, 0, 1, 0));
            clip.SetCurve("Bone0/Bone1", typeof(Transform), "m_LocalRotation.w", AnimationCurve.Linear(0, 1, 1, end.w));
            return root;
        }

        private T Track<T>(T o) where T : UnityEngine.Object { _objects.Add(o); return o; }

        private TJBakeRequest RiderRequest(GameObject root, AnimationClip clip, Transform anchorBone)
        {
            var r = new TJBakeRequest { UnitName = "Test", IsRider = true, OutputFolder = _dir, Root = root, Fps = 30 };
            r.Slots.Add(new TJBakeSlotRequest { Name = "idle", Clip = clip, Loop = true });
            r.Slots.Add(new TJBakeSlotRequest { Name = "attack", Clip = clip, ReturnToIdle = true, ReturnAt = 0.8f });
            r.Slots.Add(new TJBakeSlotRequest { Name = "death", Clip = clip });
            r.Anchors.Add(new TJBakeAnchorRequest { Name = "Tip", Transform = anchorBone });
            return r;
        }
        #endregion

        [Test]
        public void Bake_ReproducesTheKnownPoseFromTheMatrixFile()
        {
            GameObject root = Rig(out Transform bone1, out AnimationClip clip);
            UnitJson json = TJBakeSampler.Bake(RiderRequest(root, clip, bone1), new StringBuilder());

            // The death slot holds, so its last row is the clip's end: the tip vertex (0, 2, 0) lands on (0, 1, 1).
            SlotJson death = json.slots[2];
            int lastRow = death.start + death.count - 1;
            byte[] texels = AnimBin.Read(File.ReadAllBytes(Path.Combine(_dir, "anim.bin")), out int bones, out _);
            float3x4 skin = Bone(texels, bones, lastRow, 1);
            float3 tip = math.mul(skin, new float4(0, 2, 0, 1));
            Assert.That(math.distance(tip, new float3(0, 1, 1)), Is.LessThan(0.01f), $"tip at {tip}");

            // The first row of every slot is the rest pose.
            float3 rest = math.mul(Bone(texels, bones, death.start, 1), new float4(0, 2, 0, 1));
            Assert.That(math.distance(rest, new float3(0, 2, 0)), Is.LessThan(0.01f), $"rest at {rest}");
        }

        [Test]
        public void Bake_WritesSlotFlagsAndAnAnimationInclusiveBox()
        {
            GameObject root = Rig(out Transform bone1, out AnimationClip clip);
            UnitJson json = TJBakeSampler.Bake(RiderRequest(root, clip, bone1), new StringBuilder());
            Assert.That(json.slots[0].loop, Is.True);
            Assert.That(json.slots[1].returnToIdle, Is.True);
            Assert.That(json.slots[1].returnAt, Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(json.slots[2].loop || json.slots[2].returnToIdle, Is.False);

            // The bent tip reaches z = 1, which the bind pose box (z <= 0.1) does not hold.
            MeshBinData body = MeshBin.Read(File.ReadAllBytes(Path.Combine(_dir, json.variants[0].lods[0].meshes[0].file)), "body", json.skeleton.boneCount);
            Assert.That(body.BoundsMax.z, Is.GreaterThan(0.95f));
        }

        [Test]
        public void Bake_AnchorFollowsItsBone()
        {
            GameObject root = Rig(out Transform bone1, out AnimationClip clip);
            UnitJson json = TJBakeSampler.Bake(RiderRequest(root, clip, bone1), new StringBuilder());
            float3x4[] anchors = AnchorsBin.Read(File.ReadAllBytes(Path.Combine(_dir, "anchors.bin")), out _, out _);
            SlotJson death = json.slots[2];
            float3x4 end = anchors[death.start + death.count - 1];
            // Bone1 sits at (0, 1, 0) and turns its up axis onto +Z.
            Assert.That(math.distance(end.c3, new float3(0, 1, 0)), Is.LessThan(1e-3f));
            Assert.That(math.distance(math.normalize(end.c1), new float3(0, 0, 1)), Is.LessThan(1e-3f));
        }

        [Test]
        public void Validation_RefusesAMissingSlotAndAnUnknownAnchor()
        {
            GameObject root = Rig(out Transform bone1, out AnimationClip clip);
            UnitJson json = TJBakeSampler.Bake(RiderRequest(root, clip, bone1), new StringBuilder());

            UnitJson fewSlots = JsonUtility.FromJson<UnitJson>(JsonUtility.ToJson(json));
            fewSlots.slots.RemoveAt(2);
            Assert.Throws<GpuAnimFormatException>(() => TJBakeValidation.ValidateManifest(fewSlots, _dir));

            UnitJson badAnchor = JsonUtility.FromJson<UnitJson>(JsonUtility.ToJson(json));
            badAnchor.variants[0].attachments.Add(new AttachmentJson { file = "anim.bin", material = 0, anchor = "Nowhere", role = "prop" });
            var e = Assert.Throws<GpuAnimFormatException>(() => TJBakeValidation.ValidateManifest(badAnchor, _dir));
            StringAssert.Contains("Nowhere", e.Message);

            UnitJson escape = JsonUtility.FromJson<UnitJson>(JsonUtility.ToJson(json));
            escape.materials[0].baseColor = "../outside.png";
            Assert.Throws<GpuAnimFormatException>(() => TJBakeValidation.ValidateManifest(escape, _dir));
        }

        [Test]
        public void Playback_ReturnsToIdleAndRestartsTheSameSlot()
        {
            var slots = new List<TJBakeSlot>
            {
                new TJBakeSlot { Start = 0, Count = 10, Fps = 10, Loop = true },
                new TJBakeSlot { Start = 10, Count = 11, Fps = 10, ReturnToIdle = true, ReturnAt = 0.5f },
            };
            TJBakePlaybackState s = TJBakePlaybackState.Stopped;
            TJBakePlayback.Play(ref s, slots, 1, 0f);
            bool returned = false;
            for (int i = 0; i < 20 && !returned; i++) returned = TJBakePlayback.Step(ref s, slots, 0.1f);
            Assert.That(returned, Is.True);
            Assert.That(s.CurrentSlot, Is.EqualTo(0));

            TJBakePlayback.Play(ref s, slots, 1, 0f);
            TJBakePlayback.Step(ref s, slots, 0.3f);
            TJBakePlayback.Play(ref s, slots, 1, 0f);
            Assert.That(s.CurrentFrame, Is.EqualTo(10f), "playing the slot again starts it from its first frame");
        }

        private static float3x4 Bone(byte[] texels, int bones, int row, int bone)
        {
            int o = (row * bones + bone) * AnimBin.BytesPerBone;
            float H(int k) => math.f16tof32(BitConverter.ToUInt16(texels, o + k * 2));
            return new float3x4(new float3(H(0), H(4), H(8)), new float3(H(1), H(5), H(9)), new float3(H(2), H(6), H(10)), new float3(H(3), H(7), H(11)));
        }
    }
}
