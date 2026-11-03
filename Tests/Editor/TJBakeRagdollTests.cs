using System.Collections.Generic;
using MemoriStudios.TJBake.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MemoriStudios.TJBake.Tests
{
    /// <summary>
    /// A stick figure built in code: hips carrying a spine, a head, two arms and two legs. A recorded fall must lay it on
    /// the ground without pushing anything through the floor, keep every limb its length, repeat for the same seed and
    /// leave unlisted bones alone.
    /// </summary>
    public class TJBakeRagdollTests
    {
        private readonly List<Object> _objects = new();
        private GameObject _figure;
        private Transform _hips, _spine, _head, _armL, _armR, _legL, _legR;

        [SetUp]
        public void Build()
        {
            _figure = Track(new GameObject("Figure"));
            Transform root = Bone("Root", _figure.transform, Vector3.zero);
            _hips = Bone("Hips", root, new Vector3(0f, 1f, 0f));
            _spine = Bone("Spine", _hips, new Vector3(0f, 0.1f, 0f));
            _head = Bone("Head", _spine, new Vector3(0f, 0.65f, 0f));
            _armL = Bone("ArmL", _spine, new Vector3(-0.35f, 0.55f, 0f));
            _armR = Bone("ArmR", _spine, new Vector3(0.35f, 0.55f, 0f));
            _legL = Bone("LegL", _hips, new Vector3(-0.12f, 0f, 0f));
            _legR = Bone("LegR", _hips, new Vector3(0.12f, 0f, 0f));
            // A lean the clips must respect: the spine's rest is not upright.
            _spine.localRotation = Quaternion.Euler(20f, 0f, 0f);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
        }

        #region Helpers
        private T Track<T>(T o) where T : Object { _objects.Add(o); return o; }

        private Transform Bone(string name, Transform parent, Vector3 localPosition)
        {
            Transform t = Track(new GameObject(name)).transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            return t;
        }

        private TJBakeRagdollRig Rig()
        {
            var rig = new TJBakeRagdollRig { Root = _figure, Pelvis = _hips };
            rig.Limbs.Add(new TJBakeRagdollLimb { Bone = _head, Tip = new Vector3(0f, 0.28f, 0f), Radius = 0.13f, MaxSwing = 50f });
            rig.Limbs.Add(new TJBakeRagdollLimb { Bone = _armL, Tip = new Vector3(0f, -0.7f, 0f), Radius = 0.07f, MaxSwing = 150f });
            rig.Limbs.Add(new TJBakeRagdollLimb { Bone = _armR, Tip = new Vector3(0f, -0.7f, 0f), Radius = 0.07f, MaxSwing = 150f });
            rig.Limbs.Add(new TJBakeRagdollLimb { Bone = _legL, Tip = new Vector3(0f, -0.96f, 0f), Radius = 0.09f, MaxSwing = 100f });
            rig.Limbs.Add(new TJBakeRagdollLimb { Bone = _legR, Tip = new Vector3(0f, -0.96f, 0f), Radius = 0.09f, MaxSwing = 100f });
            return rig;
        }

        private AnimationClip Record(TJBakeRagdollRig rig, Vector3 push, int seed = 1, float seconds = 1.2f)
        {
            var options = new TJBakeRagdollOptions { Push = push, Seed = seed, Seconds = seconds };
            return Track(TJBakeRagdoll.Record(rig, options, "fall"));
        }

        private Vector3 TipOf(TJBakeRagdollRig rig, int limb) => rig.Limbs[limb].Bone.TransformPoint(rig.Limbs[limb].Tip);
        #endregion

        [Test]
        public void Record_LaysTheBodyOnTheGroundTheWayItWasPushed()
        {
            TJBakeRagdollRig rig = Rig();
            AnimationClip clip = Record(rig, new Vector3(0f, 1.5f, -4f));
            Assert.That(clip.length, Is.EqualTo(1.2f).Within(1e-3f));

            clip.SampleAnimation(_figure, clip.length);
            Assert.That(_hips.position.y, Is.LessThan(0.5f), $"the hips came down, to y {_hips.position.y}");
            Assert.That(_hips.position.z, Is.LessThan(-0.15f), $"pushed backward, the hips moved to z {_hips.position.z}");
            Assert.That(_head.position.y, Is.LessThan(0.7f), $"the head is down too, at y {_head.position.y}");
            // The torso fell on its back: its up axis points along -Z, give or take.
            Assert.That(Vector3.Dot(_hips.up, Vector3.back), Is.GreaterThan(0.6f), $"hips up is {_hips.up}");
            for (int l = 0; l < rig.Limbs.Count; l++)
                Assert.That(TipOf(rig, l).y, Is.GreaterThan(0f), $"limb {l}'s end stays above the floor");
        }

        [Test]
        public void Record_KeepsEveryLimbItsLengthAndNeverSinksAJointThroughTheFloor()
        {
            TJBakeRagdollRig rig = Rig();
            var restLengths = new float[rig.Limbs.Count];
            for (int l = 0; l < rig.Limbs.Count; l++) restLengths[l] = (TipOf(rig, l) - rig.Limbs[l].Bone.position).magnitude;
            AnimationClip clip = Record(rig, new Vector3(4f, 1.5f, 0f), seed: 7);
            for (float t = 0f; t <= clip.length; t += 0.1f)
            {
                clip.SampleAnimation(_figure, t);
                for (int l = 0; l < rig.Limbs.Count; l++)
                {
                    Assert.That((TipOf(rig, l) - rig.Limbs[l].Bone.position).magnitude, Is.EqualTo(restLengths[l]).Within(1e-3f), $"limb {l} at {t:F1} s");
                    Assert.That(TipOf(rig, l).y, Is.GreaterThan(-0.01f), $"limb {l}'s end at {t:F1} s");
                }
                Assert.That(_hips.position.y, Is.GreaterThan(0.1f), $"the hips at {t:F1} s");
            }
        }

        [Test]
        public void Record_StartsFromTheRestPose()
        {
            TJBakeRagdollRig rig = Rig();
            Vector3 hipsRest = _hips.position;
            Quaternion armRest = _armL.rotation;
            AnimationClip clip = Record(rig, new Vector3(0f, 1.5f, -4f));
            clip.SampleAnimation(_figure, 0f);
            Assert.That(Vector3.Distance(_hips.position, hipsRest), Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(_armL.rotation, armRest), Is.LessThan(0.1f));
        }

        [Test]
        public void Record_RepeatsForTheSameSeedAndDiffersForAnother()
        {
            TJBakeRagdollRig rig = Rig();
            AnimationClip a = Record(rig, new Vector3(0f, 1.5f, -4f), seed: 3);
            AnimationClip b = Record(rig, new Vector3(0f, 1.5f, -4f), seed: 3);
            AnimationClip c = Record(rig, new Vector3(0f, 1.5f, -4f), seed: 4);
            a.SampleAnimation(_figure, 0.5f);
            Vector3 fromA = _armR.position;
            b.SampleAnimation(_figure, 0.5f);
            Assert.That(Vector3.Distance(_armR.position, fromA), Is.LessThan(1e-5f), "the same seed is the same fall");
            c.SampleAnimation(_figure, 0.5f);
            Assert.That(Vector3.Distance(_armR.position, fromA), Is.GreaterThan(1e-3f), "another seed is another fall");
        }

        [Test]
        public void Record_LeavesTheBonesItWasNotGivenAlone()
        {
            AnimationClip clip = Record(Rig(), new Vector3(0f, 1.5f, -4f));
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                Assert.That(binding.path, Does.Not.EndWith("Spine").And.Not.EqualTo("Root"), $"{binding.path} {binding.propertyName}");
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(3 + 4 + 5 * 4), "pelvis position and rotation, five limb rotations");
        }

        [Test]
        public void Record_RefusesARigWithoutAPelvisOrATiplessLimb()
        {
            var noPelvis = new TJBakeRagdollRig { Root = _figure };
            Assert.Throws<System.ArgumentException>(() => TJBakeRagdoll.Record(noPelvis, new TJBakeRagdollOptions(), "x"));
            TJBakeRagdollRig rig = Rig();
            rig.Limbs[0].Tip = Vector3.zero;
            Assert.Throws<System.ArgumentException>(() => TJBakeRagdoll.Record(rig, new TJBakeRagdollOptions(), "x"));
        }
    }
}
