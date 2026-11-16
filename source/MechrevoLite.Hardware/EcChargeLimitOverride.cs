using System;

namespace MechrevoLite.Hardware;

/// <summary>非 10/16/20/30 系的 EC 三档充电上限，按 OR 平台门控顺序写入。</summary>
internal static class EcChargeLimitOverride
{
    private const int UpperRegister = 1977;   // 0x7B9
    private const int LowerRegister = 2000;   // 0x7D0
    private const int OverrideRegister = 1904; // 0x770

    /// <summary>
    /// 0x770（ROM ID 首字节）只在读到 4/5 时才复位成 0xFF —— 那是旧版本伪装留下的残留值。
    /// 其它值一律不写：读不到不写、也不据此判失败；正常值原样保留。
    /// </summary>
    private static bool NormalizeOverrideIfResidual()
    {
        if (!AcpiDriverEc.TryReadByte(OverrideRegister, out byte current))
        {
            return true;
        }
        if (current != 0x04 && current != 0x05)
        {
            return true;
        }
        bool reset = AcpiDriverEc.TryWriteByte(OverrideRegister, 0xFF);
        Logger.WriteLine($"[charge-limit] 0x{OverrideRegister:X3} 残留 0x{current:X2} → 复位 0xFF（残留伪装值，非正常平台值）");
        return reset;
    }
    private const int PlatformIdRegister = 1987; // 0x7C3
    private const int ProtectionModeRegister = 1958; // 0x7A6
    private const int TouchpadBit = 0x08;


    /// <summary>
    /// 三档限充（不显示 Windows“智能充电”提示）。
    /// 长效：清空 0x7B9/0x7D0；平衡/工作站：写上限+下限，门控 2.5s 后恢复并清空 0x7B9。
    /// 0x7A6 只改 bit4/5，bit0 智能充电提示位保持 0。
    /// </summary>

    /// <summary>读 0x7A6 bit4/5 档位：0 长效 / 1 平衡 / 2 工作站；同时回读 0x7B9。</summary>
    internal static bool TryReadMode(out int mode, out int upperPercent, out byte raw)
    {
        mode = -1;
        upperPercent = -1;
        raw = 0;
        if (!AcpiDriverEc.TryReadByte(ProtectionModeRegister, out raw))
        {
            return false;
        }
        int code = (raw >> 4) & 0x03;
        mode = code switch { 0 => 0, 1 => 1, 2 => 2, _ => -1 };
        if (AcpiDriverEc.TryReadByte(UpperRegister, out byte upper))
        {
            upperPercent = upper & 0x7F;
        }
        return mode >= 0;
    }

    /// <summary>档位对应的 0x7A6 模式位（100% = 不限充 = 0x00；≤65% = 工作站 0x20；其余 0x10）。</summary>
    internal static int ComputeModeBits(int percent)
    {
        return ChargeModePolicy.ComputeModeBits(percent);
    }

