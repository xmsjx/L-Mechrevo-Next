using System;
using System.Threading;
using System.Threading.Tasks;

namespace MechrevoLite.Hardware;

/// <summary>
/// NUC/UMI EC 单区 RGB 键盘：识别方法与单区效果常量按单区协议实现。
/// EC 地址：0x766 支持位、0x78C 电源/亮度、0x767 锁存、0x769~0x76B RGB、0x7C5 呼吸色码。
/// </summary>
internal static class NucKeyboardLighting
{
    private const int AddressSupportByte2 = 1894; // 0x766
    private const int AddressBacklightWhite = 1932; // 0x78C
    private const int AddressTriggerByte = 1895; // 0x767
    private const int AddressRgbControl = 1989; // 0x7C5
    private const int AddressRgbRed = 1897;
    private const int AddressRgbGreen = 1898;
    private const int AddressRgbBlue = 1899;
    private const byte TriggerLatch = 0x20;
    private const byte TriggerRainbow = 0x80;

    private static readonly object Gate = new();
    /// <summary>软件轮播的速度档数：1 档最慢、8 档最快（界面 1/8 .. 8/8）。</summary>
    internal const int SpeedLevels = 8;

    /// <summary>
    /// 档位 → 每帧间隔（ms）。下标 = 档位 - 1，**数值越大越快**。
    /// 两端对齐「最快 341ms / 最慢 1421ms」的实测区间，中间等间隔（约 154ms/档）。
    /// </summary>
    private static readonly int[] CycleIntervalMs = { 1421, 1267, 1112, 958, 804, 649, 495, 341 };
    private static CancellationTokenSource? _cycleCts;
    /// <summary>轮播代次：每次 start/stop 递增，只有当前代次有权写 EC。</summary>
    private static long _cycleEpoch;

    internal static bool IsSingleZone()
    {
        return AcpiDriverEc.TryReadByte(AddressSupportByte2, out byte support)
            && (support & 0x04) != 0;
    }

