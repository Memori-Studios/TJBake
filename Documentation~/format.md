# TJBake file format, version 1

The files a TJBake bake writes and a host reads. One folder per baked character. Nothing in it depends on Unity
serialization, so a reader in any language can parse it. Examples use Tabletop Tavern, the first host; its slot table
is the default the baker offers. A host decides which slots and roles it needs; Tabletop Tavern's rules are in its
modding guide.

## 1. Folder layout

```text
Mods/<ModName>/unit_visuals/<UnitName>/
  unit.json              manifest, required
  anim.bin               bone matrices per frame, required
  anchors.bin            anchor matrices per frame, required when any attachment exists
  body_<n>.mesh.bin      one per body mesh (per variant, per LOD, per material slot)
  attach_<n>.mesh.bin    one per attachment mesh
  <texture>.png          every texture the materials name
  icon.png               optional, the unit's card icon (256x256 RGBA)
  rider/                 optional, the same layout again with kind "rider"
```

`<UnitName>` is the enum member name (`HelmwallDefenders`), matched exactly. A folder for a name the build does
not know is skipped with one warning, like every other override file. `_` prefixed folders are ignored.

## 2. unit.json

Plain UTF-8 JSON. Unknown fields are ignored so a newer baker can add fields an older game skips. Every file name is
relative to the folder, no subfolders except `rider/`.

```json
{
  "format": 1,
  "kind": "unit",
  "unitName": "HelmwallDefenders",
  "baker": "TJBake 1.0.0",
  "skeleton": { "boneCount": 49, "names": ["Hips", "Spine", "..."] },
  "frameRate": 30,
  "slots": [
    { "id": 0,  "name": "idle",           "start": 0,   "count": 73, "loop": true,  "returnToIdle": false },
    { "id": 4,  "name": "meleeAttack",    "start": 211, "count": 43, "loop": false, "returnToIdle": true, "returnAt": 0.9 },
    { "id": 8,  "name": "death1",         "start": 402, "count": 58, "loop": false, "returnToIdle": false }
  ],
  "anchors": ["Head", "LeftShoulder", "RightShoulder", "LeftHand", "RightHand"],
  "materials": [
    { "name": "Body", "baseColor": "body_albedo.png", "normal": null, "emission": null,
      "tint": [1, 1, 1, 1], "smoothness": 0.2, "transparent": false }
  ],
  "lodSwitch": [0.10, 0.04],
  "variants": [
    {
      "lods": [
        { "meshes": [ { "file": "body_0.mesh.bin", "material": 0 } ] },
        { "meshes": [ { "file": "body_1.mesh.bin", "material": 0 } ] }
      ],
      "attachments": [
        { "file": "attach_0.mesh.bin", "material": 0, "anchor": "Head",     "role": "prop",   "lods": [0, 1] },
        { "file": "attach_1.mesh.bin", "material": 0, "anchor": "LeftHand", "role": "shield", "lods": [0, 1] }
      ]
    }
  ],
  "rider": "rider/"
}
```

| Field | Type | Rules |
| --- | --- | --- |
| `format` | int | 1. A reader refuses a higher major it does not know. |
| `kind` | `"unit"` or `"rider"` | A rider has slots 0 to 2, no `lodSwitch`, one LOD, no `rider`. |
| `unitName` | string | Must equal the folder name. Absent on a rider. |
| `baker` | string | Free text, logged. |
| `skeleton.boneCount` | int | 1 to 5461. Equals the matrix row width in `anim.bin`. |
| `skeleton.names` | string[] | Optional, `boneCount` long, for tooling. |
| `frameRate` | int | 24, 30 or 60. All slots share it. |
| `slots[]` | array | One entry per slot id the visual supplies, ids unique and contiguous from 0. A unit needs 0 to 12, or 0 to 14 when it shoots or casts. |
| `slots[].start`, `count` | int | Frame range in `anim.bin`, `count` at least 2, ranges may overlap (the same clip in two slots). |
| `slots[].loop`, `returnToIdle` | bool | See the contract, section 3. `loop` and `returnToIdle` are never both true. |
| `slots[].returnAt` | float | 0.5 to 1.0, default 0.9, only read when `returnToIdle` is true. |
| `anchors[]` | string[] | Names, unique, in `anchors.bin` order. May be empty. |
| `materials[]` | array | At least one. Texture file names must exist in the folder. `tint` is linear RGBA. |
| `lodSwitch[]` | float[] | 0 to 2 entries, descending, each in (0, 1): the screen-height fraction below which the next LOD shows. Length equals LOD count minus 1 on every variant. |
| `variants[]` | array | 1 to 3. Every variant has the same LOD count. |
| `variants[].lods[].meshes[]` | array | At least one mesh per LOD. `material` indexes `materials`. |
| `variants[].attachments[]` | array | `anchor` must be in `anchors`, `role` is one of `prop`, `bow`, `sword`, `shield`, `saddle`, `lods` lists the LOD indices that show it (default `[0, 1]`). |
| `rider` | string | Optional, always `"rider/"`. |

