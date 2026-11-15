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
    private const int CustomModeFlagAddress = 1798;
    private static long _lastSelfWriteTicks;
    private static int _lastReadVisual = -1;
    private static int _selfCustomMode;
    private static string _lastRegisterSnapshot = "";
    private const int FanRampAddress = 1927;
    private static int _probeLogged;

    /// <summary>EC 控制模式是否生效；不再是实验开关。</summary>
    internal static bool Enabled => GcuEcMode.IsEcMode;

    /// <summary>EC 本地灯带可用。</summary>
    internal static bool LocalLightbar => Enabled && IteLightingLocal.HasLightbar;

    /// <summary>EC 本地 Logo 灯可用。</summary>
    internal static bool LocalLogoLight => Enabled && IteLightingLocal.HasUsbLogo;

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
            // clamp(1873) 高半字节才是可靠档位：固件改档时不一定会清 mode 的 bit4，
            // 用 mode bit4 优先会把"实际静音/平衡"误判成狂暴（实测：静音 clamp=0xA0 + mode=0x10）。
            if (custom == 65)
            {
                visualMode = 3;
            }
            else if ((clamp & 0xF0) == 0xA0)
            {
                visualMode = 2;
            }
            else if ((clamp & 0xF0) == 0x10)
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
            // 自定义档兜底：本程序刚写过自定义、而硬件回读还没跟上时，仍按自定义显示；60 秒后以硬件为准。
            if (visualMode != 3 && System.Threading.Volatile.Read(ref _selfCustomMode) != 0)
            {
                if (SelfWroteRecently(60000))
                {
                    visualMode = 3;
                }
                else
                {
                    System.Threading.Volatile.Write(ref _selfCustomMode, 0);
                }
            }
            Logger.WriteErrorIfChanged("gcu-ec-visual-raw", "[gcu-ec] 视觉档读回 mode=0x" + mode.ToString("X2") + " turbo=0x" + turbo.ToString("X2") + " custom=0x" + custom.ToString("X2") + " clamp=0x" + clamp.ToString("X2") + " ⇒ " + visualMode);
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
            detail = "实验通道未启用";
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

    internal static bool TryApplyMode(int visualMode, out string detail)
    {
        detail = "";
        if (!Enabled)
        {
            detail = "实验通道未启用";
            return false;
        }
        if (visualMode is not (0 or 1 or 2 or 3))
        {
            detail = "实验通道只处理静音/平衡/狂暴/自定义";
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
            byte target = visualMode is 1 or 3 ? (byte)0x10 : (byte)0x00;
            byte mode = (byte)((currentMode & 0xE0) | (target & 0x1E) | (currentMode & 0x01));
            byte trigger = (byte)((currentTrigger & 0x10) | (visualMode == 2 ? 0x28 : 0x20));
            byte clamp = visualMode == 2 ? (byte)0xA0 : visualMode is 1 or 3 ? (byte)0x10 : (byte)0x00;
            // 保留强冷位（bit6），模式切换不能把强冷关掉。
            clamp = (byte)(clamp | (currentClamp & 0x40));
            // 写入顺序：风扇 clamp → 自定义档标记 → 模式字节 → trigger → turbo 标记。
            bool clampWrite = AcpiDriverEc.TryWriteByte(FanBoostClampAddress, clamp);
            bool customWrite = visualMode == 3
                || AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 64);
            bool rampWrite = AcpiDriverEc.TryWriteByte(FanRampAddress, visualMode == 3 ? 0x81 : visualMode is 1 ? 0x00 : 0x83);
            bool modeWrite = AcpiDriverEc.TryWriteByte(ModeIndexAddress, mode);
            bool triggerWrite = AcpiDriverEc.TryWriteByte(TriggerAddress, trigger);
            bool turboWrite = AcpiDriverEc.TryWriteByte(TurboTriggerAddress, visualMode == 2 ? 1 : 0);
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
                customWrite = AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 65);
                bool customReadBack = AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte customRaw);
                Logger.WriteErrorIfChanged("gcu-ec-custom-flag", "[gcu-ec] 自定义标志写后 0x" + (customReadBack ? customRaw.ToString("X2") : "读取失败"));
            }
            else
            {
                // 退出自定义必须放在 trigger 之后，否则固件会把自定义门控重新锁存。
                customWrite = AcpiDriverEc.TryWriteByte(CustomModeFlagAddress, 64);
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
            }
            bool modeRead = AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte readMode);
            bool triggerReadBack = AcpiDriverEc.TryReadByte(TriggerAddress, out byte readTrigger);
            bool clampRead = AcpiDriverEc.TryReadByte(FanBoostClampAddress, out byte readClamp);
            bool modeOk = modeRead && (readMode & 0x1E) == (mode & 0x1E);
            bool triggerOk = triggerReadBack && readTrigger == trigger;
            bool clampOk = clampRead && readClamp == clamp;
            detail = $"mode 0x{currentMode:X2}->0x{mode:X2}/0x{readMode:X2} "
                + $"trigger 0x{currentTrigger:X2}->0x{trigger:X2}/0x{readTrigger:X2} "
                + $"clamp->0x{clamp:X2}/0x{readClamp:X2} turbo={visualMode == 2} custom={visualMode == 3} "
                + $"write={modeWrite}/{triggerWrite}/{turboWrite}/{clampWrite}/{customWrite}/{rampWrite}";
            if (modeOk && triggerOk && clampOk)
            {
                System.Threading.Interlocked.Exchange(ref _lastSelfWriteTicks, Environment.TickCount64);
                System.Threading.Volatile.Write(ref _selfCustomMode, visualMode == 3 ? 1 : 0);
            }
            Logger.WriteError("[gcu-ec] 实验模式切换 " + detail + " ⇒ " + (modeOk && triggerOk && clampOk ? "OK" : "FAIL"));
            return modeWrite && triggerWrite && turboWrite && clampWrite && customWrite && modeOk && triggerOk && clampOk;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            Logger.WriteError("[gcu-ec] 实验模式切换异常：" + detail);
            return false;
        }
    }
}
