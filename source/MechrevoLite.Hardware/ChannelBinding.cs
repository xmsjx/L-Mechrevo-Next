using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace MechrevoLite.Hardware;

/// <summary>
/// 一个功能在某个通道上的绑定方式（"这个机型的功能走谁"）。
/// <para>⚠️ 绑定不等于能力：绑定只回答"从哪条通道读写"，能不能用仍然由实时探测决定。</para>
/// </summary>
public sealed class ChannelBinding
{
	/// <summary><c>none</c>：本机型明确不走这个功能（例如没有 Logo 灯、没有限充环路）。</summary>
	public const string NoneChannel = "none";

	private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
		new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

	public ChannelBinding(string channel, IReadOnlyDictionary<string, string>? parameters = null, bool declaredAsString = false)
	{
		Channel = channel;
		Parameters = parameters ?? EmptyParameters;
		DeclaredAsString = declaredAsString;
	}

	/// <summary>
	/// 声明表里是写成裸字符串（<c>"chargeLimit": "ec"</c>）还是对象（<c>{ "channel": "ec" }</c>）。
	/// 裸字符串只能用在共享档案已定义的功能上；本机型自带实现要写成对象，避免功能名拼错被当成新功能。
	/// </summary>
	public bool DeclaredAsString { get; }

	public string Channel { get; }

	public IReadOnlyDictionary<string, string> Parameters { get; }

	public bool IsNone => string.Equals(Channel, NoneChannel, StringComparison.OrdinalIgnoreCase);

	public static ChannelBinding Disabled { get; } = new ChannelBinding(NoneChannel);

	public string? Param(string key)
	{
		return Parameters.TryGetValue(key, out string value) ? value : null;
	}

	/// <summary>
	/// 取地址参数（写成 <c>"0x7B9"</c> 这种十六进制，也接受十进制）。缺省/写错/越界一律回 <paramref name="fallback" />，
	/// 并留一行日志——声明表写错不能让功能静默写到别的寄存器上。
	/// </summary>
	public int Address(string key, int fallback)
	{
		string text = Param(key);
		if (string.IsNullOrWhiteSpace(text))
		{
			return fallback;
		}
		text = text.Trim();
		int value;
		try
		{
			bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
			value = hex
				? Convert.ToInt32(text.Substring(2), 16)
				: Convert.ToInt32(text, CultureInfo.InvariantCulture);
		}
		catch (Exception)
		{
			Logger.WriteLine($"[model-manifest] 地址参数 {key}={text} 无法解析 ⇒ 用默认 0x{fallback:X3}");
			return fallback;
		}
		if (value < 0 || value > 0xFFF)
		{
			Logger.WriteLine($"[model-manifest] 地址参数 {key}={text} 超出范围 ⇒ 用默认 0x{fallback:X3}");
			return fallback;
		}
		return value;
	}

	public override string ToString()
	{
		if (IsNone)
		{
			return NoneChannel;
		}
		return Parameters.Count == 0 ? Channel : Channel + "{" + string.Join(",", Parameters.Keys) + "}";
	}
}
