using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>Turns a profile into a sampler request inside a preview scene, and offers the checks the window shows.</summary>
    public static class TJBakeProfileBaker
    {
        /// <summary>Problems that stop a bake, found without sampling anything. Empty means ready.</summary>
        public static List<string> Check(TJBakeProfile p)
        {
            var problems = new List<string>();
            if (p.sourcePrefab == null) { problems.Add("Pick a source prefab."); return problems; }
            if (!p.isRider && string.IsNullOrEmpty(p.unitName)) problems.Add("Name the unit (the host's id for it).");
            if (string.IsNullOrEmpty(p.outputFolder)) problems.Add("Pick an output folder.");
            if (p.sourcePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(false).Length == 0) problems.Add("The source prefab has no active SkinnedMeshRenderer.");
            int required = p.isRider ? TJBakeValidation.RiderSlotCount : TJBakeValidation.UnitSlotCount;
            if (p.slots.Count < required) problems.Add($"{p.slots.Count} slots set up, at least {required} needed. Use Reset Slots.");
            for (int i = 0; i < p.slots.Count; i++)
            {
                if (p.slots[i].clip == null) problems.Add($"Slot {i} ({p.slots[i].name}) has no clip.");
                if (p.slots[i].loop && p.slots[i].returnToIdle) problems.Add($"Slot {i} ({p.slots[i].name}) cannot both loop and return.");
            }
            if (p.frameRate != 24 && p.frameRate != 30 && p.frameRate != 60) problems.Add("Frame rate must be 24, 30 or 60.");
            if (p.lodSwitch.Count != p.lodPrefabs.Count) problems.Add($"Set one LOD switch height per LOD prefab ({p.lodPrefabs.Count}).");
            if (p.lodPrefabs.Count > 2) problems.Add("At most two LOD prefabs (three LODs).");
            var anchorNames = new HashSet<string>();
            foreach (TJBakeProfile.Anchor a in p.anchors)
            {
                if (string.IsNullOrEmpty(a.name) || !anchorNames.Add(a.name)) problems.Add($"Anchor '{a.name}' is empty or repeated.");
                if (p.sourcePrefab.transform.Find(a.bonePath) == null) problems.Add($"Anchor '{a.name}': no bone at '{a.bonePath}'.");
            }
            var roles = new HashSet<TJBakePropRole>();
            bool saddle = false;
            foreach (TJBakeProfile.Attachment a in p.attachments)
            {
                if (!anchorNames.Contains(a.anchor)) problems.Add($"Attachment '{a.name}' names unknown anchor '{a.anchor}'.");
                if (a.role != TJBakePropRole.Prop && !roles.Add(a.role)) problems.Add($"More than one attachment has role {a.role}.");
                if (a.prefab == null && a.role == TJBakePropRole.Prop) problems.Add($"Attachment '{a.name}' has no prefab and no role.");
                if (a.role == TJBakePropRole.Saddle) saddle = true;
            }
            if (p.rider != null)
            {
                if (!saddle) problems.Add("A unit with a rider needs a saddle attachment.");
                if (!p.rider.isRider) problems.Add("The rider profile must have Is Rider ticked.");
                foreach (string r in Check(p.rider)) if (!r.StartsWith("Pick an output folder")) problems.Add("Rider: " + r);
            }
            return problems;
        }

        public static string Bake(TJBakeProfile p)
        {
            List<string> problems = Check(p);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            Scene preview = EditorSceneManager.NewPreviewScene();
            var report = new StringBuilder();
            try
            {
                TJBakeRequest request = BuildRequest(p, preview);
                if (p.rider != null) request.Rider = BuildRequest(p.rider, preview);
                TJBakeSampler.Bake(request, report);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            return report.ToString();
        }

        private static TJBakeRequest BuildRequest(TJBakeProfile p, Scene preview)
        {
            GameObject root = Spawn(p.sourcePrefab, preview);
            var request = new TJBakeRequest
            {
                UnitName = p.unitName, IsRider = p.isRider, OutputFolder = p.outputFolder, Fps = p.frameRate,
                RootScale = p.rootScale, Root = root, BakerName = "TJBake " + PackageVersion(),
            };
            foreach (GameObject lod in p.lodPrefabs) request.LodRoots.Add(Spawn(lod, preview));
            request.LodSwitch.AddRange(p.lodSwitch);
            foreach (TJBakeProfile.Slot s in p.slots)
                request.Slots.Add(new TJBakeSlotRequest { Name = s.name, Clip = s.clip, Loop = s.loop, ReturnToIdle = s.returnToIdle, ReturnAt = s.returnAt });
            foreach (TJBakeProfile.Anchor a in p.anchors)
                request.Anchors.Add(new TJBakeAnchorRequest { Name = a.name, Transform = root.transform.Find(a.bonePath) });
            foreach (TJBakeProfile.Attachment a in p.attachments)
            {
                // The holder sits at the anchor bone so the prop is baked in anchor space.
                Transform bone = root.transform.Find(p.anchors.Find(x => x.name == a.anchor).bonePath);
                var holder = new GameObject(a.name).transform;
                holder.SetParent(bone, false);
                if (a.prefab != null)
                {
                    GameObject prop = Spawn(a.prefab, preview);
                    prop.transform.SetParent(holder, false);
                    prop.transform.localPosition = a.position;
                    prop.transform.localRotation = Quaternion.Euler(a.rotation);
                    prop.transform.localScale = a.scale;
                }
                request.Attachments.Add(new TJBakeAttachmentRequest { Name = a.name, Holder = holder, Anchor = a.anchor, Role = a.role, LodMask = a.lodMask });
            }
            return request;
        }

        private static GameObject Spawn(GameObject prefab, Scene preview)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview);
            if (go == null) { go = UnityEngine.Object.Instantiate(prefab); SceneManager.MoveGameObjectToScene(go, preview); }
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        /// <summary>Guesses a clip for each empty slot from the source's Animator controller, by name.</summary>
        public static int FillClipsFromAnimator(TJBakeProfile p)
        {
            if (p.sourcePrefab == null) return 0;
            var animator = p.sourcePrefab.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.runtimeAnimatorController == null) return 0;
            AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
            int filled = 0;
            foreach (TJBakeProfile.Slot s in p.slots)
            {
                if (s.clip != null) continue;
                AnimationClip best = null;
                foreach (string key in Keys(s.name))
                {
                    foreach (AnimationClip c in clips)
                        if (c != null && c.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { best = c; break; }
                    if (best != null) break;
                }
                if (best != null) { s.clip = best; filled++; }
            }
            return filled;
        }

        // Search words per canonical slot, most specific first.
        private static IEnumerable<string> Keys(string slotName) => slotName switch
        {
            "idle" => new[] { "idle" },
            "walk" => new[] { "walk" },
            "run" => new[] { "run", "sprint" },
            "meleeStance" => new[] { "combat_idle", "battle_idle", "stance", "ready" },
            "meleeAttack" => new[] { "attack", "slash", "swing", "stab" },
            "meleeAttackAlt" => new[] { "attack2", "attack_02", "heavy", "slash2" },
            "idleVariant1" => new[] { "idle1", "idle_01", "idle_1", "fidget" },
            "idleVariant2" => new[] { "idle2", "idle_02", "idle_2" },
            "idleVariant3" => new[] { "idle3", "idle_03", "idle_3" },
            "death1" => new[] { "death1", "death_01", "die", "death" },
            "death2" => new[] { "death2", "death_02", "death" },
            "death3" => new[] { "death3", "death_03", "death" },
            "thrown" => new[] { "knockback", "knock", "hit", "fall" },
            "rangedStance" => new[] { "aim", "draw" },
            "rangedAttack" => new[] { "shoot", "fire", "cast", "release" },
            "attack" => new[] { "attack", "slash", "stab" },
            "death" => new[] { "death", "die" },
            _ => new[] { slotName },
        };

        /// <summary>Every transform path under the prefab, for the anchor bone picker.</summary>
        public static List<string> BonePaths(GameObject prefab)
        {
            var paths = new List<string>();
            if (prefab == null) return paths;
            Walk(prefab.transform, "", paths);
            return paths;
        }

        private static void Walk(Transform t, string prefix, List<string> paths)
        {
            foreach (Transform child in t)
            {
                string path = prefix.Length == 0 ? child.name : prefix + "/" + child.name;
                paths.Add(path);
                Walk(child, path, paths);
            }
        }

        public static string PackageVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TJBakeProfileBaker).Assembly);
            return info != null ? info.version : "dev";
        }
    }
}
