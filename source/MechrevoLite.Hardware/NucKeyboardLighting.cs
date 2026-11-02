using System;
using System.Threading;
using System.Threading.Tasks;

namespace MechrevoLite.Hardware;

/// <summary>
/// NUC/UMI EC 单区 RGB 键盘：识别方法与单区效果常量按 OpenRevo 实现。
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
    private static readonly int[] SpeedMap = { 10, 7, 5, 3, 1 };
    private static CancellationTokenSource? _cycleCts;

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
            0x03 => 1,
            0x05 => 2,
            0x07 => 3,
            0x09 => 4,
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
                return;
            }
            int level = Math.Clamp(brightness, 0, 4);
            if (!on || level <= 0)
            {
                AcpiDriverEc.TryWriteByte(AddressBacklightWhite, (current & 0x0D) | 0x12);
                Latch(0);
                WriteRgb(0, 0, 0);
                Trigger(false);
                return;
            }

            byte highNibble = level switch
            {
                1 => 0x30,
                2 => 0x50,
                3 => 0x70,
                _ => 0x90
            };
            byte next = (byte)((current & 0x0F) | highNibble);
            AcpiDriverEc.TryWriteByte(AddressBacklightWhite, next);
            Trigger(false);

            // effect: Single / Rainbow / Breathing / Manual(轮播)
            if (string.Equals(effect, "Rainbow", StringComparison.Ordinal))
            {
                WriteScaledColor(colorArgb);
                Trigger(true);
            }
            else if (string.Equals(effect, "Breathing", StringComparison.Ordinal))
            {
                int code = BreathCode(colorArgb);
                if (AcpiDriverEc.TryReadByte(AddressRgbControl, out byte ctrl))
                {
                    AcpiDriverEc.TryWriteByte(AddressRgbControl, (ctrl & 0xF8) | code);
                }
                Trigger(false);
            }
            else if (string.Equals(effect, "Manual", StringComparison.Ordinal))
            {
                StartCycle(cycleColors, speed);
            }
            else
            {
                WriteScaledColor(colorArgb);
                Trigger(false);
            }
        }
    }

    private static void StartCycle(int[] colors, int speed)
    {
        int[] palette = colors is { Length: > 0 } ? (int[])colors.Clone() : new[] { unchecked((int)0xFFFF0000) };
        int interval = 70 * SpeedMap[Math.Clamp(speed, 0, SpeedMap.Length - 1)];
        CancellationTokenSource cts = new();
        _cycleCts = cts;
        _ = Task.Run(async () =>
        {
            int index = 0;
            while (!cts.IsCancellationRequested)
            {
                WriteScaledColor(palette[index % palette.Length]);
                Trigger(false);
                index++;
                try
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private static void StopCycle()
    {
        _cycleCts?.Cancel();
        _cycleCts?.Dispose();
        _cycleCts = null;
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
        if (AcpiDriverEc.TryReadByte(AddressTriggerByte, out byte trigger))
        {
            AcpiDriverEc.TryWriteByte(AddressTriggerByte, trigger | TriggerLatch | extra);
        }
    }

    private static void Trigger(bool rainbow)
    {
        if (AcpiDriverEc.TryReadByte(AddressTriggerByte, out byte trigger))
        {
            byte next = (byte)((trigger & ~TriggerRainbow) | TriggerLatch);
            if (rainbow)
            {
                next |= TriggerRainbow;
            }
            AcpiDriverEc.TryWriteByte(AddressTriggerByte, next);
        }
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
