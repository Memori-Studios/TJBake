#ifndef TJBAKE_INPUT_INCLUDED
#define TJBAKE_INPUT_INCLUDED

// Material inputs for the TJBake unit shader. Replaces URP's LitInput so the animation state can live in the one
// metadata block Unity feeds per instance (MaterialPropertyMetadata); the pass bodies are URP's own.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

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
CBUFFER_END

#ifdef UNITY_DOTS_INSTANCING_ENABLED
    UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
        UNITY_DOTS_INSTANCED_PROP(float4, _GpuAnimCur)
        UNITY_DOTS_INSTANCED_PROP(float4, _GpuAnimPrev)
    UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
    #define TJBAKE_CUR  UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GpuAnimCur)
    #define TJBAKE_PREV UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _GpuAnimPrev)
#else
    #define TJBAKE_CUR  _GpuAnimCur
    #define TJBAKE_PREV _GpuAnimPrev
#endif

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

#endif
