using System;
using System.Threading;
using System.IO;
using System.Linq;

namespace MechrevoLite.Hardware;

// 机型适配参考：OpenRevo (https://github.com/faintonce/open-revo, GPL-3.0)。

/// <summary>NUC/UMI 风扇表：16 段温度/占空比 + 模式默认表 + 自定义曲线。</summary>
internal static class NucFanTable
{
    private const int CpuUptBase = 3840;
    private const int CpuDowntBase = 3856;
    private const int CpuDutyBase = 3872;
    private const int GpuUptBase = 3888;
    private const int GpuDowntBase = 3904;
    private const int GpuDutyBase = 3920;
    private const int GpuMax1 = 3933;
    private const int GpuMax2 = 3934;
    private const int TableCtrl = 3935;
    private const int TableLatch = 1990;
    private const int ApOemByte = 1857;
    private const int FanBoostClamp = 1873;
    private const int FanRamp = 1927;
    private const int FanIsolated = 1989;
    private const int TriggerByte = 1895;
    private const int LatchBit = 0x04;

    private sealed class ModeTable
    {
        public required string Name;
        public required byte[] CpuUp;
        public required byte[] CpuDown;
        public required byte[] CpuDuty;
        public required byte[] GpuUp;
        public required byte[] GpuDown;
        public required byte[] GpuDuty;
        public byte MaxDuty => GpuDuty[15];
    }

    // 官方 30 系 NUC/UMI 三档默认表；性能档/平衡档/静音档必须有不同占空比上限。
    private static readonly ModeTable Office = new()
    {
        Name = "Office",
        CpuUp = new byte[] { 0, 45, 51, 56, 62, 67, 73, 78, 84, 89, 95, 255, 255, 255, 255, 255 },
        CpuDown = new byte[] { 48, 58, 60, 62, 64, 66, 68, 70, 255, 255, 255, 255, 255, 255, 255, 255 },
        CpuDuty = new byte[] { 0, 60, 60, 60, 60, 70, 70, 90, 110, 110, 110, 110, 110, 110, 110, 110 },
        GpuUp = new byte[] { 0, 45, 50, 54, 59, 64, 68, 73, 78, 82, 87, 255, 255, 255, 255, 255 },
        GpuDown = new byte[] { 48, 56, 58, 58, 60, 63, 65, 67, 255, 255, 255, 255, 255, 255, 255, 255 },
        GpuDuty = new byte[] { 0, 60, 60, 60, 60, 70, 70, 90, 110, 110, 110, 110, 110, 110, 110, 110 }
    };

    private static readonly ModeTable Gaming = new()
    {
        Name = "Gaming",
        CpuUp = new byte[] { 0, 45, 51, 56, 62, 67, 73, 78, 84, 89, 95, 255, 255, 255, 255, 255 },
        CpuDown = new byte[] { 48, 58, 61, 65, 69, 73, 76, 79, 82, 255, 255, 255, 255, 255, 255, 255 },
        CpuDuty = new byte[] { 0, 60, 70, 80, 100, 110, 120, 130, 150, 160, 160, 160, 160, 160, 160, 160 },
        GpuUp = new byte[] { 0, 45, 50, 54, 59, 64, 68, 73, 78, 82, 87, 255, 255, 255, 255, 255 },
        GpuDown = new byte[] { 48, 58, 61, 65, 69, 73, 76, 79, 82, 255, 255, 255, 255, 255, 255, 255 },
        GpuDuty = new byte[] { 0, 60, 70, 80, 100, 110, 120, 130, 150, 160, 160, 160, 160, 160, 160, 160 }
    };

    private static readonly ModeTable Turbo = new()
    {
        Name = "Turbo",
        CpuUp = new byte[] { 0, 45, 51, 56, 62, 67, 73, 78, 84, 89, 95, 255, 255, 255, 255, 255 },
        CpuDown = new byte[] { 48, 58, 61, 65, 69, 73, 76, 79, 82, 85, 255, 255, 255, 255, 255, 255 },
        CpuDuty = new byte[] { 0, 60, 70, 80, 100, 110, 120, 130, 150, 160, 180, 180, 180, 180, 180, 180 },
        GpuUp = new byte[] { 0, 45, 50, 54, 59, 64, 68, 73, 78, 82, 87, 255, 255, 255, 255, 255 },
        GpuDown = new byte[] { 48, 58, 61, 65, 69, 73, 76, 79, 82, 85, 255, 255, 255, 255, 255, 255 },
        GpuDuty = new byte[] { 0, 60, 70, 80, 100, 110, 120, 130, 150, 160, 180, 180, 180, 180, 180, 180 }
    };

