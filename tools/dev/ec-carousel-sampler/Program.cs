using System;
using System.Collections.Generic;
using MechrevoLite.Hardware;

/// <summary>
/// 轮播采样器：按固定间隔**只读** EC（0x78C / 0x767 / 0x769 / 0x76A / 0x76B），记录颜色变化的时间点，
/// 用于量出「谁在驱动机器上的轮播」以及实际帧周期。
/// <para>⛔ 全程只读，不写任何 EC 地址，不做端口 I/O。</para>
/// <para>用法：--ms 20 --sec 30 （默认 20ms 采样、30 秒）。</para>
/// </summary>
internal static class Program
{
    private const int AddrBacklight = 1932; // 0x78C
    private const int AddrTrigger = 1895;   // 0x767
    private const int AddrRed = 1897;       // 0x769
    private const int AddrGreen = 1898;     // 0x76A
    private const int AddrBlue = 1899;      // 0x76B

    private static int Main(string[] args)
    {
        int ms = ArgInt(args, "--ms", 20);
        int sec = ArgInt(args, "--sec", 30);
        if (ms < 5) ms = 5;
        if (sec < 2) sec = 2;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (!AcpiDriverEc.TryReadByte(AddrBacklight, out byte bl))
        {
            Console.WriteLine("[失败] 读不到 EC 0x78C：ACPIDriver 服务未就绪。");
            return 1;
        }
        AcpiDriverEc.TryReadByte(AddrTrigger, out byte tr);
        AcpiDriverEc.TryReadByte(AddrRed, out byte r0);
        AcpiDriverEc.TryReadByte(AddrGreen, out byte g0);
        AcpiDriverEc.TryReadByte(AddrBlue, out byte b0);
        Console.WriteLine("=== ec-carousel-sampler === 采样间隔 " + ms + "ms · 时长 " + sec + "s（只读）");
        Console.WriteLine("基线 0x78C=0x" + bl.ToString("X2") + " 0x767=0x" + tr.ToString("X2")
            + " RGB=(" + r0 + "," + g0 + "," + b0 + ")"
            + (IsLightOff(bl) ? "  ⚠️ 键盘灯当前是关的（0x78C bit1=1）" : ""));

        List<long> changeMs = new List<long>();
        HashSet<int> colors = new HashSet<int>();
        colors.Add((r0 << 16) | (g0 << 8) | b0);
        byte lastR = r0, lastG = g0, lastB = b0, lastTr = tr, lastBl = bl;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        long lastChangeMs = 0;
        int readFail = 0;
        long nextDue = 0;
        while (clock.ElapsedMilliseconds < sec * 1000)
        {
            if (!AcpiDriverEc.TryReadByte(AddrRed, out byte r) || !AcpiDriverEc.TryReadByte(AddrGreen, out byte g)
                || !AcpiDriverEc.TryReadByte(AddrBlue, out byte b))
            {
                readFail++;
            }
            else
            {
                AcpiDriverEc.TryReadByte(AddrTrigger, out byte trNow);
                AcpiDriverEc.TryReadByte(AddrBacklight, out byte blNow);
                bool rgbChanged = r != lastR || g != lastG || b != lastB;
                bool otherChanged = trNow != lastTr || blNow != lastBl;
                if (rgbChanged || otherChanged)
                {
                    long nowMs = clock.ElapsedMilliseconds;
                    long delta = nowMs - lastChangeMs;
                    lastChangeMs = nowMs;
                    if (rgbChanged)
                    {
                        changeMs.Add(nowMs);
                        colors.Add((r << 16) | (g << 8) | b);
                        Console.WriteLine("t=" + nowMs.ToString().PadLeft(6) + "ms  Δ=" + delta.ToString().PadLeft(5)
                            + "ms  RGB=(" + r.ToString().PadLeft(2) + "," + g.ToString().PadLeft(2) + "," + b.ToString().PadLeft(2) + ")"
                            + "  0x767=0x" + trNow.ToString("X2") + " 0x78C=0x" + blNow.ToString("X2"));
                    }
                    else
                    {
                        Console.WriteLine("t=" + nowMs.ToString().PadLeft(6) + "ms  Δ=" + delta.ToString().PadLeft(5)
                            + "ms  【非颜色变化】0x767: 0x" + lastTr.ToString("X2") + "->0x" + trNow.ToString("X2")
                            + " 0x78C: 0x" + lastBl.ToString("X2") + "->0x" + blNow.ToString("X2"));
                    }
                    lastR = r; lastG = g; lastB = b; lastTr = trNow; lastBl = blNow;
                }
            }
            nextDue += ms;
            long wait = nextDue - clock.ElapsedMilliseconds;
            if (wait > 0) System.Threading.Thread.Sleep((int)wait);
        }

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        Console.WriteLine("颜色变化次数 = " + changeMs.Count + " · 不同颜色数 = " + colors.Count + " · 读失败 = " + readFail);
        if (changeMs.Count >= 2)
        {
            double sum = 0; long min = long.MaxValue, max = 0;
            for (int i = 1; i < changeMs.Count; i++)
            {
                long d = changeMs[i] - changeMs[i - 1];
                sum += d;
                if (d < min) min = d;
                if (d > max) max = d;
            }
            Console.WriteLine("实测周期：平均 " + (sum / (changeMs.Count - 1)).ToString("F1") + "ms · 最小 " + min + "ms · 最大 " + max + "ms");
        }
        else
        {
            Console.WriteLine("采样窗口内颜色没有变化 ⇒ 当前轮播不是靠反复写 RGB 实现的（或灯/轮播没在跑）。");
        }
        Console.WriteLine("结束 0x78C=0x" + lastBl.ToString("X2") + " 0x767=0x" + lastTr.ToString("X2"));
        return 0;
    }

    private static bool IsLightOff(byte bl)
    {
        return (bl & 0x02) != 0;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int v))
            {
                return v;
            }
        }
        return fallback;
    }
}