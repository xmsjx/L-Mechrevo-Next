using System;
using System.Diagnostics;
using System.Reflection;

namespace L_Mechrevo.WpfP0;

/// <summary>
/// 程序版本号（取程序集信息版本，界面不再写死）。
/// <para>读不到时依次回退到产品版本、程序集版本；全部失败返回空串。</para>
/// </summary>
public static class AppVersion
{
	/// <summary>形如 <c>beta23.8.0</c> 的展示用版本号。</summary>
	public static string Display { get; } = Resolve();

	private static string Resolve()
	{
		try
		{
			Assembly assembly = typeof(AppVersion).Assembly;
			string? informational = assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
				.InformationalVersion;
			if (!string.IsNullOrWhiteSpace(informational))
			{
				// 去掉可能附带的 +<commit> 后缀
				int plus = informational.IndexOf('+');
				return (plus > 0 ? informational.Substring(0, plus) : informational).Trim();
			}
			string? product = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
			if (!string.IsNullOrWhiteSpace(product))
			{
				return product.Trim();
			}
			return assembly.GetName().Version?.ToString() ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}
}
