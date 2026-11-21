using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// 同方 EC 16 槽风扇表实验工具（只走 \\.\ACPIDriver，不碰端口 I/O）。
// 默认只读 dump；写之前自动存快照，退出时（或 --restore）写回原值。
internal static partial class Program
{
    private const uint GenericReadWrite = 0xC0000000;
    private const uint OpenExisting = 3;
    private const uint ShareRW = 3;
    private const uint IoctlEcRead = 0x9C40A488;
    private const uint IoctlEcWrite = 0x9C40A48C;
    private const string DevicePath = "\\\\.\\ACPIDriver";

    private const int Slots = 16;
    private const int CpuUp = 0x0F00;
    private const int CpuDown = 0x0F10;
    private const int Fan1Duty = 0x0F20;
    private const int GpuUp = 0x0F30;
    private const int GpuDown = 0x0F40;
    private const int Fan2Duty = 0x0F50;
    private const int Isolate = 0x07C5;
    private const int Latch = 0x07C6;
    private const int CustomMode = 0x0706;
    private const int ModeIndex = 0x200D;
    // 本机（同方 EC）占空比口径：0x75B 主扇 / 0x75C 副扇，原始值 0..200，除以 2 得百分比。
    private const int DutyMain = 0x075B;
    private const int DutySecond = 0x075C;
    // 无界（WUJIE）口径，本机读出来是 0xFF（地址不成立），只作对照打印。
    private const int WujieDuty1 = 0x1804;
    private const int WujieDuty2 = 0x1809;
    private const int BoostClamp = 0x0751;
    private const int ApOem = 0x0741;
    private const int CpuTemp = 0x043E;
    private const int SlotMain = 0x0460;
    private const int TargetMain = 0x0461;
    private const int SlotSecond = 0x0468;
    private const int TargetSecond = 0x0469;
    private const int Rpm1Hi = 0x0464;
    private const int Rpm1Lo = 0x0465;
    // 副扇转速和主扇一样是大端：高字节 0x046C、低字节 0x046D。
    // 0x046B 不是转速字节，当成低字节会拼出假转速（例如 125 RPM）。
    private const int Rpm2Hi = 0x046C;
    private const int Rpm2Lo = 0x046D;
    private const byte LatchBit = 0x04;

    private static readonly string SnapshotPath = Path.Combine(
        AppContext.BaseDirectory, "fan-table16-snapshot.txt");

