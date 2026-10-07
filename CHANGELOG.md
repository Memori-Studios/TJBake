# Changelog

## [0.2.0] - 2026-10-07

- Several variants in one bake (`TJBakeRequest.ExtraVariants`): later rigs bind to the first rig's skeleton by bone
  name, and the report prints how far each variant's bones sit from the first. A bone only a later variant uses is
  taken from the first rig if it has one.
- Posing through the Animator (`TJBakeSlotRequest.StateName` and `Parameters`): every layer and blend tree bakes the
  way it plays. Root motion is dropped on both paths, so travelling clips animate in place.
- Rigs with no skinned mesh (`PlaceholderWhenNoMesh`) bake a one-millimetre triangle, for invisible clock rigs.
- Host textures: a manifest may name a texture `asset:<name>` that the host loads itself (`TextureReference` when
  baking, `resolveTexture` when loading). Only a host that passes a resolver accepts such names.
- Repeated `bow`, `sword` and `shield` attachments are allowed; `saddle` stays unique.
- Return points come only from clip events named for `ReturnToIdle`; hit and sound events are ignored.
- Prop meshes that are switched off (inactive GameObject or disabled renderer) are skipped, so a prop prefab can pick
  its look by hiding children.
- Materials carry `emissionColor` (HDR), baked from `_EmissionColor` and applied at load; older manifests glow white.
- Attachments carry an optional `tag` (`TJBakeAttachmentRequest.Tag`, `TJBakeAttachment.Tag`) for the host game.
- `TJBakeRequest.RootOffset` bakes a position into the root, after `RootScale`, so a rider can sit above its saddle.
- Loaded textures, meshes and materials are flagged so an unload sweep cannot free them before their owner does.

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
