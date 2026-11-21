using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>
    /// Poses a rig with a clip at a time. A Generic clip on a root whose Animator is Humanoid, such as a recorded ragdoll
    /// fall on a figure posed by Humanoid clips, has its Transform curves played here: <see cref="AnimationClip.SampleAnimation"/>
    /// ignores them on that rig, even with the Animator disabled, and leaves the body in one pose.
    /// </summary>
    public static class TJBakeClipSampling
    {
        public static void Sample(AnimationClip clip, GameObject root, float time)
        {
            Animator animator = root.GetComponent<Animator>();
            if (clip.humanMotion || animator == null || !animator.isHuman)
            {
                clip.SampleAnimation(root, time);
                return;
            }
            var rotations = new Dictionary<Transform, Vector4>();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(Transform)) throw new Exception($"clip '{clip.name}' animates {binding.type.Name}.{binding.propertyName}, which only Transform curves can play on a Humanoid rig");
                Transform bone = root.transform.Find(binding.path);
                if (bone == null) continue;
                float value = AnimationUtility.GetEditorCurve(clip, binding).Evaluate(time);
                string property = binding.propertyName;
                int axis = "xyzw".IndexOf(property[^1]);
                if (property.StartsWith("m_LocalPosition.", StringComparison.Ordinal))
                {
                    Vector3 p = bone.localPosition;
                    p[axis] = value;
                    bone.localPosition = p;
                }
                else if (property.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    Vector3 s = bone.localScale;
                    s[axis] = value;
                    bone.localScale = s;
                }
                else if (property.StartsWith("localEulerAngles", StringComparison.Ordinal))
                {
                    Vector3 e = bone.localEulerAngles;
                    e[axis] = value;
                    bone.localEulerAngles = e;
                }
                else if (property.StartsWith("m_LocalRotation.", StringComparison.Ordinal))
                {
                    // A rotation's four curves set it together, once all are read.
                    Quaternion r = bone.localRotation;
                    Vector4 q = rotations.TryGetValue(bone, out Vector4 seen) ? seen : new Vector4(r.x, r.y, r.z, r.w);
                    q[axis] = value;
                    rotations[bone] = q;
                }
                else throw new Exception($"clip '{clip.name}' animates Transform.{property}, which this sampler does not play");
            }
            foreach (KeyValuePair<Transform, Vector4> rotation in rotations)
            {
                Vector4 q = rotation.Value.normalized;
                rotation.Key.localRotation = new Quaternion(q.x, q.y, q.z, q.w);
            }
        }
    }
}
