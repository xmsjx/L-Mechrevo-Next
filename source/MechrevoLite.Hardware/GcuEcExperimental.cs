using System;
using System.Threading;
using MechrevoLite.Helpers;

namespace MechrevoLite.Hardware;

/// <summary>EC 控制模式通道：只走 ACPIDriver H2RAM，不扫描 EC 端口。</summary>
internal static class GcuEcExperimental
{
    private const int ModeIndexAddress = 1963;
    private const int TriggerAddress = 1895;
    private const int TurboTriggerAddress = 1115;
    private const int FanBoostClampAddress = 1873;
    /// <summary>节能开关位（bit2）：工作档下「标准/节能」的唯一硬件区分点，只做读-改-写这一位。</summary>
    private const int QuietModeAddress = 1957;
    private const int CustomModeFlagAddress = 1798;
    private static long _lastSelfWriteTicks;
    private static int _lastReadVisual = -1;
    private static int _selfCustomMode;
    private static string _lastRegisterSnapshot = "";

    /// <summary>当前功耗槽是否等于声明里某一档的取值（用于区分同一档位字节下的标准/节能）。</summary>
    private static bool TierSlotsMatch(string key)
    {
        string[] parts = (DeclarationRuntime.Param("plCustom", key) ?? "").Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), out int want1)
            || !int.TryParse(parts[1].Trim(), out int want2))
        {
            return false;
        }
        return AcpiDriverEc.TryReadByte(EcPlCustom.RegisterPl1, out byte now1)
            && AcpiDriverEc.TryReadByte(EcPlCustom.RegisterPl2, out byte now2)
            && now1 == want1 && now2 == want2;
    }

    /// <summary>风扇五点 PWM（0x743-0x747）：本机型作 L1-L5 档位值，只读采样。</summary>
    private static string FanFivePoints()
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            if (!AcpiDriverEc.TryReadByte(1859 + i, out byte value))
            {
                continue;
            }
            if (text.Length > 0)
            {
                text.Append('/');
            }
            text.Append(value.ToString("X2"));
        }
        return text.Length > 0 ? text.ToString() : "-";
    }
    private const int FanRampAddress = 1927;
    private static int _probeLogged;

    /// <summary>EC 控制模式是否生效。</summary>
    internal static bool Enabled => GcuEcMode.IsEcMode;

    /// <summary>EC 本地灯带可用。</summary>
    internal static bool LocalLightbar => Enabled && IteLightingLocal.HasLightbar;

    /// <summary>EC 本地 Logo 灯可用：有独立 Logo 证据，或用户在设置里显式指定了通道。</summary>
    internal static bool LocalLogoLight => Enabled
        && (IteLightingLocal.LogoChannelOverride != 0 || IteLightingLocal.HasLogoChannel);

    /// <summary>EC 本地 Hinge 灯可用。</summary>
    internal static bool LocalHingeLight => Enabled && IteLightingLocal.HasHinge;

    /// <summary>EC 本地功耗/自定义档配置可用。</summary>
    internal static bool LocalPowerConfig => Enabled;

    /// <summary>EC 本地风扇曲线可用。</summary>
    internal static bool LocalFanCurve => Enabled;

    /// <summary>EC 本地快捷键可用。</summary>
    internal static bool LocalQuickSwitch => Enabled;

    /// <summary>用户明确选择 EC 时直接进入 EC-only，不等待 GCU 连接。</summary>
    internal static bool DirectMode =>
        string.Equals(Environment.GetEnvironmentVariable("LM_GCU_EC_DIRECT"), "1", StringComparison.Ordinal)
        || AppConfig.Get("gcu_ec_direct", 0) != 0;

    internal static void LogCurrentState(string reason)
    {
        if (!Enabled || Interlocked.Exchange(ref _probeLogged, 1) != 0)
        {
            return;
        }
        if (AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte mode)
            && AcpiDriverEc.TryReadByte(TriggerAddress, out byte trigger)
            && AcpiDriverEc.TryReadByte(TurboTriggerAddress, out byte turbo))
        {
            TryReadVisualMode(out int visual);
            Logger.WriteError($"[gcu-ec] 探针（{reason}）：1963=0x{mode:X2} 1895=0x{trigger:X2} 1115=0x{turbo:X2} 直读模式={visual}");
        }
        else
        {
            Logger.WriteError("[gcu-ec] 探针失败：ACPIDriver EC 读取不可用");
        }
    }

    /// <summary>
    /// 档位相关寄存器的变化快照（只读）：固件自己处理按键时，用它看是哪一位在变。
    /// </summary>
    private static void LogRegisterSnapshot(byte mode, byte clamp, byte custom, byte turbo)
    {
        try
        {
            AcpiDriverEc.TryReadByte(TriggerAddress, out byte trigger);
            AcpiDriverEc.TryReadByte(1832, out byte silent);
            AcpiDriverEc.TryReadByte(1830, out byte whiteLed);
            AcpiDriverEc.TryReadByte(1831, out byte camFlag);
            AcpiDriverEc.TryReadByte(1989, out byte isolated);
            AcpiDriverEc.TryReadByte(0x0727, out byte unit727);
            AcpiDriverEc.TryReadByte(0x0743, out byte db743);
            AcpiDriverEc.TryReadByte(0x0746, out byte db746);
            AcpiDriverEc.TryReadByte(0x072A, out byte biosLevel);
            AcpiDriverEc.TryReadByte(1987, out byte r1987);
            AcpiDriverEc.TryReadByte(1904, out byte r1904);
            AcpiDriverEc.TryReadByte(1977, out byte r1977);
            AcpiDriverEc.TryReadByte(2000, out byte r2000);
            AcpiDriverEc.TryReadByte(1958, out byte r1958);
            AcpiDriverEc.TryReadByte(1857, out byte r1857);
            AcpiDriverEc.TryReadByte(1927, out byte r1927);
            AcpiDriverEc.TryReadByte(1990, out byte r1990);
            string hotkey = " k1987=0x" + r1987.ToString("X2") + " k1904=0x" + r1904.ToString("X2")
                + " k1977=0x" + r1977.ToString("X2") + " k2000=0x" + r2000.ToString("X2")
                + " k1958=0x" + r1958.ToString("X2") + " k1857=0x" + r1857.ToString("X2")
                + " k1927=0x" + r1927.ToString("X2") + " k1990=0x" + r1990.ToString("X2");
            string extra = " led1830=0x" + whiteLed.ToString("X2") + " cam1831=0x" + camFlag.ToString("X2")
                + " iso1989=0x" + isolated.ToString("X2") + " u727=0x" + unit727.ToString("X2")
                + " db743=0x" + db743.ToString("X2") + " db746=0x" + db746.ToString("X2")
                + " fan5=" + FanFivePoints()
                + " lvl72A=0x" + biosLevel.ToString("X2") + hotkey;
            string snapshot = "mode=0x" + mode.ToString("X2") + " trigger=0x" + trigger.ToString("X2")
                + " turbo=0x" + turbo.ToString("X2") + " silent=0x" + silent.ToString("X2")
                + " clamp=0x" + clamp.ToString("X2") + " custom=0x" + custom.ToString("X2") + extra;
            // clamp 是风扇策略位、会自己变，不参与"变化"判定，避免刷日志。
            string keySnapshot = "mode=0x" + mode.ToString("X2") + " trigger=0x" + trigger.ToString("X2")
                + " turbo=0x" + turbo.ToString("X2") + " silent=0x" + silent.ToString("X2")
                + " custom=0x" + custom.ToString("X2") + extra;
            if (!string.Equals(keySnapshot, _lastRegisterSnapshot, StringComparison.Ordinal))
            {
                _lastRegisterSnapshot = keySnapshot;
                Logger.WriteError("[ec-modespy] " + snapshot);
            }
        }
        catch
        {
        }
    }

    /// <summary>本程序最近是否写过 EC 模式寄存器（用于区分外部改档）。</summary>
    internal static bool SelfWroteRecently(int ms = 5000)
    {
        return Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastSelfWriteTicks) <= ms;
    }

    /// <summary>
    /// 让固件把性能键上报成 ACPI 事件：EC 1857 的 bit0（AP 存在位）置 1。
    /// 该位为 0 时按键由固件内部消化，用户态收不到任何事件。
    /// </summary>
    internal static bool TryEnableHotkeyNotify()
    {
        try
        {
            if (!AcpiDriverEc.TryReadByte(1857, out byte ap))
            {
                return false;
            }
            if ((ap & 0x01) != 0)
            {
                return true;
            }
            byte target = (byte)(ap | 0x01);
            if (!AcpiDriverEc.TryWriteByte(1857, target))
            {
                return false;
            }
            bool ok = AcpiDriverEc.TryReadByte(1857, out byte back) && (back & 0x01) != 0;
            Logger.WriteErrorIfChanged("hotkey-notify", "[hotkey] 1857 0x" + ap.ToString("X2") + " → 0x" + target.ToString("X2") + " · 按键上报=" + ok);
            return ok;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[hotkey] 设置按键上报位失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>GCU 服务进程是否仍在运行（EC 模式下它与本程序会抢同一批寄存器）。</summary>
    internal static bool GcuServiceProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("GCUService").Length > 0
                || System.Diagnostics.Process.GetProcessesByName("GCUBridge").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 办公档（标准/节能）在 EC 上同值时的区分：先读声明指定的风扇档首字节
    /// （Code 01 实测 平衡 0x46 / 静音 0x40），读不到再退回本程序记的办公预设。
    /// </summary>
    private static int DecodeOfficeMode(bool boostOn)
    {
        // ① PL 槽值最可靠：切档时 LM 按档位写它，回读能读到（静音 25=0x19 / 平衡 45=0x2D）。
        try
        {
            int plAddr = DeclaredByteAddress("officePlAddr");
            if (plAddr >= 0 && AcpiDriverEc.TryReadByte(plAddr, out byte pl))
            {
                int silentPl = DeclaredByteValue("officePlSilent");
                int balancePl = DeclaredByteValue("officePlBalance");
                if (silentPl >= 0 && pl == silentPl)
                {
                    return 2;
                }
                if (balancePl >= 0 && pl == balancePl)
                {
                    return 0;
                }
            }
        }
        catch
        {
        }
        // ② 风扇档字节：只在用户真改过风扇速度时才不同，强冷期间更不可信 ⇒ 放后面且强冷时跳过。
        try
        {
            int addr = boostOn ? -1 : DeclaredByteAddress("officeFanTier");
            if (addr >= 0 && AcpiDriverEc.TryReadByte(addr, out byte tier))
            {
                int balance = DeclaredByteValue("fanTierBalance");
                int silent = DeclaredByteValue("fanTierSilent");
                if (balance >= 0 && tier == balance)
                {
                    return 0;
                }
                if (silent >= 0 && tier == silent)
                {
                    return 2;
                }
            }
        }
        catch
        {
        }
        bool eco = string.Equals(
            MechrevoLite.AppConfig.GetString("perf_office_preset"),
            "eco",
            System.StringComparison.OrdinalIgnoreCase);
        return eco ? 2 : 0;
    }

    private static int DeclaredByteAddress(string name)
    {
        string? text = DeclarationRuntime.Param("modeSpy", name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return -1;
        }
        text = text.Trim();
        try
        {
            return text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
                ? System.Convert.ToInt32(text, 16)
                : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return -1;
        }
    }

    private static int DeclaredByteValue(string name) => DeclaredByteAddress(name);

    /// <summary>GCU 未接管时直接从 EC 读当前模式：狂暴=1963 bit4；静音=1873=0xA0；其余平衡。</summary>
    internal static bool TryReadVisualMode(out int visualMode)
    {
        visualMode = -1;
        if (!Enabled)
        {
            return false;
        }
        try
        {
            if (!AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte mode)
                || !AcpiDriverEc.TryReadByte(1873, out byte clamp)
                || !AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte custom)
                || !AcpiDriverEc.TryReadByte(TurboTriggerAddress, out byte turbo))
            {
                return false;
            }
            // 节能开关位是可选判据：读不到就退回软件记忆，不影响其余档位判定。
            bool quietKnown = AcpiDriverEc.TryReadByte(QuietModeAddress, out byte quietMode);
            // clamp(1873) 高半字节才是可靠档位：固件改档时不一定会清 mode 的 bit4，
            // 用 mode bit4 优先会把"实际静音/平衡"误判成狂暴（实测：静音 clamp=0xA0 + mode=0x10）。
            // 自定义门控字节（0x0706）只作辅助判据：固件在档位写完之后会把它周期性锁存成自定义槽，
            // 而实际档位/性能仍是内置档，所以只有本程序确实在自定义档时才认这一位。
            // 声明了 customByMemory 的机型：自定义档没有任何可信的硬件标志（门控字节是固件自己的计数器），
            // 只认本程序的软件记忆，不看 0x0706。
            bool customByMemory = PlSwitch("customByMemory");
            bool customLatch = !customByMemory && (custom & 0x3F) != 0;
            bool selfCustom = System.Threading.Volatile.Read(ref _selfCustomMode) != 0;
            // bit6（0x40）是强冷位，不是档位的一部分：开着强冷时 clamp 会变成 0xE0/0x50，
            // 直接取高半字节会把档位误判成狂暴，所以先把它剔掉再判档位。
            int clampMode = clamp & ~0x40;
            if (customLatch && selfCustom)
            {
                visualMode = 3;
            }
            else if (DeclarationRuntime.Flag("officePreset", "enabled"))
            {
                // 声明了「标准/节能=办公档、狂暴=渲染」的机型，写入表是 办公=0xA0 / 渲染=0x00 / 自定义=0x10
                // （与老表 0x00=平衡、0x10=狂暴相反）⇒ 回读必须用同一套，否则渲染被解码成平衡、狂暴永远验不过。
                int officeHigh = clampMode & 0xF0;
                if (officeHigh == 0xA0)
                {
                    // 强冷开着时风扇档字节会被改，⛔ 不能拿它当办公档的区分依据。
                    visualMode = DecodeOfficeMode(boostOn: (clamp & 0x40) != 0);
                }
                else if (officeHigh == 0x10)
                {
                    visualMode = 3;
                }
                else
                {
                    visualMode = 1;
                }
            }
            else if ((clampMode & 0xF0) == 0xA0)
            {
                visualMode = 2;
            }
            else if ((clampMode & 0xF0) == 0x10)
            {
                visualMode = 1;
            }
            else if ((mode & 0x10) != 0)
            {
                visualMode = 1;
            }
            else
            {
                visualMode = 0;
            }
            // mode 的 bit4 仍保留作辅助判据：clamp 落在未知值时靠它兜底；
            // 两者矛盾时以 clamp 为准，并记一条冲突日志，便于继续观察固件改档行为。
            bool modeSaysTurbo = (mode & 0x10) != 0;
            bool clampSaysTurbo = (clampMode & 0xF0) == 0x10;
            bool clampSaysSilent = (clampMode & 0xF0) == 0xA0;
            if (!(customLatch && selfCustom) && !clampSaysSilent && modeSaysTurbo != clampSaysTurbo)
            {
                Logger.WriteErrorIfChanged("ec-mode-conflict-" + mode + "-" + clamp, "[gcu-ec] 档位判据冲突：mode=0x" + mode.ToString("X2") + " clamp=0x" + clamp.ToString("X2") + " ⇒ 以 clamp 为准（判为 " + visualMode + "）");
            }
            // 自定义档兜底：本程序刚写过自定义、而硬件回读还没跟上时，仍按自定义显示；60 秒后以硬件为准。
            if (visualMode != 3 && System.Threading.Volatile.Read(ref _selfCustomMode) != 0)
            {
                if (customByMemory || SelfWroteRecently(60000))
                {
                    visualMode = 3;
                }
                else
                {
                    System.Threading.Volatile.Write(ref _selfCustomMode, 0);
                }
            }
            // 声明了本映射的机型：工作档下「标准/节能」由节能开关位（0x07A5 bit2）区分 —— 位为 0 是标准（按平衡显示），
            // 位为 1 是节能（按静音显示）。位读不到时才退回软件记忆（OfficePreset）。
            if (visualMode == 2 && DeclarationRuntime.Flag("officePreset", "enabled"))
            {
                bool eco;
                if (DeclarationRuntime.Flag("officePreset", "quietBit"))
                {
                    eco = quietKnown
                        ? (quietMode & 0x04) != 0
                        : !string.Equals(MechrevoLite.Mode.PerfModeStore.OfficePreset, MechrevoLite.Mode.PerfModeStore.OfficePresetStd, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // 不写节能开关位的机型：标准与节能在 EC 上是同一个档位字节，只能靠功耗槽区分。
                    eco = TierSlotsMatch("plSilent");
                }
                if (!eco)
                {
                    visualMode = 0;
                }
            }
            Logger.WriteErrorIfChanged("gcu-ec-visual-raw", "[gcu-ec] 视觉档读回 mode=0x" + mode.ToString("X2") + " turbo=0x" + turbo.ToString("X2") + " custom=0x" + custom.ToString("X2") + " clamp=0x" + clamp.ToString("X2") + " quiet=" + (quietKnown ? "0x" + quietMode.ToString("X2") : "--") + " ⇒ " + visualMode);
            LogRegisterSnapshot(mode, clamp, custom, turbo);
            if (_lastReadVisual < 0)
            {
                _lastReadVisual = visualMode;
            }
            else if (_lastReadVisual != visualMode)
            {
                bool self = SelfWroteRecently();
                Logger.WriteError("[gcu-ec] 视觉档 " + _lastReadVisual + " → " + visualMode
                    + (self ? "（本程序近期写过 EC）" : "（本程序近期未写 EC ⇒ 外部改档：GCU 服务/固件）")
                    + (GcuServiceProcessRunning() ? " · GCU 进程在跑" : ""));
                _lastReadVisual = visualMode;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>EC 本地静音狂暴子模式：1832 bit0。true=静音狂暴，false=高能狂暴。</summary>
    internal static bool TrySetSilentTurbo(bool silent, out string detail)
    {
        // 与 TurboSubModeWmi 共用同一把锁：两条路径都改 1832 bit0，不能交叉读-改-写。
        lock (TurboSubModeWmi.ApplyGate)
        {
            return TrySetSilentTurboUnlocked(silent, out detail);
        }
    }

    private static bool TrySetSilentTurboUnlocked(bool silent, out string detail)
    {
        detail = "";
        if (!Enabled)
        {
            detail = "EC 通道未启用";
            return false;
        }
        try
        {
            if (!AcpiDriverEc.TryReadByte(1832, out byte current))
            {
                detail = "读取 1832 失败";
                return false;
            }
            byte target = (byte)(silent ? (current | 0x01) : (current & ~0x01));
            bool write = AcpiDriverEc.TryWriteByte(1832, target);
            bool read = AcpiDriverEc.TryReadByte(1832, out byte readback);
            bool ok = write && read && ((readback & 0x01) == (target & 0x01));
            detail = $"1832 0x{current:X2}->0x{target:X2}/0x{readback:X2} silent={silent}";
            Logger.WriteError("[gcu-ec] 静音狂暴 " + detail + " ⇒ " + (ok ? "OK" : "FAIL"));
            return ok;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            Logger.WriteError("[gcu-ec] 静音狂暴异常：" + detail);
            return false;
        }
    }

    /// <summary>
    /// plCustom 声明里的开关项：未声明或写 1 表示写该字节；写 0/false 表示本机型不写。
    /// </summary>
    private static bool PlSwitch(string key)
    {
        string? text = DeclarationRuntime.Param("plCustom", key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        text = text.Trim();
        return !(text == "0" || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 自定义功耗前先落模式字节 1963：保留高 3 位与最低位，置 0x10。
    /// </summary>
    internal static bool TrySetCustomModeGate()
    {
        if (!AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte mode))
        {
            return false;
        }
        if (!PlSwitch("modeIndex"))
        {
            return true;
        }
        return AcpiDriverEc.TryWriteByte(ModeIndexAddress, (byte)((mode & 0xE0) | 0x10 | (mode & 0x01)));
    }

    internal static bool TryApplyMode(int visualMode, out string detail)
    {
        detail = "";
        if (!Enabled)
        {
            detail = "EC 通道未启用";
            return false;
        }
        if (visualMode is not (0 or 1 or 2 or 3))
        {
            detail = "EC 通道只处理静音/平衡/狂暴/自定义";
            return false;
        }
        try
        {
            if (!AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte currentMode)
                || !AcpiDriverEc.TryReadByte(TriggerAddress, out byte currentTrigger)
                || !AcpiDriverEc.TryReadByte(FanBoostClampAddress, out byte currentClamp))
            {
                detail = "读取 1963/1895/1873 失败";
                return false;
            }
            // 「是否从自定义退出」只看软件记忆：门控字节会被固件周期性锁存回自定义槽，
            // 拿它当状态位会让每次内置档切换都被当成「退出自定义」，还会把切换误判成失败。
            bool flagReadable = AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte flagBefore);
            bool memoryWasCustom = System.Threading.Volatile.Read(ref _selfCustomMode) != 0;
            bool leavingCustom = visualMode != 3 && memoryWasCustom;
            if (visualMode != 3 && !memoryWasCustom && flagReadable && (flagBefore & 0x3F) != 0)
            {
                Logger.WriteErrorIfChanged("gcu-ec-custom-latch", "[gcu-ec] 自定义门控回读 0x" + flagBefore.ToString("X2") + "（记忆里不是自定义）⇒ 按内置档处理，写档后清一次");
            }
            byte target = visualMode is 1 or 3 ? (byte)0x10 : (byte)0x00;
            byte mode = (byte)((currentMode & 0xE0) | (target & 0x1E) | (currentMode & 0x01));
            byte trigger = (byte)((currentTrigger & 0x10) | (visualMode == 2 ? 0x28 : 0x20));
            // 声明了「平衡=工作模式标准 / 静音=工作模式节能 / 狂暴=渲染模式」的机型：
            // 平衡与静音在 EC 上是同一个工作档（0xA0），差别只在参数（由办公预设记录区分）；狂暴走渲染档（0x00）。
            // 未声明 ⇒ 保持原表（静音=0xA0、狂暴/自定义=0x10、其余=0x00）。
            bool officePresetMapping = DeclarationRuntime.Flag("officePreset", "enabled");
            byte clamp;
            if (officePresetMapping)
            {
                clamp = visualMode switch
                {
                    1 => 0x00,
                    3 => 0x10,
                    _ => 0xA0,
                };
            }
            else
            {
                clamp = visualMode == 2 ? (byte)0xA0 : visualMode is 1 or 3 ? (byte)0x10 : (byte)0x00;
            }
            // 保留强冷位（bit6），模式切换不能把强冷关掉。
            clamp = (byte)(clamp | (currentClamp & 0x40));
            // 节能开关（0x07A5 bit2）：静音档置位、其余档位清零 —— 工作档的"标准/节能"在固件里靠这一位区分，
            // 也是固件走静音风扇曲线的开关。读-改-写只碰 bit2，LED / 超频 / 供电位一概不动。
            // 只在声明了本映射的机型上写；写失败只记明细，不判整次切档失败。
            bool quietWrite = true;
            // quietBit=0：该机型固件不用 0x07A5 bit2 区分标准/节能（节能靠槽值区分），跳过这一位。
            if (officePresetMapping && DeclarationRuntime.Flag("officePreset", "quietBit"))
            {
                if (AcpiDriverEc.TryReadByte(QuietModeAddress, out byte quietCurrent))
                {
                    byte quietTarget = (byte)((quietCurrent & 0xFB) | (visualMode == 2 ? 0x04 : 0x00));
                    quietWrite = quietTarget == quietCurrent || AcpiDriverEc.TryWriteByte(QuietModeAddress, quietTarget);
                    Logger.WriteErrorIfChanged("gcu-ec-quiet-bit", "[gcu-ec] 节能开关 0x" + quietCurrent.ToString("X2") + " ⇒ 0x" + quietTarget.ToString("X2") + "（" + (visualMode == 2 ? "节能" : "非节能") + "）write=" + quietWrite);
                }
                else
                {
                    quietWrite = false;
                    Logger.WriteErrorIfChanged("gcu-ec-quiet-bit", "[gcu-ec] 节能开关位读取失败，本次未写");
                }
            }
            // 写入顺序：节能开关 → 风扇 clamp → 自定义档标记 → 模式字节 → trigger → turbo 标记。
            bool customFlagOn = PlSwitch("customFlag");
            bool triggerOn = PlSwitch("trigger");
            bool turboOn = PlSwitch("turbo");
            bool modeOn = PlSwitch("modeIndex");
            bool rampOn = PlSwitch("ramp");
            // customClamp=0：自定义档不写档位字节（0x751），只发槽值 —— 用于验证纯功耗下发是否足够。
            bool customClampOff = visualMode == 3 && !PlSwitch("customClamp");
            bool clampWrite = customClampOff || AcpiDriverEc.TryWriteByte(FanBoostClampAddress, clamp);
            // 内置档功耗槽：该机型固件不按档位字节改槽值（0x751 只分渲染/非渲染），标准/渲染/节能的槽值必须显式下发。
            bool builtInSlotsWrite = true;
            if (DeclarationRuntime.Flag("plCustom", "builtInPl") && visualMode != 3)
            {
                string tierKey = (visualMode == 1) ? "plTurbo" : (visualMode == 2 ? "plSilent" : "plBalance");
                string[] tierParts = (DeclarationRuntime.Param("plCustom", tierKey) ?? "").Split('/');
                if (tierParts.Length == 2 && int.TryParse(tierParts[0].Trim(), out int tierPl1) && int.TryParse(tierParts[1].Trim(), out int tierPl2))
                {
                    if (clampWrite && visualMode == 1)
                    {
                        // 档位字节先生效，槽值随后才被采纳。
                        System.Threading.Thread.Sleep(60);
                    }
                    bool slotW1 = AcpiDriverEc.TryWriteByte(EcPlCustom.RegisterPl1, (byte)tierPl1);
                    bool slotW2 = AcpiDriverEc.TryWriteByte(EcPlCustom.RegisterPl2, (byte)tierPl2);
                    bool slotR1 = AcpiDriverEc.TryReadByte(EcPlCustom.RegisterPl1, out byte slotNow1);
                    bool slotR2 = AcpiDriverEc.TryReadByte(EcPlCustom.RegisterPl2, out byte slotNow2);
                    builtInSlotsWrite = slotW1 && slotW2;
                    Logger.WriteErrorIfChanged("gcu-ec-builtin-pl",
                        "[gcu-ec] 内置档槽值 " + tierKey + " 写 " + tierPl1 + "/" + tierPl2 + " W ⇒ 回读 "
                        + (slotR1 ? slotNow1.ToString() : "读取失败") + "/" + (slotR2 ? slotNow2.ToString() : "读取失败") + " W");
                }
            }
            bool customWrite = visualMode == 3 || !leavingCustom || !customFlagOn
                || AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 64);
            bool rampWrite = !rampOn || AcpiDriverEc.TryWriteByte(FanRampAddress, visualMode == 3 ? 0x81 : visualMode is 1 ? 0x00 : 0x83);
            bool modeWrite = !modeOn || AcpiDriverEc.TryWriteByte(ModeIndexAddress, mode);
            bool triggerWrite = triggerOn ? AcpiDriverEc.TryWriteByte(TriggerAddress, trigger) : true;
            bool turboWrite = turboOn ? AcpiDriverEc.TryWriteByte(TurboTriggerAddress, visualMode == 2 ? 1 : 0) : true;
            bool whiteLedWrite = true;
            bool isolatedWrite = true;
            if (visualMode == 3)
            {
                // 自定义档白灯 + 自定义风扇隔离
                if (AcpiDriverEc.TryReadByte(1830, out byte ac))
                {
                    AcpiDriverEc.TryWriteByte(1830, (byte)(ac | 0x80));
                }
                if (AcpiDriverEc.TryReadByte(1831, out byte cam))
                {
                    AcpiDriverEc.TryWriteByte(1831, (byte)(cam | 0x40));
                }
                if (AcpiDriverEc.TryReadByte(1989, out byte isoCustom))
                {
                    isolatedWrite = AcpiDriverEc.TryWriteByte(1989, (byte)(isoCustom | 0x80));
                }
                if (customFlagOn)
                {
                    customWrite = AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 65);
                    bool customReadBack = AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte customRaw);
                    Logger.WriteErrorIfChanged("gcu-ec-custom-flag", "[gcu-ec] 自定义标志写后 0x" + (customReadBack ? customRaw.ToString("X2") : "读取失败"));
                }
            }
            else
            {
                // 退出自定义必须放在 trigger 之后，否则固件会把自定义门控重新锁存。
                if (customFlagOn)
                {
                    customWrite = AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 64);
                }
                if (AcpiDriverEc.TryReadByte(1830, out byte ac))
                {
                    whiteLedWrite &= AcpiDriverEc.TryWriteByte(1830, (byte)(ac & ~0x80));
                }
                if (AcpiDriverEc.TryReadByte(1831, out byte cam))
                {
                    whiteLedWrite &= AcpiDriverEc.TryWriteByte(1831, (byte)(cam & ~0x40));
                }
                // 内置档交还固件风扇表：清自定义风扇隔离位（1989 bit7）。
                if (AcpiDriverEc.TryReadByte(1989, out byte isolated))
                {
                    isolatedWrite = AcpiDriverEc.TryWriteByte(1989, (byte)(isolated & ~0x80));
                }
                // 内置档一律把使能位清回 0x40：固件在 trigger 写完后会把它重新锁存成自定义槽，
                // 所以清写放在 trigger 之后，写后回读确认，仍被锁回去就补写，直到确实读到 0x40。
                bool customCleared = true;
                if (customFlagOn)
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (!AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte flag) || (flag & 0x3F) == 0)
                        {
                            break;
                        }
                        customCleared &= AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 64);
                        System.Threading.Thread.Sleep(20);
                    }
                    if (AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte clearedFlag))
                    {
                        customCleared &= (clearedFlag & 0x3F) == 0;
                        Logger.WriteErrorIfChanged("gcu-ec-custom-clear", "[gcu-ec] 自定义使能位回读 0x" + clearedFlag.ToString("X2")
                            + (customCleared ? "（使能位已清）" : "（仍被锁存，需检查写序）"));
                    }
                }
                // 清不干净只影响「从自定义退出」这一路的结果判定；本来就在内置档时，
                // 档位是否切成功仍只看模式/trigger/clamp，避免多出来的清写把切换误判成失败。
                customWrite &= customCleared || !leavingCustom;
            }
            bool modeRead = AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte readMode);
            bool triggerReadBack = AcpiDriverEc.TryReadByte(TriggerAddress, out byte readTrigger);
            bool clampRead = AcpiDriverEc.TryReadByte(FanBoostClampAddress, out byte readClamp);
            bool modeOk = !modeOn || (modeRead && (readMode & 0x1E) == (mode & 0x1E));
            bool triggerOk = !triggerOn || (triggerReadBack && readTrigger == trigger);
            bool clampOk = customClampOff || (clampRead && readClamp == clamp);
            // 技术明细只进日志；返回给调用方（界面）的只说结论。
            string technical = $"mode 0x{currentMode:X2}->0x{mode:X2}/0x{readMode:X2} "
                + $"trigger 0x{currentTrigger:X2}->0x{trigger:X2}/0x{readTrigger:X2} "
                + $"clamp->0x{clamp:X2}/0x{readClamp:X2} turbo={visualMode == 2} custom={visualMode == 3} quiet={quietWrite} "
                + $"write={modeWrite}/{triggerWrite}/{turboWrite}/{clampWrite}/{customWrite}/{rampWrite}";
            bool wroteMode = modeWrite && triggerWrite && turboWrite && clampWrite && customWrite && builtInSlotsWrite;
            detail = modeOk && triggerOk && clampOk ? "档位已切换" : (wroteMode ? "档位已切换（未验证）" : "档位未切换");
            if (modeOk && triggerOk && clampOk)
            {
                System.Threading.Interlocked.Exchange(ref _lastSelfWriteTicks, Environment.TickCount64);
                System.Threading.Volatile.Write(ref _selfCustomMode, visualMode == 3 ? 1 : 0);
                if (visualMode != 3)
                {
                    ScheduleCustomFlagRecheck();
                }
            }
            Logger.WriteError("[gcu-ec] EC 模式切换 " + technical + " ⇒ " + (modeOk && triggerOk && clampOk ? "OK" : "FAIL"));
            return modeWrite && triggerWrite && turboWrite && clampWrite && customWrite && modeOk && triggerOk && clampOk;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            Logger.WriteError("[gcu-ec] EC 模式切换异常：" + detail);
            return false;
        }
    }

    /// <summary>离开自定义后的延迟复查：固件可能在模式写完之后又把自定义使能位锁回去。</summary>
    private static void ScheduleCustomFlagRecheck()
    {
        try
        {
            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte flag) && (flag & 0x3F) != 0)
                    {
                        bool cleared = AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 64);
                        bool readBack = AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte after);
                        Logger.WriteError("[gcu-ec] 离开自定义复查：1.5s 后被锁回 0x" + flag.ToString("X2")
                            + "（再次清除=" + cleared + "，回读=" + (readBack ? "0x" + after.ToString("X2") : "读取失败") + "）");
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteError("[gcu-ec] 离开自定义复查失败：" + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    timer?.Dispose();
                }
            }, null, 1500, System.Threading.Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Logger.WriteError("[gcu-ec] 离开自定义复查排程失败：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