Role counts the loader checks against the unit's `UnitType` and `UnitSize`: ranged needs one `bow`, hybrid one `bow`
and one `sword`, cavalry with a rider one `saddle`. More than one of a role fails the file.

## 3. anim.bin

Little-endian. One row per frame, every bone's skinning matrix as 3 rows of 4 half floats (the fourth matrix row is
0 0 0 1 and is not stored). The skinning matrix is `boneWorldToModel * bindPose`, in the unit's model space, so the
shader applies it straight to bind-pose vertices.

```text
offset  size  field
0       4     magic "TJBA"
4       4     u32 version = 1
8       4     u32 boneCount
12      4     u32 frameCount
16      4     u32 reserved = 0 (padding to 16-byte alignment of the data)
20      12    reserved, zero
32      ...   frameCount * boneCount * 24 bytes
              frame f, bone b at 32 + (f * boneCount + b) * 24:
              half m00 m01 m02 m03  m10 m11 m12 m13  m20 m21 m22 m23
```

The runtime uploads it as an RGBAHalf texture of width `boneCount * 3`, height `frameCount`, no mips, not readable,
point sampled; frame interpolation is done by the shader between two rows. Limits: `boneCount * 3 <= 16384`,
`frameCount <= 16384`, file size equals `32 + frameCount * boneCount * 24` or the file is refused.

## 4. anchors.bin

```text
offset  size  field
0       4     magic "TJBK"
4       4     u32 version = 1
8       4     u32 anchorCount
12      4     u32 frameCount  (equals anim.bin frameCount)
16      16    reserved, zero
32      ...   frameCount * anchorCount * 48 bytes
              frame f, anchor a at 32 + (f * anchorCount + a) * 48:
              float m00 m01 m02 m03  m10 m11 m12 m13  m20 m21 m22 m23   (model-space anchor transform, rotation+translation)
```

Full float: props are placed by this matrix and a half-float wobble is visible on a spear tip. The attachment follow
job reads two rows and blends them the same way the shader blends bone rows, so a prop never lags the hand.

## 5. *.mesh.bin

```text
offset  size  field
0       4     magic "TJBM"
4       4     u32 version = 1
8       4     u32 vertexCount
12      4     u32 indexCount           (multiple of 3; triangles only)
16      4     u32 flags                bit0 hasNormals, bit1 hasTangents, bit2 hasUV0, bit3 skinned, bit4 indices32
20      24    float boundsMin xyz, boundsMax xyz   (animation-inclusive for a body mesh, bind pose for an attachment)
44      4     u32 submeshCount
48      ...   submeshCount * (u32 indexStart, u32 indexCount)
...     ...   streams, each 16-byte aligned, in this order and only when its flag is set:
              positions   float3 * vertexCount
              normals     float3 * vertexCount
              tangents    float4 * vertexCount        (w is the handedness)
              uv0         float2 * vertexCount
              boneIndices u16x4 * vertexCount         (skinned only; index < boneCount)
              boneWeights float4 * vertexCount        (skinned only; sum 1 within 1/255, descending)
              indices     u16 or u32 * indexCount     (u32 when bit4)
```

Rules: an attachment mesh is never skinned; a body mesh always is. A submesh is a material split only when a single
`materials` entry cannot express it; the baker prefers one submesh per file. Vertex limit 1,000,000 per mesh.

## 6. Textures and icon

PNG, 8-bit RGB or RGBA, power of two not required, at most 4096 on a side. Base colour is sRGB, normal maps are
tangent-space OpenGL convention linear, emission is sRGB. `icon.png` is 256x256 RGBA; a host may use it as
the character's card icon.

## 7. Validation and failure behaviour

Validation happens twice: in TJBake before writing (every rule above plus the contract's clip rules), and in the game
at load. The game's reader is defensive:

- Every count, offset and index is bounds-checked against the file length before any allocation.
- A refused file produces exactly one `LogError` naming the folder, file and rule, and the unit keeps its built-in
  visual. The reader never throws past the host's load code.
- A manifest that references a missing file is refused as a whole; a visual is never half-built.
- Limits: `anim.bin` at most 256 MB, a mesh file at most 64 MB, a folder at most 512 MB. Beyond that the folder is
  refused with a size message. Steam's own Workshop item limit applies on top.
- Allocation happens once per battle per overridden unit, at battle load, and everything built is destroyed in
  the host's cleanup.