    internal static bool TryReadBrightness(out int level)
    {
        level = 0;
        if (!AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte value))
        {
            return false;
        }
        if ((value & 0x02) != 0)
        {
            level = 0;
            return true;
        }
        level = (value >> 4) switch
        {
            0x02 or 0x03 => 1,
            0x04 or 0x05 => 2,
            0x06 or 0x07 => 3,
            0x08 or 0x09 => 4,
            _ => 0
        };
        return true;
    }

    internal static void Apply(string effect, bool on, int brightness, int speed, int colorArgb, int[] cycleColors)
    {
        lock (Gate)
        {
            StopCycle();
            if (!AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte current))
            {
                Logger.WriteError("[gcu-ec-kbd] EC单区 apply：0x78C 读取失败 ⇒ 本次不下发（轮播已停）");
                return;
            }
            int level = Math.Clamp(brightness, 0, 4);
            if (!on || level <= 0)
            {
                AcpiDriverEc.TryWriteByte(AddressBacklightWhite, (current & 0x0D) | 0x12);
                StopRainbow();
                ClearBreathing();
                WriteRgb(0, 0, 0);
                return;
            }

            byte highNibble = level switch
            {
                1 => 0x30,
                2 => 0x50,
                3 => 0x70,
                _ => 0x90
            };
            byte next = (byte)((current & 0x0D) | highNibble);
            AcpiDriverEc.TryWriteByte(AddressBacklightWhite, next);

            // 顺序按 EC 官方实现：
            // 单色：StopRainbow → StopBreathing → 写 RGB → 0x767 bit5
            // 彩虹：StopBreathing → RGB=0 → 0x767 bit7（不碰 bit5）
            // 呼吸：StopRainbow → 0x7C5 低 3 位色码（不碰 0x767）
            // 轮播：StopRainbow → 定时写 RGB + 0x767 bit5
            if (string.Equals(effect, "Rainbow", StringComparison.Ordinal))
            {
                ClearBreathing();
                // 彩虹需要非 0 种子色，且必须同时置 bit5(锁存) 与 bit7(彩虹)
                WriteRgb(50, 40, 40);
                EcTriggerByte.TryApply((byte)(EcTriggerByte.Latch | EcTriggerByte.Rainbow), 0);
            }
            else if (string.Equals(effect, "Breathing", StringComparison.Ordinal))
            {
                int code = BreathCode(colorArgb);
                if (AcpiDriverEc.TryReadByte(AddressRgbControl, out byte ctrl))
                {
                    AcpiDriverEc.TryWriteByte(AddressRgbControl, (byte)((ctrl & 0xF8) | code));
                }
                // 呼吸同样要走 0x767：清 bit7、置 bit5 锁存，固件才会切到呼吸模式
                EcTriggerByte.TryApply(EcTriggerByte.Latch, EcTriggerByte.Rainbow);
            }
            else if (string.Equals(effect, "Manual", StringComparison.Ordinal))
            {
                StopRainbow();
                // ⭐ 软件轮播只服务「同方单分区键盘」：0x766 bit2 不成立就绝不启动，退回常亮当前色，
                // 免得把单键/其它分区机型的灯写乱。
                if (!IsSingleZone())
                {
                    Logger.WriteError("[gcu-ec-kbd] EC单区轮播：0x766 bit2=0（非单分区键盘）⇒ 不启动，改为常亮当前色");
                    WriteScaledColor(colorArgb);
                    SetRgbLatch();
                }
                else
                {
                    StartCycle(cycleColors, speed);
                }
            }
            else
            {
                StopRainbow();
                ClearBreathing();
                WriteScaledColor(colorArgb);
                SetRgbLatch();
            }
            AcpiDriverEc.TryReadByte(AddressBacklightWhite, out byte blRead);
            AcpiDriverEc.TryReadByte(AddressTriggerByte, out byte trRead);
            AcpiDriverEc.TryReadByte(AddressRgbControl, out byte ctrlRead);
            Logger.WriteError("[gcu-ec-kbd] EC单区 apply effect=" + effect + " on=" + on
                + " level=" + level + " speed=" + speed
                + " · 0x78C=0x" + blRead.ToString("X2")
                + " 0x767=0x" + trRead.ToString("X2")
                + " 0x7C5=0x" + ctrlRead.ToString("X2"));
        }
    }

    /// <summary>
    /// 轮播循环：每启动一次领一个**代次**，只有当前代次有权写 EC。
    /// <para>⚠️ stop 只递增代次（不发共享停止标志）：旧循环哪怕正睡在 delay 里，醒来复核代次就自行退出，
    /// 既不残留线程，也不必 join 卡住调用方。</para>
    /// </summary>
    private static void StartCycle(int[] colors, int speed)
    {
        int[] palette = colors is { Length: > 0 } ? (int[])colors.Clone() : new[] { unchecked((int)0xFFFF0000) };
        // 入口统一钳到 ≥20ms：任何档位都不允许把 EC 写成高频轮询。
        int interval = Math.Clamp(CycleIntervalMs[Math.Clamp(speed, 1, SpeedLevels) - 1], 20, 1500);
        long epoch = Interlocked.Increment(ref _cycleEpoch);
        CancellationTokenSource cts = new();
        _cycleCts = cts;
        _ = Task.Run(() =>
        {
            try
            {
                int index = 0;
                // 绝对时间基准推进：写入耗时不累积进周期，否则快档会被 EC 写入成本吃掉，各档看起来一样慢。
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                long nextDueMs = 0;
                long worstWriteMs = 0;
                int frames = 0;
                while (IsCurrentCycle(epoch) && !cts.IsCancellationRequested)
                {
                    int argb = palette[index % palette.Length];
                    index++;
                    long beforeMs = clock.ElapsedMilliseconds;
                    // 一帧的颜色 + 锁存在同一把写锁里完成：同一时刻只有 1 个线程能写 EC。
                    lock (Gate)
                    {
                        // 写之前再复核一次代次：过期的旧代次一帧都不写。
                        if (!IsCurrentCycle(epoch))
                        {
                            return;
                        }
                        // ⭐ 灯被关掉时（0x78C bit1=关灯 / 亮度 0）循环必须自己退出：
                        // 否则它会一直往 RGB + 锁存写字，把刚关掉的灯又点亮、且白白刷 EC。
                        // ⚠️ 连读两次都判“关”才算真关：单次读数偶发异常（驱动/EC 抢读）不能误杀轮播。
                        if (TryReadBrightness(out int nowLevel) && nowLevel <= 0
                            && TryReadBrightness(out int confirmLevel) && confirmLevel <= 0)
                        {
                            Logger.WriteError("[gcu-ec-kbd] EC单区轮播：检测到键盘灯已关闭 ⇒ 循环自行退出（已跑 " + frames + " 帧）");
                            return;
                        }
                        WriteScaledColor(argb);
                        SetRgbLatch();
                    }
                    long costMs = clock.ElapsedMilliseconds - beforeMs;
                    if (costMs > worstWriteMs)
                    {
                        worstWriteMs = costMs;
                    }
                    frames++;
                    if (frames == 1 || frames % 40 == 0)
                    {
                        Logger.WriteError("[gcu-ec-kbd] EC单区轮播 目标间隔=" + interval + "ms·实测平均周期="
                            + (clock.ElapsedMilliseconds / frames) + "ms·单帧写入=" + worstWriteMs + "ms·帧=" + frames);
                    }
                    // ⭐ 每 40 帧复核一次“仍是单分区键盘”：固件切换/换机型时立刻收手，不在别的分区上继续刷。
                    if (frames % 40 == 0 && !IsSingleZone())
                    {
                        Logger.WriteError("[gcu-ec-kbd] EC单区轮播：0x766 bit2 已掉（不再是单分区键盘）⇒ 循环退出");
                        return;
                    }
                    nextDueMs += interval;
                    long waitMs = nextDueMs - clock.ElapsedMilliseconds;
                    if (waitMs <= 0)
                    {
                        continue;
                    }
                    try
                    {
                        Task.Delay((int)waitMs, cts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteError("[gcu-ec-kbd] EC单区轮播：异常停止（" + ex.GetType().Name + "）");
            }
            finally
            {
                cts.Dispose();
                Interlocked.CompareExchange(ref _cycleCts, null, cts);
            }
        });
    }

    /// <summary>外部关灯入口（Fn 亮度到 0 / 主界面开关 / 白光通道）：只递增代次，⛔ 不 join。</summary>
    internal static void StopCarousel() => StopCycle();

    /// <summary>该代次是否仍是当前代次（旧代次一律失效）。</summary>
    private static bool IsCurrentCycle(long epoch) => epoch == Volatile.Read(ref _cycleEpoch);

    /// <summary>停止轮播：递增代次让所有旧代次失效（⛔ 不 join、不阻塞调用方）。</summary>
    private static void StopCycle()
    {
        Interlocked.Increment(ref _cycleEpoch);
        CancellationTokenSource? cts = _cycleCts;
        _cycleCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void WriteScaledColor(int argb)
    {
        int r = (argb >> 16) & 0xFF;
        int g = (argb >> 8) & 0xFF;
        int b = argb & 0xFF;
        if (r == 255 && g == 255 && b == 255)
        {
            WriteRgb(50, 40, 40);
            return;
        }
        WriteRgb(Scale(r), Scale(g), Scale(b));
    }

    private static int Scale(int value) => value == 0 ? 0 : Math.Clamp(value * 50 / 255, 1, 50);

    private static void WriteRgb(int r, int g, int b)
    {
        AcpiDriverEc.TryWriteByte(AddressRgbRed, r);
        AcpiDriverEc.TryWriteByte(AddressRgbGreen, g);
        AcpiDriverEc.TryWriteByte(AddressRgbBlue, b);
    }

    private static void Latch(byte extra)
    {
        EcTriggerByte.TryApply((byte)(EcTriggerByte.Latch | extra), 0);
    }

    /// <summary>只清/置 0x767 bit7，不写 bit5；彩虹模式按官方实现走这条。</summary>
    private static void SetRainbow(bool enabled)
    {
        // 只改 bit7；bit0-2（快捷开关命令码）与 bit4（USB 充电脉冲）原样保留。
        EcTriggerByte.TryApply(
            enabled ? EcTriggerByte.Rainbow : (byte)0,
            enabled ? (byte)0 : EcTriggerByte.Rainbow);
    }

    private static void StopRainbow() => SetRainbow(false);

    /// <summary>单色/轮播刷新：只置 0x767 bit5，保留 bit7 的当前状态。</summary>
    private static void SetRgbLatch()
    {
        EcTriggerByte.TryApply(EcTriggerByte.Latch, 0);
    }

    /// <summary>退出呼吸：只清 0x7C5 低 3 位，不碰 0x767。</summary>
    private static void ClearBreathing()
    {
        if (AcpiDriverEc.TryReadByte(AddressRgbControl, out byte ctrl) && (ctrl & 0x07) != 0)
        {
            AcpiDriverEc.TryWriteByte(AddressRgbControl, (byte)(ctrl & 0xF8));
        }
    }

    private static void Trigger(bool rainbow)
    {
        EcTriggerByte.TryApply(
            rainbow ? (byte)(EcTriggerByte.Latch | EcTriggerByte.Rainbow) : EcTriggerByte.Latch,
            rainbow ? (byte)0 : EcTriggerByte.Rainbow);
    }

    private static int BreathCode(int argb)
    {
        (int Code, int R, int G, int B)[] palette =
        {
            (1, 255, 255, 255), (2, 255, 0, 0), (3, 0, 255, 0), (4, 0, 0, 255),
            (5, 255, 255, 0), (6, 136, 0, 255), (7, 0, 255, 255)
        };
        int r = (argb >> 16) & 0xFF;
        int g = (argb >> 8) & 0xFF;
        int b = argb & 0xFF;
        int best = 1;
        long bestDistance = long.MaxValue;
        foreach ((int Code, int R, int G, int B) item in palette)
        {
            int dr = r - item.R;
            int dg = g - item.G;
            int db = b - item.B;
            long distance = (long)dr * dr + (long)dg * dg + (long)db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = item.Code;
            }
        }
        return best;
    }
}
