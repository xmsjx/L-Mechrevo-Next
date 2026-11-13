using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.Architecture;
using L_Mechrevo.WpfP0.Services;

using L_Mechrevo.WpfP0.Resources;
namespace L_Mechrevo.WpfP0.Services;

/// <summary>⭐ 灯光通道 topic 常量（与生产代码 <c>MqttTopics</c> 同源；铰链/同步目前只有字面量）。</summary>
internal static class LightingTopics
{
    internal const string Keyboard = MqttTopics.KeyboardCtrl;
    internal const string Lightbar = MqttTopics.LightbarCtrl;
    internal const string Logo = MqttTopics.LogoLightCtrl;

    /// <summary>⚠️ 生产侧尚无 <c>MqttTopics</c> 常量（BackendProbe 里也是字面量）⇒ 先在此集中。</summary>
    internal const string Hinge = "HidLightbar_Hinge/Ctrl";

    /// <summary>⚠️ 同 <see cref="Hinge" />。</summary>
    internal const string Sync = "HidLightbar_Sync/Ctrl";
}

/// <summary>
/// ⭐ 灯光域（通道设置持久化 + 自动关灯 + 灯效下发；两窗共享）✅
/// <para>⭐ 解决的原问题：<c>WideLightIdleOptions()/WideLightIdleSeconds()/WideSetLightIdleSeconds()</c>
/// /<c>WideLightOffOnBattery()</c> 与 <c>WideMoreSwitches()</c> 混在宽屏窗口里；迷你窗无法独立取用。</para>
/// <para>⭐ 通道设置仍复用生产 <see cref="LightingSettingsStore" />（⛔ 不复制解析/写盘逻辑）；
/// 灯效下发复用 <see cref="BackendProbe" /> 的既有封装（它内部已处理 ARGB/单色判定）。</para>
/// <para>⚠️ 迁移点：<c>ReconcileLightingPowerAsync()</c>（离电/空闲熄灭的实际下发与定时器）仍在窗口；
/// 本服务只负责配置落盘，避免把灯效电源状态机搬进共享层。</para>
/// </summary>
internal interface ILightingService
{
    /// <summary>读通道设置（不存在时返回默认值，与 <see cref="LightingSettingsStore.Load" /> 同语义）。</summary>
    LightChannelSettings LoadChannel(string topic, string defaultEffect);

    /// <summary>尝试读通道设置（不存在/读失败 ⇒ false）。</summary>
    bool TryLoadChannel(string topic, out LightChannelSettings settings);

    /// <summary>保存通道设置（用户操作路径；会记“用户明确设置过灯光”标记）。</summary>
    void SaveChannel(string topic, LightChannelSettings settings);

    /// <summary>保存通道电源开关（会写 powerexplicit=1，仅用户点击时调用）。</summary>
    void SaveChannelPower(string topic, bool powerOn, string trigger);

    /// <summary>用户是否明确设置过灯光（安装/升级后不覆盖硬件状态的前提）。</summary>
    bool HasUserLightingIntent { get; }

    /// <summary>自动关灯可选项（与 WinForms SettingsForm 的表同源）。</summary>
    IReadOnlyList<LightIdleOption> LightIdleOptions { get; }

    /// <summary>当前自动关灯秒数（宝龙达读硬件设置，其它读 config）。</summary>
    int LightIdleSeconds { get; }

    /// <summary>设置自动关灯秒数（只落盘/写硬件设置；电源状态核对仍由窗口触发）。</summary>
    ServiceResult SetLightIdleSeconds(int seconds);

    /// <summary>把当前自动关灯配置同步给 GCU 固件计时器（0 = 关闭）。</summary>
    void ApplyHardwareLightIdleTimer();

    /// <summary>离电时是否关灯（config）。</summary>
    bool LightOffOnBattery { get; }

    /// <summary>设置“离电关灯”（只落盘；实际熄灭由窗口的 Reconcile 触发）。</summary>
    ServiceResult SetLightOffOnBattery(bool on);

    /// <summary>当前键盘可见灯效（按键盘类型裁剪；与原窗口 FillKeyboardEffects 同源）。</summary>
    IReadOnlyList<(string Id, string Label)> VisibleKeyboardEffects { get; }

    /// <summary>下发键盘灯效。</summary>
    Task<ServiceResult> SetKeyboardEffectAsync(string effectId);

    /// <summary>下发灯带灯效。</summary>
    Task<ServiceResult> SetLightbarEffectAsync(string effectId);

    /// <summary>下发 Logo 灯效。</summary>
    Task<ServiceResult> SetLogoEffectAsync(string effectId);

