using System;
using System.Threading;
using MechrevoLite.Hardware;

/// <summary>
/// 单区键盘灯效探针：验证 0x767 低 3 位是否承载固件动画模式。
/// <para>只写这几个已经在正式路径里使用的地址：0x767（触发器）、0x769/0x76A/0x76B（RGB 1..50）。</para>
/// <para>⛔ 不碰 0x78C（开关/亮度）、不碰端口 I/O、不扫描任何地址。</para>
/// <para>用法：--dry 只读；--step 逐档人工确认；--cycle [毫秒] 自动轮播；--value 0x24 单次。</para>
/// </summary>
internal static class Program
{
    private const int AddressBacklightWhite = 1932; // 0x78C
    private const int AddressTrigger = 1895;       // 0x767
    private const int AddressRgbControl = 1989;    // 0x7C5
    private const int AddressRgbRed = 1897;        // 0x769
    private const int AddressRgbGreen = 1898;      // 0x76A
    private const int AddressRgbBlue = 1899;       // 0x76B
    private const byte TriggerLatch = 0x20;

    private static readonly byte[] Candidates = { 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27 };

    private const int AddressFanBoost = 1873;       // 0x751 bit6 = 强冷
    private static bool _unsafe;
    private static bool _readOnly;
    private static byte _origTrigger;
    private static byte _origRed;
    private static byte _origGreen;
    private static byte _origBlue;
    private static byte _origBacklight;
    private static bool _lightTouched;

