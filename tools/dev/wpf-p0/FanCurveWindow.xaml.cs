using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.FanCurve;
using MechrevoLite.Mode;

using L_Mechrevo.WpfP0.Resources;
using L_Mechrevo.WpfP0.Services;
namespace L_Mechrevo.WpfP0;

public partial class FanCurveWindow : Window
{
    private string _modeId;
    private readonly MechrevoHw? _hw;
    private FanCurveBackendKind BackendKind => FanCurveBackendFactory.Resolve(_hw);
    private bool _dirty;
    private WujieFanEcController? _wujieController;
    private readonly DispatcherTimer _activeModeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private static readonly List<WeakReference<FanCurveWindow>> OpenEditors = new();

    internal static bool HasAnyUnsavedChanges()
    {
        foreach (WeakReference<FanCurveWindow> reference in OpenEditors)
        {
            if (reference.TryGetTarget(out FanCurveWindow? editor) && editor.HasRealUnsavedChanges())
            {
                return true;
            }
        }
        return false;
    }

    internal bool HasRealUnsavedChanges()
    {
        if (!_dirty)
        {
            return false;
        }
        try
        {
        MechrevoLite.Mode.PerfModeSettings saved = MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId);
            if (SameDuties(CpuCurve.Duties, saved.CpuFanDuty)
                && SameDuties(GpuCurve.Duties, saved.GpuFanDuty))
            {
                _dirty = false;
                return false;
            }
        }
        catch
        {
        }
        return true;
    }

    private static bool SameDuties(int[] current, int[]? saved)
    {
        if (saved == null || saved.Length != current.Length)
        {
            return false;
        }
        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != saved[i])
            {
                return false;
            }
        }
        return true;
    }

    internal static void DiscardAllUnsavedChanges()
    {
        foreach (WeakReference<FanCurveWindow> reference in OpenEditors)
        {
            if (reference.TryGetTarget(out FanCurveWindow? editor))
            {
                editor._dirty = false;
            }
        }
    }

    public FanCurveWindow(string modeId, bool showCpu = true, bool showGpu = true)
    {
        InitializeComponent();
        OpenEditors.Add(new WeakReference<FanCurveWindow>(this));
        _modeId = modeId;
        try
        {
            PushFanCurveCheck.IsChecked = MechrevoLite.Mode.PerfModeStore.LoadSettings(modeId).FanCurveOn ?? true;
            bool wujie = L_Mechrevo.WpfP0.Services.WpfBackend.Hw?.WujieSmuApu == true;
            WujieIndependentCheck.Visibility = wujie ? Visibility.Visible : Visibility.Collapsed;
            WujieIndependentCheck.IsChecked = MechrevoLite.AppConfig.Is("wujie_fan_independent");
        }
        catch
        {
        }
        CpuSection.Visibility = showCpu ? Visibility.Visible : Visibility.Collapsed;
        GpuSection.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        _hw = L_Mechrevo.WpfP0.Services.WpfBackend.Hw ?? MechrevoLite.Program.hw;
        if (BackendKind == FanCurveBackendKind.Baolongda)
        {
            BaolongdaLoopCheck.Visibility = Visibility.Visible;
            BaolongdaSyncCheck.Visibility = Visibility.Visible;
        }
        bool legacyFanCurve = _hw != null && (_hw.IsGcuConnected || _hw.UsesBaolongda || _hw.WujieSmuApu
            || _hw.IsNucUmiFamily
            || MechrevoLite.Hardware.GcuEcExperimental.LocalFanCurve);
        bool supported = MechrevoLite.Hardware.Architecture.FeatureGate.ResolveSupportOrLegacy(
            MechrevoLite.Hardware.Architecture.FeatureId.FanCurve, _hw, legacyFanCurve);
        if (!supported)
        {
            HintText.Text = WpfStrings.FanCurveWindow_xaml_Text;
        }
        CpuCurve.Changed += MarkDirty;
        GpuCurve.Changed += MarkDirty;
        Closing += OnClosing;
        Closed += OnClosed;
        _activeModeRefreshTimer.Tick += (_, _) =>
        {
            _activeModeRefreshTimer.Stop();
            OnActiveModeChanged(force: true);
        };
        MechrevoLite.Mode.PerfModeService.Instance?.ActiveChanged += OnActiveModeChanged;
        MechrevoLite.Mode.PerfModeService.Instance?.Applied += OnAppliedMode;
        LoadFromHardware();
        // 首次打开时后端/布局可能还没完成；等消息队列空闲后再读一次，避免第一次显示旧/默认温度轴。
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_dirty && !CpuCurve.IsDragging && !GpuCurve.IsDragging)
            {
                LoadFromHardware();
            }
        }), DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        OpenEditors.RemoveAll(reference =>
            !reference.TryGetTarget(out FanCurveWindow? editor) || ReferenceEquals(editor, this));
        MechrevoLite.Mode.PerfModeService? perf = MechrevoLite.Mode.PerfModeService.Instance;
        if (perf != null)
        {
            perf.ActiveChanged -= OnActiveModeChanged;
            perf.Applied -= OnAppliedMode;
        }
        _activeModeRefreshTimer.Stop();
    }

    private void OnAppliedMode(MechrevoLite.Mode.PerfApplyOutcome outcome)
    {
        OnActiveModeChanged();
    }

    private void OnActiveModeChanged()
    {
        OnActiveModeChanged(force: false);
    }

    /// <summary>切换自定义/性能档后，已打开的风扇曲线窗口立即切到新档并重载曲线。</summary>
    private void OnActiveModeChanged(bool force)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnActiveModeChanged(force)));
            return;
        }
        string? id = MechrevoLite.Mode.PerfModeService.Instance?.DisplayedModeId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        SetModeId(id, force);
        // 档位切换的硬件回读/保存可能晚于 ActiveChanged 一瞬；再补一次强制刷新。
        _activeModeRefreshTimer.Stop();
        _activeModeRefreshTimer.Start();
    }

    internal void SetModeId(string modeId, bool force = false)
    {
        if (!force && string.Equals(_modeId, modeId, StringComparison.Ordinal))
        {
            return;
        }
        _modeId = modeId;
        LoadFromHardware();
    }

    internal void PrepareInline()
    {
        TitleText.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
        if (CpuSection.Children.Count > 0 && CpuSection.Children[0] is TextBlock cpuTitle)
        {
            cpuTitle.Visibility = Visibility.Collapsed;
        }
        if (GpuSection.Children.Count > 0 && GpuSection.Children[0] is TextBlock gpuTitle)
        {
            gpuTitle.Visibility = Visibility.Collapsed;
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_2;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }
        MessageBoxResult result = MessageBox.Show(this, WpfStrings.FanCurveWindow_xaml_MessageBoxShowthis, "L-Mechrevo",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void LoadFromHardware()
    {
        if (_hw == null)
        {
            return;
        }
        // 有未应用的拖动修改时，模式/激活事件不能把曲线重载回去，否则点位会回弹。
        if (_dirty)
        {
            return;
        }
        // 宽屏内联时模式事件可能在拖动过程中到达；拖动期间不重载，避免点位回弹。
        if (CpuCurve.IsDragging || GpuCurve.IsDragging)
        {
            return;
        }
        FanCurveLoadOutcome outcome = FanCurveLoadService.Load(BackendKind, _hw, _modeId);
        if (outcome.BaolongdaLoop.HasValue)
        {
            BaolongdaLoopCheck.IsChecked = outcome.BaolongdaLoop.Value;
        }
        if (outcome.BaolongdaSync.HasValue)
        {
            BaolongdaSyncCheck.IsChecked = outcome.BaolongdaSync.Value;
        }
        CpuCurve.Load(outcome.CpuTemps, outcome.CpuDuties);
        GpuCurve.Load(outcome.GpuTemps, outcome.GpuDuties);
        if (outcome.PersistIfMissing)
        {
            PersistHardwareCurveIfMissing(outcome.CpuSaved, outcome.GpuSaved);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_hw == null)
        {
            StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_3;
            return;
        }
        if (BackendKind == FanCurveBackendKind.NucUmi)
        {
            try
            {
                FanCurveSaveResult result = FanCurveSaveService.SaveNuc(_modeId,
                    CpuCurve.Duties, GpuCurve.Duties, CpuCurve.Temperatures, GpuCurve.Temperatures,
                    PushFanCurveCheck?.IsChecked != false);
                if (!result.PreserveDirty)
                {
                    _dirty = result.Dirty;
                }
                StatusText.Text = result.Detail;
            }
            catch (Exception ex)
            {
                StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
            }
            return;
        }
        if (BackendKind == FanCurveBackendKind.Baolongda)
        {
            try
            {
                FanCurveSaveResult result = FanCurveSaveService.SaveBaolongda(_hw, _modeId,
                    CpuCurve.Duties, GpuCurve.Duties, TempsToInts(CpuCurve.Temperatures),
                    TempsToInts(GpuCurve.Temperatures), PushFanCurveCheck?.IsChecked != false,
                    BaolongdaLoopCheck.IsChecked == true, BaolongdaSyncCheck.IsChecked == true);
                if (!result.PreserveDirty)
                {
                    _dirty = result.Dirty;
                }
                StatusText.Text = result.Detail;
            }
            catch (Exception ex)
            {
                StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
            }
            return;
        }
        if (_hw.WujieSmuApu)
        {
            try
            {
                _wujieController ??= new WujieFanEcController();
                FanCurveSaveResult result = FanCurveSaveService.SaveWujie(_wujieController, _modeId,
                    CpuCurve.Duties, GpuCurve.Duties, CpuCurve.Temperatures, GpuCurve.Temperatures,
                    WujieIndependentCheck?.IsChecked == true);
                if (!result.PreserveDirty)
                {
                    _dirty = result.Dirty;
                }
                StatusText.Text = result.Detail;
            }
            catch (Exception ex)
            {
                StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
            }
            return;
        }
        if (MechrevoLite.Hardware.GcuEcExperimental.LocalFanCurve)
        {
            try
            {
                FanCurveSaveResult result = FanCurveSaveService.SaveEc(_modeId,
                    CpuCurve.Duties, GpuCurve.Duties, CpuCurve.Temperatures, GpuCurve.Temperatures,
                    PushFanCurveCheck?.IsChecked != false);
                if (!result.PreserveDirty)
                {
                    _dirty = result.Dirty;
                }
                StatusText.Text = result.Detail;
            }
            catch (Exception ex)
            {
                StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
            }
            return;
        }
        if (!_hw.IsGcuConnected)
        {
            StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_3;
            return;
        }
        try
        {
            FanCurveSaveResult result = await FanCurveSaveService.SaveGcuAsync(_hw, _modeId,
                CpuCurve.Duties, GpuCurve.Duties, PushFanCurveCheck?.IsChecked != false);
            _dirty = result.Dirty;
            StatusText.Text = result.Detail;
        }
        catch (Exception ex)
        {
            StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
        }
    }

    private void PersistHardwareCurveIfMissing(int[]? cpuSaved, int[]? gpuSaved)
    {
        if (_hw == null || (!_modeId.StartsWith("custom", StringComparison.Ordinal)
            && !_modeId.StartsWith("fw", StringComparison.Ordinal)))
        {
            return;
        }
        if (cpuSaved is { Length: >= 2 } && gpuSaved is { Length: >= 2 })
        {
            return;
        }
        int[] cpu = CpuCurve.Duties.ToArray();
        int[] gpu = GpuCurve.Duties.ToArray();
        MechrevoLite.Mode.PerfModeSettings settings = MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId) with
        {
            CpuFanDuty = cpu,
            GpuFanDuty = gpu
        };
        MechrevoLite.Mode.PerfModeStore.SaveSettings(_modeId, settings);
    }

    private async void RestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_hw == null) return;
        MessageBoxResult confirm = MessageBox.Show(this,
            WpfStrings.L("将停止自定义风扇曲线、交还固件/EC 自动控制，并清除该档已保存的曲线点位。继续？", "This stops the custom fan curve, returns control to firmware/EC, and clears saved curve points for this mode. Continue?"),
            WpfStrings.L("恢复默认风扇曲线", "Restore default fan curve"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            FanCurveRestoreOutcome outcome = await L_Mechrevo.WpfP0.Services.FanCurveRestoreService
                .RestoreAsync(BackendKind, _hw);
            if (!outcome.Handled)
            {
                StatusText.Text = WpfStrings.L("本机不支持恢复默认曲线", "Restoring the default curve is not supported on this machine");
                return;
            }
            if (outcome.ClearSaved)
            {
                ClearSavedFanCurve();
            }
            if (outcome.CpuTemps != null && outcome.CpuDuties != null
                && outcome.GpuTemps != null && outcome.GpuDuties != null)
            {
                CpuCurve.Load(outcome.CpuTemps, outcome.CpuDuties);
                GpuCurve.Load(outcome.GpuTemps, outcome.GpuDuties);
            }
            else if (outcome.ReloadFromHardware)
            {
                LoadFromHardware();
            }
            _dirty = !outcome.Success && !outcome.ReloadFromHardware;
            StatusText.Text = outcome.Success
                ? outcome.Detail
                : WpfStrings.L("恢复默认失败：", "Restore default failed: ") + outcome.Detail;
        }
        catch (Exception ex)
        {
            StatusText.Text = WpfStrings.L("恢复默认失败：", "Restore default failed: ") + ex.Message;
            Logger.WriteLine("[fan] 恢复默认曲线失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    private void ClearSavedFanCurve()
    {
        try
        {
            PerfModeSettings settings = PerfModeStore.LoadSettings(_modeId);
            PerfModeStore.SaveSettings(_modeId, settings with { CpuFanDuty = null, GpuFanDuty = null });
        }
        catch
        {
        }
    }


    private static int[] TempsToInts(byte[] temps)
    {
        int[] ints = new int[16];
        for (int i = 0; i < ints.Length && i < temps.Length; i++)
        {
            ints[i] = temps[i];
        }
        return ints;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _wujieController?.Stop("窗口关闭");
        _wujieController?.Dispose();
        _wujieController = null;
        Close();
    }
}