    /// <summary>下发铰链灯效。</summary>
    Task<ServiceResult> SetHingeEffectAsync(string effectId);

    /// <summary>下发同步灯效。</summary>
    Task<ServiceResult> SetSyncEffectAsync(string effectId);

    /// <summary>键盘灯电源。</summary>
    Task<ServiceResult> SetKeyboardPowerAsync(bool on);

    /// <summary>指定灯光通道电源（topic 见 <see cref="LightingTopics" />）。</summary>
    Task<ServiceResult> SetLightPowerAsync(string topic, bool on);

    /// <summary>灯光完整意图：能力判定 + 电源 + 效果下发。</summary>
    Task<ServiceResult> ApplyLightIntentAsync(SetLightIntent intent);

    /// <summary>键盘灯意图：EC 单区/白光、GCU/服务、NUC 本地键盘统一入口。</summary>
    Task<ServiceResult> ApplyKeyboardIntentAsync(SetKeyboardIntent intent);

    /// <summary>灯光电源意图：能力判定 + 单通道开关。</summary>
    Task<ServiceResult> ApplyLightPowerIntentAsync(SetLightPowerIntent intent);

    /// <summary>
    /// 键盘灯开关的**界面状态**（只读）：<c>null</c> = 该后端没有可用的读数来源。
    /// <para>EC 单区/白光回读寄存器；EC 模式的 HID 单键没有回读通道 ⇒ 用 HID 电源镜像，
    /// 镜像也没有时按"用户是否显式关过"决定（⛔ 不把"读不到"显示成关）。</para>
    /// </summary>
    bool? ResolveKeyboardSwitchState();

    /// <summary>
    /// 键盘灯**真实回读**（只读）：只有 EC 单区/白光有回读通道。
    /// <para>EC 模式的 HID 单键没有回读，返回 false（调用方据此跳过"保活"，⛔ 不拿镜像当硬件真值）。</para>
    /// </summary>
    bool TryReadKeyboardPowerReadback(out bool on);

    // ── 灯光设置读写：界面层只通过这里取/存，⛔ 不直接摸硬件侧的设置存储 ──

    /// <summary>读取通道保存的效果名（无配置 ⇒ 返回 <paramref name="defaultEffect"/>）。</summary>
    string LoadChannelEffect(string topic, string defaultEffect);

    /// <summary>读取通道保存的电源状态：当前值 + 是否用户显式设置过。</summary>
    (bool PowerOn, bool PowerExplicit) LoadChannelPower(string topic);

    /// <summary>保存通道效果（只改 Effect，其余字段保留）。</summary>
    void SaveChannelEffect(string topic, string effect);

    /// <summary>亮度被压成 0 时抬到 1（只改 Light）。</summary>
    void EnsureChannelVisible(string topic, string defaultEffect);

    /// <summary>通道保存的效果是否等于给定值（大小写不敏感）。</summary>
    bool ChannelEffectIs(string topic, string effect);

    // ── EC 实验模式的键盘通道（只读事实 + 下发入口）：界面层只问这里，⛔ 不直接引用 EC 键盘驱动类型 ──

    /// <summary>EC 实验模式（无 GCU）是否接管键盘灯。</summary>
    bool EcKeyboardActive { get; }

    /// <summary>EC 模式键盘灯是否为 HID 单键（有些机型是 EC 单区/白光）。</summary>
    bool EcKeyboardHid { get; }

    /// <summary>EC 模式键盘灯是否为白光单色键盘。</summary>
    bool EcKeyboardWhite { get; }

    /// <summary>EC 模式键盘灯是否为单分区 RGB。</summary>
    bool EcKeyboardSingleZone { get; }

    /// <summary>EC 模式是否存在可控制的键盘灯通道。</summary>
    bool EcKeyboardControllable { get; }

    /// <summary>EC 模式键盘灯种类（日志/状态行展示用）。</summary>
    string EcKeyboardKind { get; }

    /// <summary>标记 EC 键盘通道已就绪（探测完成后调用一次；幂等）。</summary>
    void EnsureEcKeyboardReady();

    /// <summary>按保存的设置下发 EC 键盘灯（effect=效果名；<paramref name="light"/> 为空则用保存值）。</summary>
    bool ApplyEcKeyboard(string effect, bool on, int? light = null);

    /// <summary>EC 白光键盘（0~2 档；0 = 关）。</summary>
    bool ApplyEcWhiteKeyboard(int level);

    /// <summary>EC 键盘 Fn 亮度键（0~4 档）。</summary>
    bool ApplyEcHotkeyBrightness(int level);

