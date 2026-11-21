using System;
using System.Globalization;

namespace MechrevoLite.Mode;

public sealed record PerfModeDefinition(string Id, PerfModeKind Kind, string? UserName, PerfModeSettings Settings)
{
	public bool IsCustom => Kind == PerfModeKind.Custom;

	// 内置档（静音/平衡/狂暴）一律由固件自己处理：不再因为该档存过参数就搬进固件自定义槽，
	// 否则视觉档会变成自定义档（真机上表现为「选了静音，重启后停在自定义」）。
	public PerfModeRoute Route => IsCustom ? PerfModeRoute.FirmwareSlot : PerfModeRoute.BuiltIn;

	// 内置档不再用固件槽模拟（Route 恒 BuiltIn）⇒ 本判据恒 false。
	public bool IsEmulatedBuiltIn => false;

	public static string BuiltInId(PerfModeKind kind)
	{
		return kind switch
		{
			PerfModeKind.Silent => "silent", 
			PerfModeKind.Balanced => "balanced", 
			PerfModeKind.SilentTurbo => "silentturbo", 
			PerfModeKind.Turbo => "turbo", 
			_ => throw new ArgumentOutOfRangeException("kind", kind, "not a built-in mode kind"), 
		};
	}

	public static string CustomId(int ordinal)
	{
		return "custom" + ordinal.ToString(CultureInfo.InvariantCulture);
	}

	public PerfModeDefinition With(PerfModeSettings settings)
	{
		return this with
		{
			Settings = settings
		};
	}
}
