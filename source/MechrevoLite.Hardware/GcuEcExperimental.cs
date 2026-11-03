using System;
using System.Threading;
using MechrevoLite.Helpers;

namespace MechrevoLite.Hardware;

/// <summary>
/// 实验性 GCU EC 模式通道：只走 ACPIDriver H2RAM，不扫描 EC 端口。
/// 仅当环境变量 LM_GCU_EC_EXPERIMENT=1 或配置 gcu_ec_experiment=1 时启用。
/// </summary>
internal static class GcuEcExperimental
{
    private const int ModeIndexAddress = 1963;
    private const int TriggerAddress = 1895;
    private const int TurboTriggerAddress = 1115;
    private const int FanBoostClampAddress = 1873;
    private const int CustomModeFlagAddress = 1798;
    private const int FanRampAddress = 1927;
    private static int _probeLogged;

    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("LM_GCU_EC_EXPERIMENT"), "1", StringComparison.Ordinal)
        || AppConfig.Get("gcu_ec_experiment", 0) != 0;

    /// <summary>实验启动时直接进入 EC-only，不等待 GCU 连接。</summary>
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
                || !AcpiDriverEc.TryReadByte(CustomModeFlagAddress, out byte custom))
            {
                return false;
            }
            if (custom == 65)
            {
                visualMode = 3;
            }
            else if ((mode & 0x10) != 0)
            {
                visualMode = 1;
            }
            else if ((clamp & 0xF0) == 0xA0)
            {
                visualMode = 2;
            }
            else
            {
                visualMode = 0;
            }
            return true;
        }
        catch
        {
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
            // 按官方顺序：风扇 clamp → 自定义档标记 → 模式字节 → trigger → turbo 标记。
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
