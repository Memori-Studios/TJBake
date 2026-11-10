#ifndef TJBAKE_INPUT_INCLUDED
#define TJBAKE_INPUT_INCLUDED

// Material inputs for the TJBake unit shader. Replaces URP's LitInput so the animation state can live in the one
// metadata block Unity feeds per instance (MaterialPropertyMetadata); the pass bodies are URP's own.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

// URP 17.6's LitForwardPass includes LitInput itself; this file replaces it, so LitInput's guard keeps it out.
#define UNIVERSAL_LIT_INPUT_INCLUDED
// URP 17.6's SurfaceType.hlsl would redeclare _Surface; its guard keeps it out and the overload below stands in.
#define UNIVERSAL_SURFACE_TYPE_TRANSPARENT_INCLUDED

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _EmissionColor;
    half _Cutoff;
    half _Smoothness;
    half _Metallic;
    half _BumpScale;
    half _Surface;
    // (rowA, rowB, lerp, enabled) and (rowA, rowB, lerp, weight of the previous clip). Zero means bind pose.
    float4 _GpuAnimCur;
    float4 _GpuAnimPrev;
    half4 _BloodColor;
    half _BloodTiling;
    half _BloodGloss;
    // (blood 0..1, seed u, seed v, stone 0..1), per instance.
    float4 _UnitBlood;
    // 0..1, per instance: how far toward flat white a struck unit is drawn.
    float _UnitFlash;
    // (r, g, b, strength 0..1), per instance, linear: the colour a unit is cast toward. Strength 0 leaves it as it is.
    float4 _UnitTint;
    // (r, g, b, width), per instance, linear: the outline's colour, and its width in thousandths of the distance from
    // the camera. Width 0 draws no outline. Width below 0 marks a still body, a corpse: no outline, rim or floor.
    float4 _UnitOutline;
CBUFFER_END

TEXTURE2D(_BloodMask);
SAMPLER(sampler_BloodMask);
// Global, set by the host's Blood setting: 1 hides every stain. Unset reads 0, so a host that never sets it keeps blood.
float _UnitBloodOff;

// Globals set by the host's look so units read against the ground. Unset they read 0: no rim, no floor, real shadows.
// (r, g, b, power), linear: the rim's colour, and how tight it hugs the silhouette.
float4 _UnitRimColor;
// (strength near, strength far, near metres, far metres): the rim grows from the near strength to the far one.
float4 _UnitRimShape;
// A soft least luminance, linear and before post, for a unit's lit colour. It keeps the unit's own hue.
float _UnitBodyFloor;
// 1 casts no shadow, for a host that draws its own blob under each unit.
float _UnitShadowOff;
// Extra saturation for a unit's own colours: 0 leaves them, 1 doubles their distance from grey.
float _UnitSaturation;

#ifdef UNITY_DOTS_INSTANCING_ENABLED
    UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
        UNITY_DOTS_INSTANCED_PROP(float4, _GpuAnimCur)
        UNITY_DOTS_INSTANCED_PROP(float4, _GpuAnimPrev)
        UNITY_DOTS_INSTANCED_PROP(float4, _UnitBlood)
        UNITY_DOTS_INSTANCED_PROP(float, _UnitFlash)
        UNITY_DOTS_INSTANCED_PROP(float4, _UnitTint)
        UNITY_DOTS_INSTANCED_PROP(float4, _UnitOutline)
    UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
    #define TJBAKE_CUR   UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GpuAnimCur)
    #define TJBAKE_PREV  UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GpuAnimPrev)
    #define TJBAKE_BLOOD UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _UnitBlood)
    #define TJBAKE_FLASH UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _UnitFlash)
    #define TJBAKE_TINT  UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _UnitTint)
    #define TJBAKE_OUTLINE UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _UnitOutline)
#else
    #define TJBAKE_CUR   _GpuAnimCur
    #define TJBAKE_PREV  _GpuAnimPrev
    #define TJBAKE_BLOOD _UnitBlood
    #define TJBAKE_FLASH _UnitFlash
    #define TJBAKE_TINT  _UnitTint
    #define TJBAKE_OUTLINE _UnitOutline
#endif

// URP 17.6's forward pass asks without an argument; 17.3's passed _Surface, so both read the material's surface type.
inline bool IsSurfaceTypeTransparent()
{
    return IsSurfaceTypeTransparent(_Surface);
}

inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
#if defined(_ALPHAPREMULTIPLY_ON)
    outSurfaceData.albedo *= outSurfaceData.alpha;
#endif
    outSurfaceData.metallic = _Metallic;
    outSurfaceData.specular = half3(0.0h, 0.0h, 0.0h);
    outSurfaceData.smoothness = _Smoothness;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
    outSurfaceData.occlusion = 1.0h;
    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));
    outSurfaceData.clearCoatMask = 0.0h;
    outSurfaceData.clearCoatSmoothness = 0.0h;
}