    internal static bool TrySetNoSmartHint(int percent, out string detail)
    {
        detail = "";
        if (percent < 40 || percent > 100)
        {
            detail = "充电上限越界";
            return false;
        }
        try
        {
        int mode = ComputeModeBits(percent);
            int upper = percent >= 95 ? 0 : percent;
            int lower = percent >= 95 ? 0 : Math.Max(0, percent - 5);
            bool platformRead = AcpiDriverEc.TryReadByte(PlatformIdRegister, out byte platformOriginal);
            bool ov = NormalizeOverrideIfResidual();
            bool up = AcpiDriverEc.TryWriteByte(UpperRegister, (byte)upper);
            bool down = AcpiDriverEc.TryWriteByte(LowerRegister, (byte)lower);
            bool modeOk = false;
            if (AcpiDriverEc.TryReadByte(ProtectionModeRegister, out byte protection))
            {
                modeOk = AcpiDriverEc.TryWriteByte(ProtectionModeRegister,
                    (byte)((protection & ~0x31) | mode));
            }

            bool gated = false;
            if (percent < 95)
            {
                gated = AcpiDriverEc.TryWriteByte(PlatformIdRegister, 0x04);
                if (gated)
                {
                    System.Threading.Thread.Sleep(2500);
                    if (ChargeGatePin.Enabled)
                    {
                        // 配置开关打开：门控保持 4、不还原；上限仍要清掉，避免 Windows 显示智能充电。
                        ChargeGatePin.MarkPinned(platformRead ? platformOriginal : 0x07, percent);
                        ChargeGateKeepAlive.Start(percent);
                        AcpiDriverEc.TryWriteByte(UpperRegister, 0);
                        Logger.WriteLine($"限充门控钉住（三档）：平台ID 原值 0x{(platformRead ? platformOriginal : (byte)0x07):X2} → 保持 4（配置 charge_gate_pin=1）");
                    }
                    else
                    {
                        // 门控把上限抄进固件存储区后，恢复原平台 ID，再清 0x7B9。
                        AcpiDriverEc.TryWriteByte(PlatformIdRegister, platformRead ? platformOriginal : (byte)0x07);
                        AcpiDriverEc.TryWriteByte(UpperRegister, 0);
                    }
                    // 门控恢复/清上限后，固件可能把 0x7A6 的档位位清掉；最后再写一次档位。
                    if (AcpiDriverEc.TryReadByte(ProtectionModeRegister, out byte protectionAfterGate))
                    {
                        AcpiDriverEc.TryWriteByte(ProtectionModeRegister,
                            (byte)((protectionAfterGate & ~0x31) | mode));
                    }
                }
            }
            else if (!ChargeGatePin.Enabled && ChargeGatePin.IsPinnedByUs)
            {
                int pinnedBack = ChargeGatePin.PinnedOriginal;
                if (AcpiDriverEc.TryWriteByte(PlatformIdRegister, (byte)pinnedBack))
                {
                    ChargeGatePin.ClearPinned();
                    Logger.WriteLine($"限充门控：钉住已关闭，平台ID 还原为 0x{pinnedBack:X2}（三档）");
                }
            }
            bool modeRead = AcpiDriverEc.TryReadByte(ProtectionModeRegister, out byte modeAfter);
            detail = $"上限={percent}% 档位=0x{mode:X2} 平台ID={gated} 覆盖复位={ov}"
                + $" 上限={up} 下限={down} 模式={modeOk} 回读=0x{modeAfter:X2}"
                + $"（bit0={((modeAfter & 0x01) != 0 ? "智能充电" : "无提示")}）";
            return ov && up && modeOk && modeRead && (modeAfter & 0x31) == mode;   // 下限 best-effort，不参与成败判定
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }

    internal static bool TrySet(int percent, out string detail)
    {
        detail = "";
        if (percent < 40 || percent > 100)
        {
            detail = "充电上限越界";
            return false;
        }
        try
        {
            if (!ChargeGatePin.Enabled && ChargeGatePin.IsPinnedByUs && percent >= 95)
            {
                int pinnedBack2 = ChargeGatePin.PinnedOriginal;
                if (AcpiDriverEc.TryWriteByte(PlatformIdRegister, (byte)pinnedBack2))
                {
                    ChargeGatePin.ClearPinned();
                    Logger.WriteLine($"限充门控：钉住已关闭，平台ID 还原为 0x{pinnedBack2:X2}");
                }
            }
            int mode = percent >= 95 ? 0x01 : percent <= 65 ? 0x21 : 0x11;
            bool platform = AcpiDriverEc.TryWriteByte(PlatformIdRegister,
                percent < 95 ? (byte)0x04 : (byte)0x07);
            bool ov = NormalizeOverrideIfResidual();
            bool up = AcpiDriverEc.TryWriteByte(UpperRegister, (byte)percent);
            int lower = percent > 5 ? percent - 5 : percent;
            bool down = AcpiDriverEc.TryWriteByte(LowerRegister, (byte)lower);
            bool modeOk = false;
            if (AcpiDriverEc.TryReadByte(ProtectionModeRegister, out byte protection))
            {
                modeOk = AcpiDriverEc.TryWriteByte(ProtectionModeRegister,
                    (byte)((protection & TouchpadBit) | mode));
            }
            bool read = AcpiDriverEc.TryReadByte(UpperRegister, out byte after);
            int actual = after & 0x7F;
            detail = $"上限={percent}% 平台ID={platform} 覆盖复位={ov} 上限写入={up} 下限写入={down} 模式=0x{mode:X2}({modeOk}) 回读={after:X2}（{actual}%）";
            return platform && up && modeOk && read && actual == percent;   // 下限 best-effort，不参与成败判定
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name;
            return false;
        }
    }

	/// <summary>
	/// 写完后复查一次档位：GCU/固件会把自己的档位再写回 0x7A6，覆盖刚设的值。
	/// <para>只重写一次、只改档位位，失败就如实返回，不反复刷写。</para>
	/// </summary>
	internal static bool TryReassertMode(int mode, int delayMs, out string detail)
	{
		detail = "";
		try
		{
			if (delayMs > 0)
			{
				System.Threading.Thread.Sleep(delayMs);
			}
			if (!AcpiDriverEc.TryReadByte(ProtectionModeRegister, out byte current))
			{
				detail = "回读不到档位";
				return false;
			}
			if ((current & 0x31) == mode)
			{
				detail = "复查一致=0x" + current.ToString("X2");
				return true;
			}
			bool wrote = AcpiDriverEc.TryWriteByte(ProtectionModeRegister, (byte)((current & ~0x31) | mode));
			bool readBack = AcpiDriverEc.TryReadByte(ProtectionModeRegister, out byte after);
			detail = "复查被覆盖=0x" + current.ToString("X2") + " ⇒ 重写" + (wrote ? "OK" : "FAIL")
				+ " 回读=0x" + (readBack ? after.ToString("X2") : "??");
			return wrote && readBack && (after & 0x31) == mode;
		}
		catch (Exception ex)
		{
			detail = ex.GetType().Name + " " + ex.Message;
			return false;
		}
	}
}
