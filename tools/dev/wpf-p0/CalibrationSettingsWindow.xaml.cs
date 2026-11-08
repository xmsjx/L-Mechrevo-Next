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
    private static bool _cloudAutoAttempted;

    public CalibrationSettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        LoadState();
        Loaded += async (_, _) =>
        {
            // 自定义 ICC 存在不代表官方 ICC 已下载；只要没有 factory 文件就尝试一次云端。
            if (!IccHelper.HasFactoryIccFile() && !_cloudAutoAttempted)
            {
                _cloudAutoAttempted = true;
                CloudStatusText.Text = "本地没有官方校色文件，正在从云端获取…";
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
            IccHelper.EnsureFactoryIccBound();
            RefreshIccList();
            bool hasCustom = _localIccProfiles.Count > 0;
            bool customOn = hasCustom && AppConfig.Is("calib_custom_on");
            if (!hasCustom && (AppConfig.Is("calib_custom_on") || AppConfig.Get("calib_mode") == 999))
            {
                AppConfig.Set("calib_custom_on", 0);
                AppConfig.Set("calib_mode", 0);
                AppConfig.Remove("calib_custom_icc");
                AppConfig.Flush();
            }
            CustomSwitch.IsChecked = customOn;
            CustomSwitch.Content = WpfStrings.CustomSwitch_Content;
            IccCombo.IsEnabled = customOn && hasCustom;
            ImportButton.IsEnabled = true;
            if (DeleteButton != null) DeleteButton.IsEnabled = _localIccProfiles.Count > 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private List<string> _localIccProfiles = new();
    private string _defaultIccFile = "";
    private readonly Dictionary<string, string> _iccLabelToFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本地没有 ICC 时自动联网获取；拿到后直接关联，不用再点“云端获取”。</summary>
    private async Task TryFetchMissingIccAsync()
    {
        if (IccHelper.HasFactoryIccFile())
        {
            return;
        }
        try
        {
            CloudButton.IsEnabled = false;
            CloudIccFetcher.Result result = await CloudIccFetcher.FetchAsync();
            if (!result.Ok || string.IsNullOrWhiteSpace(result.FileName))
            {
                if (CloudStatusText != null)
                {
                    CloudStatusText.Text = string.IsNullOrWhiteSpace(result.Detail)
                        ? "云端未找到本机校色文件。"
                        : result.Detail;
                }
                return;
            }
            string path = Path.Combine(IccHelper.CustomIccDir, result.FileName);
            if (!File.Exists(path))
            {
                if (CloudStatusText != null)
                {
                    CloudStatusText.Text = "云端返回了校色文件，但本地文件不存在：" + result.FileName;
                }
                return;
            }
            AppConfig.Set("calib_factory_icc", result.FileName);
            AppConfig.Set("calib_factory_files", string.Join("|", result.FileNames ?? new[] { result.FileName }));
            AppConfig.Set("calib_icc_origin", "cloud");
            // 云端是官方校色，不是用户自定义；不要占用 calib_custom_*。
            AppConfig.Set("calib_custom_on", 0);
            AppConfig.Set("calib_mode", 0);
            AppConfig.Set("calib_enabled", 1);
            AppConfig.Flush();
            _syncing = true;
            try
            {
                CustomSwitch.IsChecked = false;
                RefreshIccList();
                IccCombo.IsEnabled = false;
                ImportButton.IsEnabled = false;
            }
            finally
            {
                _syncing = false;
            }
            await Task.Run(() => IccHelper.ApplyIcc(path));
            if (CloudStatusText != null)
            {
                CloudStatusText.Text = "已从云端获取并应用：" + result.FileName;
            }
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
        _localIccProfiles = IccHelper.ListCustomIccFiles()
            .Where(file => !IccHelper.IsFactoryIcc(file))
            .ToList();
        string sole = _localIccProfiles.FirstOrDefault() ?? "";
        _defaultIccFile = _localIccProfiles.Count <= 1
            ? sole
            : (_localIccProfiles.FirstOrDefault(IccHelper.IsDefaultIccName) ?? "");
        _iccLabelToFile.Clear();
        // 自定义列表：只显示导入的 ICC 文件名，不再混入「默认校色」。
        foreach (string file in _localIccProfiles)
        {
            _iccLabelToFile[file] = file;
            IccCombo.Items.Add(file);
        }
        string saved = AppConfig.GetString("calib_custom_icc") ?? "";
        if (!string.IsNullOrEmpty(saved))
        {
            foreach (string label in IccCombo.Items.OfType<string>())
            {
                if (string.Equals(_iccLabelToFile.GetValueOrDefault(label), saved, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(label, saved, StringComparison.OrdinalIgnoreCase))
                {
                    IccCombo.SelectedItem = label;
                    break;
                }
            }
        }
        if (DeleteButton != null) DeleteButton.IsEnabled = _localIccProfiles.Count > 0;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = IccCombo.SelectedItem as string;
        string? file = name != null && _iccLabelToFile.TryGetValue(name, out string mapped) ? mapped : name;
        if (_localIccProfiles.Count <= 1
            && (string.Equals(name, "自定义校色", StringComparison.Ordinal)
                || string.Equals(name, "原厂校色", StringComparison.Ordinal)
                || (name != null && _localIccProfiles.Contains(name, StringComparer.OrdinalIgnoreCase))))
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
        // 开关必须绑定当前下拉选中的 ICC，否则只改开关不会关联文件。
        if (on && IccCombo.SelectedItem is string selected
            && _iccLabelToFile.TryGetValue(selected, out string selectedFile))
        {
            AppConfig.Set("calib_custom_icc", selectedFile);
        }
        // 自定义必须和主界面的 calib_mode=999 同步，否则主界面会按原生校色模式 0 下发而失败。
        AppConfig.Set("calib_mode", on ? 999 : 0);
        AppConfig.Flush();
        IccCombo.IsEnabled = on && _localIccProfiles.Count > 0;
        ImportButton.IsEnabled = true;
        if (!on)
        {
            _ = System.Threading.Tasks.Task.Run(() => IccHelper.ClearAllIcc());
        }
    }

    private void IccCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing || IccCombo.SelectedItem is not string name)
        {
            return;
        }
        string? selectedFile = _iccLabelToFile.TryGetValue(name, out string mappedFile) ? mappedFile : name;
        if (string.Equals(name, "默认校色", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(_defaultIccFile))
            {
                // 多文件包的 default.icc 就是默认项；保留为可应用文件。
                AppConfig.Set("calib_custom_icc", _defaultIccFile);
                AppConfig.Set("calib_custom_on", 1);
                AppConfig.Set("calib_mode", 999);
                AppConfig.Flush();
            }
            else
            {
                AppConfig.Set("calib_custom_on", 0);
                AppConfig.Set("calib_mode", 0);
                AppConfig.Flush();
                System.Threading.Tasks.Task.Run(IccHelper.ClearAllIcc);
            }
            return;
        }
        if (string.Equals(name, "自定义校色", StringComparison.Ordinal)
            || string.Equals(name, "原厂校色", StringComparison.Ordinal))
        {
            if (_localIccProfiles.Count == 1)
            {
                AppConfig.Set("calib_custom_icc", _localIccProfiles[0]);
            }
            AppConfig.Set("calib_custom_on", 1);
            AppConfig.Set("calib_mode", 999);
            AppConfig.Flush();
            return;
        }
        AppConfig.Set("calib_custom_icc", selectedFile ?? name);
        AppConfig.Set("calib_custom_on", 1);
        AppConfig.Set("calib_mode", 999);
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
            AppConfig.Set("calib_factory_icc", result.FileName);
            AppConfig.Set("calib_factory_files", string.Join("|", result.FileNames ?? new[] { result.FileName }));
            AppConfig.Set("calib_icc_origin", "cloud");
            AppConfig.Set("calib_custom_on", 0);
            AppConfig.Set("calib_mode", 0);
            AppConfig.Flush();
            CustomSwitch.IsChecked = false;
            IccCombo.IsEnabled = false;
            ImportButton.IsEnabled = false;
            RefreshIccList();
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
        AppConfig.Set("calib_icc_origin", "user");
        AppConfig.Remove("calib_factory_icc");
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