    private static ModeTable _lastDefault = Gaming;

    internal static bool CaptureDefaultOnce()
    {
        _lastDefault ??= Gaming;
        return true;
    }

    /// <summary>自定义曲线退出/恢复默认时，恢复最近一次应用的模式默认表（不是全 0 快照）。</summary>
    internal static bool TryRestoreDefault(out string detail)
    {
        bool ok = ApplyTable(_lastDefault ?? Gaming, out detail);
        bool isolated = TrySetFanIsolated(false, out string isolatedDetail);
        detail += " · " + isolatedDetail;
        return ok && isolated;
    }

    /// <summary>按模式应用官方默认表：静音=Office、平衡=Gaming、狂暴/极致=Turbo。</summary>
    internal static bool TryApplyDefaultMode(int visualMode, out string detail)
    {
        detail = "";
        ModeTable? table = visualMode switch
        {
            2 => Office,
            0 => Gaming,
            1 or 5 => Turbo,
            _ => null
        };
        if (table == null)
        {
            detail = "自定义档不应用默认风扇表";
            return true;
        }
        _lastDefault = table;
        bool ok = ApplyTable(table, out detail);
        // 切回官方默认表时同时交还 FanIsolated，避免上一次自定义曲线留下独立输出位。
        bool isolated = TrySetFanIsolated(false, out string isolatedDetail);
        detail += " · " + isolatedDetail;
        return ok && isolated;
    }

    private static bool ApplyTable(ModeTable table, out string detail)
    {
        detail = "";
        // 强冷开启时不要覆盖风扇表，避免抢固件强冷控制。
        if (TryRead(FanBoostClamp, out int clamp) && (clamp & 0x40) != 0)
        {
            detail = "强冷开启，跳过风扇表写入（保持强冷全速）";
            return true;
        }

        // 官方顺序：APExist → latch 预脉冲 → AP Active → 写表 → 写 max/ctrl → latch 提交。
        if (TryRead(ApOemByte, out int ap))
        {
            TryWrite(ApOemByte, ap | 0x01);
        }
        TryWrite(TableLatch, LatchBit);
        TryWrite(TableLatch, 0x00);
        if (TryRead(ApOemByte, out ap))
        {
            TryWrite(ApOemByte, ap | 0x81);
        }

        bool ok = true;
        byte[] cpuDown = BuildDown(table.CpuUp);
        byte[] gpuDown = BuildDown(table.GpuUp);
        // 保留原表头第一个 Down 值（48），其余按新 10 点 Up 轴重新生成。
        cpuDown[0] = table.CpuDown[0];
        gpuDown[0] = table.GpuDown[0];
        for (int i = 0; i < 16; i++) ok &= TryWrite(CpuUptBase + i, table.CpuUp[i]);
        for (int i = 0; i < 16; i++) ok &= TryWrite(CpuDowntBase + i, cpuDown[i]);
        for (int i = 0; i < 16; i++) ok &= TryWrite(CpuDutyBase + i, i == 0 ? 0 : table.CpuDuty[i]);
        for (int i = 0; i < 16; i++) ok &= TryWrite(GpuUptBase + i, table.GpuUp[i]);
        for (int i = 0; i < 16; i++) ok &= TryWrite(GpuDowntBase + i, gpuDown[i]);
        // GPU 占空比只写 13 段（3920..3932）；3933–3935 是 Max1/Max2/TableCtrl，不能混写。
        for (int i = 0; i < 13; i++) ok &= TryWrite(GpuDutyBase + i, i == 0 ? 0 : table.GpuDuty[i]);

        int maxDuty = table.MaxDuty;
        int ctrl = Math.Max(maxDuty, 160); // 官方活动控制字节至少 0xA0，绝不能写 0/1/2/3。
        ok &= TryWrite(GpuMax1, maxDuty);
        ok &= TryWrite(GpuMax2, maxDuty);
        ok &= TryWrite(TableCtrl, ctrl);
        TryWrite(TableLatch, LatchBit);
        // 只置 bit5 提交；bit0-2 是快捷开关命令码、bit4 是 USB 充电脉冲，一律不动。
        EcTriggerByte.TryApply(EcTriggerByte.Latch, 0);

        bool readMax1 = TryRead(GpuMax1, out int read1);
        bool ctrlReadOk = TryRead(TableCtrl, out int ctrlReadback);
        detail = $"{table.Name} 默认表已应用 · maxDuty={maxDuty} ctrl={ctrl}（回读 {ctrlReadback}）";
        return ok && readMax1 && read1 == maxDuty && ctrlReadOk && ctrlReadback == ctrl;
    }

