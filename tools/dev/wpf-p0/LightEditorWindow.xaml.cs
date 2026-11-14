using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MechrevoLite;
using MechrevoLite.Hardware;
using L_Mechrevo.WpfP0.Services;

using L_Mechrevo.WpfP0.Resources;
namespace L_Mechrevo.WpfP0;

public partial class LightEditorWindow : Window
{
    private readonly string _topic;
    private readonly (string Effect, string Name)[] _effects;
    private LightChannelSettings _settings;
    private int _light;
    private int _speed;
    private int _manualInterval = 5;
    private int _breathingIndex = 1;
    private int[] _manualIndexes = (int[])LightChannelSettings.DefaultManualIndexes.Clone();
    private int _colorArgb;
    private int _monochromeIndex = 4;
    private bool _usePresetColor;
    private int[] _mixColors = (int[])LightChannelSettings.DefaultMixColors.Clone();
    private bool _gradeBrightness;
    private readonly bool _keyboard;
    private bool _gradeColors;
    private bool _wujieWhite;
    private bool _logoGrade;
    /// <summary>⭐ 2026-10-26（task-17 · 宽屏白色背光入口）：白色背光键盘只有 电源 + 2 档亮度，没有灯效/颜色/速度 ✅。</summary>
    private readonly bool _whiteBacklightOnly;
    private readonly bool _nucSingleZone;
    /// <summary>EC 实验模式（GCU 离线）下的键盘编辑器：不走 GCU 服务，直接写 EC。</summary>
    private readonly bool _ecExperimentKeyboard;
    private readonly Func<string, string, int, int, int, bool, Task<bool>>? _applyIntent;

    /// <summary>NUC/UMI 原厂灯带没有速度寄存器，编辑器隐藏速度行；其它机型不受影响。</summary>
    private readonly bool _hideSpeedForNucLightbar;
    private int _lightMax = 4;
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _suppressAutoApply;
    private bool _applying;
    private bool _inline;
    private bool _pendingAutoApply;

