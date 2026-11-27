using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// 声明表在运行期的唯一入口：惰性把"本机是哪台"解析成一条机型声明，供各功能查询该功能走哪条通道。
/// <para>⚠️ 只读：不写 EC、不写配置、不碰注册表；解析失败一律按"未声明"处理，行为回到原有探测。</para>
/// <para>⚠️ 这里不判断家族/后端，那些事实来自 <see cref="MachineIdentity" />，路由在 <see cref="BackendRouter" />。</para>
/// </summary>
internal static class DeclarationRuntime
{
	private static readonly object Gate = new object();

	private static bool _resolved;

	/// <summary>解析进行中的重入标记：解析过程本身可能回头读寄存器地址，此时必须先用默认值。</summary>
	[ThreadStatic]
	private static bool _resolving;

	private static MachineManifest _manifest;

	private static string _reason = "";

	/// <summary>本机命中的声明；未声明为 null。</summary>
	internal static MachineManifest Manifest
	{
		get
		{
			EnsureResolved();
			return _manifest;
		}
	}

	/// <summary>命中/未命中的依据，写日志与诊断用。</summary>
	internal static string Reason
	{
		get
		{
			EnsureResolved();
			return _reason;
		}
	}

	/// <summary>判定某功能在本机能不能用（未声明 ⇒ 交回原有探测）。</summary>
	internal static ChannelDecision Decide(string feature, params string[] supportedChannels)
	{
		EnsureResolved();
		if (_manifest == null)
		{
			return new ChannelDecision(ChannelDecisionKind.NotDeclared, "no-manifest");
		}
		return _manifest.Decide(feature, supportedChannels);
	}

	/// <summary>
	/// 写入口闸门：声明表明确说本机没有这个功能时返回 true（调用方应直接拒绝、不写任何寄存器）。
	/// <para>没声明 ⇒ false，调用方照旧执行；<paramref name="supportedChannels" /> 列本版本已实现的通道。</para>
	/// </summary>
	internal static bool FeatureBlocked(string feature, params string[] supportedChannels)
	{
		ChannelDecision decision = Decide(feature, supportedChannels);
		if (!decision.Blocks)
		{
			return false;
		}
		Logger.WriteLineIfChanged("model-manifest-block-" + feature,
			"[model-manifest] " + feature + " 被声明表判为不可用（" + decision + "）⇒ 拒绝执行，未写任何寄存器");
		return true;
	}

	/// <summary>
	/// 取某功能的寄存器地址：声明表里写了就用声明表的，没写/写错就用 <paramref name="fallback" />（既有常量）。
	/// </summary>
	internal static int RegisterAddress(string feature, string param, int fallback)
	{
		try
		{
			EnsureResolved();
			if (_manifest != null && _manifest.TryGetChannel(feature, out ChannelBinding binding) && !binding.IsNone)
			{
				int address = binding.Address(param, fallback);
				if (address != fallback)
				{
					Logger.WriteLineIfChanged("model-manifest-addr-" + feature + "-" + param,
						$"[model-manifest] {feature}.{param} 改走声明表地址 0x{address:X3}（默认 0x{fallback:X3}，声明 {_manifest.Id}）");
				}
				return address;
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[model-manifest] 读取地址参数失败，用默认值：" + ex.Message);
		}
		return fallback;
	}

	/// <summary>
	/// 取某功能的字符串参数（如限充的档位方案名）：声明表里写了就返回声明值，没写/没有声明就返回 null。
	/// <para>⚠️ 只读：不写 EC、不写配置。</para>
	/// </summary>
	internal static string? Param(string feature, string key)
	{
		try
		{
			EnsureResolved();
			if (_manifest != null && _manifest.TryGetChannel(feature, out ChannelBinding binding) && !binding.IsNone)
			{
				return binding.Param(key);
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[model-manifest] 读取字符串参数失败，按未声明处理：" + ex.Message);
		}
		return null;
	}

	/// <summary>
	/// 取某功能的开关型参数（"1"/"true"/"on"/"yes" 视为开，其余与未声明一律为关）。
	/// <para>⚠️ 只读：不写 EC、不写配置。未声明 ⇒ false，调用方按原有行为走。</para>
	/// </summary>
	internal static bool Flag(string feature, string key)
	{
		string? text = Param(feature, key);
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		text = text!.Trim();
		return text == "1"
			|| string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(text, "on", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>测试/复核用：丢弃解析结果，下次重新解析。</summary>
	internal static void Reset()
	{
		lock (Gate)
		{
			_resolved = false;
			_manifest = null;
			_reason = "";
		}
	}

	private static void EnsureResolved()
	{
		if (_resolved)
		{
			return;
		}
		if (_resolving)
		{
			return;
		}
		lock (Gate)
		{
			if (_resolved || _resolving)
			{
				return;
			}
			_resolving = true;
			try
			{
				MachineIdentity machineIdentity = MachineIdentity.Collect();
				ModelRegistryData modelRegistryData = ModelRegistryData.Load();
				_manifest = modelRegistryData.MatchManifest(machineIdentity.Board, machineIdentity.Model, machineIdentity.ProjectId, out string reason);
				_reason = reason ?? "no-match";
				Logger.WriteLine("[model-manifest] 本机声明=" + ((_manifest == null) ? "未命中" : _manifest.Id) + " reason=" + _reason);
			}
			catch (Exception ex)
			{
				_manifest = null;
				_reason = "resolve-failed: " + ex.Message;
				Logger.WriteLine("[model-manifest] 声明解析失败，按未声明处理：" + ex.Message);
			}
			finally
			{
				_resolving = false;
			}
			_resolved = true;
		}
	}
}
