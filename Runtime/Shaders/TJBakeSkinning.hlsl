#ifndef TJBAKE_SKINNING_INCLUDED
#define TJBAKE_SKINNING_INCLUDED

// GPU skinning from a bone matrix texture: 3 texels per bone per row (one row per frame), RGBAHalf, point sampled.
// The per-instance state macros TJBAKE_CUR and TJBAKE_PREV come from TJBakeInput.hlsl.
// Bone indices ride in TEXCOORD3, weights in TEXCOORD4, four influences.

TEXTURE2D(_BoneTex);

float3x4 TJBakeLoadBone(int bone, int row)
{
    int x = bone * 3;
    float4 r0 = LOAD_TEXTURE2D(_BoneTex, int2(x, row));
    float4 r1 = LOAD_TEXTURE2D(_BoneTex, int2(x + 1, row));
    float4 r2 = LOAD_TEXTURE2D(_BoneTex, int2(x + 2, row));
    return float3x4(r0, r1, r2);
}

float3x4 TJBakeSkinMatrix(float4 boneIndices, float4 boneWeights, float4 state)
{
    float3x4 m = (float3x4)0;
    int rowA = (int)state.x;
    int rowB = (int)state.y;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float w = boneWeights[i];
        if (w > 0.0)
        {
            int bone = (int)(boneIndices[i] + 0.5);
            float3x4 a = TJBakeLoadBone(bone, rowA);
            float3x4 b = TJBakeLoadBone(bone, rowB);
            m += w * lerp(a, b, state.z);
        }
    }
    return m;
}

// Moves a bind-pose vertex into the pose of this frame. A disabled state leaves the bind pose untouched.
void TJBakeSkin(float4 boneIndices, float4 boneWeights, inout float3 positionOS, inout float3 normalOS, inout float3 tangentOS)
{
    float4 cur = TJBAKE_CUR;
    if (cur.w <= 0.0) return;
    float3x4 m = TJBakeSkinMatrix(boneIndices, boneWeights, cur);
    float4 prev = TJBAKE_PREV;
    if (prev.w > 0.001)
    {
        float3x4 p = TJBakeSkinMatrix(boneIndices, boneWeights, prev);
        m = lerp(m, p, saturate(prev.w));
    }
    positionOS = mul(m, float4(positionOS, 1.0));
    float3x3 rot = (float3x3)m;
    normalOS = normalize(mul(rot, normalOS));
    tangentOS = normalize(mul(rot, tangentOS));
}

#endif
