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
    // ===== 单像素写入:只让线程组内(0,0) 线程执行 =====
    if (threadId.x != 0 || threadId.y != 0)
        return;

    //uint2 texCoord = StartOffset;
	uint2 texCoord = StartOffset + uint2(1, 1);
    uint2 texSize;
    Target.GetDimensions(texSize.x, texSize.y);

    // ===== 边界保护:跳过最外围一圈像素,永远不写入 =====
    bool isBorderPixel = (texCoord.x == 0) || (texCoord.x == texSize.x - 1) ||
                         (texCoord.y == 0) || (texCoord.y == texSize.y - 1);
    if (isBorderPixel)
        return;

    if (texCoord.x >= texSize.x || texCoord.y >= texSize.y)
        return;

    float oldVal = Target[texCoord];
    float newVal = oldVal + DepthOffset * oldVal; // oldVal越小(坑越深),改动影响越小
    Target[texCoord] = saturate(newVal);
}