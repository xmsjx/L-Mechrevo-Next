using System;

namespace MechrevoLite.Hardware;

/// <summary>
/// ITE 7001 的能力字节（0xA2 查询回读的 byte[2]）：灯带 / 铰链 / Logo 三路是否具备。
/// <para>同一字节在两组 BIOS 项目码上含义不同，按项目码分表解释。</para>
/// </summary>
internal readonly record struct IteLightingCapability(byte Raw, bool IsIdxFamily)
{
	/// <summary>灯带（base / coastline）。</summary>
	internal bool Lightbar => IsIdxFamily ? Raw is 4 or 6 : Raw is 1 or 3 or 4 or 5 or 7 or 9 or 11;

	/// <summary>铰链灯（电源指示灯 / 风道灯同一路）。</summary>
	internal bool Hinge => IsIdxFamily ? Raw is 0 or 1 or 4 or 5 or 6 : Raw is 0 or 2 or 3 or 4 or 6 or 7 or 10 or 11;

	/// <summary>旧 Logo 通道。</summary>
	internal bool Logo => IsIdxFamily ? Raw is 1 or 4 : Raw is 4 or 6 or 7;

	/// <summary>新 Logo（lettering）通道；另一套项目码上没有这一路。</summary>
	internal bool NewLogo => !IsIdxFamily && Raw is 5 or 8 or 9 or 10 or 11;

	internal bool AnyLogo => Logo || NewLogo;

	internal string TableName => IsIdxFamily ? "IDX/IDZ" : "IDA/IDB";

	internal string Describe()
	{
		string logo = AnyLogo ? (NewLogo ? "有(新)" : "有(旧)") : "无";
		return $"0xA2=0x{Raw:X2} · 表={TableName} ⇒ 灯带={(Lightbar ? "有" : "无")} · 铰链={(Hinge ? "有" : "无")} · Logo={logo}";
	}
}

/// <summary>能力字节查询：只查一次，没有 7001 设备或项目码不认识时不表态（返回 null）。</summary>
internal static class IteCapabilityProbe
{
	private static readonly object Gate = new();
	private static bool _resolved;
	private static IteLightingCapability? _cache;
	private static string _detail = "";

	internal static IteLightingCapability? Get(string? biosProjectName)
	{
		lock (Gate)
		{
			if (_resolved)
			{
				return _cache;
			}
			_resolved = true;
			byte? raw = IteLightingLocal.ReadCapabilityByte();
			if (raw == null)
			{
				_detail = "无 ITE 7001 设备（不查询）";
				return null;
			}
			string? family = ResolveFamily(biosProjectName, out string? rawProject);
			if (family == null)
			{
				_detail = rawProject == null
					? $"0xA2=0x{raw.Value:X2} · 无项目码（不套表）"
					: $"0xA2=0x{raw.Value:X2} · 项目码={rawProject}（不在灯带能力表内，不套表）";
				return null;
			}
			_cache = new IteLightingCapability(raw.Value, family is "IDX" or "IDZ");
			_detail = _cache.Value.Describe() + " · BIOS=" + family;
			Logger.WriteLine("[ite] 能力字节：" + _detail);
			return _cache;
		}
	}

	internal static string Describe() => _detail.Length > 0 ? _detail : "未查询";

	internal static void Invalidate()
	{
		lock (Gate)
		{
			_resolved = false;
			_cache = null;
			_detail = "";
			IteLightingLocal.ResetCapabilityCache();
		}
	}

	private static string? ResolveFamily(string? reported, out string? rawProject)
	{
		rawProject = Clean(reported);
		if (Normalize(rawProject) is string direct)
		{
			return direct;
		}
		try
		{
			Probe.IEcReadTransport? transport = MechrevoService.EcReadTransportFactory?.Invoke();
			if (transport != null)
			{
				rawProject = Clean(ModelRegistry.Read(transport).BiosProjectId) ?? rawProject;
				if (Normalize(rawProject) is string viaEc)
				{
					return viaEc;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[ite] BIOS 项目码读取失败：" + ex.GetType().Name);
		}
		return null;
	}

	private static string? Clean(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string text = value.Trim().ToUpperInvariant();
		return text is "UNKNOWN" or "NA" or "?" ? null : text;
	}

	private static string? Normalize(string? value)
	{
		string? text = Clean(value);
		return text is "IDA" or "IDB" or "IDX" or "IDZ" ? text : null;
	}
}
