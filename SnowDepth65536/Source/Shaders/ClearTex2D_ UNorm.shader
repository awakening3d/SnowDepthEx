#include "./Flax/Common.hlsl"
RWTexture2D<float> Target : register(u0);

META_CB_BEGIN(0, Params)
float FillValue;
META_CB_END
META_CS(true, FEATURE_LEVEL_SM5)

[numthreads(8,8,1)]
void CS(uint3 threadId : SV_DispatchThreadID)
{
    uint2 texCoord = threadId.xy;
    Target[texCoord] =FillValue;
}
