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
    // (amount 0..1, seed u, seed v, unused), per instance.
    float4 _UnitBlood;
CBUFFER_END

TEXTURE2D(_BloodMask);
SAMPLER(sampler_BloodMask);
// Global, set by the host's Blood setting: 1 hides every stain. Unset reads 0, so a host that never sets it keeps blood.
float _UnitBloodOff;

#ifdef UNITY_DOTS_INSTANCING_ENABLED
    UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
        UNITY_DOTS_INSTANCED_PROP(float4, _GpuAnimCur)
        UNITY_DOTS_INSTANCED_PROP(float4, _GpuAnimPrev)
        UNITY_DOTS_INSTANCED_PROP(float4, _UnitBlood)
    UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
    #define TJBAKE_CUR   UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GpuAnimCur)
    #define TJBAKE_PREV  UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GpuAnimPrev)
    #define TJBAKE_BLOOD UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _UnitBlood)
#else
    #define TJBAKE_CUR   _GpuAnimCur
    #define TJBAKE_PREV  _GpuAnimPrev
    #define TJBAKE_BLOOD _UnitBlood
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

#endif
