using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Threading;

// 实验菜单、自定义曲线、完整写表序列、自动提权。
internal static partial class Program
{
    // 副扇占空比表只有 13 槽；0x0F5D..0x0F5F 是最大占空比与表控制器，不能当占空比写。
    private const int Fan2DutyCount = 13;
    private const int RegMax1 = 0x0F5D;
    private const int RegMax2 = 0x0F5E;
    private const int RegTableCtrl = 0x0F5F;
    private const int CtrlFloor = 0xA0;
    private const int RegTrigger = 0x0767;
    private const int RegTableEnable = 0x0727;
    private const int DownHysteresis = 3;

    private static bool IsAdministrator()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static string JoinArgs(string[] args)
    {
        List<string> parts = new List<string>();
        foreach (string a in args)
        {
            parts.Add(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
        }
        return string.Join(" ", parts);
    }

    // 没管理员权限就用 runas 重新拉起自己，原进程等它退出后再结束。
    private static int RelaunchElevated(string[] args)
    {
        Console.WriteLine("需要管理员权限（要访问 \\.\\ACPIDriver），正在以管理员身份重新启动…");
        try
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Environment.ProcessPath;
            info.Arguments = JoinArgs(args);
            info.UseShellExecute = true;
            info.Verb = "runas";
            Process child = Process.Start(info);
            if (child != null)
            {
                child.WaitForExit();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("提权失败：" + ex.Message);
            Console.WriteLine("请右键用「以管理员身份运行」，或双击同目录的 双击运行-风扇表实验.cmd。");
            return 3;
        }
    }

    private static bool HasOption(string[] args, string name)
    {
        foreach (string a in args)
        {
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    // 菜单和自定义曲线走这条分支；返回 true 表示已处理。
    private static bool HandleExtended(string[] args, out int code)
    {
        code = 0;
        if (HasOption(args, "--menu"))
        {
            code = RunMenu();
            return true;
        }
        bool curve = HasOption(args, "--curve");
        bool freeReg = HasOption(args, "--reg");
        if (!curve && !freeReg && !HasOption(args, "--ap") && !HasOption(args, "--trigger")
            && !HasOption(args, "--enable"))
        {
            return false;
        }
        if (!Open())
        {
            Console.WriteLine("⛔ 打不开 " + DevicePath);
            Console.WriteLine("   需要管理员权限 + UWACPIDriver 服务在运行。");
            code = 3;
            return true;
        }
        DumpAll();
        SaveSnapshot();
        int[] axis = null;
        int[] duty = null;
        if (curve)
        {
            string text = GetOption(args, "--curve");
            if (!TryBuildCurve(text, out axis, out duty, out string curveError))
            {
                Console.WriteLine("⛔ 曲线写法不对：" + curveError);
                code = 2;
                return true;
            }
            Console.WriteLine("曲线 → 轴：" + string.Join(",", axis));
            Console.WriteLine("曲线 → 占空比：" + string.Join(",", duty));
        }
        bool ap = HasOption(args, "--ap");
        bool trigger = HasOption(args, "--trigger");
        bool enable = HasOption(args, "--enable");
        int isolate = HasOption(args, "--isolate") ? int.Parse(GetOption(args, "--isolate"), CultureInfo.InvariantCulture) : -1;
        int custom = HasOption(args, "--custom") ? int.Parse(GetOption(args, "--custom"), CultureInfo.InvariantCulture) : -1;
        int hold = HasOption(args, "--hold") ? int.Parse(GetOption(args, "--hold"), CultureInfo.InvariantCulture) : 20;
        bool keep = HasOption(args, "--keep");
        WriteTableFull(axis, duty, duty, ap, trigger, enable, isolate, custom);
        DoLatch();
        if (trigger)
        {
            PulseTrigger();
        }
        Console.WriteLine();
        Console.WriteLine("写表完成，开始观察（每 2 秒一行，共 " + hold + " 秒）：");
        Poll(hold);
        if (!keep)
        {
            Console.WriteLine();
            Console.WriteLine("还原快照…");
            DoRestore();
        }
        code = 0;
        return true;
    }

    // 曲线写法：30:0,45:20,60:40,75:60,85:100（温度:占空比 raw 0..200）。
    private static bool TryBuildCurve(string text, out int[] axis, out int[] duty, out string error)
    {
        axis = new int[Slots];
        duty = new int[Slots];
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "空";
            return false;
        }
        List<int> temps = new List<int>();
        List<int> duties = new List<int>();
        foreach (string part in text.Split(','))
        {
            string item = part.Trim();
            if (item.Length == 0)
            {
                continue;
            }
            string[] kv = item.Split(':');
            if (kv.Length != 2)
            {
                error = "应为 温度:占空比，实际 " + item;
                return false;
            }
            if (!int.TryParse(kv[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)
                || !int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d))
            {
                error = "不是整数：" + item;
                return false;
            }
            if (t < 0 || t > 120)
            {
                error = "温度要在 0..120 之间：" + t;
                return false;
            }
            if (d < 0 || d > 200)
            {
                error = "占空比要在 0..200 之间：" + d;
                return false;
            }
            if (temps.Count > 0 && t <= temps[temps.Count - 1])
            {
                error = "温度必须递增：" + t;
                return false;
            }
            temps.Add(t);
            duties.Add(d);
        }
        if (temps.Count == 0)
        {
            error = "没有有效点";
            return false;
        }
        // 点数不足 16 时，后面的槽沿用最后一个点的温度与占空比。
        int lastTemp = temps[temps.Count - 1];
        int lastDuty = duties[duties.Count - 1];
        int running = 0;
        for (int i = 0; i < Slots; i++)
        {
            axis[i] = i < temps.Count ? temps[i] : lastTemp;
            int value = i < duties.Count ? duties[i] : lastDuty;
            if (value < running)
            {
                value = running;
            }
            running = value;
            duty[i] = value;
        }
        return true;
    }

    // 降档轴：不能只减固定回差。相邻档间隔大于回差时，温度会同时满足「从下一档升档」和
    // 「从本档降档」，档位就来回横跳。每个点的降档阈值取上一档的升档阈值，且至少比本档低 1。
    private static int[] BuildDownAxis(int[] axis)
    {
        int[] down = new int[axis.Length];
        for (int i = 0; i < axis.Length; i++)
        {
            int limit = axis[i] > 0 ? axis[i] - 1 : 0;
            int previous = i > 0 ? axis[i - 1] : limit;
            int value = Math.Min(limit, previous);
            down[i] = value < 0 ? 0 : value;
        }
        return down;
    }

    private static void WriteTableFull(int[] axis, int[] fan1, int[] fan2, bool ap, bool trigger, bool enable,
        int isolate, int custom)
    {
        if (enable && Read(RegTableEnable, out int enableValue))
        {
            Write(RegTableEnable, enableValue | 0x40);
            Console.WriteLine("表使能 0x0727 = 0x" + (enableValue | 0x40).ToString("X2"));
        }
        if (custom > 0)
        {
            Write(CustomMode, 0x41);
            Console.WriteLine("自定义模式 0x0706 = 0x41");
        }
        if (ap)
        {
            Read(ApOem, out int apValue);
            Write(ApOem, apValue | 0x01);
            Console.WriteLine("APExist 0x0741 = 0x" + (apValue | 0x01).ToString("X2"));
            Write(Latch, LatchBit);
            Thread.Sleep(20);
            Write(Latch, 0x00);
            Write(ApOem, apValue | 0x81);
            Console.WriteLine("AP Active 0x0741 = 0x" + (apValue | 0x81).ToString("X2"));
        }
        if (axis != null)
        {
            int[] downAxis = BuildDownAxis(axis);
            for (int i = 0; i < Slots; i++)
            {
                int down = downAxis[i];
                Write(CpuUp + i, axis[i]);
                Write(CpuDown + i, down);
                Write(GpuUp + i, axis[i]);
                Write(GpuDown + i, down);
            }
            Console.WriteLine("温度轴已写：" + string.Join(",", axis));
            Console.WriteLine("降档轴已写：" + string.Join(",", downAxis));
        }
        if (isolate >= 0 && Read(Isolate, out int isoValue))
        {
            int want = isolate > 0 ? (isoValue | 0x80) : (isoValue & ~0x80);
            Write(Isolate, want);
            Console.WriteLine("隔离位 0x07C5 = 0x" + want.ToString("X2"));
        }
        int tableMax = CtrlFloor;
        if (fan1 != null)
        {
            for (int i = 0; i < Slots; i++)
            {
                Write(Fan1Duty + i, fan1[i]);
                if (fan1[i] > tableMax)
                {
                    tableMax = fan1[i];
                }
            }
            Console.WriteLine("fan1 表（16 槽）：" + string.Join(",", fan1));
        }
        if (fan2 != null)
        {
            for (int i = 0; i < Fan2DutyCount; i++)
            {
                Write(Fan2Duty + i, fan2[i]);
                if (fan2[i] > tableMax)
                {
                    tableMax = fan2[i];
                }
            }
            Console.WriteLine("fan2 表（只写前 " + Fan2DutyCount + " 槽）：" + string.Join(",", fan2));
        }
        Write(RegMax1, tableMax);
        Write(RegMax2, tableMax);
        Write(RegTableCtrl, tableMax);
        Console.WriteLine("最大占空比/表控制器 0x0F5D/5E/5F = " + tableMax + "/" + tableMax + "/" + tableMax);
    }

    // 0x0767 触发：保留低 4 位里的 bit4，再置 bit5。
    private static void PulseTrigger()
    {
        Read(RegTrigger, out int current);
        int want = (current & 0x10) | 0x20;
        Write(RegTrigger, want);
        Console.WriteLine("触发 0x0767 = 0x" + want.ToString("X2") + "（原 0x" + current.ToString("X2") + "）");
    }

    // 交互菜单：把手册里的试验串起来，每一步都先存快照、结束后按需还原。
    private static int RunMenu()
    {
        if (!Open())
        {
            Console.WriteLine("⛔ 打不开 " + DevicePath);
            Console.WriteLine("   需要管理员权限 + UWACPIDriver 服务在运行。");
            return 3;
        }
        Console.CancelKeyPress += OnCancel;
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("=== 风扇表实验 ===");
            Console.WriteLine("  1  只读：打印整张表与关键寄存器");
            Console.WriteLine("  2  试验A：16 槽全 60 / 全 200（首档保持原值）");
            Console.WriteLine("  3  试验B：写入自定义曲线（温度:占空比,…）");
            Console.WriteLine("  4  试验C：低温首档（30℃ 起转）");
            Console.WriteLine("  5  试验D：首档占空比 0，看会不会锁死");
            Console.WriteLine("  6  自定义：16 槽全部写同一个占空比");
            Console.WriteLine("  7  自由写寄存器（地址=值）");
            Console.WriteLine("  8  还原快照");
            Console.WriteLine("  0  退出");
            Console.Write("请选择：");
            string input = Console.ReadLine();
            if (input == null)
            {
                return 0;
            }
            string choice = input.Trim();
            try
            {
                switch (choice)
                {
                    case "0":
                        return 0;
                    case "1":
                        DumpAll();
                        break;
                    case "2":
                        RunUniformExperiment();
                        break;
                    case "3":
                        RunCurveExperiment();
                        break;
                    case "4":
                        RunLowTempExperiment();
                        break;
                    case "5":
                        RunZeroSlotExperiment();
                        break;
                    case "6":
                        RunUniformInput();
                        break;
                    case "7":
                        RunFreeRegister();
                        break;
                    case "8":
                        DoRestore();
                        break;
                    default:
                        Console.WriteLine("没有这个选项。");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("出错：" + ex.Message);
            }
        }
    }

    private static int AskInt(string prompt, int fallback)
    {
        Console.Write(prompt + "（回车 = " + fallback + "）：");
        string text = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return fallback;
        }
        return value;
    }

    private static int AskHold()
    {
        return AskInt("观察多少秒", 20);
    }

    private static int[] ReadCurrentAxis()
    {
        int[] axis = new int[Slots];
        for (int i = 0; i < Slots; i++)
        {
            Read(CpuUp + i, out axis[i]);
        }
        return axis;
    }

    private static void FinishExperiment(int hold)
    {
        Poll(hold);
        Console.WriteLine();
        Console.WriteLine("还原快照…");
        DoRestore();
    }

    // 试验 A：整张表铺同一个值，首档保持原值。
    private static void RunUniformExperiment()
    {
        int value = AskInt("16 槽全部写多少（占空比 raw 0..200）", 60);
        int hold = AskHold();
        SaveSnapshot();
        int[] current = new int[Slots];
        Read(Fan1Duty, out current[0]);
        int[] fan1 = new int[Slots];
        int[] fan2 = new int[Fan2DutyCount];
        for (int i = 0; i < Slots; i++)
        {
            fan1[i] = i == 0 ? current[0] : value;
        }
        for (int i = 0; i < Fan2DutyCount; i++)
        {
            fan2[i] = i == 0 ? current[0] : value;
        }
        WriteTableFull(ReadCurrentAxis(), fan1, fan2, false, false, false, -1, 1);
        DoLatch();
        FinishExperiment(hold);
    }

    // 试验 B：自定义曲线，EC 自己按温度选档。
    private static void RunCurveExperiment()
    {
        Console.WriteLine("示例：30:0,45:20,60:40,75:60,85:120");
        Console.Write("曲线：");
        string text = Console.ReadLine();
        if (!TryBuildCurve(text, out int[] axis, out int[] duty, out string error))
        {
            Console.WriteLine("⛔ " + error);
            return;
        }
        int hold = AskHold();
        SaveSnapshot();
        int[] fan2 = new int[Fan2DutyCount];
        Array.Copy(duty, fan2, Fan2DutyCount);
        WriteTableFull(axis, duty, fan2, false, false, false, -1, 1);
        DoLatch();
        FinishExperiment(hold);
    }

    // 试验 C：首档 30℃ 就给占空比，看低温能不能起转。
    private static void RunLowTempExperiment()
    {
        int firstTemp = AskInt("首档温度", 30);
        int firstDuty = AskInt("首档占空比 raw", 20);
        int hold = AskHold();
        int[] axis = new int[Slots];
        int[] duty = new int[Slots];
        int running = firstDuty;
        for (int i = 0; i < Slots; i++)
        {
            axis[i] = firstTemp + i * 5;
            if (i == 0)
            {
                duty[i] = firstDuty;
            }
            else
            {
                running = Math.Min(200, running + 10);
                duty[i] = running;
            }
        }
        SaveSnapshot();
        int[] fan2 = new int[Fan2DutyCount];
        Array.Copy(duty, fan2, Fan2DutyCount);
        WriteTableFull(axis, duty, fan2, false, false, false, -1, 1);
        DoLatch();
        FinishExperiment(hold);
    }

    // 试验 D：首档 0 占空比，其余固定值；温度高于首档时应当跳到后面档，落回 0 档就是锁死。
    private static void RunZeroSlotExperiment()
    {
        int restValue = AskInt("其余 15 槽占空比 raw", 200);
        int hold = AskHold();
        int[] axis = ReadCurrentAxis();
        int[] duty = new int[Slots];
        for (int i = 0; i < Slots; i++)
        {
            duty[i] = i == 0 ? 0 : restValue;
        }
        SaveSnapshot();
        int[] fan2 = new int[Fan2DutyCount];
        Array.Copy(duty, fan2, Fan2DutyCount);
        Console.WriteLine("注意：首档温度是 " + axis[0] + "℃，首档占空比写 0，其余 " + restValue);
        WriteTableFull(null, duty, fan2, false, false, false, -1, 1);
        DoLatch();
        FinishExperiment(hold);
    }

    private static void RunUniformInput()
    {
        int value = AskInt("16 槽全部写多少（0..200）", 100);
        int hold = AskHold();
        int[] fan1 = new int[Slots];
        int[] fan2 = new int[Fan2DutyCount];
        for (int i = 0; i < Slots; i++)
        {
            fan1[i] = value;
        }
        for (int i = 0; i < Fan2DutyCount; i++)
        {
            fan2[i] = value;
        }
        SaveSnapshot();
        WriteTableFull(null, fan1, fan2, false, false, false, -1, 1);
        DoLatch();
        FinishExperiment(hold);
    }

    private static void RunFreeRegister()
    {
        Console.Write("地址（可写 0x0741 这种，也可写十进制）：");
        string addressText = Console.ReadLine();
        Console.Write("值：");
        string valueText = Console.ReadLine();
        if (addressText == null || valueText == null)
        {
            return;
        }
        int address = ParseNumber(addressText.Trim());
        int value = ParseNumber(valueText.Trim());
        if (address < 0)
        {
            Console.WriteLine("地址不对。");
            return;
        }
        if (Read(address, out int before))
        {
            Console.WriteLine("原值 0x" + before.ToString("X2") + "（" + before + "）");
        }
        if (Write(address, value))
        {
            Read(address, out int after);
            Console.WriteLine("已写：0x" + address.ToString("X4") + " = 0x" + after.ToString("X2") + "（" + after + "）");
        }
        else
        {
            Console.WriteLine("写入失败。");
        }
    }

    private static int ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
            {
                return hex;
            }
            return -1;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec))
        {
            return dec;
        }
        return -1;
    }
}