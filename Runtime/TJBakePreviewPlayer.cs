using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MemoriStudios.TJBake
{
    /// <summary>
    /// Plays a baked visual on ordinary MeshRenderers, without Entities. Used by the baker's preview and by any host that
    /// shows a unit outside its battle (menus, cards, codex pages).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class TJBakePreviewPlayer : MonoBehaviour
    {
        private static readonly int CurId = Shader.PropertyToID("_GpuAnimCur");
        private static readonly int PrevId = Shader.PropertyToID("_GpuAnimPrev");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BoneTexId = Shader.PropertyToID("_BoneTex");

        [Tooltip("Folder with unit.json to load on enable. Leave empty when a host calls Show itself.")]
        [SerializeField] private string folder = "";
        [SerializeField] private int variant;
        [SerializeField] private int startSlot;
        [SerializeField] private float speed = 1f;
        [Tooltip("Ignore Time.timeScale, for previews that stay live while the game is paused.")]
        [SerializeField] private bool unscaledTime = true;

        private TJBakeVisual _visual;
        private TJBakePlaybackState _state = TJBakePlaybackState.Stopped;
        private readonly List<Renderer> _skinned = new();
        private readonly List<(Transform follower, int anchor)> _followers = new();
        private readonly List<Object> _owned = new();
        private readonly List<Material> _flatMaterials = new();
        private MaterialPropertyBlock _block;
        private TJBakePreviewPlayer _rider;
        private Transform _content;
        private int _idleSlot;

        public TJBakeVisual Visual => _visual;
        public int CurrentSlot => _state.CurrentSlot;
        public float Speed { get => speed; set => speed = value; }
        public bool UnscaledTime { get => unscaledTime; set => unscaledTime = value; }
        public int IdleSlot { get => _idleSlot; set => _idleSlot = value; }

        #region Setup
        private void OnEnable()
        {
            if (_visual == null && !string.IsNullOrEmpty(folder)) LoadFolder(folder, variant);
        }

        /// <summary>Loads a folder and shows it. This player owns and destroys everything it loads.</summary>
        public void LoadFolder(string path, int variantIndex = 0)
        {
            Clear();
            folder = path;
            TJBakeVisual visual = TJBakeVisualLoader.Load(path, _owned);
            Show(visual, variantIndex);
        }

        /// <summary>Shows an already loaded visual. The caller keeps ownership of the visual's objects.</summary>
        public void Show(TJBakeVisual visual, int variantIndex = 0)
        {
            ClearChildren();
            _visual = visual;
            variant = Mathf.Clamp(variantIndex, 0, visual.Variants.Count - 1);
            _block ??= new MaterialPropertyBlock();
            _content = new GameObject("TJBake Visual").transform;
            _content.SetParent(transform, false);
            _content.gameObject.hideFlags = HideFlags.DontSave;

            TJBakeVariant v = visual.Variants[variant];
            var lodRenderers = new List<Renderer>[v.Lods.Count];
            for (int lod = 0; lod < v.Lods.Count; lod++)
            {
                lodRenderers[lod] = new List<Renderer>();
                foreach (TJBakeMeshRef m in v.Lods[lod].Meshes)
                {
                    var go = new GameObject($"LOD{lod} {m.Mesh.name}");
                    go.transform.SetParent(_content, false);
                    go.AddComponent<MeshFilter>().sharedMesh = m.Mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterials = SubmeshMaterials(visual.SkinnedMaterials, m.MaterialIndex, m.Mesh.subMeshCount);
                    _skinned.Add(r);
                    lodRenderers[lod].Add(r);
                }
            }

            Transform saddle = null;
            foreach (TJBakeAttachment a in v.Attachments)
            {
                var follower = new GameObject($"Anchor {a.AnchorIndex} {TJBakeValidation.RoleName(a.Role)}").transform;
                follower.SetParent(_content, false);
                _followers.Add((follower, a.AnchorIndex));
                if (a.Role == TJBakePropRole.Saddle) saddle = follower;
                var go = new GameObject(a.Mesh.name);
                go.transform.SetParent(follower, false);
                go.AddComponent<MeshFilter>().sharedMesh = a.Mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = SubmeshMaterials(visual.RigidMaterials, a.MaterialIndex, a.Mesh.subMeshCount);
                for (int lod = 0; lod < lodRenderers.Length; lod++) if ((a.LodMask & (1 << lod)) != 0) lodRenderers[lod].Add(r);
            }

            if (v.Lods.Count > 1)
            {
                var group = _content.gameObject.AddComponent<LODGroup>();
                var lods = new LOD[v.Lods.Count];
                for (int i = 0; i < lods.Length; i++)
                {
                    float h = i < visual.LodSwitch.Length ? visual.LodSwitch[i] : 0f;
                    lods[i] = new LOD(h, lodRenderers[i].ToArray());
                }
                group.SetLODs(lods);
                group.RecalculateBounds();
            }

            if (visual.Rider != null && saddle != null)
            {
                var riderGo = new GameObject("Rider");
                riderGo.transform.SetParent(saddle, false);
                _rider = riderGo.AddComponent<TJBakePreviewPlayer>();
                _rider.unscaledTime = unscaledTime;
                _rider.Show(visual.Rider, 0);
            }

            _state = TJBakePlaybackState.Stopped;
            Play(Mathf.Clamp(startSlot, 0, visual.Slots.Count - 1), 0f);
            Tick(0f);
        }

        private static Material[] SubmeshMaterials(Material[] table, int index, int subMeshCount)
        {
            Material m = table.Length == 0 ? null : table[Mathf.Clamp(index, 0, table.Length - 1)];
            var result = new Material[Mathf.Max(1, subMeshCount)];
            for (int i = 0; i < result.Length; i++) result[i] = m;
            return result;
        }
        #endregion

        #region Playback
        /// <summary>Plays a slot; the rider, if any, plays the matching rider slot (attack for any attack, death for any death).</summary>
        public void Play(int slot, float transitionSeconds = 0.25f)
        {
            if (_visual == null) return;
            TJBakePlayback.Play(ref _state, _visual.Slots, slot, transitionSeconds);
            if (_rider != null && _rider._visual != null)
            {
                int riderSlot = slot == 4 || slot == 12 ? 1 : slot >= 8 && slot <= 10 ? 2 : 0;
                _rider.Play(riderSlot, transitionSeconds);
            }
        }

        private void Update()
        {
            // In edit mode the baker window drives Tick, so the preview moves without entering Play.
            if (!Application.isPlaying) return;
            Tick((unscaledTime ? Time.unscaledDeltaTime : Time.deltaTime) * speed);
        }

        /// <summary>Advances playback and pushes the state to the renderers and props.</summary>
        public void Tick(float dt)
        {
            if (_visual == null || _content == null) return;
            TJBakePlayback.Step(ref _state, _visual.Slots, dt, _idleSlot);
            _block.Clear();
            _block.SetVector(CurId, (Vector4)_state.Current);
            _block.SetVector(PrevId, (Vector4)_state.Previous);
            foreach (Renderer r in _skinned) if (r != null) r.SetPropertyBlock(_block);
            foreach ((Transform follower, int anchor) in _followers)
            {
                if (follower == null) continue;
                float3x4 m = TJBakePlayback.SampleAnchor(_visual, anchor, _state);
                follower.localPosition = m.c3;
                follower.localRotation = quaternion.LookRotationSafe(m.c2, m.c1);
                // The anchor matrix carries the bake's root scale; props were baked at scale 1.
                follower.localScale = Vector3.one * math.length(m.c0);
            }
            if (_rider != null) _rider.Tick(dt);
        }
        #endregion

        #region Looks
        /// <summary>Draws every body and prop in one flat colour and keeps animating, as a locked or undiscovered card needs. Null restores the textures.</summary>
        public void SetFlatColour(Color? colour)
        {
            DestroyFlatMaterials();
            if (_visual == null) return;
            if (colour.HasValue)
            {
                Material skinnedFlat = FlatCopy(_visual.SkinnedMaterials, colour.Value, true);
                Material rigidFlat = FlatCopy(_visual.RigidMaterials, colour.Value, false);
                foreach (Renderer r in _skinned) if (r != null) r.sharedMaterials = Fill(r.sharedMaterials.Length, skinnedFlat);
                foreach ((Transform follower, int _) in _followers)
                    foreach (Renderer r in follower.GetComponentsInChildren<Renderer>(true)) r.sharedMaterials = Fill(r.sharedMaterials.Length, rigidFlat);
            }
            else
            {
                Show(_visual, variant);
            }
            if (_rider != null) _rider.SetFlatColour(colour);
        }

        private Material FlatCopy(Material[] table, Color colour, bool skinned)
        {
            if (table.Length == 0) return null;
            var m = new Material(table[0]) { name = table[0].name + " (flat)" };
            m.SetTexture(BaseMapId, Texture2D.whiteTexture);
            m.SetColor(BaseColorId, colour);
            if (skinned && _visual.BoneTexture != null) m.SetTexture(BoneTexId, _visual.BoneTexture);
            _flatMaterials.Add(m);
            return m;
        }

        private static Material[] Fill(int count, Material m)
        {
            var result = new Material[Mathf.Max(1, count)];
            for (int i = 0; i < result.Length; i++) result[i] = m;
            return result;
        }

        /// <summary>Puts every renderer of the visual on one layer, for preview cameras with a culling mask.</summary>
        public void SetLayer(int layer)
        {
            if (_content == null) return;
            foreach (Transform t in _content.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
        #endregion

        #region Teardown
        private void ClearChildren()
        {
            if (_rider != null) _rider.ClearChildren();
            _rider = null;
            _skinned.Clear();
            _followers.Clear();
            DestroyFlatMaterials();
            if (_content != null) DestroyObject(_content.gameObject);
            _content = null;
        }

        /// <summary>Drops the shown visual and everything this player loaded.</summary>
        public void Clear()
        {
            ClearChildren();
            foreach (Object o in _owned) if (o != null) DestroyObject(o);
            _owned.Clear();
            _visual = null;
        }

        private void DestroyFlatMaterials()
        {
            foreach (Material m in _flatMaterials) if (m != null) DestroyObject(m);
            _flatMaterials.Clear();
        }

        private static void DestroyObject(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void OnDestroy() => Clear();
        #endregion
    }
}
