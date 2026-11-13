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
                int[] cpu = CpuCurve.Duties;
                int[] gpu = GpuCurve.Duties;
                if (PushFanCurveCheck?.IsChecked == false)
                {
                    PerfModeService.Instance?.RecordFanCurves(_modeId, cpu, gpu);
                    PerfModeService.Instance?.RecordFanCurveEnabled(_modeId, false);
                    _dirty = false;
                    StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_4;
                    return;
                }
                bool ok = MechrevoLite.Hardware.NucFanTable.TryApply(
                    CpuCurve.Temperatures, cpu, GpuCurve.Temperatures, gpu, out string detail);
                bool rampOk = MechrevoLite.Hardware.NucFanTable.TrySetFanRamp(true, 300, out string rampDetail);
                bool isolatedOk = MechrevoLite.Hardware.NucFanTable.TrySetFanIsolated(true, out string isolatedDetail);
                ok = ok && rampOk && isolatedOk;
                detail += " " + rampDetail + " " + isolatedDetail;
                Logger.WriteLine("[nuc-fan] 自定义风扇应用 ok=" + ok + " detail=" + detail);
                PerfModeService.Instance?.RecordFanCurves(_modeId, cpu, gpu);
                PerfModeService.Instance?.RecordFanCurveEnabled(_modeId, true);
                _dirty = false;
                StatusText.Text = ok ? WpfStrings.L("NUC/UMI 风扇曲线已写入：", "NUC/UMI fan curve written: ") + detail : WpfStrings.L("NUC/UMI 风扇曲线写入失败：", "Failed to write NUC/UMI fan curve: ") + detail;
            }
            catch (Exception ex)
            {
                StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
            }
            return;
        }
        if (BackendKind == FanCurveBackendKind.Baolongda)
        {
            // 强冷期间曲线存档被临时标成 disabled；此时保存会结束强冷并覆盖快照，直接拒绝。
            if (MechrevoLite.Hardware.MechrevoHw.FanBoostActive)
            {
                StatusText.Text = WpfStrings.L("强冷进行中，请先关闭强冷再保存曲线",
                    "Fan boost is active; turn it off before saving the fan curve");
                return;
            }
            try
            {
                int[] cpu = CpuCurve.Duties.Take(BaolongdaFanCurveSettings.PointCount).ToArray();
                int[] gpu = GpuCurve.Duties.Take(BaolongdaFanCurveSettings.PointCount).ToArray();
                int maxRpm = Math.Max(1, _hw.FanRpmMaxForPercent);
                bool pushCurve = PushFanCurveCheck?.IsChecked != false;
                bool loopOn = BaolongdaLoopCheck.IsChecked == true;
                bool previousEnabled = _hw.LoadBaolongdaFanCurve(_modeId).Enabled;
                BaolongdaFanCurveSettings curve = new()
                {
                    // 关闭“随档位下发”时只保存点位，不破坏已有闭环启用状态。
                    Enabled = pushCurve ? loopOn : previousEnabled,
                    FormatVersion = BaolongdaFanCurveSettings.CurrentFormatVersion,
                    SyncEnabled = BaolongdaSyncCheck.IsChecked == true,
                    HysteresisC = FanCurveEngine.DefaultHysteresisC,
                    IntervalMs = BaolongdaFanCurveSettings.DefaultIntervalMs,
                    MinRpm = BaolongdaFanCurveSettings.DefaultMinRpm,
                    MaxRpm = maxRpm,
                    Channel1Temperatures = TempsToInts(CpuCurve.Temperatures).Take(BaolongdaFanCurveSettings.PointCount).ToArray(),
                    Channel1Rpms = PercentToRpms(cpu, maxRpm),
                    Channel0Temperatures = TempsToInts(GpuCurve.Temperatures).Take(BaolongdaFanCurveSettings.PointCount).ToArray(),
                    Channel0Rpms = PercentToRpms(gpu, maxRpm)
                };
                bool ok = _hw.SaveBaolongdaFanCurve(_modeId, curve);
                if (ok)
                {
                    PerfModeService.Instance?.RecordFanCurves(_modeId, cpu, gpu);
                    MechrevoLite.Mode.PerfModeStore.SaveSettings(_modeId,
                        MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId) with { FanCurveOn = pushCurve });
                }
                _dirty = false;
                string? activeModeId = PerfModeService.Instance?.ActiveModeId;
                // 与服务侧 IsFanCurveModeActive 一致：启动期 ActiveModeId 为空时按活动档处理。
                bool activeMode = string.IsNullOrWhiteSpace(activeModeId)
                    || string.Equals(activeModeId, _modeId, StringComparison.Ordinal);
                string okText = !pushCurve
                    ? WpfStrings.L("宝龙达风扇曲线已保存（未随档位下发）", "Baolongda fan curve saved (not pushed with profile)")
                    : (activeMode
                        ? WpfStrings.L("宝龙达风扇曲线已保存并应用", "Baolongda fan curve saved and applied")
                        : WpfStrings.L("宝龙达风扇曲线已保存，切到该档后应用", "Baolongda fan curve saved; applies when this profile is active"));
                StatusText.Text = ok ? okText : WpfStrings.L("宝龙达风扇曲线保存失败", "Failed to save Baolongda fan curve");
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
                int[] cpu = CpuCurve.Duties;
                int[] gpu = GpuCurve.Duties;
                _wujieController ??= new WujieFanEcController();
                byte[] cpuTemps = CpuCurve.Temperatures;
                byte[] gpuTemps = GpuCurve.Temperatures;
                bool independent = WujieIndependentCheck?.IsChecked == true;
                try
                {
                    MechrevoLite.AppConfig.Set("wujie_fan_independent", independent ? 1 : 0);
                }
                catch
                {
                }
                bool ok = _wujieController.Start(cpuTemps, cpu, independent, gpuTemps, gpu, out string detail);
                PerfModeService.Instance?.RecordFanCurves(_modeId, cpu, gpu);
                SaveWujieTemps(cpuTemps, gpuTemps);
                _dirty = false;
                StatusText.Text = ok ? WpfStrings.L("无界 EC 曲线已启动：", "Wujie EC curve started: ") + detail : WpfStrings.L("无界 EC 曲线启动失败：", "Failed to start Wujie EC curve: ") + detail;
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
                int[] cpu = CpuCurve.Duties;
                int[] gpu = GpuCurve.Duties;
                bool push = PushFanCurveCheck?.IsChecked != false;
                if (!push)
                {
                    MechrevoLite.Mode.PerfModeStore.SaveSettings(_modeId, MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId) with
                    {
                        CpuFanDuty = cpu,
                        GpuFanDuty = gpu,
                        FanCurveOn = false
                    });
                    _dirty = false;
                    StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_4;
                    return;
                }
                bool ok = MechrevoLite.Hardware.NucFanTable.TryApply(
                    CpuCurve.Temperatures, cpu, GpuCurve.Temperatures, gpu, out string detail);
                bool isolated = MechrevoLite.Hardware.NucFanTable.TrySetFanIsolated(true, out string isoDetail);
                MechrevoLite.Mode.PerfModeSettings savedSettings = MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId);
                bool ramp = MechrevoLite.Hardware.NucFanTable.TrySetFanRamp(
                    savedSettings.FanSwitchSpeedOn == true,
                    savedSettings.FanSwitchSpeedMs ?? 300, out string rampDetail);
                MechrevoLite.Mode.PerfModeStore.SaveSettings(_modeId, MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId) with
                {
                    CpuFanDuty = cpu,
                    GpuFanDuty = gpu,
                    FanCurveOn = true
                });
                _dirty = false;
                ok = ok && isolated && ramp;
                detail += " " + isoDetail + " " + rampDetail;
                StatusText.Text = ok
                    ? WpfStrings.L("风扇曲线已写入 EC：", "Fan curve written to EC: ") + detail
                    : WpfStrings.L("风扇曲线写入失败：", "Failed to write fan curve: ") + detail;
                Logger.WriteError("[gcu-ec] 自定义风扇应用 ok=" + ok + " " + detail);
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
            int[] cpu = CpuCurve.Duties;
            int[] gpu = GpuCurve.Duties;
            bool push = PushFanCurveCheck?.IsChecked != false;
            if (!push)
            {
                PerfModeService.Instance?.RecordFanCurves(_modeId, cpu, gpu);
                PerfModeService.Instance?.RecordFanCurveEnabled(_modeId, false);
                _dirty = false;
                StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_4;
                return;
            }
            PerfModeService.Instance?.RecordFanCurveEnabled(_modeId, true);
            await _hw.SetFanCurve(0, cpu).ConfigureAwait(true);
            await _hw.SetFanCurve(1, gpu).ConfigureAwait(true);
            await _hw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" }).ConfigureAwait(true);
            await Task.Delay(700).ConfigureAwait(true);
            bool confirmed = CurveMatches(cpu, _hw.CpuCurveDuty, _hw.CpuCurveUpT)
                && CurveMatches(gpu, _hw.GpuCurveDuty, _hw.GpuCurveUpT);
            if (confirmed)
            {
                if (_modeId.StartsWith("fw", StringComparison.Ordinal)
                    || _modeId.StartsWith("custom", StringComparison.Ordinal))
                {
                    MechrevoLite.Mode.PerfModeSettings settings = MechrevoLite.Mode.PerfModeStore.LoadSettings(_modeId) with
                    {
                        CpuFanDuty = cpu,
                        GpuFanDuty = gpu,
                        FanCurveOn = PushFanCurveCheck?.IsChecked != false
                    };
                    MechrevoLite.Mode.PerfModeStore.SaveSettings(_modeId, settings);
                }
                else
                {
                    PerfModeService.Instance?.RecordFanCurves(_modeId, cpu, gpu);
                    PerfModeService.Instance?.RecordFanCurveEnabled(_modeId, true);
                }
                _dirty = false;
                StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_5;
            }
            else
            {
                StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_6;
            }
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

    private static bool CurveMatches(int[] request, byte[] actual, byte[] temps)
    {
        int valid = Array.IndexOf(temps, (byte)255);
        valid = valid < 2 ? Math.Min(11, Math.Max(2, request.Length)) : valid;
        for (int i = 0; i < valid; i++)
        {
            if (i < actual.Length && actual[i] != 255 && Math.Clamp((int)actual[i], 0, 100) != request[i])
            {
                return false;
            }
        }
        return true;
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


    private void SaveWujieTemps(byte[] cpuTemps, byte[] gpuTemps)
    {
        try
        {
            string key = string.IsNullOrWhiteSpace(_modeId) ? "active" : _modeId;
            MechrevoLite.AppConfig.Set("wujie_fan_cputemps_" + key, string.Join(",", cpuTemps));
            MechrevoLite.AppConfig.Set("wujie_fan_gputemps_" + key, string.Join(",", gpuTemps));
        }
        catch
        {
        }
    }

    /// <summary>表异常/错乱时回退默认 16 点轴：温度必须 0..120 或尾部 255，占空比 0..100 且单调不减。</summary>
    private static bool FanTableLooksSane(byte[] temps, byte[] duties)
    {
        if (temps == null || duties == null || temps.Length < 16 || duties.Length < 16)
        {
            return false;
        }
        bool ended = false;
        int previousTemp = -1;
        int previousDuty = -1;
        bool hasActive = false;
        for (int i = 0; i < 16; i++)
        {
            int temp = temps[i];
            if (temp == 255)
            {
                ended = true;
                continue;
            }
            if (ended || temp < 0 || temp > 120 || temp < previousTemp)
            {
                return false;
            }
            previousTemp = temp;
            int duty = duties[i];
            if (duty < 0 || duty > 100 || duty < previousDuty)
            {
                return false;
            }
            previousDuty = duty;
            if (i > 0 && temp > 0 && duty > 0)
            {
                hasActive = true;
            }
        }
        return hasActive;
    }

    private static byte[] TempsToBytes(int[] temps)
    {
        byte[] bytes = new byte[16];
        for (int i = 0; i < bytes.Length && i < temps.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp(temps[i], 0, 255);
        }
        return bytes;
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

    private static byte[] RpmsToPercent(int[] rpms, int maxRpm)
    {
        byte[] duties = new byte[16];
        int max = Math.Max(1, maxRpm);
        for (int i = 0; i < duties.Length && i < rpms.Length; i++)
        {
            duties[i] = (byte)Math.Clamp((int)Math.Round(rpms[i] * 100.0 / max), 0, 100);
        }
        return duties;
    }

    private static int[] PercentToRpms(int[] duties, int maxRpm)
    {
        int[] rpms = new int[16];
        for (int i = 0; i < rpms.Length && i < duties.Length; i++)
        {
            rpms[i] = (int)Math.Round(duties[i] * maxRpm / 100.0);
        }
        return rpms;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _wujieController?.Stop("窗口关闭");
        _wujieController?.Dispose();
        _wujieController = null;
        Close();
    }
}
