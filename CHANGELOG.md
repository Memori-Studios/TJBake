# Changelog

## [0.1.0] - 2026-10-06

First version.

- Baker window, bake profiles and the sampler: bone matrices, anchor matrices, animation-inclusive bounds, up to 3 LODs
  matched by bone name, an optional rider.
- File format version 1: `unit.json`, `anim.bin`, `anchors.bin`, `*.mesh.bin`, PNG textures.
- `TJBake/Unit` shader on URP Lit with forward, shadow caster, depth only and depth normals passes, per-instance state
  for Entities Graphics and a property block path for MeshRenderers.
- `TJBakeVisualLoader` with bounds-checked readers and one-message refusals.
- `TJBakePreviewPlayer` for edit mode and play mode, with a flat-colour mode for locked or undiscovered cards.
- Edit Mode tests: binary round trips, refusals, a two-bone rig with a known answer, playback rules.
