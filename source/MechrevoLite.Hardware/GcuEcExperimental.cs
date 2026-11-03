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
    private static int _probeLogged;

    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("LM_GCU_EC_EXPERIMENT"), "1", StringComparison.Ordinal)
        || AppConfig.Get("gcu_ec_experiment", 0) != 0;

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

    /// <summary>GCU 未接管时直接从 EC 读当前模式：狂暴=1963 bit4；静音=1895 bit3；其余平衡。</summary>
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
                || !AcpiDriverEc.TryReadByte(TriggerAddress, out byte trigger))
            {
                return false;
            }
            if ((mode & 0x10) != 0)
            {
                visualMode = 1;
            }
            else if ((trigger & 0x08) != 0)
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
        if (visualMode is not (0 or 1 or 2))
        {
            detail = "实验通道只处理静音/平衡/狂暴";
            return false;
        }
        try
        {
            if (!AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte currentMode)
                || !AcpiDriverEc.TryReadByte(TriggerAddress, out byte currentTrigger))
            {
                detail = "读取 1963/1895 失败";
                return false;
            }
            byte target = visualMode == 1 ? (byte)0x10 : (byte)0x00;
            byte mode = (byte)((currentMode & 0xE0) | (target & 0x1E) | (currentMode & 0x01));
            byte trigger = (byte)((currentTrigger & 0x10) | (visualMode == 2 ? 0x28 : 0x20));
            bool modeWrite = AcpiDriverEc.TryWriteByte(ModeIndexAddress, mode);
            bool triggerWrite = AcpiDriverEc.TryWriteByte(TriggerAddress, trigger);
            bool turboWrite = AcpiDriverEc.TryWriteByte(TurboTriggerAddress, visualMode == 2 ? 1 : 0);
            bool modeRead = AcpiDriverEc.TryReadByte(ModeIndexAddress, out byte readMode);
            bool triggerReadBack = AcpiDriverEc.TryReadByte(TriggerAddress, out byte readTrigger);
            bool modeOk = modeRead && (readMode & 0x1E) == (mode & 0x1E);
            bool triggerOk = triggerReadBack && readTrigger == trigger;
            detail = $"mode 0x{currentMode:X2}->0x{mode:X2}/0x{readMode:X2} "
                + $"trigger 0x{currentTrigger:X2}->0x{trigger:X2}/0x{readTrigger:X2} "
                + $"turbo={visualMode == 2} write={modeWrite}/{triggerWrite}/{turboWrite}";
            Logger.WriteError("[gcu-ec] 实验模式切换 " + detail + " ⇒ " + (modeOk && triggerOk ? "OK" : "FAIL"));
            return modeWrite && triggerWrite && turboWrite && modeOk && triggerOk;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            Logger.WriteError("[gcu-ec] 实验模式切换异常：" + detail);
            return false;
        }
    }
}
