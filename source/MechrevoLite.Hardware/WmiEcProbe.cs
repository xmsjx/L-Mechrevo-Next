using System;
using System.Collections.Generic;
using System.Management;
using MechrevoLite;

namespace MechrevoLite.Hardware;

/// <summary>
/// 只读探测：枚举 root\WMI 里可能承载厂商 EC 读写的接口，把它们的方法与入参记录到日志。
/// 不发写操作、不改任何寄存器，只为后续"WMI 读 EC"定位正确的类与命令字。
/// </summary>
internal static class WmiEcProbe
{
	private static bool _done;

	private static readonly string[] Keywords =
	{
		"AcpiTest", "MiInterface", "MIFS", "UniWill", "Oem", "Wmi"
	};

	internal static void LogAvailableInterfaces()
	{
		if (_done)
		{
			return;
		}
		_done = true;
		try
		{
			List<string> found = new();
			using (ManagementObjectSearcher searcher = new("root\\WMI", "SELECT * FROM meta_class"))
			{
				foreach (ManagementBaseObject item in searcher.Get())
				{
					using ManagementObject? cls = item as ManagementObject;
					if (cls == null)
					{
						continue;
					}
					string name = cls["__CLASS"]?.ToString() ?? "";
					if (name.Length == 0 || !Matches(name))
					{
						continue;
					}
					string methods = DescribeMethods(name);
					found.Add(methods.Length > 0 ? name + " · " + methods : name);
				}
			}
			if (found.Count == 0)
			{
				Logger.WriteLine("[wmi-ec] 未发现候选 WMI 接口（root\\WMI 无 AcpiTest/MiInterface/UniWill 等类）");
				return;
			}
			Logger.WriteLine("[wmi-ec] 候选 WMI 接口（只读枚举，未发任何写操作）：");
			foreach (string line in found)
			{
				Logger.WriteLine("[wmi-ec]   " + line);
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[wmi-ec] 枚举失败（已吞掉）：" + ex.GetType().Name + " " + ex.Message);
		}
	}

	private static bool Matches(string name)
	{
		foreach (string keyword in Keywords)
		{
			if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static string DescribeMethods(string className)
	{
		try
		{
			List<string> parts = new();
			using ManagementClass meta = new("root\\\\WMI", className, null);
			foreach (MethodData method in meta.Methods)
			{
				string? name = method.Name;
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}
				List<string> parameters = new();
				try
				{
					if (method.InParameters != null)
					{
						foreach (PropertyData property in method.InParameters.Properties)
						{
							parameters.Add(property.Name ?? "?");
						}
					}
				}
				catch
				{
					// 入参枚举失败不影响方法名记录。
				}
				parts.Add(parameters.Count > 0 ? name + "(" + string.Join(",", parameters) + ")" : name);
			}
			return string.Join(" / ", parts);
		}
		catch
		{
			return "";
		}
	}
}