    public LightEditorWindow(string topic, string title, (string Effect, string Name)[] effects, bool whiteBacklightOnly = false,
        Func<string, string, int, int, int, bool, Task<bool>>? applyIntent = null)
    {
        InitializeComponent();
        WelcomeAutoCheck.IsChecked = MechrevoLite.AppConfig.Is("welcome_auto");
        ManualResetButton.Content = WpfStrings.L("恢复默认六色", "Restore default six colors");
        _topic = topic;
        _effects = effects;
        _applyIntent = applyIntent;
        _whiteBacklightOnly = whiteBacklightOnly && string.Equals(topic, "Keyboard/Ctrl", StringComparison.Ordinal);
        _keyboard = string.Equals(topic, "Keyboard/Ctrl", StringComparison.Ordinal);
        var keyboardHw = L_Mechrevo.WpfP0.Services.WpfBackend.Hw;
        _ecExperimentKeyboard = _keyboard && L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdActive;
        _nucSingleZone = _keyboard
            && (((keyboardHw?.IsNucOnly == true)
                    && L_Mechrevo.WpfP0.Services.WpfBackend.rgb?.IsConnected != true
                    && L_Mechrevo.WpfP0.Services.WpfBackend.NucKbdIsSingleZone())
                // EC 实验模式的同方单区键盘与 NUC 单区同一套 EC 写法。
                || L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdIsSingleZone);
        _hideSpeedForNucLightbar = string.Equals(topic, "HidLightbar/Ctrl", StringComparison.Ordinal)
            && L_Mechrevo.WpfP0.Services.WpfBackend.Hw is { } lightbarHw
            && (lightbarHw.IsNucUmiFamily);
        Title = title;
        TitleText.Text = title;
        var hw = L_Mechrevo.WpfP0.Services.WpfBackend.Hw;
        bool gen40 = hw != null
            && !hw.UsesBaolongda
            && (hw.DgpuGeneration == MechrevoLite.Gpu.DgpuGenerationKind.Gen30
                || hw.DgpuGeneration == MechrevoLite.Gpu.DgpuGenerationKind.Gen40);
        _logoGrade = gen40 && string.Equals(topic, "HidLightbar_Logo/Ctrl", StringComparison.Ordinal);
        bool lightbarGrade = gen40 && string.Equals(topic, "HidLightbar/Ctrl", StringComparison.Ordinal);
        _gradeColors = _logoGrade || lightbarGrade;
        _gradeBrightness = _keyboard || _logoGrade;
        LightSlider.Minimum = 0;
        LightSlider.Maximum = _gradeBrightness ? 4 : 100;
        SpeedSlider.Minimum = _keyboard ? 1 : 0;
        SpeedSlider.Maximum = _keyboard ? 5 : 4;
        if (_keyboard)
        {
            bool bd = hw?.UsesBaolongda == true;
            if (bd)
            {
                try
                {
                    hw!.EnsureKeyboardLightingCalibration();
                }
                catch
                {
                }
            }
            // 无界目前只有白色背光键盘；GCU 的 Keyboard/Status 有时晚到/不报，
            // 不能因为 KeyboardStatusSeen=false 就把它当普通 RGB 键盘（滑块会落到 0..4）。
            _wujieWhite = hw?.WujieSmuApu == true && _keyboard;
            Logger.WriteLine("[light-editor] wujieWhite=" + _wujieWhite
                + " statusSeen=" + (hw?.KeyboardStatusSeen == true)
                + " solution=" + (hw?.KeyboardSolution ?? "")
                + " keyboard=" + _keyboard);
            // task-17 白色背光实际只有 2 档（暗/亮），不是 4 档；
            // 旧注释/旧范围写成 0..4 会让 UI 出现第 3/4 档但下发无效。
            if (_whiteBacklightOnly)
            {
                _lightMax = 2;
                LightSlider.Minimum = 1;
                LightSlider.Maximum = 2;
            }
            else
            {
                _lightMax = _wujieWhite ? 2 : (bd ? Math.Max(1, L_Mechrevo.WpfP0.Services.WpfBackend.KeyboardBrightnessMax()) : 4);
                LightSlider.Minimum = 0;
                LightSlider.Maximum = _lightMax;
            }
        }
        _settings = L_Mechrevo.WpfP0.Services.WpfBackend.LoadLightChannelSettings(topic, effects.Length > 0 ? effects[0].Effect : "Single");
        _light = Math.Clamp(_settings.Light, (int)LightSlider.Minimum, (int)LightSlider.Maximum);
        if (_nucSingleZone && L_Mechrevo.WpfP0.Services.WpfBackend.NucKbdTryReadBrightness(out int nucBrightness))
        {
            _light = nucBrightness;
        }
        if (_keyboard && !_wujieWhite && !_whiteBacklightOnly && !_ecExperimentKeyboard && hw != null)
        {
            // 编辑器以硬件当前亮度为准；否则 Fn 调过亮度后，编辑窗口仍显示旧的保存值。
            int actual = hw.KeyboardBrightness;
            if (actual >= 0 && actual <= _lightMax)
            {
                _light = actual;
            }
        }
        if (_keyboard && _wujieWhite)
        {
            _light = Math.Clamp(MechrevoLite.AppConfig.Get("wujie_kbd_level", 2), 1, 2);
            if (L_Mechrevo.WpfP0.Services.WpfBackend.EcKeyboardTryGet(out bool ecOn, out int ecLevel) && ecOn && ecLevel >= 1 && ecLevel <= 2)
            {
                _light = ecLevel;
            }
        }
        _speed = Math.Clamp(_settings.Speed, _keyboard ? 1 : 0, _keyboard ? 5 : 4);
        _monochromeIndex = Math.Clamp(_settings.MonochromeIndex, 1, 29);
        _usePresetColor = _settings.UsePresetColor;
        _colorArgb = _settings.EffectiveColorArgb();
        if (_settings.ManualIndexes is { Length: >= 6 } manual)
        {
            _manualIndexes = (int[])manual.Clone();
        }
        _manualInterval = Math.Clamp(_settings.Speed, 2, 10);
        _breathingIndex = Math.Clamp(_settings.BreathingIndex, 1, 4);
        LightChannelSettings mixSettings = L_Mechrevo.WpfP0.Services.WpfBackend.LoadLightChannelSettings(topic, "ColorMarquee");
        if (mixSettings.MixColors is { Length: 7 } mix)
        {
            _mixColors = (int[])mix.Clone();
        }
        PowerCheck.IsChecked = _settings.PowerOn;
        if (_ecExperimentKeyboard && L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdTryReadPower(out bool ecPowerOn))
        {
            // EC 实验模式没有 GCU 状态包，电源/白光亮度以 EC 回读为准。
            PowerCheck.IsChecked = ecPowerOn;
            if (_whiteBacklightOnly && L_Mechrevo.WpfP0.Services.WpfBackend.EcKeyboardTryGet(out _, out int whiteLevel) && whiteLevel >= 1 && whiteLevel <= 2)
            {
                _light = whiteLevel;
            }
        }
        if (_keyboard && _wujieWhite)
        {
            // 无界白键盘的实际电源状态以 EC 为准；GCU 的 power=False 可能只是状态没回读。
            // 如果 EC 已经是亮的，编辑器不能停在“电源关、滑块不可拖”。
            try
            {
                if (L_Mechrevo.WpfP0.Services.WpfBackend.EcKeyboardTryGet(out bool ecOn, out int ecLevel)
                    && (ecOn || ecLevel > 0))
                {
                    PowerCheck.IsChecked = true;
                }
            }
            catch
            {
            }
        }
        if (_keyboard)
        {
            HintText.Text = WpfStrings.LightEditorWindow_xaml_Text;
            StatusText.Text = WpfStrings.LightEditorWindow_xaml_Text_2;
        }
        _suppressAutoApply = true;
        NetSliderValues();
        LoadEffects();
        RefreshColorUi();
        BuildKeyboardExtra();
        if (_whiteBacklightOnly)
        {
            // ⭐⭐ task-17（宽屏白色背光入口）：白色背光键盘按能力只保留「电源 + 2 档亮度」，
            //    ⛔ 不显示不支持的 RGB 效果/颜色/速度/七色等控件 ✅
            HintText.Text = WpfStrings.LightWhiteKeyboardHint;
            EffectRow.Visibility = Visibility.Collapsed;
            ColorRow.Visibility = Visibility.Collapsed;
            MixRow.Visibility = Visibility.Collapsed;
            SpeedRow.Visibility = Visibility.Collapsed;
            KeyboardExtraRow.Visibility = Visibility.Collapsed;
        }
        if (_hideSpeedForNucLightbar)
        {
            // NUC/UMI 原厂只有单色/彩色，没有速度通道。
            SpeedRow.Visibility = Visibility.Collapsed;
        }
        _suppressAutoApply = false;
        PowerCheck.Checked += (_, _) =>
        {
            // 只在“关 → 开”的这一刻兜底：亮度为 0 时给 1 档。
            // 不能放在 ApplyCore 里反复执行，否则用户主动拉到 0 会被一直改回 1。
            if (_keyboard && _light <= 0 && LightSlider.Minimum <= 0)
            {
                _light = 1;
                LightSlider.Value = 1;
                LightText.Text = (_wujieWhite || _whiteBacklightOnly)
                    ? WpfStrings.LightLevelDim
                    : (_light + "/" + _lightMax);
            }
            UpdateEditorPowerUi();
            ScheduleAutoApply();
        };
        PowerCheck.Unchecked += (_, _) =>
        {
            UpdateEditorPowerUi();
            ScheduleAutoApply();
        };
        UpdateEditorPowerUi();
        _applyTimer.Tick += async (_, _) =>
        {
            _applyTimer.Stop();
            await ApplyCoreAsync(fromAuto: true);
        };
        Closed += (_, _) => _applyTimer.Stop();
    }