    // ── 本地（无 GCU）灯光通道与固件键盘面板：能力与下发都从这里走 ──

    /// <summary>本地模式是否支持灯带（无 GCU 也能下发）。</summary>
    bool LocalLightbarSupported { get; }

    /// <summary>本地模式是否支持 Logo 灯。</summary>
    bool LocalLogoSupported { get; }

    /// <summary>刷新本地（HID）灯光证据：只读探测，幂等。</summary>
    void RefreshLocalLightEvidence();

    /// <summary>本地灯光通道下发（<c>detail</c> 为失败原因）。</summary>
    bool TryApplyLocalLightChannel(string topic, string effect, bool on, int light, int speed, int colorArgb, out string detail);

    /// <summary>固件键盘面板可见灯效（按键盘类型裁剪）。</summary>
    IReadOnlyList<(string Id, string Label)> VisibleFirmwareKeyboardEffects(int keyboardType);

    /// <summary>单区固件键盘灯效常量（1 常亮 / 2 硬件彩虹 / 3 硬件呼吸 / 4 软件六色轮播）。</summary>
    IReadOnlyList<(string Id, string Label)> SingleZoneFirmwareKeyboardEffects { get; }
}

/// <inheritdoc />
internal sealed class LightingService : ILightingService
{
    private const string IdleSecondsKey = "lighting_idle_seconds";
    private const string OffOnBatteryKey = "lighting_off_on_battery";

    private readonly BackendAccessor _backend;

    internal LightingService(BackendAccessor backend)
    {
        _backend = backend;
    }

    public LightChannelSettings LoadChannel(string topic, string defaultEffect)
    {
        try
        {
            return LightingSettingsStore.Load(topic, defaultEffect);
        }
        catch
        {
            return new LightChannelSettings(defaultEffect, 4, 1, -1, PowerOn: false);
        }
    }

    public bool TryLoadChannel(string topic, out LightChannelSettings settings)
    {
        settings = default;
        try
        {
            return LightingSettingsStore.TryLoad(topic, out settings);
        }
        catch
        {
            return false;
        }
    }

    public void SaveChannel(string topic, LightChannelSettings settings)
    {
        try
        {
            LightingSettingsStore.Save(topic, settings);
        }
        catch
        {
        }
    }

    public void SaveChannelPower(string topic, bool powerOn, string trigger)
    {
        try
        {
            LightingSettingsStore.SavePower(topic, powerOn, trigger);
        }
        catch
        {
        }
    }

    public bool HasUserLightingIntent
    {
        get
        {
            try
            {
                return LightingSettingsStore.HasUserLightingIntent();
            }
            catch
            {
                return false;
            }
        }
    }

    public IReadOnlyList<LightIdleOption> LightIdleOptions
    {
        get
        {
            List<LightIdleOption> options = new();
            try
            {
                foreach ((string label, int mins, int idleSeconds) in LightingCatalog.LightingCloseTimerOptions)
                {
                    options.Add(new LightIdleOption(label, mins, idleSeconds));
                }
            }
            catch
            {
            }
            return options;
        }
    }

    public int LightIdleSeconds
    {
        get
        {
            MigrateIdleSecondsKeyOnce();
            return AppConfig.Get(IdleSecondsKey, 0);
        }
    }

    public ServiceResult SetLightIdleSeconds(int seconds)
    {
        int normalized = Math.Max(0, seconds);
        AppConfig.Set(IdleSecondsKey, normalized);
        AppConfig.Flush();
        // GCU 固件侧的键盘计时（无 GCU 时自动 no-op）；真正的关灯由窗口空闲计时器对所有通道执行。
        ApplyHardwareLightIdleTimer();
        try
        {
            KeyboardRgb? rgb = L_Mechrevo.WpfP0.Services.WpfBackend.rgb;
            if (rgb != null)
            {
                rgb.CloseTimerMinutes = normalized / 60;
                rgb.SaveConfig();
            }
        }
        catch
        {
        }
        return ServiceResult.Success(WpfStrings.LightAutoOffSaved);
    }

    private static bool _idleSecondsMigrated;

    /// <summary>旧键 light_autooff_seconds 迁移到统一键一次（旧键只读不写，避免两个键并行）。</summary>
    private static void MigrateIdleSecondsKeyOnce()
    {
        if (_idleSecondsMigrated)
        {
            return;
        }
        _idleSecondsMigrated = true;
        if (AppConfig.Get(IdleSecondsKey, -1) >= 0)
        {
            return;
        }
        int legacy = AppConfig.Get("light_autooff_seconds", 0);
        if (legacy > 0)
        {
            AppConfig.Set(IdleSecondsKey, legacy);
        }
    }