    /// <summary>风扇 Ramp Rate：1927，0x80|步长 或 0x81（关闭）。</summary>
    internal static bool TrySetFanRamp(bool enabled, int speedMs, out string detail)
    {
        detail = "";
        int value = enabled
            ? 0x80 | (Math.Clamp(speedMs, 100, 12700) / 100)
            : 0x81;
        if (!TryWrite(FanRamp, value))
        {
            detail = "FanRamp 写入失败";
            return false;
        }
        bool ok = TryRead(FanRamp, out int after) && after == value;
        detail = $"FanRamp=0x{value:X2} 回读=0x{after:X2}";
        return ok;
    }

    /// <summary>Fan Isolated：1989 bit7，读改写保留其它位。</summary>
    internal static bool TrySetFanIsolated(bool enabled, out string detail)
    {
        detail = "";
        // 强冷进行中不碰隔离位：隔离位属于「启用自定义曲线」的一部分，曲线没写进去就不该留下这半个状态。
        // 只拦「设置」（enabled=true）；交回固件时的清除必须照常执行。
        if (enabled && TryRead(FanBoostClamp, out int boostClamp) && (boostClamp & 0x40) != 0)
        {
            detail = "强冷开启，跳过风扇曲线写入";
            return false;
        }
        if (!TryRead(FanIsolated, out int current))
        {
            detail = "FanIsolated 读取失败";
            return false;
        }
        int value = enabled ? (current | 0x80) : (current & ~0x80);
        if (!TryWrite(FanIsolated, value))
        {
            detail = "FanIsolated 写入失败";
            return false;
        }
        bool ok = TryRead(FanIsolated, out int after) && after == value;
        detail = $"FanIsolated=0x{value:X2} 回读=0x{after:X2}";
        return ok;
    }

    /// <summary>本机是否有独立 GPU 表：声明表 fanCurve.params.gpuCurve 写 0 表示只有一路 PWM，GPU 表不写。</summary>
    internal static bool GpuCurveSupported
        => !string.Equals(DeclarationRuntime.Param("fanCurve", "gpuCurve"), "0", StringComparison.Ordinal);

    internal static bool TryApply(byte[] cpuTemps, int[] cpuDuties, byte[] gpuTemps, int[] gpuDuties, out string detail)
        => TryApply(cpuTemps, cpuDuties, gpuTemps, gpuDuties, "应用", out detail);

