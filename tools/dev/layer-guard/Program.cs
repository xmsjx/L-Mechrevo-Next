// UI 层 / 后端分层门禁（棘轮式）
//
// 规则：界面层（tools/dev/wpf-p0 下的窗口与控件代码）只发意图、只读能力位，
//      ⛔ 不直接引用硬件驱动类型；一切硬件动作经 Services 层。
//
// 用法：
//   LayerGuard.exe                       # 默认扫仓库内的 wpf-p0，基线取同目录 layering-baseline.txt
//   LayerGuard.exe --wpf <dir> --baseline <file>
//   LayerGuard.exe --update              # 按当前状态重写基线（只用于“搬家后收紧”或首次建立）
//   LayerGuard.exe --list                # 打印每一个越界行（排障用）
//
// 退出码：0 = 没有超出基线的越界；1 = 有新增越界；2 = 参数/路径错误。
//
// 基线是“每个文件允许的越界行数上限”：0 = 该文件一行都不许有。
// 删掉引用不会失败（棘轮只往下走）；新增文件或超上限即失败。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LayerGuard;

internal static class Program
{
    /// <summary>界面层不得直接引用的硬件类型/命名空间（新增驱动类型时补这里即可）。</summary>
    private static readonly string[] Forbidden =
    {
        "MechrevoLite.Hardware.",
        "MechrevoHw",
        "MechrevoService",
        "MechrevoBackendOrder",
        "MachineIdentity",
        "BackendRouter",
        "FeatureGate",
        "GcuEcKeyboard",
        "EcKeyboardLight",
        "NucKeyboardLighting",
        "AcpiDriverEc",
        "IteLightingLocal",
        "IteCapabilityProbe",
        "LightingSettingsStore",
        "KeyboardRgb",
        "HidDeviceWin",
        "UmiLightbar",
        "BaolongdaWmi",
        // 裸类型：窗口 `using MechrevoLite.Hardware;` + 裸写法的逃逸口（带点的命名空间 token 抓不到）
        "GcuEcExperimental",
        "GcuEcMode",
        "NucFanTable",
        "NucPerformanceMode",
        "NucGpuPower",
        "WujieFanEcController",
        "BaolongdaFanCurveApply",
        "BaolongdaVendorEc",
        "FanWriter",
        "ModeProfileConfig",
        "ReadFailureBackoff",
    };


