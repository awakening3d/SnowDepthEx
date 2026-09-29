#include "./Flax/Common.hlsl"
RWTexture2D<float> SnowDepthRT : register(u0);

META_CB_BEGIN(0, RecoverParams)
float TargetSnowValue;
float RecoverFactor;
META_CB_END

META_CS(true, FEATURE_LEVEL_SM5)
[numthreads(8,8,1)]
void DepthRecoverCS(uint3 threadId : SV_DispatchThreadID)
{
    uint2 texCoord = threadId.xy;
    uint2 texSize;
    SnowDepthRT.GetDimensions(texSize.x, texSize.y);
	
	// 第一步：先剔除超出纹理范围的多余线程，防止RW越界UB
    if(texCoord.x >= texSize.x || texCoord.y >= texSize.y)
        return;	
	
	// ===== 边界保护：跳过最外围一圈像素，永远不写入 =====
	bool isBorderPixel = (texCoord.x == 0) || (texCoord.x == texSize.x - 1) ||
						 (texCoord.y == 0) || (texCoord.y == texSize.y - 1);
	if(isBorderPixel)
		return;	
	

    float currentDepth = SnowDepthRT[texCoord];
    // 向原始积雪值插值，缓慢恢复
    float result = lerp(currentDepth, TargetSnowValue, RecoverFactor);
    SnowDepthRT[texCoord] = result;
}
