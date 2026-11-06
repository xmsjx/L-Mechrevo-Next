using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using MechrevoLite;
using MechrevoLite.Display;
using Microsoft.Win32;

using L_Mechrevo.WpfP0.Resources;
namespace L_Mechrevo.WpfP0;

public partial class CalibrationSettingsWindow : Window
{
    private bool _syncing;

    public CalibrationSettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        LoadState();
        Loaded += async (_, _) =>
        {
            if (_localIccProfiles.Count == 0)
            {
                await TryFetchMissingIccAsync();
            }
        };
        CloudButton.Visibility = Visibility.Visible;
    }

    private void LoadState()
    {
        _syncing = true;
        try
        {
            StartupSwitch.IsChecked = AppConfig.Is("calib_apply_on_startup");
            CustomSwitch.IsChecked = AppConfig.Is("calib_custom_on");
            RefreshIccList();
            bool customOn = CustomSwitch.IsChecked == true;
            IccCombo.IsEnabled = customOn;
            ImportButton.IsEnabled = customOn;
            if (DeleteButton != null) DeleteButton.IsEnabled = _localIccProfiles.Count > 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private List<string> _localIccProfiles = new();

    /// <summary>本地没有 ICC 时自动联网获取；拿到后直接关联，不用再点“云端获取”。</summary>
    private async Task TryFetchMissingIccAsync()
    {
        if (_localIccProfiles.Count > 0)
        {
            return;
        }
        try
        {
            CloudButton.IsEnabled = false;
            CloudIccFetcher.Result result = await CloudIccFetcher.FetchAsync();
            if (!result.Ok || string.IsNullOrWhiteSpace(result.FileName))
            {
                return;
            }
            string path = Path.Combine(IccHelper.CustomIccDir, result.FileName);
            if (!File.Exists(path))
            {
                return;
            }
            AppConfig.Set("calib_custom_icc", result.FileName);
            AppConfig.Set("calib_custom_on", 1);
            AppConfig.Flush();
            _syncing = true;
            try
            {
                CustomSwitch.IsChecked = true;
                RefreshIccList();
                IccCombo.IsEnabled = true;
                ImportButton.IsEnabled = true;
                int index = IccCombo.Items.IndexOf(result.FileName);
                if (index >= 0)
                {
                    IccCombo.SelectedIndex = index;
                }
            }
            finally
            {
                _syncing = false;
            }
            await Task.Run(() => IccHelper.ApplyIcc(path));
        }
        catch
        {
        }
        finally
        {
            CloudButton.IsEnabled = true;
        }
    }

    private void RefreshIccList()
    {
        IccCombo.Items.Clear();
        _localIccProfiles = IccHelper.ListCustomIccFiles();
        string saved = AppConfig.GetString("calib_custom_icc") ?? "";
        if (_localIccProfiles.Count <= 1)
        {
            IccCombo.Items.Add("默认校色");
            IccCombo.Items.Add("自定义校色");
            IccCombo.SelectedIndex = AppConfig.Is("calib_custom_on") && _localIccProfiles.Count == 1 ? 1 : 0;
            if (DeleteButton != null) DeleteButton.IsEnabled = _localIccProfiles.Count > 0;
            return;
        }
        foreach (string name in _localIccProfiles)
        {
            IccCombo.Items.Add(name);
        }
        if (!string.IsNullOrEmpty(saved))
        {
            int index = IccCombo.Items.IndexOf(saved);
            if (index >= 0)
            {
                IccCombo.SelectedIndex = index;
            }
        }
        if (DeleteButton != null) DeleteButton.IsEnabled = _localIccProfiles.Count > 0;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = IccCombo.SelectedItem as string;
        string? file = name;
        if (_localIccProfiles.Count <= 1 && string.Equals(name, "自定义校色", StringComparison.Ordinal))
        {
            file = _localIccProfiles.FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(file) || !_localIccProfiles.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "请选择要删除的校色配置", "校色", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, "删除校色配置 " + file + " ？", "校色", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        bool ok = IccHelper.DeleteIcc(file);
        if (!ok)
        {
            MessageBox.Show(this, "删除失败：" + file, "校色", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        LoadState();
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        AppConfig.Set("calib_apply_on_startup", StartupSwitch.IsChecked == true ? 1 : 0);
        AppConfig.Flush();
    }

    private void CustomSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        bool on = CustomSwitch.IsChecked == true;
        AppConfig.Set("calib_custom_on", on ? 1 : 0);
        AppConfig.Flush();
        IccCombo.IsEnabled = on;
        ImportButton.IsEnabled = on;
        if (!on)
        {
            _ = System.Threading.Tasks.Task.Run(() => IccHelper.ClearAllIcc());
            if (AppConfig.Get("calib_mode") == 999)
            {
                AppConfig.Set("calib_mode", 1);
                AppConfig.Flush();
            }
        }
    }

    private void IccCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing || IccCombo.SelectedItem is not string name)
        {
            return;
        }
        if (string.Equals(name, "默认校色", StringComparison.Ordinal))
        {
            AppConfig.Set("calib_custom_on", 0);
            AppConfig.Flush();
            System.Threading.Tasks.Task.Run(IccHelper.ClearAllIcc);
            return;
        }
        if (string.Equals(name, "自定义校色", StringComparison.Ordinal))
        {
            if (_localIccProfiles.Count == 1)
            {
                AppConfig.Set("calib_custom_icc", _localIccProfiles[0]);
            }
            AppConfig.Set("calib_custom_on", 1);
            AppConfig.Flush();
            return;
        }
        AppConfig.Set("calib_custom_icc", name);
        AppConfig.Set("calib_custom_on", 1);
        AppConfig.Flush();
    }

    private async void CloudButton_Click(object sender, RoutedEventArgs e)
    {
        CloudButton.IsEnabled = false;
        try
        {
            MechrevoLite.Display.CloudIccFetcher.Result result = await MechrevoLite.Display.CloudIccFetcher.FetchAsync();
            if (!result.Ok || string.IsNullOrWhiteSpace(result.FileName))
            {
                MessageBox.Show(this, result.Detail, WpfStrings.L("云端获取 ICC", "Get ICC from cloud"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AppConfig.Set("calib_custom_icc", result.FileName);
            AppConfig.Set("calib_custom_on", 1);
            AppConfig.Flush();
            CustomSwitch.IsChecked = true;
            IccCombo.IsEnabled = true;
            ImportButton.IsEnabled = true;
            RefreshIccList();
            int index = IccCombo.Items.IndexOf(result.FileName);
            if (index >= 0) IccCombo.SelectedIndex = index;
            string importedList = result.FileNames is { Count: > 0 } names
                ? string.Join(Environment.NewLine, names)
                : result.FileName;
            MessageBoxResult openFolder = MessageBox.Show(this,
                result.Detail + Environment.NewLine + Environment.NewLine + importedList
                + Environment.NewLine + Environment.NewLine + "是否打开校色文件所在位置？",
                WpfStrings.L("云端获取 ICC", "Get ICC from cloud"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (openFolder == MessageBoxResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + IccHelper.CustomIccDir + "\"",
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, WpfStrings.L("云端获取 ICC 失败：", "Failed to get ICC from cloud: ") + ex.Message, WpfStrings.L("云端获取 ICC", "Get ICC from cloud"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CloudButton.IsEnabled = true;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Filter = WpfStrings.L("ICC 配置文件|*.icc;*.icm|所有文件|*.*", "ICC profiles|*.icc;*.icm|All files|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        string? imported = IccHelper.ImportIcc(dialog.FileName);
        if (string.IsNullOrWhiteSpace(imported))
        {
            MessageBox.Show(this, WpfStrings.CalibrationSettingsWindow_xaml_MessageBoxShowthis, WpfStrings.L("屏幕校色", "Display calibration"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppConfig.Set("calib_custom_icc", imported);
        AppConfig.Flush();
        _syncing = true;
        try
        {
            RefreshIccList();
            int index = IccCombo.Items.IndexOf(imported);
            if (index >= 0)
            {
                IccCombo.SelectedIndex = index;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyDarkTitleBar()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }
        int enabled = 1;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
