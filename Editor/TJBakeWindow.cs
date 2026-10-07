using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MemoriStudios.TJBake.Editor
{
    /// <summary>The baker: pick a profile, check it, bake it, and watch the result play in the Scene view.</summary>
    public sealed class TJBakeWindow : EditorWindow
    {
        [SerializeField] private TJBakeProfile profile;
        private SerializedObject _so;
        private Vector2 _scroll;
        private string _report = "";
        private List<string> _problems = new();
        private TJBakePreviewPlayer _preview;
        private int _previewSlot;
        private double _lastTick;
        private string[] _bonePaths = Array.Empty<string>();
        private GameObject _bonePathsFor;

        [MenuItem("Window/TJBake")]
        public static void Open() => GetWindow<TJBakeWindow>("TJBake");

        private void OnEnable()
        {
            EditorApplication.update += TickPreview;
            _lastTick = EditorApplication.timeSinceStartup;
        }

        private void OnDisable()
        {
            EditorApplication.update -= TickPreview;
            DestroyPreview();
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            profile = (TJBakeProfile)EditorGUILayout.ObjectField("Profile", profile, typeof(TJBakeProfile), false);
            if (EditorGUI.EndChangeCheck()) { _so = null; _problems.Clear(); }
            if (profile == null)
            {
                EditorGUILayout.HelpBox("Create a profile with Assets > Create > TJBake > Bake Profile, or from a prefab below.", MessageType.Info);
                if (GUILayout.Button("New profile from the selected prefab")) CreateFromSelection();
                return;
            }
            _so ??= new SerializedObject(profile);
            _so.Update();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            Section("Source");
            Prop("unitName"); Prop("isRider"); Prop("sourcePrefab"); Prop("rootScale"); Prop("frameRate");
            Section("Level of detail");
            Prop("lodPrefabs"); Prop("lodSwitch");
            Section("Slots");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset Slots"))
                {
                    _so.ApplyModifiedProperties();
                    Undo.RecordObject(profile, "Reset TJBake slots");
                    profile.ResetSlotsToContract();
                    EditorUtility.SetDirty(profile);
                    _so.Update();
                }
                if (GUILayout.Button("Fill Clips From Animator"))
                {
                    _so.ApplyModifiedProperties();
                    Undo.RecordObject(profile, "Fill TJBake clips");
                    int n = TJBakeProfileBaker.FillClipsFromAnimator(profile);
                    EditorUtility.SetDirty(profile);
                    _so.Update();
                    _report = $"Filled {n} empty slot(s) by clip name. Check each one.";
                }
            }
            Prop("slots");
            Section("Anchors and props");
            DrawAnchorPicker();
            Prop("anchors"); Prop("attachments"); Prop("rider");
            Section("Output");
            using (new EditorGUILayout.HorizontalScope())
            {
                Prop("outputFolder");
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string chosen = EditorUtility.OpenFolderPanel("TJBake output folder", profile.outputFolder, "");
                    if (!string.IsNullOrEmpty(chosen)) { _so.FindProperty("outputFolder").stringValue = chosen; }
                }
            }
            _so.ApplyModifiedProperties();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Check", GUILayout.Height(26))) _problems = TJBakeProfileBaker.Check(profile);
                if (GUILayout.Button("Bake", GUILayout.Height(26))) Bake();
                using (new EditorGUI.DisabledScope(!File.Exists(Path.Combine(profile.outputFolder ?? "", TJBakeVisualLoader.ManifestName))))
                    if (GUILayout.Button("Preview", GUILayout.Height(26))) StartPreview();
            }
            foreach (string p in _problems) EditorGUILayout.HelpBox(p, MessageType.Warning);
            if (_preview != null && _preview.Visual != null) DrawPreviewControls();
            if (!string.IsNullOrEmpty(_report)) EditorGUILayout.HelpBox(_report, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        private void Section(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private void Prop(string name) => EditorGUILayout.PropertyField(_so.FindProperty(name), true);

        private void DrawAnchorPicker()
        {
            if (profile.sourcePrefab == null) return;
            if (_bonePathsFor != profile.sourcePrefab)
            {
                _bonePaths = TJBakeProfileBaker.BonePaths(profile.sourcePrefab).ToArray();
                _bonePathsFor = profile.sourcePrefab;
            }
            int picked = EditorGUILayout.Popup("Add anchor at bone", -1, _bonePaths);
            if (picked >= 0)
            {
                _so.ApplyModifiedProperties();
                Undo.RecordObject(profile, "Add TJBake anchor");
                string path = _bonePaths[picked];
                profile.anchors.Add(new TJBakeProfile.Anchor { name = path.Substring(path.LastIndexOf('/') + 1), bonePath = path });
                EditorUtility.SetDirty(profile);
                _so.Update();
            }
        }

        private void Bake()
        {
            _problems = TJBakeProfileBaker.Check(profile);
            if (_problems.Count > 0) return;
            try
            {
                _report = TJBakeProfileBaker.Bake(profile);
                if (_preview != null) StartPreview();
            }
            catch (Exception e)
            {
                _report = "Bake failed: " + e.Message;
                Debug.LogException(e);
            }
        }

        private void CreateFromSelection()
        {
            var prefab = Selection.activeObject as GameObject;
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab) { _report = "Select a prefab asset in the Project window first."; return; }
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(prefab));
            var p = CreateInstance<TJBakeProfile>();
            p.sourcePrefab = prefab;
            p.unitName = prefab.name.Replace(" ", "");
            p.ResetSlotsToContract();
            TJBakeProfileBaker.FillClipsFromAnimator(p);
            string path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, prefab.name + " TJBake Profile.asset"));
            AssetDatabase.CreateAsset(p, path);
            AssetDatabase.SaveAssets();
            profile = p;
            _so = null;
            _report = "Created " + path + ". Check every slot's clip.";
        }

        #region Preview
        private void StartPreview()
        {
            DestroyPreview();
            var go = new GameObject("TJBake Preview") { hideFlags = HideFlags.DontSave };
            _preview = go.AddComponent<TJBakePreviewPlayer>();
            try
            {
                _preview.LoadFolder(profile.outputFolder);
                _previewSlot = 0;
                Selection.activeGameObject = go;
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            catch (Exception e)
            {
                _report = "Preview failed: " + e.Message;
                DestroyPreview();
            }
        }

        private void DrawPreviewControls()
        {
            Section("Preview");
            var names = new string[_preview.Visual.Slots.Count];
            for (int i = 0; i < names.Length; i++) names[i] = $"{i} {_preview.Visual.Slots[i].Name}";
            int slot = EditorGUILayout.Popup("Play slot", _previewSlot, names);
            if (slot != _previewSlot || GUILayout.Button("Replay")) { _previewSlot = slot; _preview.Play(slot); }
            _preview.Speed = EditorGUILayout.Slider("Speed", _preview.Speed, 0f, 2f);
            if (GUILayout.Button("Close preview")) DestroyPreview();
        }

        private void TickPreview()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastTick);
            _lastTick = now;
            if (_preview == null || Application.isPlaying) return;
            _preview.Tick(dt * _preview.Speed);
            SceneView.RepaintAll();
        }

        private void DestroyPreview()
        {
            if (_preview != null) DestroyImmediate(_preview.gameObject);
            _preview = null;
        }
        #endregion
    }
}