    public void ApplyHardwareLightIdleTimer()
    {
        try
        {
            if (_backend.IsBaolongda)
            {
                return;
            }
            MechrevoService? service = _backend.Service;
            if (service == null)
            {
                return;
            }
            int seconds = AppConfig.Get(IdleSecondsKey, 0);
            // GCU 的 Mins 字段按秒计数：10/15/20/30/45/60/.../10800。
            _ = service.PublishKeyboardCloseTimerSeconds(seconds);
            Logger.WriteLine("[light] 已同步 GCU 自动关灯计时器：" + (seconds <= 0 ? "关闭" : seconds + " 秒"));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[light] 同步 GCU 自动关灯计时器失败：" + ex.Message);
        }
    }

    public bool LightOffOnBattery => AppConfig.Is(OffOnBatteryKey);

    public ServiceResult SetLightOffOnBattery(bool on)
    {
        AppConfig.Set(OffOnBatteryKey, on ? 1 : 0);
        AppConfig.Flush();
        return ServiceResult.Success(on ? WpfStrings.LightOffOnBatteryOnLong : WpfStrings.LightOffOnBatteryOffShort);
    }

    public IReadOnlyList<(string Id, string Label)> VisibleKeyboardEffects
    {
        get
        {
            List<(string, string)> effects = new();
            try
            {
                int keyboardType = _backend.Hw?.Capabilities.KeyboardType ?? 0;
                foreach ((string id, string label) in KeyboardFirmwareEffects.Visible(keyboardType))
                {
                    effects.Add((id, label));
                }
            }
            catch
            {
            }
            return effects;
        }
    }

    public Task<ServiceResult> SetKeyboardEffectAsync(string effectId) => RunProbeGatedAsync(
        FeatureId.KeyboardLight, _backend.Probe?.Hw?.SupportsKeyboard == true,
        static (probe, effect) => probe.SetKeyboardEffectAsync(effect), effectId, WpfStrings.HidKeyboardWindow_Text_9);

    public Task<ServiceResult> SetLightbarEffectAsync(string effectId) => RunProbeGatedAsync(
        FeatureId.Lightbar, _backend.Probe?.SupportsLightbar == true,
        static (probe, effect) => probe.SetLightbarEffectAsync(effect), effectId, WpfStrings.LightChannelLightbar);

    public Task<ServiceResult> SetLogoEffectAsync(string effectId) => RunProbeGatedAsync(
        FeatureId.LogoLight, _backend.Probe?.Hw?.SupportsLogoLight == true,
        static (probe, effect) => probe.SetLogoEffectAsync(effect), effectId, WpfStrings.LightChannelLogo);

    public Task<ServiceResult> SetHingeEffectAsync(string effectId) => RunProbeGatedAsync(
        FeatureId.HingeLight, _backend.Probe?.SupportsHingeLight == true,
        static (probe, effect) => probe.SetHingeEffectAsync(effect), effectId, WpfStrings.LightChannelHinge);

    public Task<ServiceResult> SetSyncEffectAsync(string effectId) => RunProbeGatedAsync(
        FeatureId.SyncLight, _backend.Probe?.SupportsSyncLight == true,
        static (probe, effect) => probe.SetSyncEffectAsync(effect), effectId, WpfStrings.LightChannelSync);

