using System;
using L_Mechrevo.WpfP0.Services;

namespace L_Mechrevo.WpfP0.Services
{
    /// <summary>
    /// PL4 双倍 EC 值：用户设置过的口径记录与自动下发。
    /// <para>记录在全局配置里（与本机板号无关、不受自定义档位影响），
    /// 自动下发只在「切换模式」与「LM 启动」两个时机各做一次，且 EC 已一致时不重复写。</para>
    /// </summary>
    internal static class Pl4UnitAutoApply
    {
        /// <summary>用户设置过的口径：0=单倍 1=双倍；键不存在 = 没设置过（LM 不自动下发）。</summary>
        internal const string DesiredKey = "pl4_unit_desired";

        private const string OpenKey = "pl4_edit_open";

        internal static bool TryGetDesired(out bool doubleUnit)
        {
            doubleUnit = false;
            try
            {
                if (!MechrevoLite.AppConfig.Exists(DesiredKey))
                {
                    return false;
                }
                doubleUnit = MechrevoLite.AppConfig.Get(DesiredKey, 0) != 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void Record(bool doubleUnit)
        {
            try
            {
                MechrevoLite.AppConfig.Set(DesiredKey, doubleUnit ? 1 : 0);
                MechrevoLite.AppConfig.Flush();
                Logger.WriteError("[pl4-unit] 已记录用户设置=" + (doubleUnit ? "双倍" : "单倍"));
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[pl4-unit] 记录失败：" + ex.Message);
            }
        }

        /// <summary>复位：清掉用户设置，之后 LM 不再自动下发。</summary>
        internal static void Reset()
        {
            try
            {
                if (!MechrevoLite.AppConfig.Exists(DesiredKey))
                {
                    return;
                }
                MechrevoLite.AppConfig.Remove(DesiredKey);
                MechrevoLite.AppConfig.Flush();
                Logger.WriteError("[pl4-unit] 已复位用户设置 ⇒ 之后不再自动下发");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[pl4-unit] 复位失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 按记录的口径对齐 EC：只在实验开关打开、本机可写、且 EC 与记录不一致时才写一次。
        /// </summary>
        internal static bool ApplyIfNeeded(string trigger)
        {
            try
            {
                if (!TryGetDesired(out bool desired))
                {
                    return false;
                }
                if (!MechrevoLite.AppConfig.Exists(OpenKey) || !MechrevoLite.AppConfig.Is(OpenKey))
                {
                    Logger.WriteErrorIfChanged("pl4-unit-auto-skip", "[pl4-unit] " + trigger + " 跳过：实验开关未打开");
                    return false;
                }
                if (!WpfBackend.EcPl4Supported())
                {
                    Logger.WriteErrorIfChanged("pl4-unit-auto-skip", "[pl4-unit] " + trigger + " 跳过：本机不可写");
                    return false;
                }
                if (!WpfBackend.EcPl4TryRead(out bool actual))
                {
                    Logger.WriteErrorIfChanged("pl4-unit-auto-skip", "[pl4-unit] " + trigger + " 跳过：EC 读取失败");
                    return false;
                }
                if (actual == desired)
                {
                    Logger.WriteErrorIfChanged("pl4-unit-auto-same", "[pl4-unit] " + trigger + " 跳过：EC 已是" + (desired ? "双倍" : "单倍"));
                    return false;
                }
                bool ok = WpfBackend.EcPl4TrySetDouble(desired, out string detail);
                Logger.WriteError("[pl4-unit] " + trigger + " 自动下发 " + (desired ? "双倍" : "单倍") + " ⇒ " + (ok ? "OK" : "FAIL") + " · " + detail);
                if (ok)
                {
                    WpfBackend.HwOrProgram?.SetPl4UnitDouble(desired);
                }
                return ok;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[pl4-unit] " + trigger + " 自动下发异常：" + ex.Message);
                return false;
            }
        }
    }
}