    /// <summary>宽屏卡内联：隐藏标题/提示/电源行，保留效果和完整二级菜单；控件变化直接自动应用。</summary>
    internal void PrepareWideInline()
    {
        _inline = true;
        TitleText.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        PowerRow.Visibility = Visibility.Collapsed;
        if (Content is FrameworkElement root)
        {
            root.Margin = new Thickness(0);
        }
    }

    internal bool PowerOn => PowerCheck.IsChecked == true;

    internal void SetPower(bool on)
    {
        PowerCheck.IsChecked = on;
    }

    private void ScheduleAutoApply()
    {
        if (_suppressAutoApply || (!IsLoaded && !_inline))
        {
            return;
        }
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void UpdateEditorPowerUi()
    {
        bool on = PowerCheck.IsChecked == true;
        // 白键盘只有开关 + 2 档亮度：即使电源开关暂时是关，也允许拖亮度，
        // 拖动时自动开灯（与 23.5.1 的可用性一致）。
        bool brightnessEnabled = on || (_keyboard && (_wujieWhite || _whiteBacklightOnly));
        LightSlider.IsEnabled = brightnessEnabled;
        SpeedSlider.IsEnabled = on;
        EffectRow.IsEnabled = on;
        ColorRow.IsEnabled = on;
        MixRow.IsEnabled = on;
        KeyboardExtraRow.IsEnabled = on;
        double dim = on ? 1.0 : 0.45;
        EffectRow.Opacity = dim;
        BrightnessRow.Opacity = brightnessEnabled ? 1.0 : 0.45;
        SpeedRow.Opacity = dim;
        ColorRow.Opacity = dim;
        MixRow.Opacity = dim;
        KeyboardExtraRow.Opacity = dim;
    }

    private void ManualReset_Click(object sender, RoutedEventArgs e)
    {
        _manualIndexes = (int[])LightChannelSettings.DefaultManualIndexes.Clone();
        try
        {
            LightChannelSettings settings = L_Mechrevo.WpfP0.Services.WpfBackend.LoadLightChannelSettings(_topic, SelectedEffect)
                with { ManualIndexes = (int[])_manualIndexes.Clone() };
            L_Mechrevo.WpfP0.Services.WpfBackend.SaveLightChannelSettings(_topic, settings);
        }
        catch
        {
        }
        RefreshKeyboardUi();
        ScheduleAutoApply();
    }

    private void ResetMix_Click(object sender, RoutedEventArgs e)
    {
        _mixColors = (int[])LightChannelSettings.DefaultMixColors.Clone();
        try
        {
            LightChannelSettings mixSettings = L_Mechrevo.WpfP0.Services.WpfBackend.LoadLightChannelSettings(_topic, "ColorMarquee");
            L_Mechrevo.WpfP0.Services.WpfBackend.SaveLightChannelSettings(_topic, mixSettings with { MixColors = (int[])_mixColors.Clone() });
        }
        catch
        {
        }
        RefreshColorUi();
        ScheduleAutoApply();
    }

    private void NetSliderValues()
    {
        if (_keyboard)
        {
            LightSlider.Value = _light;
            LightText.Text = (_wujieWhite || _whiteBacklightOnly) ? (_light >= 2 ? WpfStrings.LightLevelBright : WpfStrings.LightLevelDim) : (_light + "/" + _lightMax);
            SpeedSlider.Value = _speed;
            SpeedText.Text = _speed + "/5";
            return;
        }
        LightSlider.Value = _gradeBrightness ? _light : _light * 25;
        LightText.Text = ((int)LightSlider.Value).ToString();
        SpeedSlider.Value = _speed;
        SpeedText.Text = _speed.ToString();
    }

    private void LoadEffects()
    {
        EffectCombo.Items.Clear();
        string selected = _settings.Effect;
        int selectedIndex = 0;
        for (int i = 0; i < _effects.Length; i++)
        {
            EffectCombo.Items.Add(new ComboBoxItem { Content = _effects[i].Name, Tag = _effects[i].Effect });
            if (string.Equals(_effects[i].Effect, selected, StringComparison.Ordinal))
            {
                selectedIndex = i;
            }
        }
        EffectCombo.SelectedIndex = selectedIndex;
    }

    private string SelectedEffect =>
        EffectCombo.SelectedItem is ComboBoxItem item && item.Tag is string effect
            ? effect
            : (_effects.Length > 0 ? _effects[0].Effect : "Single");

    private void RefreshColorUi()
    {
        if (_whiteBacklightOnly)
        {
            // ⭐ task-17：白色背光只有开/关 + 2 档亮度，没有任何 RGB 颜色/效果行 ✅
            ColorRow.Visibility = Visibility.Collapsed;
            MixRow.Visibility = Visibility.Collapsed;
            SpeedRow.Visibility = Visibility.Collapsed;
            KeyboardExtraRow.Visibility = Visibility.Collapsed;
            return;
        }
        string effect = SelectedEffect;
        if (_keyboard)
        {
            MixRow.Visibility = Visibility.Collapsed;
            bool wujie = L_Mechrevo.WpfP0.Services.WpfBackend.Hw?.WujieSmuApu == true;
            bool keyboardManual = string.Equals(effect, "Manual", StringComparison.Ordinal);
            bool keyboardBreathing = string.Equals(effect, "Breathing", StringComparison.Ordinal);
            bool keyboardRainbow = string.Equals(effect, "Rainbow", StringComparison.Ordinal);
            bool firmwareSingleZone = L_Mechrevo.WpfP0.Services.WpfBackend.Hw != null
                && KeyboardFirmwareEffects.IsSingleZoneRgb(L_Mechrevo.WpfP0.Services.WpfBackend.Hw.Capabilities.KeyboardType);
            // EC 单区支持单色/呼吸颜色下发；NUC 固件单区的呼吸是硬件固定色，才隐藏颜色。
            bool colorVisible = _nucSingleZone
                ? string.Equals(effect, "Single", StringComparison.Ordinal)
                : (!wujie && !keyboardManual && !keyboardRainbow
                    && (!keyboardBreathing || !firmwareSingleZone)
                    && KeyboardFirmwareEffects.IsSingleColorEffect(effect));
            ColorRow.Visibility = colorVisible ? Visibility.Visible : Visibility.Collapsed;
            UpdateColorButton();
            RefreshKeyboardUi();
            return;
        }
        if (_gradeColors)
        {
            int mode = LightingCatalog.LightEffectColorMode(effect);
            ColorRow.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            MixRow.Visibility = mode == 7 ? Visibility.Visible : Visibility.Collapsed;
            if (mode == 7)
            {
                MixGrid.Children.Clear();
                for (int i = 0; i < 7; i++)
                {
                    System.Drawing.Color c = System.Drawing.Color.FromArgb(_mixColors[i]);
                    Button swatch = new()
                    {
                        Height = 26,
                        Margin = new Thickness(2),
                        Padding = new Thickness(0),
                        Tag = i,
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B)),
                        ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                    };
                    swatch.Click += Mix_Click;
                    MixGrid.Children.Add(swatch);
                }
            }
        }
        else
        {
            // 非 Gen40 普通灯带/Logo/铰链/同步：颜色按单色系效果判断，没有七色混色。
            ColorRow.Visibility = KeyboardFirmwareEffects.IsSingleColorEffect(effect)
                ? Visibility.Visible
                : Visibility.Collapsed;
            MixRow.Visibility = Visibility.Collapsed;
        }
        UpdateColorButton();
        RefreshKeyboardUi();
    }

    private void BuildKeyboardExtra()
    {
        if (!string.Equals(_topic, "Keyboard/Ctrl", StringComparison.Ordinal))
        {
            return;
        }
        ManualPaletteGrid.Children.Clear();
        for (int i = 0; i < 6; i++)
        {
            int index = i;
            System.Drawing.Color c = System.Drawing.Color.FromArgb(
                LightChannelSettings.ManualPalette[Math.Clamp(_manualIndexes[i], 1, 29) - 1]);
            Button button = new()
            {
                Height = 26,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Tag = index,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B)),
                ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            };
            button.Click += ManualColor_Click;
            ManualPaletteGrid.Children.Add(button);
        }
        BreathGrid.Children.Clear();
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            System.Drawing.Color c = System.Drawing.Color.FromArgb(LightChannelSettings.BreathingPalette[i]);
            Button button = new()
            {
                Height = 26,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Tag = index + 1,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B)),
                ToolTip = WpfStrings.LightEditorWindow_xaml_ToolTip + (index + 1)
            };
            button.Click += BreathColor_Click;
            BreathGrid.Children.Add(button);
        }
    }

    private void RefreshKeyboardUi()
    {
        if (KeyboardExtraRow == null)
        {
            return;
        }
        bool keyboard = string.Equals(_topic, "Keyboard/Ctrl", StringComparison.Ordinal);
        KeyboardExtraRow.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
        if (!keyboard)
        {
            return;
        }
        string effect = SelectedEffect;
        bool manual = string.Equals(effect, "Manual", StringComparison.Ordinal);
        bool breathing = string.Equals(effect, "Breathing", StringComparison.Ordinal)
            && (_nucSingleZone
                || L_Mechrevo.WpfP0.Services.WpfBackend.IsSingleZoneRgbKeyboard());
        // 单区只有轮播（Manual）有速度概念，单色/彩虹固定速度。
        SpeedRow.Visibility = _nucSingleZone && manual ? Visibility.Visible : Visibility.Collapsed;
        PerKeyPanel.Visibility = manual && !_nucSingleZone ? Visibility.Visible : Visibility.Collapsed;
        ManualPalettePanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        BreathPanel.Visibility = breathing ? Visibility.Visible : Visibility.Collapsed;
        if (manual && PerKeySlider.Value != _manualInterval)
        {
            PerKeySlider.Value = _manualInterval;
        }
        PerKeyText.Text = _manualInterval + "/10";
        for (int i = 0; i < ManualPaletteGrid.Children.Count; i++)
        {
            if (ManualPaletteGrid.Children[i] is Button button)
            {
                System.Drawing.Color c = System.Drawing.Color.FromArgb(
                    LightChannelSettings.ManualPalette[Math.Clamp(_manualIndexes[i], 1, 29) - 1]);
                button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
                button.ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }
        for (int i = 0; i < BreathGrid.Children.Count; i++)
        {
            if (BreathGrid.Children[i] is Button button)
            {
                button.BorderThickness = new Thickness(i + 1 == _breathingIndex ? 2 : 0);
            }
        }
    }

    private void PerKey_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _manualInterval = Math.Clamp((int)Math.Round(e.NewValue), 2, 10);
        if (PerKeyText != null)
        {
            PerKeyText.Text = _manualInterval + "/10";
        }
        ScheduleAutoApply();
    }

    private void BreathColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            _breathingIndex = Math.Clamp(index, 1, 4);
            RefreshKeyboardUi();
            ScheduleAutoApply();
        }
    }

    private ColorPickerWindow CreateColorPicker(System.Drawing.Color initial)
    {
        ColorPickerWindow picker = new(initial);
        // 宽屏内嵌时 this 是一个从未 Show 过的 Window，直接设 Owner 会抛
        // “无法将 Owner 设置为之前未显示的 Window”。给当前可见窗口当 Owner。
        Window? owner = null;
        if (IsVisible)
        {
            owner = this;
        }
        else if (Application.Current != null)
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (!ReferenceEquals(window, this) && window.IsVisible)
                {
                    owner = window;
                    break;
                }
            }
        }
        if (owner != null)
        {
            picker.Owner = owner;
        }
        return picker;
    }

    private void ManualColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index })
        {
            return;
        }
        System.Drawing.Color current = System.Drawing.Color.FromArgb(
            LightChannelSettings.ManualPalette[Math.Clamp(_manualIndexes[index], 1, 29) - 1]);
        ColorPickerWindow picker = CreateColorPicker(current);
        if (picker.ShowDialog() != true || picker.Result is not System.Drawing.Color picked)
        {
            return;
        }
        _manualIndexes[index] = NearestPaletteIndex(picked);
        RefreshKeyboardUi();
        ScheduleAutoApply();
    }

    private static int NearestPaletteIndex(System.Drawing.Color color)
    {
        int best = 1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < LightChannelSettings.ManualPalette.Length; i++)
        {
            System.Drawing.Color c = System.Drawing.Color.FromArgb(LightChannelSettings.ManualPalette[i]);
            int distance = (c.R - color.R) * (c.R - color.R) + (c.G - color.G) * (c.G - color.G) + (c.B - color.B) * (c.B - color.B);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i + 1;
            }
        }
        return best;
    }

    private void UpdateColorButton()
    {
        System.Drawing.Color c = System.Drawing.Color.FromArgb(_colorArgb);
        ColorButton.Content = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        ColorButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private void Effect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ecExperimentKeyboard && _nucSingleZone && SelectedEffect is not ("Off" or "None"))
        {
            // 切效果即开灯；否则电源关着时选效果会被当成关灯下发。
            if (PowerCheck.IsChecked != true)
            {
                PowerCheck.IsChecked = true;
            }
        }
        RefreshColorUi();
        ScheduleAutoApply();
    }

    private void Light_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_keyboard)
        {
            _light = Math.Clamp((int)Math.Round(e.NewValue), (int)LightSlider.Minimum, _lightMax);
            if ((_wujieWhite || _whiteBacklightOnly) && PowerCheck.IsChecked != true)
            {
                // 拉亮度视为开灯，避免只更新数值但下发时仍按 power=false 处理。
                PowerCheck.IsChecked = true;
            }
            if (LightText != null)
            {
                LightText.Text = (_wujieWhite || _whiteBacklightOnly) ? (_light >= 2 ? WpfStrings.LightLevelBright : WpfStrings.LightLevelDim) : (_light + "/" + _lightMax);
            }
            ScheduleAutoApply();
            return;
        }
        _light = _gradeBrightness
            ? Math.Clamp((int)Math.Round(e.NewValue), 0, 4)
            : Math.Clamp((int)Math.Round(e.NewValue * 4 / 100.0), 0, 4);
        if (LightText != null)
        {
            LightText.Text = ((int)e.NewValue).ToString();
        }
        ScheduleAutoApply();
    }

    private void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_keyboard)
        {
            _speed = Math.Clamp((int)Math.Round(e.NewValue), 1, 5);
            if (SpeedText != null)
            {
                SpeedText.Text = _speed + "/5";
            }
            ScheduleAutoApply();
            return;
        }
        _speed = Math.Clamp((int)Math.Round(e.NewValue), 0, 4);
        if (SpeedText != null)
        {
            SpeedText.Text = _speed.ToString();
        }
        ScheduleAutoApply();
    }

    private void Mix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } button || index < 0 || index >= _mixColors.Length)
        {
            return;
        }
        ColorPickerWindow picker = CreateColorPicker(System.Drawing.Color.FromArgb(_mixColors[index]));
        if (picker.ShowDialog() != true || picker.Result is not System.Drawing.Color picked)
        {
            return;
        }
        _mixColors[index] = picked.ToArgb();
        System.Drawing.Color c = picked;
        button.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
        button.ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        try
        {
            LightChannelSettings mixSettings = L_Mechrevo.WpfP0.Services.WpfBackend.LoadLightChannelSettings(_topic, "ColorMarquee");
            L_Mechrevo.WpfP0.Services.WpfBackend.SaveLightChannelSettings(_topic, mixSettings with { MixColors = (int[])_mixColors.Clone() });
        }
        catch
        {
        }
        ScheduleAutoApply();
    }

    private void WelcomeAuto_Changed(object sender, RoutedEventArgs e)
    {
        MechrevoLite.AppConfig.Set("welcome_auto", WelcomeAutoCheck.IsChecked == true ? 1 : 0);
        MechrevoLite.AppConfig.Flush();
    }

    private async void Welcome_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is OriginalStyleWindow owner)
        {
            await owner.TriggerWelcomeEffectAsync("迷你窗");
            StatusText.Text = "迎宾灯效已触发";
            return;
        }
        if (!await L_Mechrevo.WpfP0.Services.WpfBackend.TriggerWelcomeAsync("迷你窗"))
        {
            StatusText.Text = "迎宾灯效仅支持 GCU 键盘";
            return;
        }
        StatusText.Text = "迎宾灯效已触发";
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        ColorPickerWindow picker = CreateColorPicker(System.Drawing.Color.FromArgb(_colorArgb));
        if (picker.ShowDialog() == true && picker.Result is System.Drawing.Color picked)
        {
            _colorArgb = picked.ToArgb();
            _usePresetColor = picker.PresetIndex.HasValue;
            _monochromeIndex = picker.PresetIndex ?? 0;
            UpdateColorButton();
            ScheduleAutoApply();
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        _applyTimer.Stop();
        await ApplyCoreAsync(fromAuto: false);
    }

    private async Task<bool> ApplyCoreAsync(bool fromAuto)
    {
        if (_applying)
        {
            if (fromAuto)
            {
                _pendingAutoApply = true;
            }
            return false;
        }
        _applying = true;
        try
        {
            // 软件内调节键盘灯时不要再弹亮度 OSD；Fn 键的亮度 OSD 不受影响。
            if (_keyboard)
            {
                OriginalStyleWindow.SuppressKeyboardBrightnessOsd(3000);
            }
            // 单键 RGB 的 HID/固件写可能较慢；先让出一次 UI 线程，避免拖动时窗口被判无响应。
            await Task.Yield();
            string effect = SelectedEffect;
            bool power = PowerCheck.IsChecked == true;
            bool powerWasOn = _settings.PowerOn;
            bool manualKeyboard = string.Equals(_topic, "Keyboard/Ctrl", StringComparison.Ordinal)
                && string.Equals(effect, "Manual", StringComparison.Ordinal);
            int applySpeed = _keyboard ? (manualKeyboard ? _manualInterval : 0) : _speed;
            LightChannelSettings next = _settings with
            {
                Effect = effect,
                Light = (_wujieWhite || _whiteBacklightOnly) ? (_light >= 2 ? 4 : 2) : _light,
                Speed = manualKeyboard ? _manualInterval : (_keyboard ? _settings.Speed : _speed),
                ColorArgb = _colorArgb,
                UsePresetColor = _usePresetColor,
                MonochromeIndex = _monochromeIndex,
                MixColors = (int[])_mixColors.Clone(),
                ManualIndexes = (int[])_manualIndexes.Clone(),
                BreathingIndex = _breathingIndex,
                PowerOn = power
            };
            bool powerChanged = _settings.PowerOn != power;
            L_Mechrevo.WpfP0.Services.WpfBackend.SaveLightChannelSettings(_topic, next);
            if (powerChanged)
            {
                L_Mechrevo.WpfP0.Services.WpfBackend.SaveLightChannelPower(_topic, power, "用户编辑灯光电源");
            }
            _settings = next;
            bool ok = true;
            if (!_keyboard && _applyIntent != null)
            {
                bool applied = await _applyIntent(_topic, effect, _light, applySpeed, _colorArgb, power);
                if (!fromAuto || !applied)
                {
                    StatusText.Text = applied ? WpfStrings.LightSavedApplied : WpfStrings.LightSavedUnconfirmed;
                }
                return applied;
            }
            if (L_Mechrevo.WpfP0.Services.WpfBackend.Service == null && !_ecExperimentKeyboard)
            {
                if (!fromAuto)
                {
                    StatusText.Text = WpfStrings.LightEditorWindow_xaml_Text_3;
                }
                return false;
            }
            if (string.Equals(_topic, "Keyboard/Ctrl", StringComparison.Ordinal))
            {
                if (_ecExperimentKeyboard && L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdIsWhite)
                {
                    // EC 实验模式白光：只有电源 + 2 档亮度。
                    return L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdApplyWhite(power, _light >= 2 ? 2 : 1);
                }
                if (_ecExperimentKeyboard && (L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdIsHid || !L_Mechrevo.WpfP0.Services.WpfBackend.EcKbdControllable))
                {
                    // HID 单键/四分区由 HidKeyboardWindow 处理；这里不要再落 EC 白光。
                    return false;
                }
                if (_nucSingleZone)
                {
                    int[] cycleColors = new int[_manualIndexes.Length];
                    for (int i = 0; i < _manualIndexes.Length; i++)
                    {
                        int paletteIndex = Math.Clamp(_manualIndexes[i], 1, LightChannelSettings.ManualPalette.Length);
                        cycleColors[i] = LightChannelSettings.ManualPalette[paletteIndex - 1];
                    }
                    int nucSpeed = _nucSingleZone
                        ? Math.Clamp(_speed, 1, 5)
                        : (manualKeyboard
                            ? Math.Clamp((_manualInterval - 2) / 2, 1, 5)
                            : Math.Clamp(_speed, 1, 5));
                    int nucColor = string.Equals(effect, "Breathing", StringComparison.Ordinal)
                        ? _breathingIndex switch
                        {
                            1 => unchecked((int)0xFFFFFFFF),
                            2 => unchecked((int)0xFFFF0000),
                            3 => unchecked((int)0xFF00FF00),
                            4 => unchecked((int)0xFF0000FF),
                            _ => _colorArgb
                        }
                        : _colorArgb;
                    L_Mechrevo.WpfP0.Services.WpfBackend.NucKbdApply(effect, power, _light, nucSpeed, nucColor, cycleColors);
                    return true;
                }
                if (_applyIntent != null && !_wujieWhite && !_whiteBacklightOnly)
                {
                    bool applied = await _applyIntent("Keyboard/Ctrl", effect, _light, applySpeed, _colorArgb, power);
                    if (!fromAuto || !applied)
                    {
                        StatusText.Text = applied ? WpfStrings.LightSavedApplied : WpfStrings.LightSavedUnconfirmed;
                    }
                    return applied;
                }
                if (power)
                {
                    await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetKeyboardPower(true);
                    if (!powerWasOn)
                    {
                        await Task.Delay(300);
                    }
                    if (_wujieWhite)
                    {
                        ok = await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetKeyboardBrightnessPreservingEffect(_light >= 2 ? 4 : 2);
                        return ok;
                    }
                    if (_whiteBacklightOnly)
                    {
                        // task-17：白色背光只有 2 档亮度，硬件协议用 2/4 两档；
                        // 不下发效果/颜色。
                        ok = await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetKeyboardBrightnessPreservingEffect(_light >= 2 ? 4 : 2);
                        return ok;
                    }
                    bool baolongda = L_Mechrevo.WpfP0.Services.WpfBackend.Hw?.UsesBaolongda == true;
                    System.Drawing.Color? color = baolongda
                        ? System.Drawing.Color.FromArgb(L_Mechrevo.WpfP0.Services.WpfBackend.Hw != null ? _settings.EffectiveColorArgb() : _colorArgb)
                        : (string.Equals(effect, "Single", StringComparison.Ordinal)
                            ? System.Drawing.Color.FromArgb(_colorArgb)
                            : null);
                    ok = await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetKeyboardEffect(effect, _light, applySpeed, "None", color, save: true);
                    if (ok)
                    {
                        ok = await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetKeyboardBrightnessPreservingEffect(_light);
                    }
                }
                else
                {
                    ok = await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetKeyboardPower(false);
                }
            }
            else if (power)
            {
                bool nucLightbar = string.Equals(_topic, "HidLightbar/Ctrl", StringComparison.Ordinal)
                    && L_Mechrevo.WpfP0.Services.WpfBackend.Hw is { } lbHw
                    && (lbHw.IsNucUmiFamily);
                if (nucLightbar)
                {
                    // NUC/UMI 灯带走本地 ACPI 通道；XAML 编辑器原来走 GCU service，
                    // 彩虹/混色的亮度和颜色根本没下发。
                    L_Mechrevo.WpfP0.Services.WpfBackend.NucLightbarSetPower(true);
                    if (!powerWasOn)
                    {
                        await Task.Delay(600);
                    }
                    System.Drawing.Color nucColor = System.Drawing.Color.FromArgb(_colorArgb);
                    ok = L_Mechrevo.WpfP0.Services.WpfBackend.NucLightbarSetEffect(effect, nucColor, _light);
                }
                else
                {
                    await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetLightPower(_topic, true);
                    if (!powerWasOn)
                    {
                        await Task.Delay(600);
                    }
                    System.Drawing.Color? color = KeyboardFirmwareEffects.IsSingleColorEffect(effect)
                        ? System.Drawing.Color.FromArgb(_colorArgb)
                        : null;
                    ok = await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetLightEffect(_topic, effect, _light, _speed, "None", color);
                }
            }
            else
            {
                bool nucLightbarOff = string.Equals(_topic, "HidLightbar/Ctrl", StringComparison.Ordinal)
                    && L_Mechrevo.WpfP0.Services.WpfBackend.Hw is { } lbHwOff
                    && (lbHwOff.IsNucUmiFamily);
                ok = nucLightbarOff
                    ? L_Mechrevo.WpfP0.Services.WpfBackend.NucLightbarSetPower(false)
                    : await L_Mechrevo.WpfP0.Services.WpfBackend.Service.SetLightPower(_topic, false);
            }
            if (ok)
            {
                if (powerChanged)
                {
                    L_Mechrevo.WpfP0.OsdService.Show(OriginalStyleWindow.LightChannelGlyph(_topic),
                        OriginalStyleWindow.LightChannelTitle(_topic), power ? WpfStrings.StateOn : WpfStrings.StateOff);
                }
            }
            if (!fromAuto || !ok)
            {
                StatusText.Text = ok ? WpfStrings.LightSavedApplied : WpfStrings.LightSavedUnconfirmed;
            }
            return ok;
        }
        catch (Exception ex)
        {
            if (!fromAuto)
            {
                StatusText.Text = WpfStrings.CpuTuningWindow_xaml_Text_6 + ex.Message;
            }
            return false;
        }
        finally
        {
            _applying = false;
            if (_pendingAutoApply)
            {
                _pendingAutoApply = false;
                _applyTimer.Start();
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