    public async Task<ServiceResult> SetKeyboardPowerAsync(bool on)
    {
        BackendProbe? probe = _backend.Probe;
        if (probe == null)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
        }
        if (!FeatureGate.ResolveSupportOrLegacy(FeatureId.KeyboardLight, _backend.Hw,
            probe.Hw?.SupportsKeyboard == true))
        {
            return ServiceResult.Fail(WpfStrings.LightKeyboardPowerFailed);
        }
        try
        {
            bool ok = await probe.SetKeyboardPowerAsync(on).ConfigureAwait(false);
            return ok ? ServiceResult.Success(on ? WpfStrings.LightKeyboardOn : WpfStrings.LightKeyboardOff) : ServiceResult.Fail(WpfStrings.LightKeyboardPowerFailed);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(WpfStrings.LightKeyboardPowerExceptionPrefix + ex.GetType().Name);
        }
    }

    public async Task<ServiceResult> SetLightPowerAsync(string topic, bool on)
    {
        BackendProbe? probe = _backend.Probe;
        if (probe == null)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
        }
        bool gateOk;
        if (string.Equals(topic, LightingTopics.Keyboard, StringComparison.Ordinal))
        {
            gateOk = FeatureGate.ResolveSupportOrLegacy(FeatureId.KeyboardLight, _backend.Hw,
                probe.Hw?.SupportsKeyboard == true);
        }
        else
        {
            FeatureId? feature = FeatureForLightTopic(topic);
            bool legacy = topic switch
            {
                LightingTopics.Lightbar => probe.SupportsLightbar,
                LightingTopics.Logo => probe.Hw?.SupportsLogoLight == true,
                LightingTopics.Hinge => probe.SupportsHingeLight,
                LightingTopics.Sync => probe.SupportsSyncLight,
                _ => false
            };
            gateOk = feature != null && FeatureGate.ResolveSupportOrLegacy(feature.Value, _backend.Hw, legacy);
        }
        if (!gateOk)
        {
            return ServiceResult.Fail(WpfStrings.LightPowerFailed);
        }
        try
        {
            bool ok = await probe.SetLightPowerAsync(topic, on).ConfigureAwait(false);
            return ok ? ServiceResult.Success(on ? WpfStrings.LightPowerOn : WpfStrings.LightPowerOff) : ServiceResult.Fail(WpfStrings.LightPowerFailed);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(WpfStrings.LightPowerExceptionPrefix + ex.GetType().Name);
        }
    }

    public async Task<ServiceResult> ApplyLightPowerIntentAsync(SetLightPowerIntent intent)
    {
        BackendProbe? probe = _backend.Probe;
        if (probe == null)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
        }
        if (string.Equals(intent.Topic, LightingTopics.Keyboard, StringComparison.Ordinal))
        {
            return await SetKeyboardPowerAsync(intent.On).ConfigureAwait(false);
        }
        FeatureId? feature = FeatureForLightTopic(intent.Topic);
        if (feature == null)
        {
            return ServiceResult.Fail(WpfStrings.LightPowerFailed);
        }
        bool legacySupport = intent.Topic switch
        {
            LightingTopics.Lightbar => probe.SupportsLightbar,
            LightingTopics.Logo => probe.Hw?.SupportsLogoLight == true
                || (GcuEcExperimental.LocalLogoLight),
            LightingTopics.Hinge => probe.SupportsHingeLight,
            LightingTopics.Sync => probe.SupportsSyncLight,
            _ => false
        };
        FeatureGateShadow.LogFeature(feature.Value, probe.Hw, legacySupport, "app-intent-power", "dispatch");
        if (!FeatureGate.ResolveSupportOrLegacy(feature.Value, probe.Hw, legacySupport))
        {
            return ServiceResult.Fail(WpfStrings.LightPowerFailed);
        }
        return await SetLightPowerAsync(intent.Topic, intent.On).ConfigureAwait(false);
    }

    public async Task<ServiceResult> ApplyLightIntentAsync(SetLightIntent intent)
    {
        BackendProbe? probe = _backend.Probe;
        if (probe == null)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
        }
        if (string.Equals(intent.Topic, LightingTopics.Keyboard, StringComparison.Ordinal))
        {
            return ServiceResult.Fail(WpfStrings.L("键盘灯意图走键盘通道", "keyboard intent handled separately"));
        }
        FeatureId? feature = FeatureForLightTopic(intent.Topic);
        if (feature == null)
        {
            return ServiceResult.Fail(WpfStrings.LightPowerFailed);
        }
        try
        {
            bool legacySupport = intent.Topic switch
            {
                LightingTopics.Lightbar => probe.SupportsLightbar,
                LightingTopics.Logo => probe.Hw?.SupportsLogoLight == true
                    || (GcuEcExperimental.LocalLogoLight),
                LightingTopics.Hinge => probe.SupportsHingeLight,
                LightingTopics.Sync => probe.SupportsSyncLight,
                _ => false
            };
            if (feature == FeatureId.LogoLight)
            {
                FeatureGateShadow.LogLogoLight(probe.Hw, legacySupport, "app-intent", "dispatch");
            }
            else
            {
                FeatureGateShadow.LogFeature(feature.Value, probe.Hw, legacySupport, "app-intent", "dispatch");
            }
            if (!FeatureGate.ResolveSupportOrLegacy(feature.Value, probe.Hw, legacySupport))
            {
                return ServiceResult.Fail(WpfStrings.LightPowerFailed);
            }
            if (!intent.On)
            {
                bool off = await probe.SetLightPowerAsync(intent.Topic, false).ConfigureAwait(false);
                return off ? ServiceResult.Success(WpfStrings.LightPowerOff) : ServiceResult.Fail(WpfStrings.LightPowerFailed);
            }
            await probe.SetLightPowerAsync(intent.Topic, true).ConfigureAwait(false);
            MechrevoService? service = probe.Service;
            bool topicKnown = string.Equals(intent.Topic, LightingTopics.Lightbar, StringComparison.Ordinal)
                || string.Equals(intent.Topic, LightingTopics.Logo, StringComparison.Ordinal)
                || string.Equals(intent.Topic, LightingTopics.Hinge, StringComparison.Ordinal);
            bool localSupported = intent.Topic switch
            {
                // Intel NUC/UMI 的灯带走本地 UmiLightbar，⛔ 不能进 ITE 通道。
                LightingTopics.Lightbar => LocalLightbarSupported && probe.Hw?.IsIntelNucMachine != true,
                LightingTopics.Logo => LocalLogoSupported,
                LightingTopics.Hinge => GcuEcExperimental.LocalHingeLight,
                _ => false
            };
            LightingChannelPlan plan = LightingChannelRouting.Decide(service != null, topicKnown, localSupported);
            if (plan == LightingChannelPlan.Unsupported)
            {
                Logger.WriteError("[light-plan] " + intent.Topic + " ⇒ 不可用（服务=" + (service != null)
                    + " · 已知通道=" + topicKnown + " · 本地通道=" + localSupported + "）");
                return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
            }
            if (plan == LightingChannelPlan.LocalChannel)
            {
                // ⭐ EC/本地模式（没有 GCU 服务）：灯带/Logo/铰链的灯效走本地 HID 通道。
                // 以前这里直接判失败 ⇒ 界面显示"已保存，但设备未确认"，而灯带其实完全没下发。
                bool localOk = TryApplyLocalLightChannel(intent.Topic, intent.Effect, true, intent.Light,
                    intent.Speed, intent.ColorArgb, out string localDetail);
                Logger.WriteError("[local-light] " + intent.Topic + " effect=" + intent.Effect
                    + " light=" + intent.Light + " speed=" + intent.Speed
                    + " ⇒ " + (localOk ? "已下发" : "失败：" + localDetail));
                return localOk ? ServiceResult.Success(WpfStrings.LightSavedApplied) : ServiceResult.Fail(WpfStrings.LightSavedUnconfirmed);
            }
            Color? color = KeyboardFirmwareEffects.IsSingleColorEffect(intent.Effect)
                ? Color.FromArgb(intent.ColorArgb)
                : null;
            if (string.Equals(intent.Topic, LightingTopics.Lightbar, StringComparison.Ordinal)
                && probe.Hw?.IsIntelNucMachine == true)
            {
                if (intent.Effect is not "Off" and not "None" and not "")
                {
                    color = Color.FromArgb(intent.ColorArgb);
                }
                bool rawOk = probe.ApplyIntelNucLightbar(intent.Effect, intent.Light, color);
                Logger.WriteError("[umi-lightbar] apply topic=" + intent.Topic + " effect=" + intent.Effect + " light=" + intent.Light + " raw=" + rawOk);
                return rawOk ? ServiceResult.Success(WpfStrings.SentSuffix) : ServiceResult.Fail(WpfStrings.ApplyFailedSuffix);
            }
            bool ok = await service.SetLightEffect(intent.Topic, intent.Effect, intent.Light,
                intent.Speed, "None", color, true).ConfigureAwait(false);
            return ok ? ServiceResult.Success(WpfStrings.SentSuffix) : ServiceResult.Fail(WpfStrings.ApplyFailedSuffix);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(WpfStrings.ExceptionPrefix + ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public bool? ResolveKeyboardSwitchState()
    {
        if (!GcuEcKeyboard.Active)
        {
            return null;
        }
        if (GcuEcKeyboard.IsHid)
        {
            // HID 单键没有回读通道：先用 HID 侧电源镜像，取不到就按"是否显式关过"决定。
            KeyboardRgb? rgb = L_Mechrevo.WpfP0.Services.WpfBackend.rgb;
            if (rgb != null)
            {
                Logger.WriteLineIfChanged("ec-kbd-switch",
                    "[gcu-ec-kbd] 键盘灯开关：HID 单键无回读 ⇒ 按电源镜像显示=" + rgb.KbPowerOn);
                return rgb.KbPowerOn;
            }
            LightChannelSettings saved = LightingSettingsStore.Load(LightingTopics.Keyboard, "Single");
            bool fallback = LightingState.ResolveSwitchWithoutReadback(saved.PowerExplicit, saved.PowerOn);
            Logger.WriteLineIfChanged("ec-kbd-switch", "[gcu-ec-kbd] 键盘灯开关：HID 单键未连接 ⇒ 按保存设置显示=" + fallback
                + "（显式=" + saved.PowerExplicit + " · 保存值=" + saved.PowerOn + "）");
            return fallback;
        }
        return GcuEcKeyboard.TryReadPower(out bool on) ? on : (bool?)null;
    }

    /// <inheritdoc />
    public bool TryReadKeyboardPowerReadback(out bool on)
    {
        on = false;
        if (!GcuEcKeyboard.Active)
        {
            return false;
        }
        if (GcuEcKeyboard.IsHid)
        {
            Logger.WriteLineIfChanged("kbd-keepalive-hid",
                "[light] 保活：HID 单键键盘没有回读通道 ⇒ 不做自动补点（灯灭需 Fn 或界面重新开）。");
            return false;
        }
        return GcuEcKeyboard.TryReadPower(out on);
    }

    /// <inheritdoc />
    public string LoadChannelEffect(string topic, string defaultEffect)
    {
        return LightingSettingsStore.Load(topic, defaultEffect).Effect;
    }

    /// <inheritdoc />
    public (bool PowerOn, bool PowerExplicit) LoadChannelPower(string topic)
    {
        LightChannelSettings settings = LightingSettingsStore.Load(topic, "Single");
        return (settings.PowerOn, settings.PowerExplicit);
    }

    /// <inheritdoc />
    public void SaveChannelEffect(string topic, string effect)
    {
        LightingSettingsStore.Save(topic, LightingSettingsStore.Load(topic, effect) with { Effect = effect });
    }

    /// <inheritdoc />
    public void EnsureChannelVisible(string topic, string defaultEffect)
    {
        LightChannelSettings settings = LightingSettingsStore.Load(topic, defaultEffect);
        if (settings.Light <= 0)
        {
            LightingSettingsStore.Save(topic, settings with { Light = 1 });
        }
    }

    /// <inheritdoc />
    public bool ChannelEffectIs(string topic, string effect)
    {
        return string.Equals(LightingSettingsStore.Load(topic, effect).Effect, effect, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool EcKeyboardActive => EcKeyboardFacts.Active;

    /// <inheritdoc />
    public bool EcKeyboardHid => EcKeyboardFacts.Hid;

    /// <inheritdoc />
    public bool EcKeyboardWhite => EcKeyboardFacts.White;

    /// <inheritdoc />
    public bool EcKeyboardSingleZone => EcKeyboardFacts.SingleZone;

    /// <inheritdoc />
    public bool EcKeyboardControllable => EcKeyboardFacts.Controllable;

    /// <inheritdoc />
    public string EcKeyboardKind => EcKeyboardFacts.Kind;

    /// <inheritdoc />
    public void EnsureEcKeyboardReady()
    {
        GcuEcKeyboard.MarkActive();
    }

    /// <inheritdoc />
    public bool ApplyEcKeyboard(string effect, bool on, int? light = null)
    {
        return GcuEcKeyboard.ApplyFromSettings(effect, on, light);
    }

    /// <inheritdoc />
    public bool ApplyEcWhiteKeyboard(int level)
    {
        int clamped = Math.Clamp(level, 0, 2);
        return GcuEcKeyboard.ApplyWhite(clamped > 0, clamped);
    }

    /// <inheritdoc />
    public bool ApplyEcHotkeyBrightness(int level)
    {
        return GcuEcKeyboard.ApplyHotkeyBrightness(level);
    }

    /// <inheritdoc />
    public bool LocalLightbarSupported => GcuEcExperimental.LocalLightbar;

    /// <inheritdoc />
    public bool LocalLogoSupported => GcuEcExperimental.LocalLogoLight;

    /// <inheritdoc />
    public void RefreshLocalLightEvidence()
    {
        IteLightingLocal.RefreshFromHid();
    }

    /// <inheritdoc />
    public bool TryApplyLocalLightChannel(string topic, string effect, bool on, int light, int speed, int colorArgb, out string detail)
    {
        if (string.Equals(topic, LightingTopics.Lightbar, StringComparison.Ordinal))
        {
            return IteLightingLocal.TryApplyLightbar(effect, on, light, speed, colorArgb, out detail);
        }
        if (string.Equals(topic, LightingTopics.Logo, StringComparison.Ordinal))
        {
            return IteLightingLocal.TryApplyLogo(effect, on, light, speed, colorArgb, out detail);
        }
        if (string.Equals(topic, LightingTopics.Hinge, StringComparison.Ordinal))
        {
            return IteLightingLocal.TryApplyHinge(effect, on, light, speed, colorArgb, out detail);
        }
        detail = "不支持的通道：" + topic;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Id, string Label)> VisibleFirmwareKeyboardEffects(int keyboardType)
    {
        return LightingFacts.VisibleFirmwareKeyboardEffects(keyboardType);
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Id, string Label)> SingleZoneFirmwareKeyboardEffects => LightingFacts.SingleZoneFirmwareKeyboardEffects;

    public async Task<ServiceResult> ApplyKeyboardIntentAsync(SetKeyboardIntent intent)
    {
        BackendProbe? probe = _backend.Probe;
        if (probe == null)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
        }
        bool legacySupport = probe.Hw?.SupportsKeyboard == true;
        FeatureGateShadow.LogKeyboardLight(probe.Hw, legacySupport, "app-intent", "dispatch");
        if (!FeatureGate.ResolveSupportOrLegacy(FeatureId.KeyboardLight, probe.Hw, legacySupport))
        {
            return ServiceResult.Fail(WpfStrings.LightKeyboardPowerFailed);
        }
        try
        {
            if (GcuEcKeyboard.Active)
            {
                bool ecOk = GcuEcKeyboard.ApplyFromSettings(intent.Effect, intent.On, intent.Light);
                return ecOk ? ServiceResult.Success(WpfStrings.LightSavedApplied) : ServiceResult.Fail(WpfStrings.LightSavedUnconfirmed);
            }
            if (!intent.On)
            {
                bool off = await probe.SetKeyboardPowerAsync(false).ConfigureAwait(false);
                return off ? ServiceResult.Success(WpfStrings.LightKeyboardOff) : ServiceResult.Fail(WpfStrings.LightKeyboardPowerFailed);
            }
            await probe.SetKeyboardPowerAsync(true).ConfigureAwait(false);
            MechrevoService? service = probe.Service;
            if (service == null)
            {
                return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
            }
            Color? color = (probe.Hw?.UsesBaolongda == true
                || string.Equals(intent.Effect, "Single", StringComparison.Ordinal))
                ? Color.FromArgb(intent.ColorArgb)
                : null;
            bool effectOk = await service.SetKeyboardEffect(intent.Effect, intent.Light,
                intent.Speed, "None", color, true).ConfigureAwait(false);
            if (effectOk && intent.Light > 0)
            {
                await service.SetKeyboardBrightnessPreservingEffect(intent.Light).ConfigureAwait(false);
            }
            return effectOk ? ServiceResult.Success(WpfStrings.LightSavedApplied) : ServiceResult.Fail(WpfStrings.LightSavedUnconfirmed);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(WpfStrings.LightPowerExceptionPrefix + ex.GetType().Name);
        }
    }

    private static FeatureId? FeatureForLightTopic(string topic)    {
        return topic switch
        {
            LightingTopics.Logo => FeatureId.LogoLight,
            LightingTopics.Lightbar => FeatureId.Lightbar,
            LightingTopics.Hinge => FeatureId.HingeLight,
            LightingTopics.Sync => FeatureId.SyncLight,
            _ => null
        };
    }

    private Task<ServiceResult> RunProbeGatedAsync(FeatureId feature, bool legacySupport,
        Func<BackendProbe, string, Task<bool>> apply, string effectId, string displayName)
    {
        if (!FeatureGate.ResolveSupportOrLegacy(feature, _backend.Hw, legacySupport))
        {
            return Task.FromResult(ServiceResult.Fail(displayName + WpfStrings.ApplyFailedSuffix));
        }
        return RunProbeAsync(apply, effectId, displayName);
    }

    private async Task<ServiceResult> RunProbeAsync(
        Func<BackendProbe, string, Task<bool>> apply,
        string effectId,
        string displayName)
    {
        BackendProbe? probe = _backend.Probe;
        if (probe == null)
        {
            return ServiceResult.Fail(WpfStrings.OriginalStyleWindow_xaml_Text_51);
        }
        if (string.IsNullOrWhiteSpace(effectId))
        {
            return ServiceResult.Fail(displayName + WpfStrings.IsEmptySuffix);
        }
        try
        {
            bool ok = await apply(probe, effectId).ConfigureAwait(false);
            return ok ? ServiceResult.Success(displayName + WpfStrings.SentSuffix) : ServiceResult.Fail(displayName + WpfStrings.ApplyFailedSuffix);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(displayName + WpfStrings.ExceptionPrefix + ex.GetType().Name);
        }
    }
}
