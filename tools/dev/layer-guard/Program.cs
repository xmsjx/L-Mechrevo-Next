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
            Console.WriteLine("[UPDATE] 已写入基线：" + baseline + " · 条目=" + scan.Count);
            return 0;
        }

        Dictionary<string, int> budget = LoadBudget(baseline, out string baselineError);
        if (baselineError != null)
        {
            Console.WriteLine("[WARN] 基线读取失败：" + baselineError);
        }

        List<string> violations = new List<string>();
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
                bool hit = false;
                foreach (string token in Forbidden)
                {
                    if (text.Contains(token, StringComparison.Ordinal))
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
                    matched.Add(relative + ":" + lineNumber + " " + text.Trim());
                }
            }
            if (hits > 0)
            {
                result[relative] = (hits, matched);
            }
        }
        return result;
    }

    /// <summary>Services 层就是“允许碰硬件”的地方；obj/bin 是生成物。</summary>
    private static bool IsExempt(string relative)
    {
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
}
