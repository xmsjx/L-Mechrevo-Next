using System;

namespace MechrevoLite.Hardware
{
    /// <summary>
    /// 风扇域的**单一家族分派点**。
    /// <para>原先"读转速 / 读占空比 / 开强冷"这三件事在服务层和界面层各自判断机型
    /// （宝龙达 / NUC·UMI / 无界 / 同方 EC 各一支），同一套语义写了两三遍，口径容易漂。
    /// 这里把分派收成一处：上层只说"读风扇""开强冷"，由本类决定走哪条通道。</para>
    /// <para>⚠️ 本类只做分派，不复制任何实现：每个分支仍调用既有的通道实现。</para>
    /// </summary>
    internal static class FanChannel
    {
        /// <summary>强冷分派结果。</summary>
        internal enum BoostRoute
        {
            /// <summary>本通道已处理（成功或失败都以 Confirmed 表示）。</summary>
            Handled,
            /// <summary>本类不处理该机型（交给上层走厂商话题）。</summary>
            NotHandled,
            /// <summary>本通道尝试过但失败。</summary>
            Failed
        }

        /// <summary>强冷执行结果。</summary>
        internal readonly record struct BoostOutcome(BoostRoute Route, bool Confirmed, string ChannelName);

        /// <summary>风扇家族（用于日志与参数查询，不参与行为判断）。</summary>
        internal enum Family
        {
            Gcu,
            TongfangEc,
            WujieTable,
            NucUmiLocal,
            BaolongdaLocal
        }

        internal static Family FamilyOf(MechrevoHw hw)
        {
            if (hw == null)
            {
                return Family.Gcu;
            }
            if (hw.UsesBaolongda)
            {
                return Family.BaolongdaLocal;
            }
            if (hw.WujieSmuApu)
            {
                return Family.WujieTable;
            }
            if (hw.IsNucUmiFamily)
            {
                return Family.NucUmiLocal;
            }
            return hw.IsGcuConnected ? Family.Gcu : Family.TongfangEc;
        }

        internal static string NameOf(MechrevoHw hw)
        {
            return FamilyOf(hw) switch
            {
                Family.BaolongdaLocal => "宝龙达本机",
                Family.WujieTable => "无界 EC 表",
                Family.NucUmiLocal => "NUC/UMI 本机",
                Family.TongfangEc => "同方 EC",
                _ => "同方 GCU"
            };
        }

        /// <summary>读双扇转速（RPM）。读不到时返回 false，调用方沿用旧值。</summary>
        internal static bool TryReadSpeeds(MechrevoHw hw, out int mainRpm, out int secondaryRpm)
        {
            mainRpm = -1;
            secondaryRpm = -1;
            if (hw == null)
            {
                return false;
            }
            try
            {
                // 声明表给了本机转速字地址就按它读；读不到再走下面的机型分支（不改变既有通路）。
                if (EcFanRpm.TryRead(out int declaredMain, out int declaredSecond))
                {
                    mainRpm = declaredMain;
                    secondaryRpm = declaredSecond;
                    return true;
                }
                switch (FamilyOf(hw))
                {
                    case Family.BaolongdaLocal:
                        return hw.TryReadFanSpeedsViaBackend(out mainRpm, out secondaryRpm);
                    case Family.WujieTable:
                        return hw.TryReadWujieEcFanSpeeds(out mainRpm, out secondaryRpm);
                    case Family.NucUmiLocal:
                        return UmiFanReader.TryReadFanRpm(out mainRpm, out secondaryRpm);
                    default:
                        return hw.TryReadGcuEcFanSpeeds(out mainRpm, out secondaryRpm);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[fan] 通道读转速异常（" + NameOf(hw) + "）：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>读双扇占空比（%）。读不到或不可信时返回 false。</summary>
        internal static bool TryReadDuty(MechrevoHw hw, out int mainPercent, out int secondaryPercent)
        {
            mainPercent = -1;
            secondaryPercent = -1;
            if (hw == null)
            {
                return false;
            }
            try
            {
                switch (FamilyOf(hw))
                {
                    case Family.WujieTable:
                        // 无界的占空比与厂商风扇信息同源：0x75B/0x75C，原始值 0..200。
                        if (!AcpiDriverEc.TryReadByte(0x75B, out byte rawMain) || !AcpiDriverEc.TryReadByte(0x75C, out byte rawSecondary))
                        {
                            return false;
                        }
                        if (rawMain == 0xFF || rawSecondary == 0xFF || rawMain > 200 || rawSecondary > 200)
                        {
                            return false;
                        }
                        mainPercent = Math.Clamp(rawMain / 2, 0, 100);
                        secondaryPercent = Math.Clamp(rawSecondary / 2, 0, 100);
                        return true;
                    case Family.NucUmiLocal:
                        return UmiFanReader.TryReadFanDuty(out mainPercent, out secondaryPercent);
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[fan] 通道读占空比异常（" + NameOf(hw) + "）：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 强冷：本地通道能做的机型直接做掉；需要走厂商话题的机型返回 NotHandled，由上层继续。
        /// <para>语义与原分散实现一致：宝龙达/UMI 失败即失败；同方 EC 失败不拦，继续尝试厂商通道。</para>
        /// </summary>
        internal static BoostOutcome ApplyBoost(MechrevoHw hw, bool on)
        {
            if (hw == null)
            {
                return new BoostOutcome(BoostRoute.NotHandled, false, "无后端");
            }
            string name = NameOf(hw);
            try
            {
                switch (FamilyOf(hw))
                {
                    case Family.BaolongdaLocal:
                    {
                        bool ok = hw.TrySetFanBoostViaBackend(on, out bool confirmed);
                        return new BoostOutcome(ok ? BoostRoute.Handled : BoostRoute.Failed, confirmed, name);
                    }
                    case Family.NucUmiLocal:
                    {
                        bool ok = NucQuickSwitches.TrySetFanBoost(on, out bool confirmed);
                        return new BoostOutcome(ok ? BoostRoute.Handled : BoostRoute.Failed, confirmed, name);
                    }
                    case Family.WujieTable:
                    {
                        // 无界在 GCU 可用时走厂商话题；GCU 不可用时与同方 EC 同一入口（clamp 位）。
                        if (hw.IsGcuConnected)
                        {
                            return new BoostOutcome(BoostRoute.NotHandled, false, name);
                        }
                        bool ok = hw.TrySetEcFanBoost(on, out bool confirmed);
                        return new BoostOutcome(ok ? BoostRoute.Handled : BoostRoute.NotHandled, confirmed, name);
                    }
                    case Family.TongfangEc:
                    {
                        // GCU 未连接时只有本地 EC 这一条；失败不在这里判死，交给上层继续尝试厂商通道。
                        bool ok = hw.TrySetEcFanBoost(on, out bool confirmed);
                        return new BoostOutcome(ok ? BoostRoute.Handled : BoostRoute.NotHandled, confirmed, name);
                    }
                    default:
                        return new BoostOutcome(BoostRoute.NotHandled, false, name);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[fan] 强冷通道异常（" + name + "）：" + ex.GetType().Name + " " + ex.Message);
                return new BoostOutcome(BoostRoute.Failed, false, name);
            }
        }
    }
}
