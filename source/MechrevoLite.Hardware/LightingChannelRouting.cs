namespace MechrevoLite.Hardware;

/// <summary>灯光意图应该走哪条通道。</summary>
internal enum LightingChannelPlan
{
	/// <summary>交给后端服务（GCU/MQTT 等）。</summary>
	RemoteService,

	/// <summary>没有后端服务时的本地通道（HID/EC 直控）。</summary>
	LocalChannel,

	/// <summary>既没有服务也没有本地通道。</summary>
	Unsupported
}

/// <summary>
/// 灯光意图的通道判决（纯函数，可离线断言）。
/// <para>⭐ 场景：EC 模式（无 GCU 服务）时灯带/Logo/铰链必须走本地 HID 通道；
/// 以前这里直接判失败，界面会显示"已保存，但设备未确认"而灯其实没下发。</para>
/// </summary>
internal static class LightingChannelRouting
{
	/// <summary>
	/// 决定本次灯光意图的通道。
	/// </summary>
	/// <param name="hasService">后端服务是否可用（GCU 等）。</param>
	/// <param name="topicKnown">是否为已知的灯光通道（灯带/Logo/铰链/同步）。</param>
	/// <param name="localSupported">该通道在当前机型上是否有本地通道（无服务时才有意义）。</param>
	internal static LightingChannelPlan Decide(bool hasService, bool topicKnown, bool localSupported)
	{
		if (hasService)
		{
			return LightingChannelPlan.RemoteService;
		}
		if (!topicKnown)
		{
			return LightingChannelPlan.Unsupported;
		}
		return localSupported ? LightingChannelPlan.LocalChannel : LightingChannelPlan.Unsupported;
	}
}
