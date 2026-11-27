using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MechrevoLite.Hardware;

/// <summary>某功能在声明表里的判定结果。</summary>
public enum ChannelDecisionKind
{
	/// <summary>声明表没提这个功能 ⇒ 按各功能原有探测，不受影响。</summary>
	NotDeclared,
	/// <summary>本机型明确没有这个功能（写成 <c>none</c>）。</summary>
	Absent,
	/// <summary>声明走别的通道，但该通道在本版本还没实现。</summary>
	UnsupportedChannel,
	/// <summary>声明走本版本支持的通道。</summary>
	Supported
}

/// <summary>功能通道判定：给调用方决定"能不能用"，<see cref="Note" /> 直接进日志。</summary>
public readonly record struct ChannelDecision(ChannelDecisionKind Kind, string Detail)
{
	public bool Blocks => Kind == ChannelDecisionKind.Absent || Kind == ChannelDecisionKind.UnsupportedChannel;

	public override string ToString()
	{
		return Kind + (string.IsNullOrEmpty(Detail) ? "" : "(" + Detail + ")");
	}
}

/// <summary>取通道定义时要问的机型事实（由调用方从机型识别结果填，不依赖后端）。</summary>
public readonly record struct ManifestQuery(string? Board, string? Model, string? PlatformCode)
{
	/// <summary>本机确实开放本地 EC 模式（走 GCU 或本地实现还没做时为 false）。</summary>
	public bool LocalEcAvailable { get; init; }

	/// <summary>同方家族（含无界 X、UMI）。</summary>
	public bool TongfangFamily { get; init; }

	/// <summary>宝龙达白名单：必须排除，永远拿不到同方通道。</summary>
	public bool BaolongdaWhitelisted { get; init; }

	/// <summary>星耀 14（本地 WMAA）与星耀 15、无界 LNL（暂挂 GCU）：都不走同方通道。</summary>
	public bool NonTongfangFamily { get; init; }
}

/// <summary>机型匹配条件：平台码 / 板号前缀 / 型号关键字，任一命中即算命中。</summary>
public sealed class ManifestMatch
{
	private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

	public ManifestMatch(IReadOnlyList<string>? platformCodes = null, IReadOnlyList<string>? boardPrefixes = null, IReadOnlyList<string>? modelKeywords = null)
	{
		PlatformCodes = platformCodes ?? Empty;
		BoardPrefixes = boardPrefixes ?? Empty;
		ModelKeywords = modelKeywords ?? Empty;
	}

	public IReadOnlyList<string> PlatformCodes { get; }

	public IReadOnlyList<string> BoardPrefixes { get; }

	public IReadOnlyList<string> ModelKeywords { get; }

	public bool IsEmpty => PlatformCodes.Count == 0 && BoardPrefixes.Count == 0 && ModelKeywords.Count == 0;

	/// <summary>命中返回命中的那一条依据（写日志用），未命中原样返回 null。</summary>
	public string? Hit(string? board, string? model, string? platformCode)
	{
		string text = platformCode ?? "";
		foreach (string code in PlatformCodes)
		{
			if (text.Length != 0 && string.Equals(text, code, StringComparison.OrdinalIgnoreCase))
			{
				return "platformCode=" + code;
			}
		}
		string text2 = board ?? "";
		foreach (string prefix in BoardPrefixes)
		{
			if (text2.Length != 0 && text2.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return "board=" + prefix;
			}
		}
		string text3 = model ?? "";
		foreach (string keyword in ModelKeywords)
		{
			if (text3.Length != 0 && text3.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			{
				return "model=" + keyword;
			}
		}
		return null;
	}
}

/// <summary>
/// 一条机型声明：匹配条件 + 共享档案名 + 本机型自己的通道覆盖。
/// <para>合并结果 = <c>channelProfiles[Profile]</c> ← 本机型覆盖；覆盖写 <c>none</c> 表示本机型没有该功能。</para>
/// </summary>
public sealed class MachineManifest
{
	private static readonly IReadOnlyDictionary<string, ChannelBinding> EmptyChannels =
		new ReadOnlyDictionary<string, ChannelBinding>(new Dictionary<string, ChannelBinding>(StringComparer.Ordinal));