    /// <summary>带来源的下发：整表写入串行化，同一张表不重复写，现场一致时直接跳过。</summary>
    internal static bool TryApply(byte[] cpuTemps, int[] cpuDuties, byte[] gpuTemps, int[] gpuDuties, string source, out string detail, bool includeGpu = true)
    {
        if (DeclarationRuntime.FeatureBlocked("fanCurve", "ec", "gcu"))
        {
            detail = "声明表判定本机无风扇曲线通道，未写任何寄存器";
            return false;
        }
        // 老双模模具的固件自己维护风扇状态机：往里塞 RAM 风扇表会把它搞乱，直接让给固件曲线。
        if (UefiGpuMux.IsLegacyDualModeConfirmed)
        {
            detail = "老双模模具：风扇表由固件维护，软件不注入（" + source + "）";
            Logger.WriteLine("[fan] " + detail);
            return false;
        }
        WriteGate.Wait();
        try
        {
            return TryApplyLocked(cpuTemps, cpuDuties, gpuTemps, gpuDuties, source, includeGpu, out detail);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>写表闸门：软件内所有来源（应用/切档/开机重放）共用，避免并发写半截表。</summary>
    private static readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);
    private static string _lastSignature = string.Empty;
    private static DateTime _lastAppliedAt = DateTime.MinValue;

    private static bool TryApplyLocked(byte[] cpuTemps, int[] cpuDuties, byte[] gpuTemps, int[] gpuDuties, string source, bool includeGpu, out string detail)
    {
        detail = "";
        if (cpuTemps.Length != 16 || gpuTemps.Length != 16 || cpuDuties.Length != 16 || gpuDuties.Length != 16)
        {
            detail = "风扇曲线必须是 16 段";
            return false;
        }
        if (TryRead(FanBoostClamp, out int clamp) && (clamp & 0x40) != 0)
        {
            detail = "强冷开启，跳过风扇曲线写入";
            return false;
        }

        byte[] cpuDuty = NormalizeDuty(cpuDuties);
        // 只有一路 PWM 的机型不碰 GPU 表：整段跳过，也不参与最大占空比计算。
        byte[] gpuDuty = includeGpu ? NormalizeDuty(gpuDuties) : (byte[])cpuDuty.Clone();
        if (includeGpu)
        {
            byte gpuTail = Math.Max(gpuDuty[12], gpuDuty[15]);
            gpuDuty[13] = Math.Max(gpuDuty[13], gpuTail);
            gpuDuty[14] = Math.Max(gpuDuty[14], gpuTail);
            gpuDuty[15] = gpuTail;
        }
        byte[] cpuDown = BuildDown(cpuTemps);
        byte[] gpuDown = includeGpu ? BuildDown(gpuTemps) : (byte[])cpuDown.Clone();

        int maxDuty = Math.Max(cpuDuty[15], gpuDuty[15]);
        int ctrl = Math.Max(maxDuty, 160);
        string signature = Convert.ToBase64String(cpuTemps) + "|" + string.Join(",", cpuDuty)
            + "|" + Convert.ToBase64String(gpuTemps) + "|" + string.Join(",", gpuDuty);
        if (signature == _lastSignature && (DateTime.UtcNow - _lastAppliedAt).TotalSeconds < 3)
        {
            detail = "3 秒内重复下发同一张表，已跳过（来源=" + source + "）";
            Logger.WriteLine("[fan] 下发跳过：" + detail);
            return true;
        }
        if (TableMatches(cpuTemps, cpuDown, cpuDuty, gpuTemps, gpuDown, gpuDuty, maxDuty, ctrl))
        {
            _lastSignature = signature;
            _lastAppliedAt = DateTime.UtcNow;
            detail = "EC 现表与目标表一致，未重写（来源=" + source + "）";
            Logger.WriteLine("[fan] 下发跳过：" + detail);
            return true;
        }
        if (TryRead(ApOemByte, out int ap)) TryWrite(ApOemByte, ap | 0x81);
        TryWrite(TableLatch, 0x00);
        for (int i = 0; i < 16; i++) if (!TryWrite(CpuUptBase + i, cpuTemps[i])) { detail = "CPU UpT 写入失败@" + i; return false; }
        for (int i = 0; i < 16; i++) if (!TryWrite(CpuDowntBase + i, cpuDown[i])) { detail = "CPU DownT 写入失败@" + i; return false; }
        for (int i = 0; i < 16; i++) if (!TryWrite(CpuDutyBase + i, cpuDuty[i])) { detail = "CPU Duty 写入失败@" + i; return false; }
        if (includeGpu)
        {
            for (int i = 0; i < 16; i++) if (!TryWrite(GpuUptBase + i, gpuTemps[i])) { detail = "GPU UpT 写入失败@" + i; return false; }
            for (int i = 0; i < 16; i++) if (!TryWrite(GpuDowntBase + i, gpuDown[i])) { detail = "GPU DownT 写入失败@" + i; return false; }
            for (int i = 0; i < 13; i++) if (!TryWrite(GpuDutyBase + i, gpuDuty[i])) { detail = "GPU Duty 写入失败@" + i; return false; }
        }
        // 阈值 / 控制器 / 锁存也要确认写进去了：只写表体、尾部失败仍报成功，会让上层以为曲线已生效。
        bool tailOk = TryWrite(GpuMax1, maxDuty);
        tailOk &= TryWrite(GpuMax2, maxDuty);
        tailOk &= TryWrite(TableCtrl, ctrl);
        tailOk &= TryWrite(TableLatch, LatchBit);
        if (!tailOk)
        {
            detail = "最大占空比阈值 / 表控制器 / 锁存写入失败";
            return false;
        }
        _lastSignature = signature;
        _lastAppliedAt = DateTime.UtcNow;
        Logger.WriteLine("[fan] 下发来源=" + source);
        detail = $"CPU={string.Join(',', cpuDuty)} GPU={string.Join(',', gpuDuty)} max={maxDuty} ctrl={ctrl}";
        return true;
    }

    /// <summary>只读当前 EC 风扇表（UpT + Duty），用于无 GCU 时替代写死的默认表；
    /// 数组下标与 EC 槽位一一对应，读到的就是槽里的值。重复的 0℃ 占位槽只留一个，
    /// 避免历史数据把真实点位挤到后面。</summary>
    internal static bool TryReadCurrent(out byte[] cpuUpT, out byte[] cpuDuty, out byte[] gpuUpT, out byte[] gpuDuty)
    {
        byte[] ct = new byte[16];
        byte[] cd = new byte[16];
        byte[] gt = new byte[16];
        byte[] gd = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            if (!TryRead(CpuUptBase + i, out int cpuTemp)
                || !TryRead(CpuDutyBase + i, out int cpuRaw)
                || !TryRead(GpuUptBase + i, out int gpuTemp)
                || !TryRead(GpuDutyBase + Math.Min(i, 12), out int gpuRaw))
            {
                cpuUpT = new byte[16];
                cpuDuty = new byte[16];
                gpuUpT = new byte[16];
                gpuDuty = new byte[16];
                return false;
            }
            ct[i] = (byte)cpuTemp;
            cd[i] = (byte)Math.Clamp(cpuRaw / 2, 0, 100);
            gt[i] = (byte)gpuTemp;
            gd[i] = (byte)Math.Clamp(gpuRaw / 2, 0, 100);
        }
        cpuUpT = SqueezePlaceholders(ct, cd, out cpuDuty);
        gpuUpT = SqueezePlaceholders(gt, gd, out gpuDuty);
        return true;
    }

