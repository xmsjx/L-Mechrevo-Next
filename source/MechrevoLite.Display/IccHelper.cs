using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MechrevoLite.Display;

/// <summary>自定义校色 ICC：直接经 Windows 颜色管理（WCS）关联/解除，不经由 GCU。</summary>
public static class IccHelper
{
	private const uint WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER = 1;
	private const string IcmRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM";
	private const string SpoolColorDir = @"C:\Windows\System32\spool\drivers\color";
	private static readonly string UserColorDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		@"Microsoft\Windows\Color");

	[DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool WcsAssociateColorProfileWithDevice(uint scope, string profileName, string deviceName);

	[DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool WcsDisassociateColorProfileFromDevice(uint scope, string profileName, string deviceName);

	/// <summary>云端官方 ICC 目录。</summary>
	public static string OfficialIccDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite", "ICC");

	/// <summary>用户自定义 ICC 目录。</summary>
	public static string CustomIccDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite", "customICC");

	/// <summary>旧版本放在程序根目录的 customICC（只用于一次性迁移）。</summary>
	private static string LegacyCustomIccDir => Path.Combine(AppContext.BaseDirectory, "customICC");
	private static string LegacyUserIccDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite", "customICC");

	public static void EnsureCustomIccDir()
	{
		try
		{
			Directory.CreateDirectory(OfficialIccDir);
			Directory.CreateDirectory(CustomIccDir);
			MigrateLegacyCustomIccOnce();
			MigrateLegacyUserIccOnce();
			ImportBundledIccFromProgramDir();
		}
		catch
		{
		}
	}

	/// <summary>程序目录或程序目录\ICC 下自带的 ICC，首次发现时复制到用户 customICC。</summary>
	private static void ImportBundledIccFromProgramDir()
	{
		try
		{
			string[] dirs =
			{
				AppContext.BaseDirectory,
				Path.Combine(AppContext.BaseDirectory, "ICC"),
				Path.Combine(AppContext.BaseDirectory, "customICC")
			};
			foreach (string dir in dirs)
			{
				if (!Directory.Exists(dir))
				{
					continue;
				}
				foreach (string file in Directory.EnumerateFiles(dir, "*.icm").Concat(Directory.EnumerateFiles(dir, "*.icc")))
				{
					string name = Path.GetFileName(file);
					string dest = Path.Combine(CustomIccDir, name);
					if (File.Exists(dest))
					{
						continue;
					}
					try
					{
						File.Copy(file, dest, overwrite: false);
						Logger.WriteLine("已导入程序目录 ICC: " + name);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}
	/// <summary>旧版 %APPDATA%\MechrevoLite\customICC 迁移到 ICC 数据目录（只在新目录为空时做）。</summary>
	private static void MigrateLegacyUserIccOnce()
	{
		try
		{
			string legacy = LegacyUserIccDir;
			if (!Directory.Exists(legacy) || string.Equals(legacy.TrimEnd('\\'), CustomIccDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			if (Directory.EnumerateFiles(CustomIccDir, "*.ic*").FirstOrDefault() != null)
			{
				return;
			}
			foreach (string file in Directory.EnumerateFiles(legacy, "*.ic*"))
			{
				string dest = Path.Combine(CustomIccDir, Path.GetFileName(file));
				if (!File.Exists(dest))
				{
					File.Copy(file, dest, overwrite: false);
				}
			}
			Logger.WriteLine("已迁移旧 customICC 到数据 ICC 目录");
		}
		catch
		{
		}
	}

	/// <summary>旧版 customICC（程序目录）里的文件迁移到用户数据目录；只做一次，失败不影响启动。</summary>
	private static void MigrateLegacyCustomIccOnce()
	{
		try
		{
			string legacy = LegacyCustomIccDir;
			if (!Directory.Exists(legacy) || string.Equals(legacy.TrimEnd('\\'), CustomIccDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			if (Directory.EnumerateFiles(CustomIccDir, "*.ic*").FirstOrDefault() != null || Directory.EnumerateFiles(legacy, "*.ic*").FirstOrDefault() == null)
			{
				return;
			}
			foreach (string file in Directory.EnumerateFiles(legacy))
			{
				string dest = Path.Combine(CustomIccDir, Path.GetFileName(file));
				if (!File.Exists(dest))
				{
					File.Copy(file, dest, overwrite: false);
				}
			}
		}
		catch
		{
		}
	}


	/// <summary>列出 customICC 目录下的全部 .icc/.icm 文件名（排序）。</summary>
	/// <summary>多文件 ICC 包里的默认校色文件名；单文件包没有这个文件。</summary>
	public const string DefaultIccFileName = "default.icc";

	public static bool IsDefaultIccName(string? fileName)
	{
		string name = Path.GetFileName(fileName ?? "");
		return string.Equals(name, "default.icc", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "default.icm", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>多文件 ICC 包按标准名显示；未知文件保留文件名。</summary>
	public static string DisplayNameFor(string? fileName)
	{
		string name = Path.GetFileName(fileName ?? "");
		if (IsDefaultIccName(name)) return "默认校色";
		string stem = Path.GetFileNameWithoutExtension(name);
		return stem.ToLowerInvariant() switch
		{
			"srgb" => "sRGB",
			"displayp3" or "p3" => "P3",
			"adobergb" => "AdobeRGB",
			_ => name
		};
	}

	/// <summary>标准校色文件 → 原生档位：2=sRGB、3=P3、4=AdobeRGB。</summary>
	public static int StandardLevelFor(string? fileName)
	{
		string name = Path.GetFileName(fileName ?? "");
		if (IsDefaultIccName(name)) return 1;
		string stem = Path.GetFileNameWithoutExtension(name);
		return stem.ToLowerInvariant() switch
		{
			"srgb" => 2,
			"displayp3" or "p3" => 3,
			"adobergb" => 4,
			_ => 0
		};
	}

	public static string? FindStandardIcc(int mode)
	{
		foreach (string file in ListOfficialIccFiles())
		{
			if (StandardLevelFor(file) == mode)
			{
				return file;
			}
		}
		return null;
	}

	public static int HighestStandardLevel()
	{
		int level = 1;
		foreach (string file in ListOfficialIccFiles())
		{
			level = Math.Max(level, StandardLevelFor(file));
		}
		return level;
	}

	public static IReadOnlyList<string> FactoryIccFiles()
	{
		string raw = AppConfig.GetString("calib_factory_files") ?? "";
		if (!string.IsNullOrWhiteSpace(raw))
		{
			return raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
		string one = AppConfig.GetString("calib_factory_icc") ?? "";
		if (!string.IsNullOrWhiteSpace(one)) return new[] { one };
		return ListOfficialIccFiles();
	}

	/// <summary>当前应作为“默认校色”的 ICC：优先云端 factory，其次单文件包唯一文件，最后 Default.icm。</summary>
	public static string ResolveDefaultIccFile()
	{
		string configured = AppConfig.GetString("calib_factory_icc") ?? "";
		if (!string.IsNullOrWhiteSpace(configured)) return configured;
		List<string> files = ListOfficialIccFiles();
		if (files.Count == 1) return files[0];
		return files.FirstOrDefault(IsDefaultIccName) ?? "";
	}

	/// <summary>只对“云端获取”的来源补登记官方默认；用户导入的 ICC 不能被误当成出厂校色。</summary>
	public static string EnsureFactoryIccBound()
	{
		string configured = AppConfig.GetString("calib_factory_icc") ?? "";
		if (!string.IsNullOrWhiteSpace(configured)
			&& File.Exists(Path.Combine(OfficialIccDir, Path.GetFileName(configured))))
		{
			return configured;
		}
		// ICC 目录本身就是官方云端目录；单文件 BOE 也属于官方，不再要求 origin 标记。
		List<string> files = ListOfficialIccFiles();
		if (files.Count == 0) return "";
		string candidate = files.FirstOrDefault(IsDefaultIccName) ?? files[0];
		AppConfig.Set("calib_factory_icc", candidate);
		AppConfig.Set("calib_factory_files", string.Join("|", files));
		AppConfig.Set("calib_icc_origin", "cloud");
		AppConfig.Flush();
		return candidate;
	}

	public static bool HasFactoryIccFile()
	{
		foreach (string file in FactoryIccFiles())
		{
			if (File.Exists(Path.Combine(OfficialIccDir, Path.GetFileName(file))))
			{
				return true;
			}
		}
		return false;
	}

	public static bool IsFactoryIcc(string? fileName)
	{
		string name = Path.GetFileName(fileName ?? "");
		foreach (string factory in FactoryIccFiles())
		{
			if (string.Equals(Path.GetFileName(factory), name, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static List<string> ListOfficialIccFiles()
	{
		EnsureCustomIccDir();
		return EnumerateIccFiles(OfficialIccDir);
	}

	public static List<string> ListCustomIccFiles()
	{
		EnsureCustomIccDir();
		return EnumerateIccFiles(CustomIccDir);
	}

	private static List<string> EnumerateIccFiles(string dir)
	{
		List<string> list = new List<string>();
		try
		{
			foreach (string file in Directory.EnumerateFiles(dir, "*.icc"))
			{
				list.Add(Path.GetFileName(file));
			}
			foreach (string file in Directory.EnumerateFiles(dir, "*.icm"))
			{
				list.Add(Path.GetFileName(file));
			}
		}
		catch
		{
		}
		list.Sort(StringComparer.OrdinalIgnoreCase);
		return list;
	}

	/// <summary>导入 ICC 到 customICC 目录，返回落地后的文件名；同名自动加序号。</summary>
	public static string? ImportIcc(string sourcePath)
	{
		EnsureCustomIccDir();
		string fileName = Path.GetFileName(sourcePath);
		string dest = Path.Combine(CustomIccDir, fileName);
		try
		{
			if (string.Equals(sourcePath, dest, StringComparison.OrdinalIgnoreCase))
			{
				return fileName;
			}
			if (File.Exists(dest))
			{
				string stem = Path.GetFileNameWithoutExtension(fileName);
				string ext = Path.GetExtension(fileName);
				bool placed = false;
				for (int i = 1; i < 1000; i++)
				{
					string candidate = Path.Combine(CustomIccDir, stem + " (" + i + ")" + ext);
					if (!File.Exists(candidate))
					{
						dest = candidate;
						placed = true;
						break;
					}
				}
				if (!placed)
				{
					return null;
				}
			}
			File.Copy(sourcePath, dest, overwrite: false);
			return Path.GetFileName(dest);
		}
		catch (Exception ex)
		{
			Logger.WriteLine("ICC 导入失败: " + ex.Message);
			return null;
		}
	}

	/// <summary>删除 customICC/ICC 目录中的一个配置文件；如果它是当前应用项，先解除关联。</summary>
	public static string? ImportOfficialIcc(string sourcePath)
	{
		EnsureCustomIccDir();
		string name = Path.GetFileName(sourcePath);
		if (string.IsNullOrWhiteSpace(name)) return null;
		string dest = Path.Combine(OfficialIccDir, name);
		try
		{
			if (string.Equals(sourcePath, dest, StringComparison.OrdinalIgnoreCase))
			{
				return name;
			}
			File.Copy(sourcePath, dest, overwrite: true);
			return name;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>手动云端获取成功后，用新包覆盖官方目录：删除不在新包里的旧文件。</summary>
	public static void ReplaceOfficialIccSet(IEnumerable<string> fileNames)
	{
		try
		{
			HashSet<string> keep = new(fileNames.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
			EnsureCustomIccDir();
			foreach (string file in Directory.EnumerateFiles(OfficialIccDir, "*.ic*"))
			{
				if (!keep.Contains(Path.GetFileName(file)))
				{
					File.Delete(file);
				}
			}
		}
		catch
		{
		}
	}

	public static bool DeleteIcc(string fileName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(fileName)
				|| !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
			{
				return false;
			}
			EnsureCustomIccDir();
			string path = Path.Combine(CustomIccDir, fileName);
			if (!File.Exists(path))
			{
				return false;
			}
			bool applied = string.Equals(AppConfig.GetString("calib_custom_icc"), fileName, StringComparison.OrdinalIgnoreCase);
			bool customMode = AppConfig.Get("calib_mode") == 999;
			bool lastCustom = ListCustomIccFiles().Count <= 1;
			if (applied || customMode)
			{
				// 删掉的如果是当前自定义 ICC：先切回默认校色，
				// 不能留下 calib_mode=999 + calib_custom_on=1 的悬空状态。
				ClearAllIcc();
				AppConfig.Remove("calib_custom_icc");
				AppConfig.Set("calib_custom_on", 0);
				AppConfig.Set("calib_enabled", 0);
				AppConfig.Set("calib_mode", 0);
				AppConfig.Flush();
			}
			else if (lastCustom)
			{
				// 删的是最后一个未应用 ICC：清掉自定义选择，但不关闭当前原生校色。
				AppConfig.Remove("calib_custom_icc");
				AppConfig.Set("calib_custom_on", 0);
				AppConfig.Flush();
			}
			File.Delete(path);
			Logger.WriteLine("自定义 ICC 已删除: " + fileName);
			return true;
		}
		catch (Exception ex)
		{
			Logger.WriteLine("删除自定义 ICC 失败: " + ex.Message);
			return false;
		}
	}

	/// <summary>把指定 ICC 文件关联到笔记本内屏（不走 GCU）。</summary>
	public static bool ApplyIcc(string profilePath)
	{
		try
		{
			return ApplyIccCore(profilePath);
		}
		catch (Exception ex)
		{
			Logger.WriteLine("自定义 ICC 应用异常: " + ex.Message);
			return false;
		}
	}

	private static bool ApplyIccCore(string profilePath)
	{
		string? device = ScreenNative.FindLaptopScreen(log: true);
		if (string.IsNullOrEmpty(device) || !File.Exists(profilePath))
		{
			Logger.WriteLine("ICC 应用失败：设备或文件不可用");
			return false;
		}
		// 用户配置文件标记：复制到用户颜色目录 + HKCU RegisteredProfiles 空子键，
		// 否则 Win11 设置-颜色管理页的配置文件列表为空（找不到配置文件）
		string fileName = Path.GetFileName(profilePath);
		try
		{
			if (!Directory.Exists(UserColorDir))
			{
				Directory.CreateDirectory(UserColorDir);
			}
			string userPath = Path.Combine(UserColorDir, fileName);
			if (!File.Exists(userPath))
			{
				File.Copy(profilePath, userPath, overwrite: false);
			}
			using (RegistryKey? sub = Registry.CurrentUser.CreateSubKey(IcmRoot + @"\RegisteredProfiles\" + fileName))
			{
				// 空子键即注册标记
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("自定义 ICC 用户配置标记写入异常: " + ex.Message);
		}
		// 注册表自洽：注册项键名=文件名 -> spool 完整路径，并写入传统 DISPLAY 关联槽，
		// 否则 Windows 设置-颜色配置文件按关联的文件名找不到注册项，判定无配置文件
		string spoolPath = Path.Combine(SpoolColorDir, fileName);
		try
		{
			if (!File.Exists(spoolPath))
			{
				File.Copy(profilePath, spoolPath, overwrite: false);
			}
			using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(IcmRoot + @"\RegisteredProfiles", writable: true))
			{
				key?.SetValue(fileName, spoolPath, RegistryValueKind.String);
			}
			using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(IcmRoot + @"\ProfileAssociations\Display\DISPLAY", writable: true))
			{
				key?.SetValue("ICMProfile", fileName, RegistryValueKind.String);
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("自定义 ICC 注册表写入异常: " + ex.Message);
		}
		// 直接写 HKCU 显示器关联槽（MultiString 完整文件名）——设置-颜色管理页显示配置的关键。
		// WcsAssociate 会把槽写成逐字符数组导致设置页丢失配置文件，故弃用。
		try
		{
			using (RegistryKey? pa = Registry.CurrentUser.CreateSubKey(IcmRoot + @"\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}"))
			{
				for (int i = 0; i < 9; i++)
				{
					using (RegistryKey? slot = pa?.CreateSubKey("000" + i))
					{
						slot?.SetValue("ICMProfile", new[] { fileName }, RegistryValueKind.MultiString);
					}
				}
			}
			Logger.WriteLine("自定义 ICC 已应用: " + fileName);
			AppConfig.Set("calib_applied_icc", fileName);
			AppConfig.Flush();
			return true;
		}
		catch (Exception ex)
		{
			Logger.WriteLine("自定义 ICC 关联槽写入异常: " + ex.Message);
			return false;
		}
	}

	/// <summary>取消所有自定义 ICC：对 customICC 目录内每个配置文件逐个解除关联。</summary>
	public static void ClearAllIcc()
	{
		try
		{
			ClearAllIccCore();
		}
		catch (Exception ex)
		{
			Logger.WriteLine("取消自定义 ICC 异常: " + ex.Message);
		}
	}

	private static void ClearAllIccCore()
	{
		string? device = ScreenNative.FindLaptopScreen(log: true);
		if (string.IsNullOrEmpty(device))
		{
			return;
		}
		EnsureCustomIccDir();
		// 不遍历目录：只清理当前应用的自定义 ICC（AppConfig calib_custom_icc 记录文件名）
		string? appliedName = AppConfig.GetString("calib_custom_icc");
		if (string.IsNullOrWhiteSpace(appliedName))
		{
			appliedName = AppConfig.GetString("calib_applied_icc");
		}
		try
		{
			using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(IcmRoot + @"\ProfileAssociations\Display\DISPLAY", writable: true))
			{
				key?.SetValue("ICMProfile", "", RegistryValueKind.String);
			}
			if (!string.IsNullOrEmpty(appliedName))
			{
				using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(IcmRoot + @"\RegisteredProfiles", writable: true))
				{
					key?.DeleteValue(appliedName, throwOnMissingValue: false);
				}
				Registry.CurrentUser.DeleteSubKey(IcmRoot + @"\RegisteredProfiles\" + appliedName, throwOnMissingSubKey: false);
				try
				{
					string userPath = Path.Combine(UserColorDir, appliedName);
					if (File.Exists(userPath))
					{
						File.Delete(userPath);
					}
				}
				catch
				{
					// 用户目录文件删除失败不阻断（可能被系统占用）
				}
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("取消自定义 ICC 注册表清理异常: " + ex.Message);
		}
		// 清掉显示器下所有 LM 写过的关联槽；不能只按 appliedName 匹配，
		// 否则配置里的 applied marker 丢失后会残留 000* 槽。
		try
		{
			using RegistryKey? pa = Registry.CurrentUser.OpenSubKey(IcmRoot + @"\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}", writable: true);
			if (pa != null)
			{
				foreach (string sub in pa.GetSubKeyNames())
				{
					if (!sub.StartsWith("000"))
					{
						continue;
					}
					using RegistryKey? slot = pa.OpenSubKey(sub, writable: true);
					if (slot == null)
					{
						continue;
					}
					object? v = slot.GetValue("ICMProfile");
					string? first = v is string[] arr && arr.Length > 0 ? arr[0] : v as string;
					if (!string.IsNullOrEmpty(first))
					{
						if (string.IsNullOrEmpty(appliedName)) appliedName = first;
						using RegistryKey? registered = Registry.CurrentUser.OpenSubKey(IcmRoot + @"\RegisteredProfiles", writable: true);
						registered?.DeleteValue(first, throwOnMissingValue: false);
						Registry.CurrentUser.DeleteSubKey(IcmRoot + @"\RegisteredProfiles\" + first, throwOnMissingSubKey: false);
					}
					slot.DeleteValue("ICMProfile", throwOnMissingValue: false);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("取消自定义 ICC 关联槽清理异常: " + ex.Message);
		}
		AppConfig.Remove("calib_applied_icc");
		AppConfig.Flush();
		Logger.WriteLine("取消自定义 ICC 完成");
	}
}