// Stains the surface with this instance's blood. The mask is projected in bind-pose model space, not UV space: unit
// textures are palette atlases where a whole colour region shares one UV spot, so a UV mask would stain regions whole.
void TJBakeApplyBlood(float3 bindPosOS, float3 bindNormalOS, inout SurfaceData surfaceData)
{
    float4 blood = TJBAKE_BLOOD;
    UNITY_BRANCH
    if (blood.x > 0.0 && _BloodColor.a > 0.0 && _UnitBloodOff < 0.5)
    {
        float3 p = bindPosOS * _BloodTiling + blood.yzy;
        // Triplanar, sharpened so one projection owns each face and the three samples do not average the peaks away.
        float3 w = pow(abs(bindNormalOS) + 1e-4, 4.0);
        w /= w.x + w.y + w.z;
        half mask = SAMPLE_TEXTURE2D(_BloodMask, sampler_BloodMask, p.zy).r * w.x
                  + SAMPLE_TEXTURE2D(_BloodMask, sampler_BloodMask, p.xz).r * w.y
                  + SAMPLE_TEXTURE2D(_BloodMask, sampler_BloodMask, p.xy).r * w.z;
        // The stain grows out of the mask's brightest spots as the amount rises. The threshold runs from 0.55 (first
        // specks) down to 0.15 (about half the body); the mask's grain stays below 0.13 so a full unit is never solid.
        half threshold = 0.55 - 0.4 * blood.x;
        half stain = smoothstep(threshold, threshold + 0.1, mask) * _BloodColor.a;
        surfaceData.albedo = lerp(surfaceData.albedo, _BloodColor.rgb, stain);
        surfaceData.smoothness = lerp(surfaceData.smoothness, _BloodGloss, stain);
    }
}

// Turns this instance to grey stone by its stone amount: the colour goes, the albedo's light and dark stay as carving.
void TJBakeApplyStone(inout SurfaceData surfaceData)
{
    half stone = TJBAKE_BLOOD.w;
    UNITY_BRANCH
    if (stone > 0.0)
    {
        half grey = dot(surfaceData.albedo, half3(0.299, 0.587, 0.114));
        half3 rock = (0.2 + 0.7 * grey) * half3(1.0, 0.97, 0.91);
        surfaceData.albedo = lerp(surfaceData.albedo, rock, stone);
        surfaceData.smoothness = lerp(surfaceData.smoothness, 0.1, stone);
        surfaceData.metallic = lerp(surfaceData.metallic, 0.0, stone);
    }
}

// Casts this instance toward its tint colour by the tint's strength. Only the albedo changes, so light and shade stay.
void TJBakeApplyTint(inout SurfaceData surfaceData)
{
    half4 tint = TJBAKE_TINT;
    UNITY_BRANCH
    if (tint.a > 0.0)
    {
        surfaceData.albedo = lerp(surfaceData.albedo, tint.rgb, saturate(tint.a));
    }
}

// Washes this instance toward white by its flash amount. The white is emitted, so it reads in shade and at night too.
void TJBakeApplyFlash(inout SurfaceData surfaceData)
{
    half flash = TJBAKE_FLASH;
    UNITY_BRANCH
    if (flash > 0.0)
    {
        surfaceData.albedo = lerp(surfaceData.albedo, half3(1.0, 1.0, 1.0), flash);
        surfaceData.emission = lerp(surfaceData.emission, half3(1.0, 1.0, 1.0), flash);
    }
}

// Pushes a living unit's own colours away from grey, before blood, tint and flash, so those keep their colours.
void TJBakeApplySaturation(inout SurfaceData surfaceData)
{
    UNITY_BRANCH
    if (_UnitSaturation <= 0.0 || TJBAKE_OUTLINE.w < 0.0) return;
    half grey = dot(surfaceData.albedo, half3(0.2126729h, 0.7151522h, 0.0721750h));
    surfaceData.albedo = max(lerp(grey.xxx, surfaceData.albedo, 1.0h + (half)_UnitSaturation), 0.0h);
}

// Lifts a dark body toward the look's floor in its own hue, then adds a rim that grows with distance. The rim takes the unit's
// tint as the tint takes the body, and fades as the hit flash rises, so a wind-up, a status and a hit still read first.
void TJBakeApplyReadability(float3 positionWS, half3 normalWS, half3 viewDirectionWS, half3 albedo, inout half3 color)
{
    UNITY_BRANCH
    if (TJBAKE_OUTLINE.w < 0.0) return;
    // Rec. 709 luminance weights, as Core RP's Luminance, which not every pass includes.
    const half3 weights = half3(0.2126729h, 0.7151522h, 0.0721750h);
    half3 hue = albedo / max(dot(albedo, weights), 0.01h);
    // A soft floor: far above it nothing changes, at black it gives the floor, and shading between keeps its steps.
    half luminance = max(dot(color, weights), 0.0h);
    half floorLuminance = (half)_UnitBodyFloor;
    color += hue * (sqrt(luminance * luminance + floorLuminance * floorLuminance) - luminance);

    float4 shape = _UnitRimShape;
    UNITY_BRANCH
    if (shape.x > 0.0 || shape.y > 0.0)
    {
        float distance = length(GetCameraPositionWS() - positionWS);
        half strength = lerp(shape.x, shape.y, saturate((distance - shape.z) / max(shape.w - shape.z, 0.001)));
        half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirectionWS)), max((half)_UnitRimColor.w, 0.5h));
        half4 tint = TJBAKE_TINT;
        half3 rim = lerp((half3)_UnitRimColor.rgb, (half3)tint.rgb, saturate((half)tint.a));
        color += rim * (fresnel * strength * (1.0h - saturate((half)TJBAKE_FLASH)));
    }
}

#endif
