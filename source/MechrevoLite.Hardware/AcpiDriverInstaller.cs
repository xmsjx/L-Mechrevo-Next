using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MechrevoLite.Hardware;

/// <summary>
/// ACPIDriver（UWACPIDriver.sys）自愈：安装目录里带着驱动文件，但系统里设备打不开时，
/// 用 pnputil 装一次，让 EC 通道恢复。只有装过驱动才能读写 EC，缺它时大量功能只能失败兜底。
/// </summary>
internal static class AcpiDriverInstaller
{
	private const string DevicePath = "\\\\.\\ACPIDriver";
	private const string AttemptKey = "acpi_driver_install_at";
	private const int GenericRead = unchecked((int)0x80000000);
	private const int GenericWrite = 0x40000000;
	private const int FileShareReadWrite = 3;
	private const int OpenExisting = 3;
	private const int RetryHours = 6;
	private const int ProbeCacheMs = 3000;

	private static readonly object Gate = new();
	private static long _probeAt = -100000;
	private static bool _probeOk;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string name, int access, int share, IntPtr security, int creation, int flags, IntPtr template);

	/// <summary>设备是否可用（打不开 = 驱动没装/没加载/权限不足）。结果缓存 3 秒，可被界面高频调用。</summary>
	internal static bool IsDeviceAvailable()
	{
		return IsDeviceAvailable(forceProbe: false);
	}

	internal static bool IsDeviceAvailable(bool forceProbe)
	{
		lock (Gate)
		{
			long now = Environment.TickCount64;
			if (!forceProbe && now - _probeAt < ProbeCacheMs)
			{
				return _probeOk;
			}
			_probeOk = TryOpenDevice();
			_probeAt = Environment.TickCount64;
			return _probeOk;
		}
	}

	private static bool TryOpenDevice()
	{
		try
		{
			using SafeFileHandle handle = CreateFileW(DevicePath, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
			return !handle.IsInvalid;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>安装目录里的驱动安装文件目录（{app}\UWACPIDriver）。</summary>
	internal static string DriverFolder => Path.Combine(AppContext.BaseDirectory, "UWACPIDriver");

	/// <summary>安装目录里是否带着完整的驱动安装文件。</summary>
	internal static bool DriverFilesPresent =>
		File.Exists(Path.Combine(DriverFolder, "UWACPIDriver.inf"))
		&& File.Exists(Path.Combine(DriverFolder, "UWACPIDriver.sys"));

	/// <summary>
	/// 设备打不开且安装文件在，就装一次（默认 6 小时内不重复尝试，force=true 可强制）。
	/// </summary>
	internal static bool EnsureInstalled(bool force, out string detail)
	{
		detail = "";
		try
		{
			if (IsDeviceAvailable(forceProbe: true))
			{
				detail = "ACPIDriver 设备已可用，无需安装";
				return true;
			}
			string inf = Path.Combine(DriverFolder, "UWACPIDriver.inf");
			if (!DriverFilesPresent)
			{
				detail = "缺少驱动安装文件：" + inf;
				Logger.WriteErrorIfChanged("acpi-drv-files", "[acpi-drv] " + detail);
				return false;
			}
			long last = 0;
			long.TryParse(AppConfig.GetString(AttemptKey) ?? "", out last);
			if (!force && last > 0 && DateTime.UtcNow.Ticks - last < TimeSpan.FromHours(RetryHours).Ticks)
			{
				detail = RetryHours + " 小时内已尝试过安装，本次跳过";
				return false;
			}
			AppConfig.Set(AttemptKey, DateTime.UtcNow.Ticks.ToString());
			AppConfig.Flush();
			Logger.WriteError("[acpi-drv] 设备打不开 ⇒ 用安装目录里的驱动装一次：" + inf);
			int code = RunPnputil(inf, out string output);
			// 设备节点建立/驱动加载需要一点时间，最多等 6 秒。
			for (int i = 0; i < 12; i++)
			{
				Thread.Sleep(500);
				if (IsDeviceAvailable(forceProbe: true))
				{
					detail = "驱动安装成功（pnputil 退出码 " + code + "）";
					Logger.WriteError("[acpi-drv] " + detail);
					// 设备刚能打开：机型判定/门控结论要重算，否则还是"打不开驱动"那一版缓存。
					RuntimeModelSupport.InvalidateCache();
					return true;
				}
			}
			detail = "安装后设备仍打不开（pnputil 退出码 " + code + "）" + FormatOutput(output);
			Logger.WriteErrorIfChanged("acpi-drv-fail", "[acpi-drv] " + detail);
			return false;
		}
		catch (Exception ex)
		{
			detail = "驱动安装异常：" + ex.GetType().Name + " " + ex.Message;
			Logger.WriteErrorIfChanged("acpi-drv-ex", "[acpi-drv] " + detail);
			return false;
		}
	}

	private static int RunPnputil(string inf, out string output)
	{
		output = "";
		try
		{
			ProcessStartInfo startInfo = new("pnputil.exe", "/add-driver \"" + inf + "\" /install")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			using Process process = Process.Start(startInfo)!;
			string stdout = process.StandardOutput.ReadToEnd();
			string stderr = process.StandardError.ReadToEnd();
			if (!process.WaitForExit(60000))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch
				{
				}
				output = "pnputil 超时（60s）";
				return -1;
			}
			output = (stdout + " " + stderr).Trim();
			return process.ExitCode;
		}
		catch (Exception ex)
		{
			output = ex.GetType().Name + " " + ex.Message;
			return -1;
		}
	}

	private static string FormatOutput(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return "";
		}
		string text = output.Replace("\r", " ").Replace("\n", " ").Trim();
		return "：" + (text.Length > 200 ? text.Substring(0, 200) : text);
	}

	/// <summary>诊断用一行状态。</summary>
	internal static string Describe()
	{
		string state = IsDeviceAvailable(forceProbe: true) ? "可用" : "不可用";
		string files = DriverFilesPresent ? "有" : "缺";
		long last = 0;
		long.TryParse(AppConfig.GetString(AttemptKey) ?? "", out last);
		string lastText = last > 0 ? new DateTime(last, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") : "从未";
		return "ACPIDriver=" + state + " · 安装文件=" + files + " · 上次尝试=" + lastText;
	}
}
