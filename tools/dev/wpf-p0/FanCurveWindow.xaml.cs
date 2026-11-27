using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MechrevoLite;
using MechrevoLite.Mode;

using L_Mechrevo.WpfP0.Resources;
using L_Mechrevo.WpfP0.Services;
namespace L_Mechrevo.WpfP0;

public partial class FanCurveWindow : Window
{
    private string _modeId;
    private static FanCurveMold Mold => FanCurveTarget.Mold;
    private bool _dirty;
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
        MechrevoLite.Mode.PerfModeSettings saved = MechrevoLite.Mode.ModeSettings.Load(_modeId);
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
            PushFanCurveCheck.IsChecked = MechrevoLite.Mode.ModeSettings.Load(modeId).FanCurveOn ?? true;
            // 风扇独立控制由固件开关决定（回读确认），与曲线内容无关。
            // 宽屏把 CPU/GPU 拆成两份实例，开关只在 CPU 那份里出现。
            MechrevoLite.Hardware.MechrevoHw? gcu = FanCurveTarget.Hw;
            bool gcuPath = FanCurveTarget.Kind == MechrevoLite.Hardware.FanCurve.FanCurveBackendKind.Gcu;
            _hasRespectiveSwitch = gcuPath && gcu != null && gcu.IsGcuConnected;
            _respectiveAvailable = _hasRespectiveSwitch && showCpu;
            GcuRespectiveCheck.Visibility = _respectiveAvailable ? Visibility.Visible : Visibility.Collapsed;
            GcuRespectiveCheck.IsChecked = gcu != null && gcu.FanRespective;
            Logger.WriteLine("[fan] 独立控制勾选框：后端=" + FanCurveTarget.Kind + " · GCU连接=" + (gcu?.IsGcuConnected == true)
                + " · 固件上报该字段=" + (gcu?.SupportsFanRespective == true) + " · 当前值=" + (gcu?.FanRespective == true)
                + " ⇒ 显示=" + _respectiveAvailable);
        }
        catch
        {
        }
        CpuSection.Visibility = showCpu ? Visibility.Visible : Visibility.Collapsed;
        GpuSection.Visibility = showGpu ? Visibility.Visible : Visibility.Collapsed;
        // 无界只有一路温度输入：GPU 表不再展示，也不再作为独立曲线使用。
        if (FanCurveTarget.IsWujieSmuApu)
        {
            GpuSection.Visibility = Visibility.Collapsed;
        }
        // 声明表说本机只有一路 PWM（fanCurve.params.gpuCurve=0）时同样不展示 GPU 曲线：
        // 那种机型第二个风扇由同一个 PWM 表驱动，编辑 GPU 表既无通道也不会有任何效果。
        if (FanCurveTarget.Hw?.SupportsGpuFanCurve == false || MechrevoLite.Hardware.EcFanFivePoint.InUse)
        {
            GpuSection.Visibility = Visibility.Collapsed;
        }
        ApplyRespectiveVisual(GcuRespectiveCheck.IsChecked == true);
        RespectiveStateChanged += OnRespectiveStateChanged;
        if (Mold == FanCurveMold.Baolongda)
        {
            BaolongdaLoopCheck.Visibility = Visibility.Visible;
            BaolongdaSyncCheck.Visibility = Visibility.Visible;
        }
        bool legacyFanCurve = FanCurveTarget.LegacyFanCurve;
        bool supported = L_Mechrevo.WpfP0.Services.WpfBackend.ResolveFanCurveSupport(legacyFanCurve);
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
        Loaded += async (_, _) => await TryEnableRespectiveOnOpenAsync();
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
        RespectiveStateChanged -= OnRespectiveStateChanged;
        L_Mechrevo.WpfP0.Services.FanCurveSaveService.StopWujie(this, "窗口关闭");
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
        if (!FanCurveTarget.Available)
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
        FanCurveLoadOutcome outcome = FanCurveLoadService.Load(_modeId);
        if (outcome.BaolongdaLoop.HasValue)
        {
            BaolongdaLoopCheck.IsChecked = outcome.BaolongdaLoop.Value;
        }
        if (outcome.BaolongdaSync.HasValue)
        {
            BaolongdaSyncCheck.IsChecked = outcome.BaolongdaSync.Value;
        }
        // 五点机型：温度轴由固件固定，只显示与编辑 5 个占空比。
        if (MechrevoLite.Hardware.EcFanFivePoint.InUse)
        {
            if (MechrevoLite.Hardware.EcFanFivePoint.TryRead(out int[] fiveDuties))
            {
                CpuCurve.LoadPoints(MechrevoLite.Hardware.EcFanFivePoint.Count, fiveDuties);
            }
            return;
        }
        CpuCurve.Load(outcome.CpuTemps, outcome.CpuDuties);
        GpuCurve.Load(outcome.GpuTemps, outcome.GpuDuties);
        if (outcome.PersistIfMissing)
        {
            PersistHardwareCurveIfMissing(outcome.CpuSaved, outcome.GpuSaved);
        }
    }

    private bool _syncingRespective;
    private bool _hasRespectiveSwitch;
    private bool _respectiveAvailable;
    private bool _respectiveAutoTried;

    /// <summary>宽屏下 CPU/GPU 是两份实例，开关状态变化要同步到另一份。</summary>
    internal static event Action<bool>? RespectiveStateChanged;

    private void OnRespectiveStateChanged(bool on)
    {
        // CPU / GPU 拆成两份内嵌实例时，窗口本体没有被 Show 过，IsLoaded 恒为 false：
        // 不能因为没装载就丢掉同步，否则 GPU 那份会一直停在置灰状态。
        Dispatcher.BeginInvoke(new Action(() => ApplyRespectiveState(on)));
    }

    /// <summary>当前独立控制开关状态（CPU 实例是权威来源）。</summary>
    internal bool RespectiveOn => GcuRespectiveCheck.IsChecked == true;

    /// <summary>按给定状态同步开关与 GPU 表可编辑性，不访问固件。</summary>
    internal void ApplyRespectiveState(bool on)
    {
        _syncingRespective = true;
        GcuRespectiveCheck.IsChecked = on;
        _syncingRespective = false;
        ApplyRespectiveVisual(on);
    }

    /// <summary>独立控制关闭时 GPU 曲线不参与输出，置灰只读。</summary>
    private void ApplyRespectiveVisual(bool on)
    {
        bool editable = !_hasRespectiveSwitch || on;
        GpuCurve.IsEnabled = editable;
        GpuCurve.Opacity = editable ? 1.0 : 0.45;
    }

    private async Task TryEnableRespectiveOnOpenAsync()
    {
        if (_respectiveAutoTried || !_respectiveAvailable)
        {
            return;
        }
        _respectiveAutoTried = true;
        MechrevoLite.Hardware.MechrevoHw? hw = FanCurveTarget.Hw;
        MechrevoLite.Hardware.MechrevoService? service = WpfBackend.Service;
        if (hw == null || service == null || hw.FanRespective)
        {
            return;
        }
        _syncingRespective = true;
        GcuRespectiveCheck.IsChecked = true;
        _syncingRespective = false;
        ApplyRespectiveVisual(true);
        bool ok = await service.SwitchFanRespective(true);
        if (!ok)
        {
            _syncingRespective = true;
            GcuRespectiveCheck.IsChecked = hw.FanRespective;
            _syncingRespective = false;
            ApplyRespectiveVisual(hw.FanRespective);
            StatusText.Text = "开启独立控制未生效（回读与请求不符）";
            return;
        }
        StatusText.Text = "已自动开启 CPU/GPU 独立控制";
        RespectiveStateChanged?.Invoke(true);
    }

    private async void GcuRespective_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingRespective || !IsLoaded)
        {
            return;
        }
        MechrevoLite.Hardware.MechrevoHw? hw = FanCurveTarget.Hw;
        MechrevoLite.Hardware.MechrevoService? service = WpfBackend.Service;
        if (hw == null || service == null)
        {
            return;
        }
        bool requested = GcuRespectiveCheck.IsChecked == true;
        GcuRespectiveCheck.IsEnabled = false;
        try
        {
            bool ok = await service.SwitchFanRespective(requested);
            if (!ok)
            {
                _syncingRespective = true;
                GcuRespectiveCheck.IsChecked = hw.FanRespective;
                _syncingRespective = false;
            }
            StatusText.Text = ok
                ? (requested ? "已开启 CPU/GPU 独立控制" : "已关闭 CPU/GPU 独立控制")
                : "切换独立控制未生效（回读与请求不符）";
            if (ok)
            {
                ApplyRespectiveVisual(requested);
                RespectiveStateChanged?.Invoke(requested);
            }
        }
        catch (Exception ex)
        {
            _syncingRespective = true;
            GcuRespectiveCheck.IsChecked = hw.FanRespective;
            _syncingRespective = false;
            StatusText.Text = "切换独立控制失败：" + ex.Message;
        }
        finally
        {
            GcuRespectiveCheck.IsEnabled = true;
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!FanCurveTarget.Available)
        {
            StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_3;
            return;
        }
        if (Mold == FanCurveMold.NucUmi)
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
        if (Mold == FanCurveMold.Baolongda)
        {
            try
            {
                FanCurveSaveResult result = FanCurveSaveService.SaveBaolongda(_modeId,
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
        if (FanCurveTarget.IsWujieSmuApu)
        {
            try
            {
                FanCurveSaveResult result = FanCurveSaveService.SaveWujie(this, _modeId,
                    CpuCurve.Duties, GpuCurve.Duties, CpuCurve.Temperatures, GpuCurve.Temperatures,
                    false);
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
        if (Mold == FanCurveMold.Ec)
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
        if (!FanCurveTarget.IsGcuConnected)
        {
            StatusText.Text = WpfStrings.FanCurveWindow_xaml_Text_3;
            return;
        }
        try
        {
            FanCurveSaveResult result = await FanCurveSaveService.SaveGcuAsync(_modeId,
                CpuCurve.Duties, GpuCurve.Duties, PushFanCurveCheck?.IsChecked != false,
                CpuCurve.Temperatures, GpuCurve.Temperatures);
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
        if (!FanCurveTarget.Available)
        {
            return;
        }
        FanCurveSaveService.PersistDutiesIfMissing(_modeId, cpuSaved, gpuSaved, CpuCurve.Duties, GpuCurve.Duties);
    }

    private async void RestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        if (!FanCurveTarget.Available) return;
        // EC 直连/无界：只把编辑器换成内置默认曲线，不擅自改写 EC 表；下发由「应用」执行。
        // 无界与同方同一套 16 点表，默认曲线同样是本机的表，交还固件等于丢掉曲线。
        if (Mold == FanCurveMold.Ec || Mold == FanCurveMold.NucUmi || Mold == FanCurveMold.Wujie)
        {
            // 无界的默认轴从 45℃ 起（控制器 45℃ 以下是交还区间），其余后端用同一套 16 点表。
            bool wujie = Mold == FanCurveMold.Wujie;
            CpuCurve.Load(wujie ? FanCurveDefaultView.WujieCpuTemperatures : FanCurveDefaultView.CpuTemperatures,
                wujie ? FanCurveDefaultView.WujieCpuDutyBytes : FanCurveDefaultView.CpuDutyBytes);
            GpuCurve.Load(wujie ? FanCurveDefaultView.WujieGpuTemperatures : FanCurveDefaultView.GpuTemperatures,
                wujie ? FanCurveDefaultView.WujieGpuDutyBytes : FanCurveDefaultView.GpuDutyBytes);
            _dirty = true;
            StatusText.Text = WpfStrings.L("已载入默认曲线（未下发，点「应用」写入 EC）", "Default curve loaded (not sent; press Apply to write to EC)");
            return;
        }
        MessageBoxResult confirm = MessageBox.Show(this,
            WpfStrings.L("将停止自定义风扇曲线、交还固件/EC 自动控制，并清除该档已保存的曲线点位。继续？", "This stops the custom fan curve, returns control to firmware/EC, and clears saved curve points for this mode. Continue?"),
            WpfStrings.L("恢复默认风扇曲线", "Restore default fan curve"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            FanCurveRestoreOutcome outcome = await L_Mechrevo.WpfP0.Services.FanCurveRestoreService
                .RestoreAsync();
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
        FanCurveSaveService.ClearSavedDuties(_modeId);
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
        Close();
    }
}