    /// <summary>把连续重复的 0℃ 占位槽压成一个；遇到 255 结束位后按结束位补齐。</summary>
    private static byte[] SqueezePlaceholders(byte[] temps, byte[] duties, out byte[] squeezedDuties)
    {
        byte[] result = new byte[16];
        byte[] duty = new byte[16];
        int n = 0;
        bool sawZero = false;
        for (int i = 0; i < 16 && n < 16; i++)
        {
            if (temps[i] == 255)
            {
                break;
            }
            if (temps[i] == 0)
            {
                if (sawZero)
                {
                    continue;
                }
                sawZero = true;
            }
            result[n] = temps[i];
            duty[n] = duties[i];
            n++;
        }
        byte tailDuty = n > 0 ? duty[n - 1] : (byte)0;
        for (int i = n; i < 16; i++)
        {
            result[i] = 255;
            duty[i] = tailDuty;
        }
        squeezedDuties = duty;
        return result;
    }

    /// <summary>目标表与 EC 现表逐字节比对：全等就不必再写一遍。</summary>
    private static bool TableMatches(byte[] cpuUp, byte[] cpuDown, byte[] cpuDuty, byte[] gpuUp, byte[] gpuDown, byte[] gpuDuty,
        int maxDuty, int ctrl)
    {
        for (int i = 0; i < 16; i++)
        {
            if (!TryRead(CpuUptBase + i, out int v) || v != cpuUp[i]) return false;
            if (!TryRead(CpuDowntBase + i, out v) || v != cpuDown[i]) return false;
            if (!TryRead(CpuDutyBase + i, out v) || v != cpuDuty[i]) return false;
            if (!TryRead(GpuUptBase + i, out v) || v != gpuUp[i]) return false;
            if (!TryRead(GpuDowntBase + i, out v) || v != gpuDown[i]) return false;
        }
        for (int i = 0; i < 13; i++)
        {
            if (!TryRead(GpuDutyBase + i, out int g) || g != gpuDuty[i]) return false;
        }
        // 表体一致不代表生效：阈值与控制器没写进去时 EC 仍按旧上限跑。
        if (!TryRead(GpuMax1, out int m1) || m1 != maxDuty) return false;
        if (!TryRead(GpuMax2, out int m2) || m2 != maxDuty) return false;
        if (!TryRead(TableCtrl, out int c) || c != ctrl) return false;
        return true;
    }

    private static byte[] NormalizeDuty(int[] percents) { byte[] result = new byte[16]; int running = 0; for (int i = 0; i < 16; i++) { int raw = Math.Clamp(percents[i], 0, 100) * 2; running = Math.Max(running, raw); result[i] = (byte)running; } return result; }
    private static byte[] BuildDown(byte[] upt) { byte[] down = new byte[16]; int previous = 0; for (int i = 1; i < 16; i++) { if (upt[i] == 255) { down[i] = 255; continue; } int hysteresis = upt[i] <= 55 ? 8 : 5; int value = Math.Max(previous, upt[i] - hysteresis); value = Math.Max(0, Math.Min(value, upt[i])); down[i] = (byte)value; previous = value; } return down; }
    private static bool TryRead(int address, out int value) { if (AcpiDriverEc.TryReadByte(address, out byte b)) { value = b; return true; } if (EcPortIo.TryReadByteStable(address, out int legacy, out _)) { value = legacy; return true; } value = -1; return false; }
    private static bool TryWrite(int address, int value) => AcpiDriverEc.TryWriteByte(address, value) || EcPortIo.TryWriteByte(address, value);
}
