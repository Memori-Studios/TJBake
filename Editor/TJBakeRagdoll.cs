using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>A limb of a ragdoll: one bone that swings from its own joint on the torso, with a free end.</summary>
    public sealed class TJBakeRagdollLimb
    {
        public Transform Bone;

        /// <summary>The free end in the bone's own space at rest: the hand of an arm, the foot of a leg, the top of a head.</summary>
        public Vector3 Tip;

        /// <summary>Half the limb's thickness; the tip stays this far off the ground.</summary>
        public float Radius = 0.07f;

        /// <summary>The most the limb may swing away from its rest direction on the torso, in degrees.</summary>
        public float MaxSwing = 120f;
    }

    /// <summary>The rig a ragdoll recording moves: a rigid torso with limbs hanging off it. Every limb's parent is part of the torso.</summary>
    public sealed class TJBakeRagdollRig
    {
        /// <summary>The GameObject the bake samples (<see cref="TJBakeRequest.Root"/>); the clip's paths are relative to it.</summary>
        public GameObject Root;

        /// <summary>The bone that carries the body. It moves and turns with the torso; its parent stays put.</summary>
        public Transform Pelvis;

        public List<TJBakeRagdollLimb> Limbs = new();

        /// <summary>Half the torso's thickness; its joints stay this far off the ground.</summary>
        public float Radius = 0.12f;
    }

    public sealed class TJBakeRagdollOptions
    {
        public int Fps = 30;
        public float Seconds = 1.2f;

        /// <summary>The shove that starts the fall, in the root's space in metres per second. Full on the torso's highest joint, nothing at its lowest.</summary>
        public Vector3 Push = new(0f, 1.5f, -4f);

        /// <summary>How much the seed varies the push and jolts the limbs: 0 is none, 1 is a lot.</summary>
        public float Variation = 0.3f;

        public int Seed;
        public float Gravity = 9.81f;

        /// <summary>The floor's height in the root's space.</summary>
        public float Ground;
    }

    /// <summary>
    /// Records a ragdoll fall as an <see cref="AnimationClip"/> the baker can sample like any other: the torso is one
    /// rigid body of joints and each limb a stick that swings from its joint, solved position-based with a floor and
    /// friction. The same rig, options and seed always record the same clip, so a bake is repeatable on any machine.
    /// </summary>
    public static class TJBakeRagdoll
    {
        private const int Substeps = 4;
        private const int Iterations = 6;
        private const float Damping = 0.995f;
        private const float Friction = 0.6f;
        // The torso outweighs a limb's free end, so a swinging arm hardly tugs the body.
        private const float TipMass = 0.1f;

        public static AnimationClip Record(TJBakeRagdollRig rig, TJBakeRagdollOptions options, string name)
        {
            if (rig == null || rig.Root == null) throw new ArgumentException("the rig needs a Root");
            if (rig.Pelvis == null) throw new ArgumentException("the rig needs a Pelvis bone");
            if (options.Fps <= 0 || options.Seconds <= 0f) throw new ArgumentException("Fps and Seconds must be positive");
            foreach (TJBakeRagdollLimb limb in rig.Limbs)
            {
                if (limb.Bone == null) throw new ArgumentException("a limb has no bone");
                if (limb.Tip.sqrMagnitude < 1e-6f) throw new ArgumentException($"limb {limb.Bone.name} has no tip");
            }

            var body = new Body(rig, options);
            int frames = Mathf.RoundToInt(options.Seconds * options.Fps) + 1;
            var tracks = new Tracks(rig, frames);
            float dt = 1f / (options.Fps * Substeps);
            for (int f = 0; f < frames; f++)
            {
                if (f > 0)
                    for (int s = 0; s < Substeps; s++) body.Step(dt);
                tracks.Key(f / (float)options.Fps, body);
            }
            return tracks.ToClip(name, options.Fps);
        }

        #region Simulation
        /// <summary>Points in the root's space: the torso's joints first, then one free end per limb.</summary>
        private sealed class Body
        {
            public readonly Vector3[] Position;
            private readonly Vector3[] _previous;
            private readonly float[] _inverseMass;
            private readonly float[] _radius;
            private readonly float _gravity;
            private readonly float _ground;

            // The torso at rest, centred, and its joints' current rotation.
            private readonly Vector3[] _torsoRest;
            public readonly int TorsoCount;
            public Quaternion TorsoRotation = Quaternion.identity;

            // Per limb: its joint's point, its free end's point, its length and its rest direction in the root's space.
            public readonly int[] LimbJoint;
            public readonly int[] LimbTip;
            private readonly float[] _limbLength;
            public readonly Vector3[] LimbRestDirection;
            private readonly float[] _maxSwingCos;

            public Body(TJBakeRagdollRig rig, TJBakeRagdollOptions options)
            {
                _gravity = options.Gravity;
                _ground = options.Ground;
                Transform root = rig.Root.transform;
                var points = new List<Vector3> { root.InverseTransformPoint(rig.Pelvis.position) };
                var radii = new List<float> { rig.Radius };
                LimbJoint = new int[rig.Limbs.Count];
                LimbTip = new int[rig.Limbs.Count];
                _limbLength = new float[rig.Limbs.Count];
                LimbRestDirection = new Vector3[rig.Limbs.Count];
                _maxSwingCos = new float[rig.Limbs.Count];
                for (int i = 0; i < rig.Limbs.Count; i++)
                {
                    Vector3 joint = root.InverseTransformPoint(rig.Limbs[i].Bone.position);
                    // Limbs that share a joint share its point.
                    int index = points.FindIndex(p => (p - joint).sqrMagnitude < 1e-6f);
                    if (index < 0)
                    {
                        index = points.Count;
                        points.Add(joint);
                        radii.Add(rig.Radius);
                    }
                    LimbJoint[i] = index;
                }
                TorsoCount = points.Count;
                for (int i = 0; i < rig.Limbs.Count; i++)
                {
                    TJBakeRagdollLimb limb = rig.Limbs[i];
                    Vector3 tip = root.InverseTransformPoint(limb.Bone.TransformPoint(limb.Tip));
                    Vector3 joint = points[LimbJoint[i]];
                    LimbTip[i] = points.Count;
                    points.Add(tip);
                    radii.Add(limb.Radius);
                    _limbLength[i] = (tip - joint).magnitude;
                    LimbRestDirection[i] = (tip - joint).normalized;
                    _maxSwingCos[i] = Mathf.Cos(Mathf.Clamp(limb.MaxSwing, 0f, 180f) * Mathf.Deg2Rad);
                }

                Position = points.ToArray();
                _previous = (Vector3[])Position.Clone();
                _radius = radii.ToArray();
                _inverseMass = new float[Position.Length];
                for (int i = 0; i < Position.Length; i++) _inverseMass[i] = i < TorsoCount ? 1f : 1f / TipMass;

                Vector3 centre = Centroid(Position, TorsoCount);
                _torsoRest = new Vector3[TorsoCount];
                for (int i = 0; i < TorsoCount; i++) _torsoRest[i] = Position[i] - centre;

                Shove(options);
            }

            /// <summary>Starts the fall: the push, scaled by height up the torso, plus the seed's variation.</summary>
            private void Shove(TJBakeRagdollOptions options)
            {
                var random = new System.Random(options.Seed);
                float Spread() => (float)(random.NextDouble() * 2.0 - 1.0) * options.Variation;
                Vector3 push = Quaternion.AngleAxis(Spread() * 35f, Vector3.up) * options.Push * (1f + Spread() * 0.4f);

                float low = float.MaxValue, high = float.MinValue;
                for (int i = 0; i < TorsoCount; i++)
                {
                    low = Mathf.Min(low, Position[i].y);
                    high = Mathf.Max(high, Position[i].y);
                }
                float span = Mathf.Max(high - low, 0.01f);
                float dt = 1f / (options.Fps * Substeps);
                var velocity = new Vector3[Position.Length];
                for (int i = 0; i < TorsoCount; i++) velocity[i] = push * Mathf.Clamp01((Position[i].y - low) / span);
                for (int l = 0; l < LimbTip.Length; l++)
                {
                    var jolt = new Vector3(Spread(), Spread(), Spread()) * 2f;
                    velocity[LimbTip[l]] = velocity[LimbJoint[l]] + jolt;
                }
                for (int i = 0; i < Position.Length; i++) _previous[i] = Position[i] - velocity[i] * dt;
            }

            public void Step(float dt)
            {
                for (int i = 0; i < Position.Length; i++)
                {
                    Vector3 velocity = (Position[i] - _previous[i]) * Damping;
                    _previous[i] = Position[i];
                    Position[i] += velocity + new Vector3(0f, -_gravity, 0f) * (dt * dt);
                }

                for (int it = 0; it < Iterations; it++)
                {
                    MatchTorso();
                    for (int l = 0; l < LimbTip.Length; l++) SolveLimb(l);
                    for (int i = 0; i < Position.Length; i++) SolveGround(i);
                }
            }

            /// <summary>Moves the torso's joints back onto their rest shape, rotated and placed to fit where they are now.</summary>
            private void MatchTorso()
            {
                Vector3 centre = Centroid(Position, TorsoCount);
                TorsoRotation = ExtractRotation(_torsoRest, Position, centre, TorsoCount, TorsoRotation);
                for (int i = 0; i < TorsoCount; i++) Position[i] = centre + TorsoRotation * _torsoRest[i];
            }

            /// <summary>Keeps a limb its length, and inside its swing from the direction it hangs at rest.</summary>
            private void SolveLimb(int l)
            {
                int j = LimbJoint[l], t = LimbTip[l];
                Vector3 delta = Position[t] - Position[j];
                float length = delta.magnitude;
                if (length < 1e-6f) delta = TorsoRotation * LimbRestDirection[l] * _limbLength[l];
                else delta *= _limbLength[l] / length;

                Vector3 rest = TorsoRotation * LimbRestDirection[l];
                Vector3 direction = delta / _limbLength[l];
                if (Vector3.Dot(direction, rest) < _maxSwingCos[l])
                {
                    float maxSwing = Mathf.Acos(_maxSwingCos[l]) * Mathf.Rad2Deg;
                    direction = Vector3.RotateTowards(rest, direction, maxSwing * Mathf.Deg2Rad, 0f).normalized;
                    delta = direction * _limbLength[l];
                }

                Vector3 correction = delta - (Position[t] - Position[j]);
                float wj = _inverseMass[j], wt = _inverseMass[t];
                Position[t] += correction * (wt / (wj + wt));
                Position[j] -= correction * (wj / (wj + wt));
            }

            private void SolveGround(int i)
            {
                float floor = _ground + _radius[i];
                if (Position[i].y >= floor) return;
                Position[i].y = floor;
                // A point on the floor loses most of its slide.
                Vector3 slide = Position[i] - _previous[i];
                _previous[i] = new Vector3(_previous[i].x + slide.x * Friction, _previous[i].y, _previous[i].z + slide.z * Friction);
            }

            private static Vector3 Centroid(Vector3[] points, int count)
            {
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < count; i++) sum += points[i];
                return sum / Mathf.Max(count, 1);
            }

            /// <summary>The rotation that best carries the rest shape onto the current points (Müller 2016), warm-started from the last one.</summary>
            private static Quaternion ExtractRotation(Vector3[] rest, Vector3[] current, Vector3 centre, int count, Quaternion start)
            {
                // The covariance of current against rest, by column.
                Vector3 a0 = Vector3.zero, a1 = Vector3.zero, a2 = Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    Vector3 q = current[i] - centre, p = rest[i];
                    a0 += q * p.x;
                    a1 += q * p.y;
                    a2 += q * p.z;
                }
                Quaternion rotation = start;
                for (int iter = 0; iter < 12; iter++)
                {
                    Vector3 r0 = rotation * Vector3.right, r1 = rotation * Vector3.up, r2 = rotation * Vector3.forward;
                    Vector3 omega = (Vector3.Cross(r0, a0) + Vector3.Cross(r1, a1) + Vector3.Cross(r2, a2))
                                    * (1f / (Mathf.Abs(Vector3.Dot(r0, a0) + Vector3.Dot(r1, a1) + Vector3.Dot(r2, a2)) + 1e-9f));
                    float angle = omega.magnitude;
                    if (angle < 1e-6f) break;
                    rotation = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, omega / angle) * rotation;
                    rotation.Normalize();
                }
                return rotation;
            }
        }
        #endregion

        #region Clip
        /// <summary>The pelvis's position and rotation and every limb's rotation per frame, as local-space curves.</summary>
        private sealed class Tracks
        {
            private readonly TJBakeRagdollRig _rig;
            private readonly string _pelvisPath;
            private readonly string[] _limbPaths;
            private readonly Quaternion _pelvisParentRest;
            private readonly Vector3 _pelvisParentPosition;
            private readonly Quaternion _pelvisRest;
            private readonly Quaternion[] _limbRest;
            private readonly Quaternion[] _limbParentRest;
            private readonly AnimationCurve[] _pelvisPosition = { new(), new(), new() };
            private readonly AnimationCurve[] _pelvisRotation = { new(), new(), new(), new() };
            private readonly AnimationCurve[][] _limbRotation;
            private Quaternion _lastPelvis;
            private readonly Quaternion[] _lastLimb;

            public Tracks(TJBakeRagdollRig rig, int frames)
            {
                _rig = rig;
                Transform root = rig.Root.transform;
                _pelvisPath = AnimationUtility.CalculateTransformPath(rig.Pelvis, root);
                // Everything is measured in the root's space; the pelvis's parent never moves.
                Transform pelvisParent = rig.Pelvis.parent != null ? rig.Pelvis.parent : root;
                _pelvisParentRest = Quaternion.Inverse(root.rotation) * pelvisParent.rotation;
                _pelvisParentPosition = root.InverseTransformPoint(pelvisParent.position);
                _pelvisRest = Quaternion.Inverse(root.rotation) * rig.Pelvis.rotation;
                _lastPelvis = rig.Pelvis.localRotation;

                int limbs = rig.Limbs.Count;
                _limbPaths = new string[limbs];
                _limbRest = new Quaternion[limbs];
                _limbParentRest = new Quaternion[limbs];
                _limbRotation = new AnimationCurve[limbs][];
                _lastLimb = new Quaternion[limbs];
                for (int i = 0; i < limbs; i++)
                {
                    Transform bone = rig.Limbs[i].Bone;
                    _limbPaths[i] = AnimationUtility.CalculateTransformPath(bone, root);
                    _limbRest[i] = Quaternion.Inverse(root.rotation) * bone.rotation;
                    Transform parent = bone.parent != null ? bone.parent : root;
                    _limbParentRest[i] = Quaternion.Inverse(root.rotation) * parent.rotation;
                    _limbRotation[i] = new AnimationCurve[] { new(), new(), new(), new() };
                    _lastLimb[i] = bone.localRotation;
                }
            }

            public void Key(float time, Body body)
            {
                Quaternion torso = body.TorsoRotation;
                // The pelvis carries the torso: its world rotation turns with the torso, and it sits on its own point.
                Quaternion pelvisWorld = torso * _pelvisRest;
                Quaternion pelvisLocal = Quaternion.Inverse(_pelvisParentRest) * pelvisWorld;
                Vector3 pelvisLocalPosition = Quaternion.Inverse(_pelvisParentRest) * (body.Position[0] - _pelvisParentPosition);
                pelvisLocal = Continuous(pelvisLocal, ref _lastPelvis);
                AddKey(_pelvisPosition, time, pelvisLocalPosition);
                AddKey(_pelvisRotation, time, pelvisLocal);

                for (int i = 0; i < _limbPaths.Length; i++)
                {
                    Vector3 direction = body.Position[body.LimbTip[i]] - body.Position[body.LimbJoint[i]];
                    // The limb swings away from where it hangs on the turned torso; its twist follows the torso.
                    Vector3 rest = torso * body.LimbRestDirection[i];
                    Quaternion swing = direction.sqrMagnitude > 1e-10f ? Quaternion.FromToRotation(rest, direction.normalized) : Quaternion.identity;
                    Quaternion world = swing * torso * _limbRest[i];
                    Quaternion parentWorld = torso * _limbParentRest[i];
                    Quaternion local = Continuous(Quaternion.Inverse(parentWorld) * world, ref _lastLimb[i]);
                    AddKey(_limbRotation[i], time, local);
                }
            }

            public AnimationClip ToClip(string name, int fps)
            {
                var clip = new AnimationClip { name = name, frameRate = fps };
                string[] position = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" };
                string[] rotation = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
                for (int c = 0; c < 3; c++) clip.SetCurve(_pelvisPath, typeof(Transform), position[c], _pelvisPosition[c]);
                for (int c = 0; c < 4; c++) clip.SetCurve(_pelvisPath, typeof(Transform), rotation[c], _pelvisRotation[c]);
                for (int i = 0; i < _limbPaths.Length; i++)
                    for (int c = 0; c < 4; c++) clip.SetCurve(_limbPaths[i], typeof(Transform), rotation[c], _limbRotation[i][c]);
                return clip;
            }

            // A quaternion and its negation are one rotation; keep the sign that follows the last key, so the curves never flip.
            private static Quaternion Continuous(Quaternion q, ref Quaternion last)
            {
                if (Quaternion.Dot(q, last) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                last = q;
                return q;
            }

            private static void AddKey(AnimationCurve[] curves, float time, Vector3 v)
            {
                curves[0].AddKey(new Keyframe(time, v.x));
                curves[1].AddKey(new Keyframe(time, v.y));
                curves[2].AddKey(new Keyframe(time, v.z));
            }

            private static void AddKey(AnimationCurve[] curves, float time, Quaternion q)
            {
                curves[0].AddKey(new Keyframe(time, q.x));
                curves[1].AddKey(new Keyframe(time, q.y));
                curves[2].AddKey(new Keyframe(time, q.z));
                curves[3].AddKey(new Keyframe(time, q.w));
            }
        }
        #endregion
    }
}
