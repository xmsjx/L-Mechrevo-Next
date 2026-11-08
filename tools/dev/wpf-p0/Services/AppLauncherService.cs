using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using MechrevoLite;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>性能模式按键「启动应用」：选择并记忆，之后直接启动；目标被删除时不抛异常。</summary>
internal static class AppLauncherService
{
    internal const string ConfigKey = "perf_key_app";

    internal static bool LaunchRememberedOrPick()
    {
        try
        {
            string path = AppConfig.GetString(ConfigKey) ?? "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    AppConfig.Remove(ConfigKey);
                    AppConfig.Flush();
                }
                if (!PickAndRemember("请选择性能模式按键要启动的应用"))
                {
                    return false;
                }
                path = AppConfig.GetString(ConfigKey) ?? "";
            }
            return LaunchPath(path);
        }
        catch (Exception ex)
        {
            Logger.WriteError("[quick-launch] 异常：" + ex.Message);
            return false;
        }
    }

    internal static bool PickAndRemember(string title)
    {
        string? path = PickApplication(title);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        AppConfig.Set(ConfigKey, path);
        AppConfig.Flush();
        return true;
    }

    private static bool LaunchPath(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteError("[quick-launch] 启动失败：" + path + " · " + ex.Message);
            MessageBoxCompat("应用启动失败，可能已被删除或没有权限。", "启动应用");
            return false;
        }
    }

    private static string? PickApplication(string title)
    {
        try
        {
            OpenFileDialog dialog = new()
            {
                Title = title,
                Filter = "应用程序|*.exe;*.bat;*.cmd;*.lnk|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
        catch (Exception ex)
        {
            Logger.WriteError("[quick-launch] 选择应用失败：" + ex.Message);
            return null;
        }
    }

    private static void MessageBoxCompat(string message, string title)
    {
        try
        {
            System.Windows.MessageBox.Show(message, title,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch
        {
        }
    }
}
