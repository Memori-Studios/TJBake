# TJBake

GPU animation baking for Unity. TJBake samples a rigged, animated prefab into a bone matrix texture and plain mesh
files, then plays them back on the GPU with no Animator at runtime. Thousands of animated characters cost a few draw
calls.

It was built for modding [Tabletop Tavern](https://store.steampowered.com/app/3337380), where players reskin the
game's units. Nothing in the package is specific to that game: any project can bake with it and play the result back
on ordinary MeshRenderers or on an Entities Graphics batch.

## What you get

- **A baker window** (`Window > TJBake`) that turns a prefab and its clips into a folder of files.
- **A file format** with no Unity serialization in it: a JSON manifest, a half-float matrix file, anchor matrices and
  meshes. Any language can read it. See [the format](Documentation~/format.md).
- **A shader**, `TJBake/Unit`, built on URP Lit with forward, shadow, depth and depth-normal passes, so baked
  characters light, shadow and outline like any other mesh.
- **A preview player**, `TJBakePreviewPlayer`, that plays a baked folder on MeshRenderers in edit mode or play mode.
- **The loader** the game uses, `TJBakeVisualLoader`, with every count and offset bounds-checked. A damaged or hostile
  file is refused with one clear message.

## Install

In Unity 6 or later with URP, open `Window > Package Manager`, press `+`, choose **Install package from git URL** and
enter:

```text
https://github.com/Memori-Studios/TJBake.git
```

## Bake a character

1. Select your rigged character prefab in the Project window. It needs a SkinnedMeshRenderer and an Animator whose
   controller holds the clips you want.
2. Open `Window > TJBake` and press **New profile from the selected prefab**. TJBake creates a profile next to the
   prefab and guesses a clip for each slot by name.
3. Check every slot. Each slot has a clip and a kind: it loops, it plays once and returns to idle, or it plays once and
   holds its last frame (deaths). A returning slot hands back at `returnAt`, a fraction of the clip: the time of a clip event whose function or string
   parameter contains `ReturnToIdle`, otherwise 0.9. You can also type it in.
4. Add anchors for anything the character holds or wears (`Add anchor at bone`), then list the props under
   **Attachments**: a prop prefab, the anchor it follows, and its role.
5. Pick the output folder. For a Tabletop Tavern mod that is `Mods/<YourMod>/unit_visuals/<UnitName>`.
6. Press **Check**, then **Bake**, then **Preview** to watch it play in the Scene view.

Optional: add lower-detail prefabs that share the same bone names as LOD1 and LOD2, with a screen-height switch for
each, and a rider profile for a mounted unit (the mount needs a `saddle` attachment).

## Limits

- 4 bone influences per vertex. Rigs with more are renormalized to their strongest four.
- Up to 5461 bones and 16384 frames in total across all slots.
- 24, 30 or 60 frames per second, the same for every slot.
- Root motion is not baked. The character animates in place.
- No crossfade between LODs; the switch is a hard cut.

## Licence

MIT. See [LICENSE.md](LICENSE.md). Pull requests are welcome under the same licence; see
[CONTRIBUTING.md](CONTRIBUTING.md).

The technique, bone matrices baked into a texture and fetched in the vertex shader, is public and old (GPU Gems 3,
2007, chapter 2, among others). This package is an independent implementation and contains no third-party code or
assets.