	public MachineManifest(string id, string? profile, ManifestMatch match, IReadOnlyDictionary<string, ChannelBinding>? overrides, IReadOnlyDictionary<string, ChannelBinding> channels)
	{
		Id = id;
		Profile = profile;
		Match = match;
		Overrides = overrides ?? EmptyChannels;
		Channels = channels;
	}

	public string Id { get; }

	/// <summary>共享档案名；为空表示本机型完全不引用同方通用定义（例如星耀、宝龙达）。</summary>
	public string? Profile { get; }

	public ManifestMatch Match { get; }

	/// <summary>本机型自己写的差异（只在声明表里有，已合并进 <see cref="P" />）。</summary>
	public IReadOnlyDictionary<string, ChannelBinding> Overrides { get; }

	public IReadOnlyDictionary<string, ChannelBinding> Channels { get; }

	/// <summary>取某功能的通道；声明里写了 <c>none</c> 也返回 true（值本身是 none），没声明返回 false。</summary>
	public bool TryGetChannel(string feature, out ChannelBinding binding)
	{
		binding = null;
		if (feature == null)
		{
			return false;
		}
		return Channels.TryGetValue(feature, out binding);
	}

	public bool IsNone(string feature)
	{
		return TryGetChannel(feature, out ChannelBinding binding) && binding.IsNone;
	}

	/// <summary>
	/// 机型覆盖项与档案定义合并：覆盖项只写"要改的那几个参数"，其余参数（以及通道名）沿用档案。
	/// <para>⚠️ 没有档案（本机型自带实现）时原样返回覆盖项。</para>
	/// </summary>
	public static ChannelBinding Merge(ChannelBinding profileBinding, ChannelBinding overridden)
	{
		if (profileBinding == null || overridden == null || overridden.IsNone || overridden.DeclaredAsString)
		{
			return overridden;
		}
		Dictionary<string, string> merged = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (KeyValuePair<string, string> item in profileBinding.Parameters)
		{
			merged[item.Key] = item.Value;
		}
		foreach (KeyValuePair<string, string> item2 in overridden.Parameters)
		{
			merged[item2.Key] = item2.Value;
		}
		return new ChannelBinding(overridden.Channel, new ReadOnlyDictionary<string, string>(merged), overridden.DeclaredAsString);
	}

	/// <summary>
	/// 判定某功能在本机型能不能用：没声明 ⇒ 交回原有探测；声明 <c>none</c> ⇒ 明确不可用；
	/// 声明走别的通道 ⇒ 本版本没实现该通道，同样不可用；声明走 <paramref name="supportedChannel" /> ⇒ 可用。
	/// </summary>
	public ChannelDecision Decide(string feature, params string[] supportedChannels)
	{
		if (!TryGetChannel(feature, out ChannelBinding binding))
		{
			return new ChannelDecision(ChannelDecisionKind.NotDeclared, Id);
		}
		if (binding.IsNone)
		{
			return new ChannelDecision(ChannelDecisionKind.Absent, Id);
		}
		if (supportedChannels != null)
		{
			foreach (string supportedChannel in supportedChannels)
			{
				if (string.Equals(binding.Channel, supportedChannel, StringComparison.OrdinalIgnoreCase))
				{
					return new ChannelDecision(ChannelDecisionKind.Supported, Id + ":" + binding.Channel);
				}
			}
		}
		return new ChannelDecision(ChannelDecisionKind.UnsupportedChannel, Id + ":" + binding.Channel);
	}

	public override string ToString()
	{
		List<string> list = Channels.Select((KeyValuePair<string, ChannelBinding> kv) => kv.Key + "=" + kv.Value).ToList();
		list.Sort(StringComparer.Ordinal);
		return Id + "[" + (Profile ?? "-") + "] " + string.Join(" ", list);
	}
}