    private static int Main(string[] args)
    {
        if (!AcpiDriverEc.TryReadByte(AddressTrigger, out _origTrigger))
        {
            Console.WriteLine("[失败] 读不到 EC 0x767：请确认 ACPIDriver 服务已就绪（本程序不支持端口 I/O）。");
            return 1;
        }
        AcpiDriverEc.TryReadByte(AddressRgbRed, out _origRed);
        AcpiDriverEc.TryReadByte(AddressRgbGreen, out _origGreen);
        AcpiDriverEc.TryReadByte(AddressRgbBlue, out _origBlue);
        Dump("基线");
        if (_origTrigger != 0x20)
        {
            Console.WriteLine("[提示] 0x767 当前 = 0x" + _origTrigger.ToString("X2") + "，结束时会写回这个值。");
        }

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
        }
        _unsafe = Array.Exists(args, a => string.Equals(a, "--unsafe", StringComparison.OrdinalIgnoreCase));
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--menu";
        int dwellMs = 4000;
        if (mode == "--cycle" && args.Length > 1 && int.TryParse(args[1], out int parsed) && parsed >= 500)
        {
            dwellMs = parsed;
        }
        try
        {
            switch (mode)
            {
                case "--dry":
                    Console.WriteLine("[只读] 未写入任何字节。");
                    return 0;
                case "--watch":
                    _readOnly = true;
                    Watch(args.Length > 1 && int.TryParse(args[1], out int secs) ? secs : 20);
                    return 0;
                case "--bench":
                    Benchmark(args.Length > 1 && int.TryParse(args[1], out int rounds) ? rounds : 40);
                    return 0;
                case "--dump":
                    DumpAll();
                    return 0;
                case "--fanboost":
                    if (args.Length < 2 || !bool.TryParse(args[1].Equals("on", StringComparison.OrdinalIgnoreCase) ? "true" : args[1].Equals("off", StringComparison.OrdinalIgnoreCase) ? "false" : args[1], out bool boostOn))
                    {
                        Console.WriteLine("[失败] --fanboost 需要 on 或 off。");
                        return 1;
                    }
                    FanBoost(boostOn);
                    return 0;
                case "--value":
                    if (args.Length < 2 || !TryParseHex(args[1], out byte single))
                    {
                        Console.WriteLine("[失败] --value 需要 0x20~0x27。");
                        return 1;
                    }
                    Apply(single, "单次写入");
                    return 0;
                case "--cycle":
                    Cycle(dwellMs, interactive: false);
                    return 0;
                case "--step":
                    Cycle(dwellMs, interactive: true);
                    return 0;
                default:
                    Menu();
                    return 0;
            }
        }
        finally
        {
            Restore();
        }
    }

    /// <summary>交互式菜单：想试哪档就输哪档，随时还原退出。</summary>
    private static void Menu()
    {
        EnsureOn();
        WriteRgb(0, 30, 50);
        int index = 0;
        Console.WriteLine("=== 单区键盘灯效探针（交互）===");
        Console.WriteLine("输入 0x20~0x27 / 0xA0 / 0~7 = 写该值；b=彩虹(0xA0) · h=呼吸 · s=单色；回车 = 下一档；"
            + "o = 点灯；x = 关灯；r = 还原并重开；q = 退出（自动还原）。");
        while (true)
        {
            Dump("状态");
            Console.Write("> ");
            string input = Console.ReadLine();
            if (input == null)
            {
                Console.WriteLine();
                return;
            }
            string cmd = input.Trim().ToLowerInvariant();
            if (cmd.Length == 0)
            {
                Apply(Candidates[index % Candidates.Length], "下一档");
                index++;
                continue;
            }
            if (cmd == "q")
            {
                return;
            }
            if (cmd == "o")
            {
                LightOn(true);
                continue;
            }
            if (cmd == "x")
            {
                LightOn(false);
                continue;
            }
            if (cmd == "b")
            {
                WriteRgb(50, 40, 40);
                Apply(0xA0, "硬件彩虹(0xA0)");
                continue;
            }
            if (cmd == "h")
            {
                Apply(0x20, "呼吸(0x7C5 色码)");
                Breathing(1);
                continue;
            }
            if (cmd == "s")
            {
                Apply(0x20, "单色(0x20)");
                WriteRgb(0, 30, 50);
                continue;
            }
            if (cmd == "r")
            {
                Restore();
                EnsureOn();
                WriteRgb(0, 30, 50);
                continue;
            }
            if (TryParseHex(cmd, out byte code))
            {
                Apply((byte)(code > 0x27 ? 0x27 : code), "手动");
                continue;
            }
            Console.WriteLine("  [提示] 只认 0x20~0x27 / 0~7 / 回车 / o / x / r / q。");
        }
    }

    /// <summary>呼吸：0x7C5 低 3 位放色码（1 白 / 2 红 / 3 绿 / 4 蓝 / 5 黄 / 6 紫 / 7 青）。</summary>
    private static void Breathing(int code)
    {
        if (!AcpiDriverEc.TryReadByte(AddressRgbControl, out byte ctrl))
        {
            return;
        }
        byte next = (byte)((ctrl & 0xF8) | Math.Clamp(code, 1, 7));
        bool ok = AcpiDriverEc.TryWriteByte(AddressRgbControl, next);
        AcpiDriverEc.TryReadByte(AddressRgbControl, out byte readBack);
        Console.WriteLine("  [呼吸] 0x7C5 0x" + ctrl.ToString("X2") + " ⇒ 0x" + next.ToString("X2")
            + (ok ? " 成功" : " 失败") + " · 读回 0x" + readBack.ToString("X2"));
    }

    /// <summary>量一帧轮播的 EC 成本：写 RGB 三字节 + 锁存，重复若干轮。</summary>
    private static void Benchmark(int rounds)
    {
        EnsureOn();
        rounds = Math.Clamp(rounds, 5, 200);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            WriteRgb((byte)(10 + i % 40), 30, (byte)(50 - i % 30));
            Apply(0x20, "bench");
        }
        long total = sw.ElapsedMilliseconds;
        Console.WriteLine("[基准] " + rounds + " 帧 = " + total + "ms · 每帧 " + (total / (double)rounds).ToString("F1")
            + "ms（含 RGB 三字节 + 0x767 锁存，每次都是一个 EC 事务）");
    }

    /// <summary>只读监视：20Hz 采样 0x767/0x768/0x74E/0x751/0x78C/0x7C5，只在变化时打印。</summary>
    private static void Watch(int seconds)
    {
        Console.WriteLine("=== 只读监视 " + seconds + " 秒（不写任何字节）：请按 Fn+F5 / Fn+F6 / Fn+Q 等组合 ===");
        Console.WriteLine("时间ms   0x767 0x768 0x74E 0x751 0x78C 0x7C5");
        long start = Environment.TickCount64;
        string last = "";
        while (true)
        {
            long elapsed = Environment.TickCount64 - start;
            if (elapsed > seconds * 1000L)
            {
                break;
            }
            int[] addrs = { 1895, 1896, 1870, 1873, 1932, 1989 };
            var sb = new System.Text.StringBuilder();
            foreach (int addr in addrs)
            {
                sb.Append(AcpiDriverEc.TryReadByte(addr, out byte v) ? v.ToString("X2") : "??").Append("    ");
            }
            string line = sb.ToString();
            if (line != last)
            {
                Console.WriteLine(elapsed.ToString().PadLeft(6) + "   " + line);
                last = line;
            }
            Thread.Sleep(50);
        }
        Console.WriteLine("[只读] 结束，未写入任何字节。");
    }

    /// <summary>只读快照：键盘相关 + 强冷/性能档相关寄存器。</summary>
    private static void DumpAll()
    {
        Dump("键盘");
        AcpiDriverEc.TryReadByte(AddressFanBoost, out byte fb);
        AcpiDriverEc.TryReadByte(1798, out byte custom);
        AcpiDriverEc.TryReadByte(1963, out byte mode);
        AcpiDriverEc.TryReadByte(1870, out byte fnLock);   // 0x74E bit4 = Fn 锁
        AcpiDriverEc.TryReadByte(1896, out byte winKey);   // 0x768 bit0 = Win 键锁
        AcpiDriverEc.TryReadByte(1958, out byte touchpad); // 0x7A6 bit3 = 触摸板
        Console.WriteLine("[快捷] 0x74E=0x" + fnLock.ToString("X2") + "（Fn锁=" + (((fnLock & 0x10) != 0) ? "开" : "关") + "）"
            + " 0x768=0x" + winKey.ToString("X2") + "（Win锁=" + (((winKey & 0x01) != 0) ? "开" : "关") + "）"
            + " 0x7A6=0x" + touchpad.ToString("X2") + "（触摸板断电=" + (((touchpad & 0x08) != 0) ? "是" : "否") + "）");
        Console.WriteLine("[整机] 0x751=0x" + fb.ToString("X2") + "（强冷=" + (((fb & 0x40) != 0) ? "开" : "关") + "）"
            + " 0x706=0x" + custom.ToString("X2") + " 0x7AB=0x" + mode.ToString("X2"));
    }

    /// <summary>强冷开关：0x751 bit6（与正式路径同一地址/位）。</summary>
    private static void FanBoost(bool on)
    {
        if (!AcpiDriverEc.TryReadByte(AddressFanBoost, out byte current))
        {
            Console.WriteLine("[失败] 读不到 0x751。");
            return;
        }
        byte next = on ? (byte)(current | 0x40) : (byte)(current & ~0x40);
        bool ok = AcpiDriverEc.TryWriteByte(AddressFanBoost, next);
        AcpiDriverEc.TryReadByte(AddressFanBoost, out byte readBack);
        Console.WriteLine("  [强冷" + (on ? "开" : "关") + "] 0x751 0x" + current.ToString("X2") + " ⇒ 0x" + next.ToString("X2")
            + (ok ? " 成功" : " 失败") + " · 读回 0x" + readBack.ToString("X2")
            + "（强冷=" + (((readBack & 0x40) != 0) ? "开" : "关") + "）");
    }

    /// <summary>点灯/关灯：只改 0x78C 的 bit1 与亮度高 4 位。</summary>
    private static void LightOn(bool on)
    {
        if (!AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte bl))
        {
            return;
        }
        byte next = on ? (byte)((bl & 0x0D) | 0x90) : (byte)((bl & 0x0D) | 0x12);
        bool ok = AcpiDriverEc.TryWriteByte(AddressBacklightWhite, next);
        AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte readBack);
        Console.WriteLine("  [" + (on ? "点灯" : "关灯") + "] 0x78C 0x" + bl.ToString("X2") + " ⇒ 0x" + next.ToString("X2")
            + (ok ? " 成功" : " 失败") + " · 读回 0x" + readBack.ToString("X2"));
    }

    private static void Cycle(int dwellMs, bool interactive)
    {
        EnsureOn();
        Console.WriteLine(interactive
            ? "[逐档] 每档按回车切到下一档（先按住回车键快速过一遍也行），眼睛看键盘灯。Ctrl+C 可中断（会自动还原）。"
            : "[自动] 每档 " + dwellMs + "ms，共 " + Candidates.Length + " 档，结束后自动还原。");
        // 固定一个中间色，动画/呼吸都有东西可看。
        WriteRgb(0, 30, 50);
        for (int i = 0; i < Candidates.Length; i++)
        {
            byte code = Candidates[i];
            Apply(code, "第 " + (i + 1) + "/" + Candidates.Length + " 档");
            if (interactive)
            {
                Console.Write("    看完按回车继续…");
                Console.ReadLine();
            }
            else
            {
                Thread.Sleep(dwellMs);
            }
        }
    }

    private static void Apply(byte code, string note)
    {
        if ((code & 0x07) != 0 && !_unsafe)
        {
            Console.WriteLine("  [拒绝] 0x" + code.ToString("X2") + "：0x767 低 3 位会触发强冷/快捷开关（实测 0x27 会打开强冷），"
                + "默认不写。确实要试请加 --unsafe。");
            return;
        }
        if (!AcpiDriverEc.TryReadByte(AddressTrigger, out byte current))
        {
            Console.WriteLine("[失败] 读 0x767 失败 ⇒ 跳过 " + note);
            return;
        }
        // 保留 bit3/bit4、清 bit7（彩虹会盖掉动画），低 3 位换成目标档，bit5 保持生效。
        byte next = (byte)((current & 0x38) | TriggerLatch | (code & 0x87));
        bool ok = AcpiDriverEc.TryWriteByte(AddressTrigger, next);
        AcpiDriverEc.TryReadByte(AddressTrigger, out byte readBack);
        Console.WriteLine("  " + note + " ⇒ 写入 0x" + next.ToString("X2") + (ok ? " 成功" : " 失败")
            + " · 读回 0x" + readBack.ToString("X2"));
    }

    /// <summary>灯关着就没东西可看：按常规档位点亮（0x78C 高 4 位 9 = 4 档，清 bit1），结束时还原。</summary>
    private static void EnsureOn()
    {
        if (!AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte bl))
        {
            return;
        }
        _origBacklight = bl;
        if ((bl & 0x02) == 0)
        {
            return;
        }
        byte next = (byte)((bl & 0x0D) | 0x90);
        bool ok = AcpiDriverEc.TryWriteByte(AddressBacklightWhite, next);
        _lightTouched = ok;
        AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte readBack);
        Console.WriteLine("  [点灯] 0x78C 0x" + bl.ToString("X2") + " ⇒ 0x" + next.ToString("X2")
            + (ok ? " 成功" : " 失败") + " · 读回 0x" + readBack.ToString("X2"));
    }

    private static void Restore()
    {
        if (_readOnly)
        {
            Console.WriteLine("[只读] 未写入任何字节。");
            return;
        }
        WriteRgb(_origRed, _origGreen, _origBlue);
        if (_lightTouched)
        {
            AcpiDriverEc.TryWriteByte(AddressBacklightWhite, _origBacklight);
            AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte blBack);
            Console.WriteLine("[还原] 0x78C 写回 0x" + _origBacklight.ToString("X2") + " · 读回 0x" + blBack.ToString("X2"));
        }
        bool ok = AcpiDriverEc.TryWriteByte(AddressTrigger, _origTrigger);
        AcpiDriverEc.TryReadByte(AddressTrigger, out byte readBack);
        Console.WriteLine("[还原] 0x767 写回 0x" + _origTrigger.ToString("X2") + (ok ? " 成功" : " 失败")
            + " · 读回 0x" + readBack.ToString("X2")
            + " · RGB 写回 (" + _origRed + "," + _origGreen + "," + _origBlue + ")");
    }

    private static void WriteRgb(int r, int g, int b)
    {
        AcpiDriverEc.TryWriteByte(AddressRgbRed, Math.Clamp(r, 0, 50));
        AcpiDriverEc.TryWriteByte(AddressRgbGreen, Math.Clamp(g, 0, 50));
        AcpiDriverEc.TryWriteByte(AddressRgbBlue, Math.Clamp(b, 0, 50));
    }

    private static void Dump(string title)
    {
        AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte bl);
        AcpiDriverEc.TryReadByte(AddressTrigger, out byte tr);
        AcpiDriverEc.TryReadByte(AddressRgbControl, out byte ctrl);
        AcpiDriverEc.TryReadByte(AddressRgbRed, out byte r);
        AcpiDriverEc.TryReadByte(AddressRgbGreen, out byte g);
        AcpiDriverEc.TryReadByte(AddressRgbBlue, out byte b);
        Console.WriteLine("[" + title + "] 0x78C=0x" + bl.ToString("X2") + " 0x767=0x" + tr.ToString("X2")
            + " 0x7C5=0x" + ctrl.ToString("X2") + " RGB=(" + r + "," + g + "," + b + ")"
            + " · 灯" + ((bl & 0x02) != 0 ? "关" : "开") + " 亮度档=" + ((bl >> 4) & 0x0F));
    }

    private static bool TryParseHex(string text, out byte value)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(2);
        }
        return byte.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value)
            && (value <= 0x27 || value == 0x80 || value == 0xA0);
    }
}