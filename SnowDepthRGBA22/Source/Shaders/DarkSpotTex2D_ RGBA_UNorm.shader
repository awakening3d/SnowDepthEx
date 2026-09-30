#include "./Flax/Common.hlsl"

RWTexture2D<float4> Target : register(u0);   // R8G8B8A8_UNorm

META_CB_BEGIN(0, Params)
	float2 PixelCenter;   // pixel center aligned to +0.5 (in big rt4096)
	float  PixelRadius;   // pixel radius (in big rt4096)
	float  DepthOffset;   // modify depth value
	int2   StartOffset;   // bounding box top-left corner in physical texel space
META_CB_END

META_CS(true, FEATURE_LEVEL_SM5)

[numthreads(8,8,1)]
void CS(uint3 threadId : SV_DispatchThreadID)
{
	uint2 texCoord = threadId.xy + (uint2)StartOffset;

	uint2 texSize;
	Target.GetDimensions(texSize.x, texSize.y);

	// 边界保护，跳过最外围一圈像素，永远不写入
	bool isBorderPixel = (texCoord.x == 0) || (texCoord.x == texSize.x - 1) ||
	                     (texCoord.y == 0) || (texCoord.y == texSize.y - 1);
	if (isBorderPixel)
		return;

	// 越界保护，保证下标不越界
	if (texCoord.x >= texSize.x || texCoord.y >= texSize.y)
		return;

	// 物理 texel -> 逻辑大图 2x2 的 4 个像素中心
	// r 左上, g 右上, b 左下, a 右下
	float2 bigR = float2(2 * texCoord.x,     2 * texCoord.y    ) + 0.5;
	float2 bigG = float2(2 * texCoord.x + 1, 2 * texCoord.y    ) + 0.5;
	float2 bigB = float2(2 * texCoord.x,     2 * texCoord.y + 1) + 0.5;
	float2 bigA = float2(2 * texCoord.x + 1, 2 * texCoord.y + 1) + 0.5;

	// 逐通道 falloff（逻辑大图空间）
	float falloffR = 1.0f - smoothstep(0, PixelRadius, length(bigR - PixelCenter));
	float falloffG = 1.0f - smoothstep(0, PixelRadius, length(bigG - PixelCenter));
	float falloffB = 1.0f - smoothstep(0, PixelRadius, length(bigB - PixelCenter));
	float falloffA = 1.0f - smoothstep(0, PixelRadius, length(bigA - PixelCenter));

	// 4 个通道 falloff 都足够小，本次修改几乎无贡献，直接退出，跳过纹理读写
	if (falloffR < 0.001f && falloffG < 0.001f &&
	    falloffB < 0.001f && falloffA < 0.001f)
		return;



    float4 oldVal = Target[texCoord];
    float4 falloff = float4(falloffR, falloffG, falloffB, falloffA);

    // 逐通道更新，向量化
    float4 newVal = saturate(oldVal + DepthOffset * falloff * oldVal);

	Target[texCoord] = newVal;
}