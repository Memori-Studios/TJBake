using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>
    /// The steps a game's figure baker shares: a flat material, its clips loaded and played by an Animator, ragdoll falls
    /// on a Synty humanoid rig, and the bake written to its folder. Every object made here goes in the caller's owned list.
    /// </summary>
    public static class TJBakeFigure
    {
        #region Assets
        /// <summary>A material on TJBake's rigid shader in one colour, with no texture yet, for a figure's body or a piece.</summary>
        public static Material Flat(string name, Color color, float smoothness, List<Object> owned)
        {
            var material = new Material(Shader.Find(TJBakeVisualLoader.RigidShaderName)) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            owned.Add(material);
            return material;
        }

        public static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new FileNotFoundException($"No {typeof(T).Name} at {path}.");
            return asset;
        }

        /// <summary>The file's first clip, past Unity's preview clips.</summary>
        public static AnimationClip LoadClip(string path)
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__")) return clip;
            throw new FileNotFoundException($"No clip in {path}.");
        }
        #endregion

        #region Posing
        /// <summary>
        /// Plays Humanoid clips on the figure's Animator, one state each, in the given order. The first is the default
        /// state, so the rig rests in its pose, which ragdoll falls start from and the sampler restores between slots.
        /// </summary>
        public static void PoseWithClips(GameObject figure, IReadOnlyList<string> states, Dictionary<string, AnimationClip> clips, List<Object> owned)
        {
            // Built by hand: AddLayer and AddState record Undo, which marks the open scene as changed.
            var children = new ChildAnimatorState[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                var state = new AnimatorState { name = states[i], motion = clips[states[i]], hideFlags = HideFlags.HideAndDontSave };
                owned.Add(state);
                children[i] = new ChildAnimatorState { state = state };
            }
            var machine = new AnimatorStateMachine { name = "Base", hideFlags = HideFlags.HideAndDontSave, states = children, defaultState = children[0].state };
            owned.Add(machine);
            var controller = new AnimatorController { name = figure.name, hideFlags = HideFlags.HideAndDontSave };
            owned.Add(controller);
            controller.layers = new[] { new AnimatorControllerLayer { name = "Base", stateMachine = machine, defaultWeight = 1f } };

            Animator animator = figure.GetComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Update(0f);
        }
        #endregion

        #region Falls
        // Falls per direction; the contract's death1 to death3 are the first three onto the back.
        private const int FallsPerSide = 4;

        /// <summary>
        /// Ragdoll falls recorded on a Synty humanoid rig (Hips, Head, Shoulder, Hand, UpperLeg and Ankle bones): four each
        /// onto its back, face, left and right, pushed harder each time. The first three onto the back are death1 to death3,
        /// the rest death&lt;Side&gt;&lt;n&gt;. Seeded by the figure's name, so a rebake records the same falls.
        /// </summary>
        public static Dictionary<string, AnimationClip> RecordFalls(GameObject figure, string figureName, List<Object> owned)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (Transform t in figure.GetComponentsInChildren<Transform>(true)) bones.TryAdd(t.name, t);

            var rig = new TJBakeRagdollRig { Root = figure, Pelvis = bones["Hips"], Radius = 0.12f };
            Transform head = bones["Head"];
            rig.Limbs.Add(new TJBakeRagdollLimb { Bone = head, Tip = head.InverseTransformPoint(head.position + Vector3.up * 0.25f), Radius = 0.13f, MaxSwing = 50f });
            rig.Limbs.Add(Limb(bones["Shoulder_L"], bones["Hand_L"], 0.07f, 150f));
            rig.Limbs.Add(Limb(bones["Shoulder_R"], bones["Hand_R"], 0.07f, 150f));
            rig.Limbs.Add(Limb(bones["UpperLeg_L"], bones["Ankle_L"], 0.09f, 100f));
            rig.Limbs.Add(Limb(bones["UpperLeg_R"], bones["Ankle_R"], 0.09f, 100f));

            var deaths = new Dictionary<string, AnimationClip>();
            (string side, Vector3 push)[] sides =
            {
                ("Back", new Vector3(0f, 1.5f, -4f)), ("Front", new Vector3(0f, 1.5f, 4f)),
                ("Left", new Vector3(-4f, 1.5f, 0f)), ("Right", new Vector3(4f, 1.5f, 0f)),
            };
            foreach ((string side, Vector3 push) in sides)
                for (int i = 0; i < FallsPerSide; i++)
                {
                    string name = side == "Back" && i < 3 ? $"death{i + 1}" : $"death{side}{i + 1}";
                    var options = new TJBakeRagdollOptions { Push = push * (0.8f + 0.25f * i), Seed = Seed(figureName + name) };
                    AnimationClip clip = TJBakeRagdoll.Record(rig, options, name);
                    owned.Add(clip);
                    deaths[name] = clip;
                }
            return deaths;
        }

        private static TJBakeRagdollLimb Limb(Transform bone, Transform end, float radius, float maxSwing) =>
            new() { Bone = bone, Tip = bone.InverseTransformPoint(end.position), Radius = radius, MaxSwing = maxSwing };

        /// <summary>FNV-1a over the text: the same seed in every process, so a rebake is the same bake.</summary>
        public static int Seed(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
        #endregion

        #region Output
        /// <summary>
        /// The mark <see cref="Write"/> adds to a bake's baker name: its falls move. A figure baked before tjbake 0.2.18
        /// held one still pose in every fall and lacks it.
        /// </summary>
        public const string FallsMark = ", falls";

        /// <summary>Whether a figure's unit.json text comes from a bake whose falls held one still pose, so it should bake again.</summary>
        public static bool HasStillFalls(string manifestText) => !manifestText.Contains(FallsMark);

        /// <summary>
        /// Bakes the request into its output folder, emptied first, refreshes the asset database and returns the bake's
        /// report. The baker name gains <see cref="FallsMark"/>.
        /// </summary>
        public static string Write(TJBakeRequest request)
        {
            if (!request.BakerName.Contains(FallsMark)) request.BakerName += FallsMark;
            if (Directory.Exists(request.OutputFolder)) Directory.Delete(request.OutputFolder, true);
            var report = new StringBuilder();
            TJBakeSampler.Bake(request, report);
            AssetDatabase.Refresh();
            return report.ToString();
        }

        /// <summary>Deletes each figure folder under the root that is not one of the names, with its meta, since a folder of StreamingAssets ships whole.</summary>
        public static void DeleteOthers(string root, IReadOnlyCollection<string> names)
        {
            if (!Directory.Exists(root)) return;
            var keep = new HashSet<string>(names);
            foreach (string folder in Directory.GetDirectories(root))
            {
                if (keep.Contains(Path.GetFileName(folder))) continue;
                Directory.Delete(folder, true);
                File.Delete(folder + ".meta");
            }
        }
        #endregion
    }
}