    /// <summary>界面层不得出现的赋值/写法（比类型名更贴近「越权写」）。</summary>
    private static readonly (Regex Pattern, string Note)[] ForbiddenWrites =
    {
        (new Regex(@"(?<![A-Za-z0-9_])ActiveModeId\s*=(?![=>])", RegexOptions.Compiled), "界面层不得直接写 ActiveModeId（应由 PerfModeService 单点写并广播）"),
        (new Regex(@"(?<![A-Za-z0-9_])PerfModeStore\.", RegexOptions.Compiled), "界面层不得直接碰档位存储"),
    };

// 后端能力位（界面层直接读这些就说明"能力由后端声明"还没落地）
private static readonly string[] CapabilityNames =
{
    "Lightbar", "KeyboardLight", "HingeLight", "SyncLight", "LocalDimming", "LcdOverdrive",
    "DisplayEffect", "ColorCalibration", "FanCurve", "FanBoost", "ChargeLimit", "PerformanceMode",
    "RefreshRate", "ProfileAvailable", "TurboMode", "SilentTurbo", "SilentTurboAvailability",
    "DgpuDirect", "IgpuOnly", "GpuHotSwap", "CpuPerformanceTuning", "FanSettings",
    "WmiBrightness", "DisplayRefresh", "Keyboard", "LogoLight",
};

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string repoRoot = FindRepoRoot();
        string wpfDir = ArgValue(args, "--wpf") ?? Path.Combine(repoRoot, "tools", "dev", "wpf-p0");
        string baseline = ArgValue(args, "--baseline")
            ?? Path.Combine(repoRoot, "tools", "dev", "layer-guard", "layering-baseline.txt");
        bool update = args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase));
        bool list = args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase));

        if (!Directory.Exists(wpfDir))
        {
            Console.WriteLine("[ERR] 界面层目录不存在：" + wpfDir);
            return 2;
        }

        Dictionary<string, (int Hits, List<string> Lines)> scan = Scan(wpfDir);
        int totalHits = scan.Values.Sum(v => v.Hits);
        Console.WriteLine("界面层：" + wpfDir);
        Console.WriteLine("文件数=" + scan.Count + " · 越界行合计=" + totalHits);
        Console.WriteLine("禁用清单=" + string.Join(", ", Forbidden));
    Dictionary<string, (int Hits, List<string> Lines)> capsScan = ScanCapabilities(wpfDir);
    int capsTotal = capsScan.Values.Sum(v => v.Hits);
    Console.WriteLine("能力位直读行=" + capsTotal + "（目标 0：界面只读门控结果）");
    int nullableTotal = CountNullableCapabilityReads(wpfDir);
    string nullableReport = Path.Combine(Path.GetDirectoryName(baseline) ?? ".", "caps-nullable-report.txt");
    int nullableBase = ReadNullableBaseline(nullableReport);
    Console.WriteLine("键盘能力位直读行=" + CountKeyboardCapabilityReads(wpfDir) + "（SupportsKeyboard / Capabilities.Keyboard · 仅报告）");
        Dictionary<string, int> hwUsings = ScanHardwareUsings(wpfDir);
        string usingBaseline = Path.Combine(Path.GetDirectoryName(baseline) ?? ".", "using-baseline.txt");
        Console.WriteLine("界面裸 using MechrevoLite.Hardware 行=" + hwUsings.Values.Sum() + "（棘轮：只准降不准升）");
        Dictionary<string, int> hwAccess = ScanHwAccess(wpfDir);
        string hwBaseline = Path.Combine(Path.GetDirectoryName(baseline) ?? ".", "hw-access-baseline.txt");
        Console.WriteLine("界面直读硬件对象行=" + hwAccess.Values.Sum() + "（棘轮：只准降不准升）");
    Console.WriteLine("能力位空读行=" + nullableTotal + "（caps?. / Capabilities?. · 基线 " + nullableBase + " · 仅报告不参与闸门）");

        if (update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            List<string> lines = new List<string>
            {
                "# UI 层 / 后端分层基线（棘轮）：<相对路径>\\t<允许的越界行数上限>",
                "# 生成命令：dotnet run --project tools/dev/layer-guard -c Release -- --update",
                "# 只会向下收紧：删掉引用后可再次 --update 降低上限；新增文件默认 0。",
            };
            foreach (KeyValuePair<string, (int Hits, List<string> Lines)> item in scan.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(item.Key + "\t" + item.Value.Hits);
            }
            File.WriteAllLines(baseline, lines, new UTF8Encoding(false));
        WriteCapsBaseline(Path.Combine(Path.GetDirectoryName(baseline)!, "caps-baseline.txt"), capsScan);
        WriteNullableBaseline(nullableReport, nullableTotal);
            List<string> hwLines = new List<string>
            {
                "# 界面直读硬件对象行（棘轮）：<相对路径>\t<允许行数上限>",
                "# 生成命令：dotnet run --project tools/dev/layer-guard -c Release -- --update",
            };
            foreach (KeyValuePair<string, int> item in hwAccess.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                hwLines.Add(item.Key + "\t" + item.Value);
            }
            File.WriteAllLines(hwBaseline, hwLines, new UTF8Encoding(false));
            List<string> usingLines = new List<string>
            {
                "# 界面层 using MechrevoLite.Hardware（棘轮）：<相对路径>\t<允许行数上限>",
                "# 生成命令：dotnet run --project tools/dev/layer-guard -c Release -- --update",
            };
            foreach (KeyValuePair<string, int> item in hwUsings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                usingLines.Add(item.Key + "\t" + item.Value);
            }
            File.WriteAllLines(usingBaseline, usingLines, new UTF8Encoding(false));
            Console.WriteLine("[UPDATE] 已写入基线：" + baseline + " · 条目=" + scan.Count);
            return 0;
        }

        Dictionary<string, int> budget = LoadBudget(baseline, out string baselineError);
        if (baselineError != null)
        {
            Console.WriteLine("[WARN] 基线读取失败：" + baselineError);
        }

        List<string> violations = new List<string>();
        Dictionary<string, int> hwBudget = LoadBudget(hwBaseline, out _);
        foreach (KeyValuePair<string, int> item in hwAccess.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            int allowed = hwBudget.TryGetValue(item.Key, out int hwValue) ? hwValue : 0;
            if (item.Value > allowed)
            {
                violations.Add(item.Key + "：直读硬件对象 " + item.Value + " 行 > 上限 " + allowed + "（改走 WpfBackend）");
            }
        }
        Dictionary<string, int> usingBudget = LoadBudget(usingBaseline, out _);
        foreach (KeyValuePair<string, int> item in hwUsings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            int allowed = usingBudget.TryGetValue(item.Key, out int usingValue) ? usingValue : 0;
            if (item.Value > allowed)
            {
                violations.Add(item.Key + "：using MechrevoLite.Hardware " + item.Value + " 行 > 上限 " + allowed);
            }
        }
        foreach (KeyValuePair<string, (int Hits, List<string> Lines)> item in scan.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            int allowed = budget.TryGetValue(item.Key, out int value) ? value : 0;
            if (item.Value.Hits > allowed)
            {
                violations.Add(item.Key + "：越界行 " + item.Value.Hits + " > 上限 " + allowed
                    + (allowed == 0 && !budget.ContainsKey(item.Key) ? "（新文件，默认 0）" : ""));
                if (list)
                {
                    foreach (string line in item.Value.Lines)
                    {
                        violations.Add("    " + line);
                    }
                }
            }
        }

        int shrunk = 0;
        foreach (KeyValuePair<string, int> item in budget)
        {
            int hits = scan.TryGetValue(item.Key, out (int Hits, List<string> Lines) v) ? v.Hits : 0;
            if (hits < item.Value)
            {
                shrunk++;
            }
        }

        Console.WriteLine();
        if (violations.Count == 0)
        {
            Console.WriteLine("[layer-guard] PASS=1 FAIL=0 · violations=0");
            if (shrunk > 0)
            {
                Console.WriteLine("[提示] 有 " + shrunk + " 个文件的越界已低于基线，可执行 --update 收紧上限");
            }
        string capsBaseline = Path.Combine(Path.GetDirectoryName(baseline)!, "caps-baseline.txt");
        List<string> capsViolations = CheckCaps(capsScan, capsBaseline, list);
        if (capsViolations.Count > 0)
        {
            Console.WriteLine("[caps-guard] PASS=0 FAIL=" + capsViolations.Count + " · 能力位直读超上限");
            foreach (string line in capsViolations)
            {
                Console.WriteLine("  ✗ " + line);
            }
            Console.WriteLine("[RESULT] layer-guard FAILED（能力位直读增加，请改走门控）");
            return 1;
        }
        Console.WriteLine("[caps-guard] PASS=1 FAIL=0 · 能力位直读未超上限");
            Console.WriteLine("[RESULT] layer-guard ok");
            return 0;
        }

        Console.WriteLine("[layer-guard] PASS=0 FAIL=" + violations.Count + " · violations>0");
        foreach (string line in violations)
        {
            Console.WriteLine("  ✗ " + line);
        }
        Console.WriteLine("[RESULT] layer-guard FAILED");
        return 1;
    }

    private static Dictionary<string, (int Hits, List<string> Lines)> Scan(string wpfDir)
    {
        Dictionary<string, (int Hits, List<string> Lines)> result = new Dictionary<string, (int, List<string>)>(StringComparer.OrdinalIgnoreCase);
        Regex[] forbidden = Forbidden.Select(BuildTokenRegex).ToArray();
        foreach (string file in Directory.EnumerateFiles(wpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(wpfDir, file).Replace('\\', '/');
            if (IsExempt(relative))
            {
                continue;
            }
            int hits = 0;
            List<string> matched = new List<string>();
            int lineNumber = 0;
            foreach (string text in File.ReadLines(file))
            {
                lineNumber++;
                // 注释里提到类型名不算越界（判据看代码，不看说明文字）。
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                bool hit = false;
                string? note = null;
                foreach ((Regex rule, string ruleNote) in ForbiddenWrites)
                {
                    if (rule.IsMatch(text))
                    {
                        hit = true;
                        note = ruleNote;
                        break;
                    }
                }
                foreach (Regex token in forbidden)
                {
                    if (token.IsMatch(text))
                    {
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                {
                    continue;
                }
                hits++;
                if (matched.Count < 3)
                {
                    matched.Add(relative + ":" + lineNumber + " " + (note == null ? "" : "[" + note + "] ") + text.Trim());
                }
            }
            if (hits > 0)
            {
                result[relative] = (hits, matched);
            }
        }
        return result;
    }

    /// <summary>整词匹配：避免 NucFanTable 命中 NucFanTableTryApply 这类合规转发名。</summary>
    private static Regex BuildTokenRegex(string token)
    {
        string pattern = Regex.Escape(token);
        if (char.IsLetterOrDigit(token[0]) || token[0] == '_')
        {
            pattern = "(?<![A-Za-z0-9_])" + pattern;
        }
        char last = token[token.Length - 1];
        if (char.IsLetterOrDigit(last) || last == '_')
        {
            pattern += "(?![A-Za-z0-9_])";
        }
        return new Regex(pattern, RegexOptions.Compiled);
    }

    /// <summary>界面层还写着 using MechrevoLite.Hardware; 的行，按文件统计（棘轮：只准降不准升）。</summary>
    private static Dictionary<string, int> ScanHardwareUsings(string wpfDir)
    {
        Regex pattern = new Regex(@"^\s*using\s+MechrevoLite\.Hardware\s*;", RegexOptions.Compiled);
        Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(wpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(wpfDir, file).Replace('\\', '/');
            if (IsExempt(relative))
            {
                continue;
            }
            int count = 0;
            foreach (string text in File.ReadLines(file))
            {
                if (pattern.IsMatch(text))
                {
                    count++;
                }
            }
            if (count > 0)
            {
                result[relative] = count;
            }
        }
        return result;
    }

    /// <summary>界面层还直接读硬件对象的行，按文件统计（棘轮：只准降不准升）。</summary>
    private static Dictionary<string, int> ScanHwAccess(string wpfDir)
    {
        Regex pattern = new Regex(@"(?<![A-Za-z0-9_])(?:HwOrProgram|_hw|hw|_probe\.Hw|_backendProbe\.Hw|Hw)\??\.[A-Z]", RegexOptions.Compiled);
        Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(wpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(wpfDir, file).Replace('\\', '/');
            if (IsExempt(relative))
            {
                continue;
            }
            int count = 0;
            foreach (string text in File.ReadLines(file))
            {
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                if (pattern.IsMatch(text))
                {
                    count++;
                }
            }
            if (count > 0)
            {
                result[relative] = count;
            }
        }
        return result;
    }

    /// <summary>Services 层就是“允许碰硬件”的地方；obj/bin 是生成物。</summary>
    private static bool IsExempt(string relative)
    {
        // 诊断页（BackendProbe）职责就是直读硬件并展示，不计入分层闸门。
        if (relative.Equals("BackendProbe.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return relative.StartsWith("Services/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> LoadBudget(string path, out string error)
    {
        Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (!File.Exists(path))
        {
            error = "基线文件不存在：" + path;
            return map;
        }
        int lineNumber = 0;
        foreach (string raw in File.ReadLines(path))
        {
            lineNumber++;
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            string[] parts = text.Split('\t');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int budget))
            {
                error = "第 " + lineNumber + " 行格式不对（应为 <路径>\\t<上限>）";
                continue;
            }
            map[parts[0]] = budget;
        }
        return map;
    }

    private static string ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "source", "MechrevoLite.Core.csproj")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }

    private static Dictionary<string, (int Hits, List<string> Lines)> ScanCapabilities(string wpfDir)
    {
        Dictionary<string, (int Hits, List<string> Lines)> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(wpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(wpfDir, file).Replace('\\', '/');
            if (IsExempt(relative))
            {
                continue;
            }
            List<string> hits = new();
            int lineNo = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNo++;
                if (!line.Contains("caps.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Contains("Supported(", StringComparison.Ordinal))
                {
                    // 能力位只作为"门控未声明时的回退证据"传入门控 ⇒ 判定已由门控做，不计直读。
                    continue;
                }
                foreach (string name in CapabilityNames)
                {
                    if (Regex.IsMatch(line, "caps\\." + name + "\\b"))
                    {
                        hits.Add(relative + ":" + lineNo + "  " + line.Trim());
                        break;
                    }
                }
            }
            if (hits.Count > 0)
            {
                result[relative] = (hits.Count, hits);
            }
        }
        return result;
    }

    /// <summary>统计"可空直读"（caps?. / Capabilities?.）：这些绕过既有的 caps. 计数，只报数不判失败。</summary>
    /// <summary>可空直读的报告基线：只用于显示"比上次多了几处"，不参与判定。</summary>
    private static int ReadNullableBaseline(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }
            string text = File.ReadAllLines(path).FirstOrDefault(l => l.TrimStart().StartsWith("nullable=", StringComparison.Ordinal)) ?? "";
            return int.TryParse(text.Trim()["nullable=".Length..], out int value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void WriteNullableBaseline(string path, int total)
    {
        try
        {
            File.WriteAllText(path, "# 界面层可空能力位直读（caps?. / Capabilities?.）报告基线：只报数，不判失败" + Environment.NewLine + "nullable=" + total + Environment.NewLine);
        }
        catch
        {
        }
    }

    /// <summary>键盘能力位的旁路读取（hw.SupportsKeyboard / Capabilities.Keyboard）：只报数，不判失败。</summary>
    private static int CountKeyboardCapabilityReads(string wpfDir)
    {
        int total = 0;
        foreach (string file in Directory.EnumerateFiles(wpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(wpfDir, file).Replace('\\', '/');
            if (IsExempt(relative))
            {
                continue;
            }
            foreach (string line in File.ReadLines(file))
            {
                if (line.Contains("Supported(", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Contains("SupportsKeyboard", StringComparison.Ordinal)
                    || line.Contains("Capabilities.Keyboard", StringComparison.Ordinal))
                {
                    total++;
                }
            }
        }
        return total;
    }

    private static int CountNullableCapabilityReads(string wpfDir)
    {
        int total = 0;
        foreach (string file in Directory.EnumerateFiles(wpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(wpfDir, file).Replace('\\', '/');
            if (IsExempt(relative))
            {
                continue;
            }
            foreach (string line in File.ReadLines(file))
            {
                if (line.Contains("Supported(", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Contains("caps?.", StringComparison.Ordinal) || line.Contains("Capabilities?.", StringComparison.Ordinal))
                {
                    total++;
                }
            }
        }
        return total;
    }

    private static void WriteCapsBaseline(string path, Dictionary<string, (int Hits, List<string> Lines)> scan)
    {
        List<string> lines = new()
        {
            "# 界面层能力位直读基线（棘轮）：<相对路径>\\t<允许的行数上限>",
            "# 目标：界面只读门控结果，不直接读后端能力位。",
        };
        foreach (KeyValuePair<string, (int Hits, List<string> Lines)> item in scan.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(item.Key + "\t" + item.Value.Hits);
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine("[UPDATE] 已写入能力位基线：" + path + " · 条目=" + scan.Count);
    }

    private static List<string> CheckCaps(Dictionary<string, (int Hits, List<string> Lines)> scan, string path, bool list)
    {
        List<string> violations = new();
        if (!File.Exists(path))
        {
            Console.WriteLine("[caps-guard] 基线不存在，跳过判定：" + path);
            return violations;
        }
        Dictionary<string, int> budget = LoadBudget(path, out string error);
        if (error != null)
        {
            Console.WriteLine("[caps-guard] 基线读取失败：" + error);
            return violations;
        }
        foreach (KeyValuePair<string, (int Hits, List<string> Lines)> item in scan.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            int allowed = budget.TryGetValue(item.Key, out int value) ? value : 0;
            if (item.Value.Hits <= allowed)
            {
                continue;
            }
            violations.Add(item.Key + "：能力位直读 " + item.Value.Hits + " > 上限 " + allowed);
            if (list)
            {
                foreach (string line in item.Value.Lines)
                {
                    violations.Add("    " + line);
                }
            }
        }
        return violations;
    }
}
