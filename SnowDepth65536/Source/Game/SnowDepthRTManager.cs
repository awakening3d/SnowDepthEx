using FlaxEngine;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class SnowDepthRTManager : Script
{
    // ============================================================
    // 调试开关
    // ============================================================
    [Header("Debug")]
    public bool EnableDebugLog = false; // 是否输出调试日志

    // ============================================================
    // RT 设置
    // ============================================================
	// 注意：RTResolution 和 RTWorldSize 的宽高比必须一致（同步），
	// 否则脚印形状会错。当前逻辑假设：
	//  - RTWorldSize.X / RTWorldSize.Y == RTResolution.X / RTResolution.Y
	//  - 脚印在世界空间是圆，映射到 adjusted UV 空间也是圆
    [Header("RT Settings")]	
    public Int2 RTResolution = new Int2(2048,2048); // 仅用于初始化 RT 尺寸，运行时以 _rtDesc.Width, _rtDesc.Height 为准
    public Vector2 RTWorldSize = new Vector2( 2048 * 8 , 2048 * 8); // 世界物理空间大小，单位厘米
    public Material TerrainSnowMaterial;
    public Material DebugViewMaterial;
    public Shader ClearTextureShader;
    public Shader FootprintDrawShader;    // 绘制脚印黑斑CS
    public Shader DepthRecoverShader;     // 积雪深度缓慢恢复CS

    private const float RTFillValue = 0.5f; // RT缓冲区基准填充值；0.5代表无脚印扰动原始雪面

    // ============================================================
    // 测试绘制
    // ============================================================
    [Header("Test Draw Spot")]
    public bool TestDrawSpot = false;

    [Header("Debug Foot UV(0~1)")]
    [Range(0f, 1f)]
    public float DebugUV_X = 0.5f;
    [Range(0f, 1f)]
    public float DebugUV_Y = 0.5f;

    // ============================================================
    // 深度恢复
    // ============================================================
    [Header("Depth Recover 深度缓慢恢复")]
    public bool EnableDepthRecover = true;
    [Range(0.001f, 0.2f)]
    public float RecoverSpeed = 0.001f; // 每秒恢复比例（近似），0.001 表示每秒恢复剩余差距的约 0.1%
	[Range(0.01f, 1f)]
    public float RecoverInterval = 0.1f; // CS刷新间隔，0.1 = 每秒10次

    // ============================================================
    // 玩家引用
    // ============================================================
    [Header("Player")]
    public Actor player;

    // ============================================================
    // 运行时状态
    // ============================================================
    public Vector2 WindowOrigin { get; private set; }
    public Vector2 WindowCenter { get; private set; }

    private float _recoverTimeAcc; // 恢复计时器累加器
    private GPUTexture _snowDepthRT;
    private GPUTextureDescription _rtDesc;
    private GPUShader _clearGpuShader;
    private GPUShader _footprintDrawGpuShader;
    private GPUShader _depthRecoverGpuShader;

    private bool _initialized;
    private bool _hasClearedRT;

    // ============================================================
    // 常量缓冲区结构体
    // ============================================================

    // ClearRT常量缓冲区数据结构
    [StructLayout(LayoutKind.Sequential)]
    private struct ClearData
    {
        public float FillValue;
    }

	
	// 脚印绘制CS常量缓冲区
	[StructLayout(LayoutKind.Sequential)]
	private struct FootprintCSData
	{
		public Vector2 PixelCenter;   // 像素中心,已对齐 +0.5
		public float PixelRadius;     // 像素半径,X/Y 方向取小值
		public float DepthOffset;
		public Int2 StartOffset;      // 包围盒左上角
	}	


    // 积雪恢复CS常量缓冲区
    [StructLayout(LayoutKind.Sequential)]
    private struct DepthRecoverCSData
    {
        public float TargetSnowValue;
        public float RecoverFactor;
    }

    // 脚印队列元素，CPU入队，PreRender阶段批量提交GPU
    private struct FootprintInfo
    {
        public Vector2 Pos;    // 世界坐标位置
        public float Radius;   // 脚印世界半径
        public float Pressure; // 压力值，决定凹陷深度
    }

    private Queue<FootprintInfo> _footprintQueue = new Queue<FootprintInfo>();

    // ============================================================
    // 日志封装
    // ============================================================
    private void LogDebug(string msg)
    {
        if (EnableDebugLog)
            Debug.LogWarning(msg);
    }

    // ============================================================
    // 生命周期
    // ============================================================
    public override void OnStart()
    {
		// 强制对齐到 8 的倍数，避免 Dispatch 漏像素
		if (RTResolution.X % 8 != 0)
		{
			int aligned = (RTResolution.X + 7) / 8 * 8;
			LogDebug($"[SnowRT] RTResolution.X {RTResolution.X} 不是 8 的倍数，已对齐到 {aligned}");
			RTResolution.X = aligned;
		}

		if (RTResolution.Y % 8 != 0)
		{
			int aligned = (RTResolution.Y + 7) / 8 * 8;
			LogDebug($"[SnowRT] RTResolution.Y {RTResolution.Y} 不是 8 的倍数，已对齐到 {aligned}");
			RTResolution.Y = aligned;
		}

	
        // Create Depth Texture
        _rtDesc = GPUTextureDescription.New2D(
            RTResolution.X,
            RTResolution.Y,
            PixelFormat.R16_UNorm, // R8_UNorm also works
            GPUTextureFlags.UnorderedAccess | GPUTextureFlags.ShaderResource  // | GPUTextureFlags.RenderTarget
        );
        _snowDepthRT = new GPUTexture();
        bool initrt = _snowDepthRT.Init(ref _rtDesc);
        LogDebug($"[SnowRT] Init snowDepthRt={initrt}");

        // 加载清空RT shader
        if (ClearTextureShader != null)
        {
            _clearGpuShader = ClearTextureShader.GPU;
            LogDebug($"[SnowRT] ClearShader loaded. GPUShader={_clearGpuShader}");
        }
        else
        {
            LogDebug("[SnowRT] ClearTextureShader 未赋值");
        }

        // 加载脚印绘制shader
        if (FootprintDrawShader != null)
        {
            _footprintDrawGpuShader = FootprintDrawShader.GPU;
            LogDebug($"[SnowRT] FootprintDrawShader loaded. GPUShader={_footprintDrawGpuShader}");
        }
        else
        {
            LogDebug("[SnowRT] FootprintDrawShader 未赋值，无法绘制黑斑");
        }

        // 加载独立深度恢复Shader
        if (DepthRecoverShader != null)
        {
            _depthRecoverGpuShader = DepthRecoverShader.GPU;
            LogDebug($"[SnowRT] DepthRecoverShader loaded. GPUShader={_depthRecoverGpuShader}");
        }
        else
        {
            LogDebug("[SnowRT] DepthRecoverShader 未赋值，深度恢复关闭");
        }

        _initialized = true;
        _hasClearedRT = false;
        _recoverTimeAcc = 0f;
        LogDebug($"[SnowRT] SnowDepth RT created: {_rtDesc.Width}x{_rtDesc.Height} R16_UNorm");
    }

    public override void OnEnable()
    {
        if (player) SetWindowCenter(player.Position.X, player.Position.Z);

		// 先移除再添加，避免 OnEnable 重复触发导致重复订阅。
		// 依赖 Flax 的 remove 实现：移除不存在的订阅是 no-op，不会破坏计数。
		MainRenderTask.Instance.PreRender -= OnPreRender;
		MainRenderTask.Instance.PreRender += OnPreRender;
        LogDebug("[SnowRT] Subscribed to PreRender");
    }

    public override void OnDisable()
    {
        MainRenderTask.Instance.PreRender -= OnPreRender;
        LogDebug("[SnowRT] Unsubscribed from PreRender");
    }

/*    public override void OnUpdate()
    {
        // 所有GPU调度放在PreRender，Update仅保留空实现
    }
*/
    public override void OnDestroy()
    {
        MainRenderTask.Instance.PreRender -= OnPreRender;
		
        // 释放GPU资源
        if (_snowDepthRT != null)
        {
            _snowDepthRT.ReleaseGPU();
            _snowDepthRT = null;
            LogDebug("[SnowRT] RT资源释放");
        }
    }

    // ============================================================
    // 窗口参数
    // ============================================================
    public void SetWindowCenter(float x, float y)
    {
        WindowCenter = new Vector2(x, y);
        LogDebug($"Snow WindowCenter:{WindowCenter.X},{WindowCenter.Y}");
        UpdateWindowOrigin();
        PushMaterialParams();
    }

    void UpdateWindowOrigin()
    {
        // 根据窗口中心和尺寸，计算窗口左下角世界原点
        WindowOrigin = WindowCenter - RTWorldSize * 0.5f;
    }

    void PushMaterialParams()
    {
        // 把RT和窗口参数推给地形材质和调试材质
        if (TerrainSnowMaterial != null && _snowDepthRT != null)
        {
            TerrainSnowMaterial.SetParameterValue("SnowDepthRT", _snowDepthRT);
            TerrainSnowMaterial.SetParameterValue("SnowRT_WindowOrigin", WindowOrigin);
            TerrainSnowMaterial.SetParameterValue("SnowRT_WorldSize", RTWorldSize);
        }
        if (DebugViewMaterial != null && _snowDepthRT != null)
        {
            DebugViewMaterial.SetParameterValue("DebugRT", _snowDepthRT);
            LogDebug("[SnowRT] set material param DebugRT");
        }
    }

    // ============================================================
    // Compute Shader 调度
    // ============================================================

    // CS清空整张RT纹理
    void FillTexture2D(GPUContext context, GPUShader gpuShader, GPUTextureView texView,
                       float fillValue, int texWidth, int texHeight)
    {
        IntPtr clearCS = gpuShader.GetCS("CS");
        IntPtr clearCB = gpuShader.GetCB(0);
        LogDebug($"[SnowRT Fill] clearCS={clearCS}, clearCB={clearCB}");
        if (clearCS == IntPtr.Zero || clearCB == IntPtr.Zero)
        {
            LogDebug("[SnowRT Fill] CS/CB 句柄为空");
            return;
        }

        ClearData data;
        data.FillValue = fillValue;
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(data));
        Marshal.StructureToPtr(data, ptr, false);
        context.UpdateCB(clearCB, ptr);
        Marshal.FreeHGlobal(ptr);

        context.BindUA(0, texView);
        context.BindCB(0, clearCB);

        int groupX = texWidth / 8;
        int groupY = texHeight / 8;
        LogDebug($"[SnowRT Fill] Dispatch groups: X={groupX}, Y={groupY}");
        context.Dispatch(clearCS, (uint)groupX, (uint)groupY, 1U);

        context.ResetUA();
        LogDebug("[SnowRT Fill] fill texture2d done");
    }

	// CS绘制圆形脚印黑斑,局部包围盒Dispatch,只在脚印区域执行计算
	void DrawFootprintCS(GPUContext context, Vector2 footWorldPos, float worldRadius, float pressure)
	{
		if (_footprintDrawGpuShader == null || _snowDepthRT == null)
		{
			LogDebug("[SnowRT Draw] Shader或RT为空");
			return;
		}

		// 世界坐标转换到RT内0~1 UV
		Vector2 localPos = footWorldPos - WindowOrigin;
		Vector2 uvCenter = localPos / RTWorldSize;

		LogDebug($"[SnowRT Draw] Foot UV Center:{uvCenter}, worldRadius:{worldRadius}, pressure:{pressure}");

		// 坐标超出RT窗口范围,直接跳过绘制
		if (uvCenter.X < 0 || uvCenter.Y < 0 || uvCenter.X > 1 || uvCenter.Y > 1)
		{
			LogDebug("[SnowRT Draw] UV超出0~1范围,跳过");
			return;
		}

		// UV转像素中心
		float pixelCenterX = uvCenter.X * _rtDesc.Width;
		float pixelCenterY = uvCenter.Y * _rtDesc.Height;

		// 每像素世界尺寸,分别算 X 和 Y 方向
		float worldPerPixelX = RTWorldSize.X / _rtDesc.Width;
		float worldPerPixelY = RTWorldSize.Y / _rtDesc.Height;

		// 像素半径,取 X/Y 方向的较小值,保证圆斑在 RT 里是正圆不变形
		float pixelRadiusX = worldRadius / worldPerPixelX;
		float pixelRadiusY = worldRadius / worldPerPixelY;
		float pixelRadius = Math.Min(pixelRadiusX, pixelRadiusY);

		// 包围盒
		int minX = (int)Math.Max(0, pixelCenterX - pixelRadius);
		int minY = (int)Math.Max(0, pixelCenterY - pixelRadius);
		int maxX = (int)Math.Min(_rtDesc.Width, pixelCenterX + pixelRadius);
		int maxY = (int)Math.Min(_rtDesc.Height, pixelCenterY + pixelRadius);

		// 保证包围盒至少 1 像素,解决时有时无问题
		if (maxX <= minX) maxX = minX + 1;
		if (maxY <= minY) maxY = minY + 1;

		// 对齐8x8线程组,计算Dispatch范围
		int groupMinX = minX / 8;
		int groupMinY = minY / 8;
		int groupMaxX = (maxX + 7) / 8;
		int groupMaxY = (maxY + 7) / 8;

		uint groupsX = (uint)(groupMaxX - groupMinX);
		uint groupsY = (uint)(groupMaxY - groupMinY);
		if (groupsX <= 0 || groupsY <= 0)
		{
			LogDebug("[SnowRT Draw] 线程组数量<=0,跳过");
			return;
		}
		LogDebug($"[SnowRT Draw] Dispatch groupsX:{groupsX}, groupsY:{groupsY}, pixelRadius:{pixelRadius}");

		// 把中心对齐到像素中心(+0.5),解决时深时浅问题
		float alignedPixelCenterX = (float)Math.Floor(pixelCenterX) + 0.5f;
		float alignedPixelCenterY = (float)Math.Floor(pixelCenterY) + 0.5f;

		FootprintCSData data;
		data.PixelCenter = new Vector2(alignedPixelCenterX, alignedPixelCenterY);
		data.PixelRadius = pixelRadius;
		data.DepthOffset = -pressure;
		data.StartOffset = new Int2(minX, minY);

		IntPtr csPtr = _footprintDrawGpuShader.GetCS("CS");
		IntPtr cbPtr = _footprintDrawGpuShader.GetCB(0);
		if (csPtr == IntPtr.Zero || cbPtr == IntPtr.Zero)
		{
			LogDebug("[SnowRT Draw] Footprint CS/CB句柄为空,检查HLSL入口名");
			return;
		}

		IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(data));
		Marshal.StructureToPtr(data, ptr, false);
		context.UpdateCB(cbPtr, ptr);
		Marshal.FreeHGlobal(ptr);

		GPUTextureView rtView = _snowDepthRT.View();
		context.BindUA(0, rtView);
		context.BindCB(0, cbPtr);
		context.Dispatch(csPtr, groupsX, groupsY, 1U);
		context.ResetUA();
		LogDebug("[SnowRT Draw] Dispatch黑斑CS执行完成");
	}

    // 积雪恢复CS：整张RT执行，把扰动值缓慢向基准值回归
    void DepthRecoverCS(GPUContext context, float recoverFactor)
    {
		if (!EnableDepthRecover || _depthRecoverGpuShader == null || _snowDepthRT == null)
			return;

		DepthRecoverCSData data;
		data.TargetSnowValue = RTFillValue;
		data.RecoverFactor = Mathf.Clamp(recoverFactor, 0f, 1f);

		IntPtr csPtr = _depthRecoverGpuShader.GetCS("DepthRecoverCS");
		IntPtr cbPtr = _depthRecoverGpuShader.GetCB(0);
		if (csPtr == IntPtr.Zero || cbPtr == IntPtr.Zero)
		{
			LogDebug("[SnowRT Recover] DepthRecoverCS 句柄为空");
			return;
		}

		IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(data));
		Marshal.StructureToPtr(data, ptr, false);
		context.UpdateCB(cbPtr, ptr);
		Marshal.FreeHGlobal(ptr);

		GPUTextureView rtView = _snowDepthRT.View();
		context.BindUA(0, rtView);
		context.BindCB(0, cbPtr);


		int groupCountX = _rtDesc.Width / 8;
		int groupCountY = _rtDesc.Height / 8;
		context.Dispatch(csPtr, (uint)groupCountX, (uint)groupCountY, 1U);
		context.ResetUA();
    }

    // ============================================================
    // PreRender回调：所有GPU Compute指令必须在这里提交
    // ============================================================
    private void OnPreRender(GPUContext context, ref RenderContext renderContext)
    {
        if (!_initialized)
        {
            LogDebug("[SnowRT PreRender] 尚未初始化");
            return;
        }

        if (_hasClearedRT)
        {
            // 时序逻辑：先绘制本帧所有脚印，再执行积雪恢复
            // 测试黑斑单次绘制
            if (TestDrawSpot)
            {
                LogDebug("[SnowRT PreRender] 开始执行测试黑斑绘制");
                Vector2 debugWorldPos = WindowOrigin + new Vector2(DebugUV_X, DebugUV_Y) * RTWorldSize;
                DrawFootprintCS(context, debugWorldPos, 200, 1f);
                TestDrawSpot = false;
                LogDebug("[SnowRT PreRender] 测试黑斑标记关闭");
            }

            // 消费脚印队列，依次提交绘制CS
            while (_footprintQueue.Count > 0)
            {
                var fp = _footprintQueue.Dequeue();
                DrawFootprintCS(context, fp.Pos, fp.Radius, fp.Pressure);
            }

            // 积雪恢复计时器判断
            if (EnableDepthRecover)
            {
                _recoverTimeAcc += Time.DeltaTime;
                if (_recoverTimeAcc >= RecoverInterval)
                {
                    float factor = RecoverSpeed * RecoverInterval;
                    DepthRecoverCS(context, factor);
                    _recoverTimeAcc = 0f; // 触发后重置累积时间
                }
            }
            return;
        }

        // 第一帧，初始化清空RT
        if (_snowDepthRT == null || _clearGpuShader == null)
        {
            LogDebug("[SnowRT PreRender] RT或ClearShader为空，无法清空RT");
            return;
        }
        GPUTextureView snowRTView = _snowDepthRT.View();
        FillTexture2D(context, _clearGpuShader, snowRTView, RTFillValue, _rtDesc.Width, _rtDesc.Height);
        _hasClearedRT = true;
        LogDebug("[SnowRT PreRender] RT Clear finished，下一帧绘制黑斑");
    }

    // ============================================================
    // 外部接口
    // ============================================================

    // 外部调用接口：脚印入队，仅CPU入队，GPU指令延迟到PreRender执行
    public bool DrawFootprint(Vector2 footWorldPos, float radius, float pressure)
    {
        // 世界坐标超出RT窗口范围，直接丢弃
        if (footWorldPos.X < WindowOrigin.X || footWorldPos.Y < WindowOrigin.Y ||
            footWorldPos.X > WindowOrigin.X + RTWorldSize.X ||
            footWorldPos.Y > WindowOrigin.Y + RTWorldSize.Y)
        {
            LogDebug("[SnowRT DrawFootprint] 脚印超出RT窗口，丢弃");
            return false;
        }
		// 限制脚印队列长度，防止处理卡顿
		if (_footprintQueue.Count>64) {
			LogDebug($"[SnowRT DrawFootprint] 脚印队列已满");
			return false;
		}
			
        _footprintQueue.Enqueue(new FootprintInfo { Pos = footWorldPos, Radius = radius, Pressure = pressure });
        LogDebug($"[SnowRT DrawFootprint] 脚印入队 Pos:{footWorldPos}");
		
		return true;
    }

    // 获取RT供外部访问
    public GPUTexture GetRT() => _snowDepthRT;
}