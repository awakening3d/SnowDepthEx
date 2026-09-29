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
    uint2 texCoord = threadId.xy + StartOffset;
    uint2 texSize;
    Target.GetDimensions(texSize.x, texSize.y);

    // 边界保护,跳过最外围一圈像素,永远不写入
    bool isBorderPixel = (texCoord.x == 0) || (texCoord.x == texSize.x - 1) ||
                         (texCoord.y == 0) || (texCoord.y == texSize.y - 1);
    if (isBorderPixel)
        return;

    if (texCoord.x >= texSize.x || texCoord.y >= texSize.y)
        return;

    // 像素空间距离计算,直接用像素坐标,避免 Aspect 乘两次的问题
    float2 pixelPos = float2(texCoord) + 0.5;
    float2 pixelDelta = pixelPos - PixelCenter;
    float pixelDist = length(pixelDelta);

    // falloff
    float falloff = 1.0f - smoothstep(0, PixelRadius, pixelDist);

    // 阈值,falloff 足够小,本次修改几乎无贡献,直接退出,跳过纹理读写
    if (falloff < 0.001f)
        return;

    float oldVal = Target[texCoord];
    float pressureFactor = saturate(oldVal);
    float newVal = oldVal + DepthOffset * falloff * pressureFactor;
    Target[texCoord] = saturate(newVal);
}