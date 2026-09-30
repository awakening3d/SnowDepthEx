#include "./Flax/Common.hlsl"

RWTexture2D<float> Target : register(u0);


META_CB_BEGIN(0, Params)
	float2 PixelCenter;   // pixel center aligned to +0.5
	float  PixelRadius;   // pixel radius, min of X/Y direction
	float  DepthOffset;
	int2   StartOffset;   // bounding box top-left corner in pixels
META_CB_END


META_CS(true, FEATURE_LEVEL_SM5)

[numthreads(8,8,1)]
void CS(uint3 threadId : SV_DispatchThreadID)
{
    // ===== 3x3:只让 (0,0)~(2,2) 的线程执行 =====
    if (threadId.x > 2 || threadId.y > 2)
        return;

    uint2 texCoord = threadId.xy + StartOffset;
    uint2 texSize;
    Target.GetDimensions(texSize.x, texSize.y);

    // ===== 边界保护:跳过最外围一圈像素 =====
    bool isBorderPixel = (texCoord.x == 0) || (texCoord.x == texSize.x - 1) ||
                         (texCoord.y == 0) || (texCoord.y == texSize.y - 1);
    if (isBorderPixel)
        return;

    if (texCoord.x >= texSize.x || texCoord.y >= texSize.y)
        return;

    // ===== 3x3 中心为 (1,1), 用距离做柔化 =====
    float2 d = float2(threadId.xy) - float2(1.0, 1.0);
    float dist = length(d);          // 0 ~ sqrt(2)
	float softness = 1.5f;   // 柔和度 1.2 ~ 2.0
    float falloff = 1.0f - smoothstep(0.0, softness, dist);

    if (falloff < 0.001f)
        return;

    float oldVal = Target[texCoord];
    float newVal = oldVal + DepthOffset * falloff * oldVal;
    Target[texCoord] = saturate(newVal);
}