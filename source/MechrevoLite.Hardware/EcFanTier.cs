using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware
{
    /// <summary>工作模式风扇三档（慢/中/快）：三个连续字节，写完即生效，不需要触发字节。</summary>
    internal static class EcFanTier
    {
        /// <summary>记住的风扇档（0..2），回读不可信时用它稳住界面高亮。</summary>
        internal const string LastTierKey = "fan_tier_last";

        internal static bool InUse => (BaseAddress > 0) && (TierCount > 0);

        /// <summary>各模式的默认档：静音=慢，平衡/自定义=中。</summary>
        internal static int DefaultTierFor(string? modeId)
        {
            string id = string.IsNullOrWhiteSpace(modeId) ? "" : modeId!.Trim().ToLowerInvariant();
            return id == "silent" ? 0 : 1;
        }

        internal static int LastTierFor(string? modeId)
        {
            int fallback = DefaultTierFor(modeId);
            return Math.Clamp(AppConfig.Get(Key(modeId), fallback), 0, 2);
        }

        internal static void RememberTier(string? modeId, int tier)
        {
            if (tier >= 0 && tier <= 2)
            {
                AppConfig.Set(Key(modeId), tier);
            }
        }

        private static string Key(string? modeId)
        {
            string id = string.IsNullOrWhiteSpace(modeId) ? "default" : modeId!.Trim().ToLowerInvariant();
            return "fan_tier_" + id;
        }

        private static int BaseAddress
        {
            get
            {
                return ParseAddress(DeclarationRuntime.Param("fanTier", "base"));
            }
        }

        /// <summary>每组有几个字节（声明 tiers="3"；以实际数值组的长度为准，避免把 "3" 当成一个值）。</summary>
        private static int TierCount
        {
            get
            {
                int actual = TierValues(0).Length;
                if (actual > 0)
                {
                    return actual;
                }
                string? text = DeclarationRuntime.Param("fanTier", "tiers");
                if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text!.Trim(), out int declared) && declared > 0 && declared <= 8)
                {
                    return declared;
                }
                return 0;
            }
        }

        /// <summary>某一档的一整组数值（如 64/72/82）。</summary>
        internal static int[] TierValues(int tier)
        {
            string[] keys = { "slow", "mid", "fast" };
            if (tier < 0 || tier >= keys.Length)
            {
                return Array.Empty<int>();
            }
            return ParseNumbers(DeclarationRuntime.Param("fanTier", keys[tier]));
        }

        internal static string TierName(int tier)
        {
            return tier switch
            {
                0 => "慢",
                1 => "中",
                2 => "快",
                _ => "—"
            };
        }

        /// <summary>界面高亮用：回读能精确命中一档就用回读，否则退回记忆。</summary>
        internal static int ResolveForUi(string? modeId)
        {
            int exact = ReadExact();
            return exact >= 0 ? exact : LastTierFor(modeId);
        }

        /// <summary>三字节与某一档完全相等才返回该档，否则 -1。</summary>
        internal static int ReadExact()
        {
            int[] raw = ReadRaw();
            if (raw.Length == 0)
            {
                return -1;
            }
            for (int tier = 0; tier < 3; tier++)
            {
                int[] want = TierValues(tier);
                if (want.Length != raw.Length)
                {
                    continue;
                }
                bool same = true;
                for (int i = 0; i < want.Length; i++)
                {
                    if (want[i] != raw[i])
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                {
                    return tier;
                }
            }
            return -1;
        }

        /// <summary>读回当前三字节，返回最接近的一档；读不到返回 -1。</summary>
        internal static int Read()
        {
            int[] raw = ReadRaw();
            if (raw.Length == 0)
            {
                return -1;
            }
            int best = -1;
            int bestDiff = int.MaxValue;
            for (int tier = 0; tier < 3; tier++)
            {
                int[] want = TierValues(tier);
                if (want.Length != raw.Length)
                {
                    continue;
                }
                int diff = 0;
                for (int i = 0; i < want.Length; i++)
                {
                    diff += Math.Abs(raw[i] - want[i]);
                }
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = tier;
                }
            }
            return best;
        }

        /// <summary>读回三字节原始值（读不到返回空数组）。</summary>
        internal static int[] ReadRaw()
        {
            if (!InUse)
            {
                return Array.Empty<int>();
            }
            var list = new List<int>();
            for (int i = 0; i < TierCount; i++)
            {
                if (!AcpiDriverEc.TryReadByte(BaseAddress + i, out byte value))
                {
                    return Array.Empty<int>();
                }
                list.Add(value);
            }
            return list.ToArray();
        }

        /// <summary>写一档。返回是否写入且回读为期望值。</summary>
        internal static bool TrySet(int tier, string? modeId)
        {
            if (!InUse)
            {
                return false;
            }
            int[] values = TierValues(tier);
            if (values.Length == 0)
            {
                return false;
            }
            // 回读已经是这一档就不重复写（切模式时尤其避免无意义的重复下发）。
            if (ReadExact() == tier)
            {
                RememberTier(modeId, tier);
                Logger.WriteErrorIfChanged("fan-tier-skip", "[fan-tier] 风扇档 " + TierName(tier) + "：回读已是该档，跳过写入");
                return true;
            }
            int[] before = ReadRaw();
            bool written = true;
            for (int i = 0; i < values.Length; i++)
            {
                if (!AcpiDriverEc.TryWriteByte(BaseAddress + i, values[i]))
                {
                    written = false;
                }
            }
            int[] after = ReadRaw();
            string readBack = after.Length == 0 ? "读不到" : string.Join("/", after);
            string beforeText = before.Length == 0 ? "读不到" : string.Join("/", before);
            bool ok = written && after.Length == values.Length;
            if (ok)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (after[i] != values[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }
            if (ok)
            {
                RememberTier(modeId, tier);
            }
            Logger.WriteError("[fan-tier] 风扇档 " + TierName(tier) + "：写 " + string.Join("/", values)
                + "（0x" + BaseAddress.ToString("X") + "-0x" + (BaseAddress + values.Length - 1).ToString("X") + "）"
                + " 前 " + beforeText + " → 后 " + readBack
                + " ⇒ " + (ok ? "写入成功" : (written ? "写入后回读不符" : "写入失败")));
            return ok;
        }

        private static int CountParts(string text)
        {
            int count = 0;
            foreach (string part in text.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Trim().Length > 0)
                {
                    count++;
                }
            }
            return count;
        }

        private static int ParseAddress(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }
            string raw = text!.Trim();
            bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return int.TryParse(hex ? raw.Substring(2) : raw,
                hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0 ? parsed : 0;
        }

        private static int[] ParseNumbers(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<int>();
            }
            var list = new List<int>();
            foreach (string part in text!.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int value) && value >= 0 && value <= 255)
                {
                    list.Add(value);
                }
            }
            return list.ToArray();
        }
    }
}