    // 每次运行的完整输出都追加到这个文件，窗口滚过去也能回看。
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "fan-table16-log.txt");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[] inBuffer, uint inSize, byte[] outBuffer, uint outSize, out uint bytesReturned, IntPtr overlapped);

    private static SafeFileHandle _handle;

    private static bool Open()
    {
        _handle = CreateFileW(DevicePath, GenericReadWrite, ShareRW, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return !_handle.IsInvalid;
    }

    private static bool Read(int address, out int value)
    {
        value = -1;
        if (address < 0 || address > 0xFFFF)
        {
            return false;
        }
        byte[] input = BitConverter.GetBytes((uint)address);
        byte[] output = new byte[4];
        if (!DeviceIoControl(_handle, IoctlEcRead, input, 4, output, 4, out _, IntPtr.Zero))
        {
            return false;
        }
        value = (int)BitConverter.ToUInt32(output, 0);
        return true;
    }

    private static bool Write(int address, int value)
    {
        if (address < 0 || address > 0xFFFF || value < 0 || value > 255)
        {
            return false;
        }
        byte[] input = new byte[8];
        BitConverter.GetBytes((uint)address).CopyTo(input, 0);
        BitConverter.GetBytes((uint)value).CopyTo(input, 4);
        byte[] output = new byte[4];
        return DeviceIoControl(_handle, IoctlEcWrite, input, 8, output, 4, out _, IntPtr.Zero);
    }

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
        }
        StartLog(args);

        // 没有管理员权限就以管理员身份重新拉起自己（--no-elevate 可关闭）。
        if (!IsAdministrator() && !HasOption(args, "--no-elevate") && !HasOption(args, "--help")
            && !HasOption(args, "-h"))
        {
            return RelaunchElevated(args);
        }
        if (HandleExtended(args, out int extendedCode))
        {
            return extendedCode;
        }

        bool write = false;
        int baseRaw = -1;
        Dictionary<int, int> slots = new Dictionary<int, int>();
        int[] temps = null;
        int isolate = -1;
        int custom = -1;
        int hold = 20;
        bool keep = false;
        bool force = false;
        bool restore = false;
        bool sweep = false;
        bool step = false;
        int low = 60;
        int warmup = 40;
        int settle = 12;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            try
            {
                switch (a)
                {
                    case "--help":
                    case "-h":
                        Usage();
                        return 0;
                    case "--write":
                        write = true;
                        break;
                    case "--restore":
                        restore = true;
                        break;
                    case "--base":
                        baseRaw = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        write = true;
                        break;
                    case "--slots":
                        ParseSlots(args[++i], slots);
                        write = true;
                        break;
                    case "--temp":
                        temps = ParseTemps(args[++i]);
                        break;
                    case "--isolate":
                        isolate = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--custom":
                        custom = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--hold":
                        hold = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--keep":
                        keep = true;
                        break;
                    case "--force":
                        force = true;
                        break;
                    case "--sweep":
                        sweep = true;
                        break;
                    case "--step":
                        step = true;
                        break;
                    case "--low":
                        low = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--warmup":
                        warmup = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--settle":
                        settle = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    default:
                        Console.WriteLine("未知参数：" + args[i]);
                        Usage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("参数解析失败：" + a + " → " + ex.Message);
                return 2;
            }
        }

        if (!Open())
        {
            Console.WriteLine("⛔ 打不开 " + DevicePath);
            Console.WriteLine("   需要管理员权限 + UWACPIDriver 服务在运行。");
            return 3;
        }

        Console.CancelKeyPress += OnCancel;
        DumpAll();
        if (Read(BoostClamp, out int clamp) && (clamp & 0x40) != 0 && !force)
        {
            Console.WriteLine();
            Console.WriteLine("⛔ 强冷正在进行（0x0751=0x" + clamp.ToString("X2") + " bit6=1）⇒ 不写表。");
            Console.WriteLine("   先关强冷；确实要写就加 --force。");
            return 4;
        }

        if (restore)
        {
            return DoRestore();
        }
        if (sweep)
        {
            return DoSweep(baseRaw >= 0 ? baseRaw : 200, low, warmup, settle, keep, step);
        }
        if (!write)
        {
            Console.WriteLine();
            Console.WriteLine("（只读模式，没有写入。加 --write / --base / --slots 才会写表）");
            return 0;
        }

        SaveSnapshot();
        WriteTables(baseRaw, slots, temps, isolate, custom);
        DoLatch();
        Console.WriteLine();
        Console.WriteLine("已写入并锁存。开始观察（每 2 秒一行，共 " + hold + " 秒）：");
        Poll(hold);
        if (!keep)
        {
            Console.WriteLine();
            Console.WriteLine("还原快照…");
            DoRestore();
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("⚠️ --keep：没有还原，EC 表保持测试值。用 --restore 可以写回。");
        }
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("同方 EC 16 槽风扇表实验工具");
        Console.WriteLine();
        Console.WriteLine("  （不带参数）              只读 dump 六个区 + 关键寄存器");
        Console.WriteLine("  --menu                   交互菜单（推荐：双击 双击运行-风扇表实验.cmd）");
        Console.WriteLine("  --curve \"30:0,45:20,60:40,75:60,85:120\"");
        Console.WriteLine("                           按温度:占空比写整张 16 槽表（温度轴 + 占空比 + 降档轴）");
        Console.WriteLine("  --ap                     写表前先做 APExist / AP Active 两步（0x0741 |0x01 → |0x81）");
        Console.WriteLine("  --trigger                锁存后再写触发 0x0767 = (cur&0x10)|0x20");
        Console.WriteLine("  --enable                 置表使能 0x0727 bit6");
        Console.WriteLine("  --reg 0x0741 0x81        直接写任意寄存器（只读地址写了也是拒绝）");
        Console.WriteLine("  --no-elevate             不自动提权（默认会以管理员身份重新拉起自己）");
        Console.WriteLine("  --write --base 120       16 槽全部写 120（占空比 raw 0..200）");
        Console.WriteLine("  --write --slots 1=60,5=180,10=200");
        Console.WriteLine("                           逐槽指定（槽号 1..16 = EC 偏移 0..15）");
        Console.WriteLine("  --temp 0,45,51,...,255   同时写温度轴（16 个，逗号分隔）");
        Console.WriteLine("  --isolate 1              主副扇输出隔离（0x07C5 bit7）");
        Console.WriteLine("  --custom 1               自定义模式标志（0x0706 = 0x41）");
        Console.WriteLine("  --hold 30                写入后观察秒数（默认 20）");
        Console.WriteLine("  --keep                   观察完不还原");
        Console.WriteLine("  --restore                用快照文件写回原值");
        Console.WriteLine("  --force                  强冷进行中也写（默认拒绝）");
        Console.WriteLine("  --sweep [--base 200] [--low 60] [--warmup 40] [--settle 12]");
        Console.WriteLine("                           先铺满 base 等转速上平台，再逐槽下调到 low，看哪一槽能拉动转速");
        Console.WriteLine("  --step                  扫描时每槽之间等回车，可随时暂停/退出（q）");
        Console.WriteLine("  运行中按 Ctrl+C 会先写回快照再退出");
        Console.WriteLine();
        Console.WriteLine("⚠️ 运行前先退出其它风扇控制软件，否则表会被覆盖。");
        Console.WriteLine("   写之前会自动把原值存到 fan-table16-snapshot.txt，观察完自动写回，");
        Console.WriteLine("   中途 Ctrl+C 也会先写回快照。");
        Console.WriteLine("   每次输出同时追加到 fan-table16-log.txt。");
    }

    private static void ParseSlots(string text, Dictionary<int, int> into)
    {
        foreach (string part in text.Split(','))
        {
            if (part.Trim().Length == 0)
            {
                continue;
            }
            string[] kv = part.Split('=');
            if (kv.Length != 2)
            {
                throw new FormatException("槽位写法应为 槽=值：" + part);
            }
            int slot = int.Parse(kv[0].Trim(), CultureInfo.InvariantCulture);
            int value = int.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
            if (slot < 1 || slot > Slots)
            {
                throw new FormatException("槽号必须在 1..16：" + slot);
            }
            into[slot - 1] = value;
        }
    }

    private static int[] ParseTemps(string text)
    {
        string[] parts = text.Split(',');
        int[] values = new int[Slots];
        for (int i = 0; i < Slots; i++)
        {
            values[i] = i < parts.Length ? int.Parse(parts[i].Trim(), CultureInfo.InvariantCulture) : 255;
        }
        return values;
    }

    private static void DumpAll()
    {
        (string Name, int Base)[] blocks =
        {
            ("CPU 升档温度 0x0F00", CpuUp),
            ("CPU 降档温度 0x0F10", CpuDown),
            ("fan1 占空比 0x0F20", Fan1Duty),
            ("GPU 升档温度 0x0F30", GpuUp),
            ("GPU 降档温度 0x0F40", GpuDown),
            ("fan2 占空比 0x0F50", Fan2Duty)
        };
        Console.WriteLine("== 十六槽表 ==");
        foreach ((string Name, int Base) in blocks)
        {
            Console.Write(Name.PadRight(20) + " ");
            for (int i = 0; i < Slots; i++)
            {
                Console.Write(Read(Base + i, out int v) ? v.ToString().PadLeft(4) : " ???");
            }
            Console.WriteLine();
        }
        Console.WriteLine("             槽号    " + string.Join("", BuildSlotHeader()));

        (string Name, int Addr)[] regs =
        {
            ("模式 0x200D", ModeIndex),
            ("自定义 0x0706", CustomMode),
            ("锁存 0x07C6", Latch),
            ("隔离 0x07C5", Isolate),
            ("强冷 0x0751", BoostClamp),
            ("APOEM 0x0741", ApOem),
            ("占空比 主 0x75B", DutyMain),
            ("占空比 副 0x75C", DutySecond),
            ("无界 0x1804", WujieDuty1),
            ("无界 0x1809", WujieDuty2),
            ("档位 主 0x0460", SlotMain),
            ("目标 主 0x0461", TargetMain),
            ("档位 副 0x0468", SlotSecond),
            ("目标 副 0x0469", TargetSecond),
            ("CPU 温度 0x043E", CpuTemp)
        };
        Console.WriteLine();
        Console.WriteLine("== 关键寄存器 ==");
        foreach ((string Name, int Addr) in regs)
        {
            Console.WriteLine(Name.PadRight(20) + " " + (Read(Addr, out int v) ? v.ToString() : "读取失败"));
        }
        Console.WriteLine("风扇转速".PadRight(18) + " 1 号 " + Rpm(Rpm1Hi, Rpm1Lo) + " RPM · 2 号 " + Rpm(Rpm2Hi, Rpm2Lo) + " RPM");
    }

    private static string[] BuildSlotHeader()
    {
        string[] cells = new string[Slots];
        for (int i = 0; i < Slots; i++)
        {
            cells[i] = (i + 1).ToString().PadLeft(4);
        }
        return cells;
    }

    private static int[] ReadTemps()
    {
        int[] temps = new int[Slots];
        for (int i = 0; i < Slots; i++)
        {
            Read(CpuUp + i, out temps[i]);
        }
        return temps;
    }

    private static string ReadAxisText()
    {
        return string.Join(",", ReadTemps());
    }

    // 当前温度落在哪一槽：同时给出「第一个 ≥ 温度」和「最后一个 ≤ 温度」两种口径的槽号（1 基），用来判 EC 取点方向。
    private static string IndexHint(int temp)
    {
        int[] temps = ReadTemps();
        int ge = -1;
        int le = -1;
        for (int i = 0; i < Slots; i++)
        {
            if (temps[i] == 0 || temps[i] == 255)
            {
                continue;
            }
            if (ge < 0 && temps[i] >= temp)
            {
                ge = i;
            }
            if (temps[i] <= temp)
            {
                le = i;
            }
        }
        string text = "";
        if (ge >= 0)
        {
            text += " 命中槽≥" + (ge + 1) + "(" + temps[ge] + "℃)";
        }
        if (le >= 0)
        {
            text += " 命中槽≤" + (le + 1) + "(" + temps[le] + "℃)";
        }
        return text;
    }

    private static int Rpm(int hi, int lo)
    {
        if (!Read(hi, out int h) || !Read(lo, out int l))
        {
            return -1;
        }
        int rpm = (h << 8) | l;
        return rpm > 0 && rpm < 8000 ? rpm : -1;
    }

    // Ctrl+C 中断：先把快照写回去再退出，避免表停在测试值。
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;

        public TeeWriter(TextWriter console, TextWriter file)
        {
            _console = console;
            _file = file;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            _file.Write(value);
        }

        public override void Write(string value)
        {
            _console.Write(value);
            _file.Write(value);
        }

        public override void WriteLine(string value)
        {
            _console.WriteLine(value);
            _file.WriteLine(value);
        }

        public override void Flush()
        {
            _console.Flush();
            _file.Flush();
        }
    }

    private static void StartLog(string[] args)
    {
        try
        {
            StreamWriter writer = new StreamWriter(LogPath, true, Encoding.UTF8);
            writer.AutoFlush = true;
            writer.WriteLine();
            writer.WriteLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 参数：" + string.Join(" ", args) + " ===");
            Console.SetOut(new TeeWriter(Console.Out, writer));
        }
        catch
        {
        }
    }

    private static void OnCancel(object sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("已中断，正在写回快照…");
        try
        {
            DoRestore();
        }
        catch
        {
        }
        Environment.Exit(130);
    }

    // 逐槽扫描：先把 16 槽铺到 base 并等转速上平台，再逐槽下调到 low，看转速是否掉下来；
    // 掉下来的那一槽就是 EC 当前温度实际取用的槽。
    private static int DoSweep(int baseRaw, int low, int warmup, int settle, bool keep, bool step)
    {
        SaveSnapshot();
        WriteTables(baseRaw, new Dictionary<int, int>(), null, -1, -1);
        DoLatch();
        int[] axis = ReadTemps();
        Console.WriteLine();
        Console.WriteLine("基线：16 槽全写 " + baseRaw + "，等 " + warmup + " 秒让转速上平台…");
        int[] warm = SampleRange(warmup);
        int baseline = warm[warm.Length - 1];
        Read(DutyMain, out int dm);
        Read(DutySecond, out int ds);
        Console.WriteLine("基线转速 " + baseline + " RPM · 占空比 " + dm + "/" + ds + "/200");
        Console.WriteLine();
        Console.WriteLine("逐槽下调：第 N 槽 → " + low + "，观察 " + settle + " 秒，然后恢复 " + baseRaw + "。");
        int[] rpms = new int[Slots];
        int[] drops = new int[Slots];
        for (int i = 0; i < Slots; i++)
        {
            if (step)
            {
                Console.Write("回车测下一个槽（输入 q 退出并还原）… ");
                string line = Console.ReadLine();
                if (line != null && line.Trim().ToLowerInvariant() == "q")
                {
                    Console.WriteLine("退出扫描，写回快照…");
                    DoRestore();
                    return 0;
                }
            }
            Write(Fan1Duty + i, low);
            Write(Fan2Duty + i, low);
            DoLatch();
            int[] samples = SampleRange(settle);
            int settled = samples[samples.Length - 1];
            rpms[i] = settled;
            drops[i] = baseline - settled;
            Write(Fan1Duty + i, baseRaw);
            Write(Fan2Duty + i, baseRaw);
            DoLatch();
            Console.WriteLine("  第 " + (i + 1).ToString().PadLeft(2) + " 槽（轴 " + axis[i] + "℃）→ "
                + settled + " RPM · 差 " + (drops[i] >= 0 ? "-" : "+") + Math.Abs(drops[i]) + " RPM"
                + (Math.Abs(drops[i]) >= 150 ? "   ★ 有反应" : ""));
        }
        Console.WriteLine();
        Console.WriteLine("== 汇总（差 ≥150 RPM 视为该槽在用）==");
        bool any = false;
        for (int i = 0; i < Slots; i++)
        {
            if (Math.Abs(drops[i]) >= 150)
            {
                any = true;
                Console.WriteLine("  ★ 第 " + (i + 1) + " 槽（轴 " + axis[i] + "℃）→ " + rpms[i] + " RPM，差 " + drops[i]);
            }
        }
        if (!any)
        {
            Console.WriteLine("  没有哪一槽单独下调能让转速变化 ⇒ 不是单槽取值（可能是多点插值/取最大/另走固件策略）。");
        }
        if (!keep)
        {
            Console.WriteLine();
            Console.WriteLine("还原快照…");
            DoRestore();
        }
        return 0;
    }

    private static int[] SampleRange(int seconds)
    {
        int rounds = Math.Max(1, seconds / 2);
        int[] values = new int[rounds];
        for (int r = 0; r < rounds; r++)
        {
            Thread.Sleep(2000);
            Read(CpuTemp, out int temp);
            int rpm = Rpm(Rpm1Hi, Rpm1Lo);
            Read(DutyMain, out int dm);
            values[r] = rpm;
            Console.WriteLine("    [" + ((r + 1) * 2) + "s] " + temp + "℃ · " + rpm + " RPM · 占空比 " + dm + "/200");
        }
        return values;
    }

    private static void WriteTables(int baseRaw, Dictionary<int, int> overrides, int[] temps, int isolate, int custom)
    {
        int[] fan1 = new int[Slots];
        int[] fan2 = new int[Slots];
        for (int i = 0; i < Slots; i++)
        {
            fan1[i] = baseRaw >= 0 ? baseRaw : -1;
            fan2[i] = baseRaw >= 0 ? baseRaw : -1;
        }
        foreach (KeyValuePair<int, int> kv in overrides)
        {
            fan1[kv.Key] = kv.Value;
            fan2[kv.Key] = kv.Value;
        }
        if (custom > 0)
        {
            Write(CustomMode, 0x41);
        }
        if (temps != null)
        {
            int[] downAxis = BuildDownAxis(temps);
            for (int i = 0; i < Slots; i++)
            {
                int down = downAxis[i];
                Write(CpuUp + i, temps[i]);
                Write(CpuDown + i, down);
                Write(GpuUp + i, temps[i]);
                Write(GpuDown + i, down);
            }
            Console.WriteLine("温度轴已写：" + string.Join(",", temps));
        }
        if (isolate >= 0 && Read(Isolate, out int iso))
        {
            int want = isolate > 0 ? (iso | 0x80) : (iso & ~0x80);
            Write(Isolate, want);
            Console.WriteLine("隔离位 0x07C5 = 0x" + want.ToString("X2"));
        }
        int tableMax = CtrlFloor;
        for (int i = 0; i < Slots; i++)
        {
            if (fan1[i] >= 0)
            {
                Write(Fan1Duty + i, fan1[i]);
                if (fan1[i] > tableMax)
                {
                    tableMax = fan1[i];
                }
            }
        }
        // 副扇占空比表只有 13 槽，0x0F5D..0x0F5F 是最大占空比与表控制器。
        for (int i = 0; i < Fan2DutyCount; i++)
        {
            if (fan2[i] >= 0)
            {
                Write(Fan2Duty + i, fan2[i]);
                if (fan2[i] > tableMax)
                {
                    tableMax = fan2[i];
                }
            }
        }
        Write(RegMax1, tableMax);
        Write(RegMax2, tableMax);
        Write(RegTableCtrl, tableMax);
        Console.WriteLine("fan1 表（16 槽）：" + string.Join(",", fan1));
        Console.WriteLine("fan2 表（前 13 槽）：" + string.Join(",", fan2));
        Console.WriteLine("最大占空比/表控制器 0x0F5D/5E/5F = " + tableMax);
    }

    private static void DoLatch()
    {
        Write(Latch, 0x00);
        Thread.Sleep(50);
        Write(Latch, LatchBit);
        Console.WriteLine("已锁存（0x07C6 → 0x00 → 0x04）");
    }

    private static void Poll(int seconds)
    {
        Console.WriteLine("温度轴（0x0F00）：" + ReadAxisText());
        int rounds = Math.Max(1, seconds / 2);
        for (int r = 0; r < rounds; r++)
        {
            Thread.Sleep(2000);
            Read(DutyMain, out int d1);
            Read(DutySecond, out int d2);
            Read(CpuTemp, out int temp);
            Read(SlotMain, out int slot1);
            Read(TargetMain, out int target1);
            Read(SlotSecond, out int slot2);
            Read(TargetSecond, out int target2);
            Console.Write("[+" + ((r + 1) * 2) + "s] CPU " + temp + "℃" + IndexHint(temp)
                + " · 档位/目标 " + slot1 + "/" + target1 + "（副 " + slot2 + "/" + target2 + "）"
                + " · 占空比 " + d1 + "/" + d2 + "/200 ⇒ " + (d1 / 2) + "%/" + (d2 / 2) + "%"
                + " · 转速 " + Rpm(Rpm1Hi, Rpm1Lo) + "/" + Rpm(Rpm2Hi, Rpm2Lo) + " RPM · fan1 表 ");
            for (int i = 0; i < Slots; i++)
            {
                Console.Write(Read(Fan1Duty + i, out int v) ? v.ToString().PadLeft(4) : " ???");
            }
            Console.WriteLine();
        }
    }

    private static void SaveSnapshot()
    {
        List<string> lines = new List<string>();
        (int Base, string Name)[] blocks =
        {
            (CpuUp, "cpuUp"), (CpuDown, "cpuDown"), (Fan1Duty, "fan1"),
            (GpuUp, "gpuUp"), (GpuDown, "gpuDown"), (Fan2Duty, "fan2")
        };
        foreach ((int Base, string Name) in blocks)
        {
            for (int i = 0; i < Slots; i++)
            {
                if (Read(Base + i, out int v))
                {
                    lines.Add("{0} {1} {2}".Replace("{0}", Name).Replace("{1}", (Base + i).ToString())
                        .Replace("{2}", v.ToString()));
                }
            }
        }
        if (Read(Isolate, out int iso))
        {
            lines.Add("isolate " + Isolate + " " + iso);
        }
        if (Read(CustomMode, out int cm))
        {
            lines.Add("custom " + CustomMode + " " + cm);
        }
        File.WriteAllLines(SnapshotPath, lines);
        Console.WriteLine("原值快照：" + SnapshotPath + "（" + lines.Count + " 字节）");
    }

    private static int DoRestore()
    {
        if (!File.Exists(SnapshotPath))
        {
            Console.WriteLine("⛔ 没有快照文件，无法还原：" + SnapshotPath);
            return 5;
        }
        int count = 0;
        foreach (string line in File.ReadAllLines(SnapshotPath))
        {
            string[] parts = line.Split(' ');
            if (parts.Length != 3)
            {
                continue;
            }
            if (int.TryParse(parts[1], out int addr) && int.TryParse(parts[2], out int value))
            {
                if (Write(addr, value))
                {
                    count++;
                }
            }
        }
        Write(Latch, 0x00);
        Thread.Sleep(50);
        Write(Latch, LatchBit);
        Console.WriteLine("已还原 " + count + " 字节并锁存");
        DumpAll();
        return 0;
    }
}
