using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MechrevoLite;

/// <summary>
/// 在应用内移除本程序安装的 GCU：停掉并删除本程序 GCU 目录下的 GCUBridge / GCUService 服务，删掉 {app}\GCU 下的 GCU 本体目录。
/// <para>⛔ 只处理可执行文件位于本程序 GCU 目录内的服务与进程：其它位置的同名服务（官方控制台 / 外置 GCU 包）一律不碰。</para>
/// <para>⛔ 不碰官方控制台、不删 UWACPIDriver 驱动库条目、不删 HKLM\SOFTWARE\OEM 下的厂商键（自定义风扇曲线 / 灯效在里面）。</para>
/// </summary>
internal static class GcuPackageRemoval
{
	private const string ServiceName = "GCUBridge";

	// GCU 包里还有一个 GCUService；只删 GCUBridge 会留下一个指向已删目录的自启动服务。
	private const string ServiceName2 = "GCUService";

	/// <summary>确认后移除；返回是否成功与逐条结果（供界面显示）。</summary>
	internal static async Task<(bool Ok, string Detail)> RemoveAsync(IWin32Window owner)
	{
		string root = GcuRoot();
		DialogResult answer = MessageBox.Show(owner,
			"将停止并删除本程序 GCU 目录下的服务（GCUBridge / GCUService），再删除 GCU 本体目录：" + Environment.NewLine +
			Path.Combine(root, "AiStoneService") + Environment.NewLine +
			Path.Combine(root, "UniwillService") + Environment.NewLine + Environment.NewLine +
			"只处理可执行文件在本程序目录内的同名服务与进程，其它位置的 GCU 不受影响。" + Environment.NewLine +
			"不受影响：官方控制台、UWACPIDriver 驱动、自定义风扇曲线与灯效设置。" + Environment.NewLine +
			"移除后风扇 / 功耗 / 灯光改走本地 EC，需要重启程序生效。" + Environment.NewLine + Environment.NewLine +
			"继续移除？",
			"移除 GCU", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
		if (answer != DialogResult.Yes)
		{
			return (false, "已取消");
		}
		(bool Ok, string Detail) result = await Task.Run(RemoveCore);
		Logger.WriteError("[gcu-remove] " + result.Detail.Replace(Environment.NewLine, " | "));
		if (result.Ok)
		{
			MessageBox.Show(owner, "GCU 已移除。" + Environment.NewLine + Environment.NewLine + result.Detail,
				"移除 GCU", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}
		else
		{
			MessageBox.Show(owner, "GCU 未完全移除：" + Environment.NewLine + Environment.NewLine + result.Detail
				+ Environment.NewLine + Environment.NewLine + "可先切到「EC 控制模式」，或稍后重试。",
				"移除 GCU", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		return result;
	}

	/// <summary>服务探针结果：不存在 / 属于本程序 / 属于别处 / 查不出来。</summary>
	private enum ServiceProbe
	{
		Absent,
		Ours,
		Foreign,
		Unknown
	}

	private static (bool Ok, string Detail) RemoveCore()
	{
		StringBuilder log = new();
		bool ok = true;
		string root = GcuRoot();
		string[] names = new[] { ServiceName, ServiceName2 };
		// 先按可执行文件位置判定归属：只有指向本程序 GCU 目录的服务才算本程序的，
		// 其它位置（官方控制台 / 外置 GCU 包）的同名服务一律不碰。
		List<string> ours = new();
		foreach (string name in names)
		{
			ServiceProbe probe = ProbeService(name, root, out string imagePath);
			switch (probe)
			{
			case ServiceProbe.Ours:
				ours.Add(name);
				log.AppendLine("服务 " + name + " 属于本程序（" + imagePath + "）");
				break;
			case ServiceProbe.Foreign:
				log.AppendLine("跳过服务 " + name + "：指向 " + imagePath + "，不是本程序安装的");
				break;
			case ServiceProbe.Unknown:
				ok = false;
				log.AppendLine("服务 " + name + " 状态查不出来 ⇒ 不做删除（避免误删别的 GCU）");
				break;
			}
		}
		foreach (string name in ours)
		{
			if (!RunSc("stop " + name, log))
			{
				log.AppendLine(name + " 停止未确认，继续按进程处理");
			}
		}
		WaitStopped(20000, root, log);
		KillProcesses(root, log);
		foreach (string name in ours)
		{
			if (ProbeService(name, root, out _) != ServiceProbe.Ours)
			{
				continue;
			}
			if (!RunSc("delete " + name, log))
			{
				ok = false;
				log.AppendLine(name + " 删除失败（需要管理员权限，或被系统占用）");
			}
			else if (ProbeService(name, root, out _) == ServiceProbe.Ours)
			{
				ok = false;
				log.AppendLine(name + " 删除后仍然存在");
			}
		}
		foreach (string dir in new[] { Path.Combine(root, "AiStoneService"), Path.Combine(root, "UniwillService") })
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}
			try
			{
				Directory.Delete(dir, recursive: true);
				log.AppendLine("已删除 " + dir);
			}
			catch (Exception ex)
			{
				ok = false;
				log.AppendLine("删除失败 " + dir + "：" + ex.Message);
			}
		}
		return (ok, log.ToString().TrimEnd());
	}

	private static string GcuRoot()
	{
		return Path.Combine(AppContext.BaseDirectory, "GCU");
	}

	private static ServiceProbe ProbeService(string name, string root, out string imagePath)
	{
		imagePath = "";
		try
		{
			using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
				"SYSTEM\\CurrentControlSet\\Services\\" + name);
			if (key == null)
			{
				return ServiceProbe.Absent;
			}
			imagePath = key.GetValue("ImagePath") as string ?? "";
			if (imagePath.Length == 0)
			{
				// 读不到可执行路径就不敢删：宁可报失败，也不误删别人的服务。
				return ServiceProbe.Unknown;
			}
			return IsUnderRoot(imagePath, root) ? ServiceProbe.Ours : ServiceProbe.Foreign;
		}
		catch (Exception)
		{
			return ServiceProbe.Unknown;
		}
	}

	/// <summary>把服务 ImagePath 规整成绝对路径，判断是否落在本程序 GCU 目录内。</summary>
	private static bool IsUnderRoot(string imagePath, string root)
	{
		string path = imagePath.Trim();
		if (path.StartsWith("\"", StringComparison.Ordinal))
		{
			int end = path.IndexOf('"', 1);
			if (end > 1)
			{
				path = path.Substring(1, end - 1);
			}
		}
		else
		{
			// 未加引号时 ImagePath 可能带启动参数：截到第一个 .exe 为止。
			int exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
			if (exe > 0)
			{
				path = path.Substring(0, exe + 4);
			}
		}
		if (path.StartsWith("\\??\\", StringComparison.Ordinal))
		{
			path = path.Substring(4);
		}
		else if (path.StartsWith("\\SystemRoot\\", StringComparison.OrdinalIgnoreCase))
		{
			path = Path.Combine(Environment.SystemDirectory, path.Substring("\\SystemRoot\\".Length));
		}
		else if (path.StartsWith("System32\\", StringComparison.OrdinalIgnoreCase))
		{
			path = Path.Combine(Environment.SystemDirectory, path.Substring("System32\\".Length));
		}
		try
		{
			path = Environment.ExpandEnvironmentVariables(path);
			string full = Path.GetFullPath(path);
			string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void WaitStopped(int timeoutMs, string root, StringBuilder log)
	{
		long deadline = Environment.TickCount64 + timeoutMs;
		while (Environment.TickCount64 < deadline)
		{
			if (!HasOursProcessRunning(root))
			{
				log.AppendLine("本程序 GCU 进程已退出");
				return;
			}
			Thread.Sleep(300);
		}
		log.AppendLine("等 " + (timeoutMs / 1000) + " 秒仍有本程序 GCU 进程，改由结束进程处理");
	}

	private static bool HasOursProcessRunning(string root)
	{
		foreach (string name in new[] { "GCUService", "GCUBridge" })
		{
			foreach (Process process in Process.GetProcessesByName(name))
			{
				using (process)
				{
				if (IsOurProcess(process, root))
				{
					return true;
				}
				}
			}
		}
		return false;
	}

	private static bool IsOurProcess(Process process, string root)
	{
		try
		{
			string? path = process.MainModule?.FileName;
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			return Path.GetFullPath(path).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			// 读不到别人进程的路径（权限 / 位数不同）⇒ 当作不是本程序的，不结束它。
			return false;
		}
	}

	private static void KillProcesses(string root, StringBuilder log)
	{
		foreach (string name in new[] { "GCUService", "GCUBridge" })
		{
			try
			{
				foreach (Process process in Process.GetProcessesByName(name))
				{
					using (process)
					{
					if (!IsOurProcess(process, root))
					{
						log.AppendLine("跳过进程 " + name + "(" + process.Id + ")：不在本程序 GCU 目录里");
						continue;
					}
					process.Kill();
					if (process.WaitForExit(5000))
					{
						log.AppendLine("已结束进程 " + name + "(" + process.Id + ")");
					}
					else
					{
						log.AppendLine("进程 " + name + "(" + process.Id + ") 5 秒内未退出");
					}
					}
				}
			}
			catch (Exception ex)
			{
				log.AppendLine("结束进程 " + name + " 失败：" + ex.Message);
			}
		}
	}

	private static bool RunSc(string arguments, StringBuilder log)
	{
		try
		{
			ProcessStartInfo info = new("sc.exe", arguments)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			using Process process = Process.Start(info);
			if (process == null)
			{
				log.AppendLine("sc " + arguments + "：进程启动失败");
				return false;
			}
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			if (!process.WaitForExit(15000))
			{
				log.AppendLine("sc " + arguments + " 超时未返回");
				return false;
			}
			log.AppendLine("sc " + arguments + " → exit=" + process.ExitCode);
			if (process.ExitCode != 0)
			{
				string extra = (output + error).Trim();
				if (extra.Length > 0)
				{
					log.AppendLine("  " + extra.Replace(Environment.NewLine, " "));
				}
			}
			return process.ExitCode == 0;
		}
		catch (Exception ex)
		{
			log.AppendLine("sc " + arguments + " 异常：" + ex.Message);
			return false;
		}
	}
}
