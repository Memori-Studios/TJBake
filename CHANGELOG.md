# Changelog

## [0.2.7] - 2026-11-10

- `TJBake/Unit` can cast a unit toward a colour. `_UnitTint` (per instance; linear rgb, and a strength from 0 to 1 in
  alpha) blends the albedo toward the colour, after the blood and the stone and before the hit flash, so a struck unit
  still flashes white over its tint. Light and shade stay, since only the albedo changes. Unset reads 0, so a host that
  never writes it is unchanged. Only the forward pass reads it; the shadow and depth passes are the same as before.

## [0.2.6] - 2026-11-09

- `TJBake/Unit` can flash a unit white when it is struck. `_UnitFlash` (per instance, 0 to 1) washes the albedo and the
  emission toward white, so the flash reads in shade too. Unset reads 0, so a host that never writes it is unchanged.

## [0.2.5] - 2026-11-03

- `TJBakeRagdoll.Record` (Editor) records a ragdoll fall as an `AnimationClip`: the rig's torso is one rigid body of
  joints and each limb a stick swinging from its joint, solved position-based with a floor, friction and a swing limit,
  from a push that fades down the torso. The same rig, options and seed record the same clip on any machine, so a host
  can bake death variations without authoring them, and hand them to the baker as slots like any other clip.
- The sampler restores the rig's rest pose before every slot. A clip that leaves a bone unkeyed now shows it at rest,
  not where the previous slot's last frame left it. Re-bake a unit whose clips key only some bones to pick this up.

## [0.2.4] - 2026-10-29

- `TJBake/Unit` can turn a unit to grey stone. `_UnitBlood.w` (per instance, 0 to 1; it was unused) fades the surface
  to a warm grey that keeps the albedo's light and dark, with low smoothness and no metal. The forward pass applies it
  after the blood stain. Nothing changes while it is 0.

## [0.2.3] - 2026-10-28

- A prop made of several meshes bakes as one attachment per material, not one per mesh. A host that hides or swaps
  a prop by its role (a three-piece rifle hidden when the unit draws its sword) now reaches the whole prop. Parts that
  use different materials, or carry different vertex streams, still bake as separate attachments. Re-bake a unit to
  pick this up; the file format is unchanged.

## [0.2.2] - 2026-10-20

- `TJBake/Unit` can stain a unit with blood. `_UnitBlood` (per instance: amount 0 to 1, a seed in yz) drives a tiling
  mask (`_BloodMask`, `_BloodTiling`) projected triplanar in bind-pose model space, so it works on palette-atlas UVs
  and sits still through every pose; `_BloodColor` (alpha 0 = never) and `_BloodGloss` are per material. The forward
  pass has its own fragment entry (`TJBakeForwardFragment`, URP's `LitPassFragment` body) and a nested
  `TJBakeVaryings` carrying the bind-pose position and normal. Nothing changes for a material that leaves the defaults.

## [0.2.1] - 2026-10-19

- `TJBake/Unit` compiles on URP 17.6 (Unity 6.6). URP 17.6's `LitForwardPass.hlsl` includes `LitInput.hlsl` and
  `SurfaceType.hlsl` itself and calls an argument-free `IsSurfaceTypeTransparent()`; `TJBakeInput.hlsl` now keeps both
  files out by their include guards and supplies that function from `_Surface`. On URP 17.3 the guards are never hit.
- Optional progress callback (`TJBakeRequest.Progress`, a `Func<string, float, bool>`): it gets the current step
  (reading meshes, sampling each slot, writing animation, writing meshes and textures, writing the manifest; a rider's
  steps start with `rider:`) and the fraction of the bake done, from 0 to 1. Return true to cancel; the bake then
  throws `OperationCanceledException` and may leave some files in the output folder, so bake into a temporary folder
  if a cancel must keep the old output. Left null, the bake behaves exactly as before.

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
