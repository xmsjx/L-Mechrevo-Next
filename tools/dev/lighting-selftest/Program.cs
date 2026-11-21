using System;
using System.IO;
using MechrevoLite.Contracts;
using System.Linq;
using MechrevoLite.Hardware;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string extra = "")
    {
        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(extra) ? "" : "  ::  " + extra));
        if (!ok)
        {
            _fail++;
        }
    }

    private static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lm-light-selftest-" + Environment.ProcessId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, dir);

        // ① 空目录 ⇒ 磁盘上没有用户灯光配置（旧版会谎报"有"）
        Check("空目录 ⇒ 磁盘无灯光配置", !LightingSettingsStore.HasPersistedLightingIntent());
        Check("空目录 ⇒ 通道无配置", !LightingSettingsStore.TryLoad("HidLightbar_Logo/Ctrl", out _));

        // ② 用户点开关 ⇒ 落盘 + 标记 + 读回一致
        LightingSettingsStore.SavePower("HidLightbar_Logo/Ctrl", true, "selftest");
        Check("SavePower 写出 logolight.cfg", File.Exists(Path.Combine(dir, "logolight.cfg")));
        Check("SavePower 写出用户意图标记", File.Exists(Path.Combine(dir, "lighting.user-configured")));
        Check("SavePower 后磁盘可判定有配置", LightingSettingsStore.HasPersistedLightingIntent());
        bool loaded = LightingSettingsStore.TryLoad("HidLightbar_Logo/Ctrl", out LightChannelSettings s1);
        Check("读回 电源=开 且 显式", loaded && s1.PowerOn && s1.PowerExplicit,
            "loaded=" + loaded + " power=" + s1.PowerOn + " explicit=" + s1.PowerExplicit);

        // ③ 效果/亮度/速度往返（键盘 HID 单键的效果 id 是 "hid:N"）
        LightingSettingsStore.Save("HidLightbar_Logo/Ctrl", s1 with { Effect = "hid:7", Light = 2, Speed = 3 });
        bool reloaded = LightingSettingsStore.TryLoad("HidLightbar_Logo/Ctrl", out LightChannelSettings s2);
        Check("效果/亮度/速度往返", reloaded && s2.Effect == "hid:7" && s2.Light == 2 && s2.Speed == 3,
            "effect=" + s2.Effect + " light=" + s2.Light + " speed=" + s2.Speed);

        // ④ 键盘独立通道互不串味
        LightingSettingsStore.SavePower("Keyboard/Ctrl", false, "selftest");
        Check("键盘开关落盘为关",
            LightingSettingsStore.TryLoad("Keyboard/Ctrl", out LightChannelSettings kb) && !kb.PowerOn && kb.PowerExplicit);
        Check("灯带与键盘互不覆盖",
            LightingSettingsStore.TryLoad("HidLightbar/Ctrl", out _) == false);

        // ⑤ 诊断快照要能一眼看出"文件在不在"
        string desc = LightingSettingsStore.DescribeForDiagnostics();
        Check("诊断含 有(文件)", desc.Contains("有(文件)"), desc);
        Check("诊断列出 Logo 的保存内容", desc.Contains("hid:7"), desc);
        Check("诊断标出灯带=无配置", desc.Contains("灯带=无配置(文件缺失)"), desc);

        // ⑦ 精确亮度百分比（0..100）：HID 单键键盘亮度是连续的，必须原样记住
        LightingSettingsStore.Save("Keyboard/Ctrl", kb with { Light = 3, BrightnessPct = 87 });
        bool pctLoaded = LightingSettingsStore.TryLoad("Keyboard/Ctrl", out LightChannelSettings kbPct);
        Check("brightness_pct=87 往返", pctLoaded && kbPct.BrightnessPct == 87 && kbPct.Light == 3,
            "pct=" + kbPct.BrightnessPct + " light=" + kbPct.Light);
        Check("有精确值时重放按精确值", kbPct.RestoreBrightnessPercent == 87, "restore=" + kbPct.RestoreBrightnessPercent);
        Check("诊断亮度带百分比", LightingSettingsStore.DescribeForDiagnostics().Contains("亮度:3(87%)"));

        // ⑧ 老存档（没有该行）⇒ 按档位折算，行为与旧版一致
        string keyboardCfg = Path.Combine(dir, "keyboard.cfg");
        string[] legacyLines = File.ReadAllLines(keyboardCfg).Where(l => !l.StartsWith("brightness_pct=", StringComparison.Ordinal)).ToArray();
        File.WriteAllLines(keyboardCfg, legacyLines);
        LightingSettingsStore.TryLoad("Keyboard/Ctrl", out LightChannelSettings legacy);
        Check("老存档无 brightness_pct ⇒ -1", legacy.BrightnessPct == -1, "pct=" + legacy.BrightnessPct);
        Check("老存档按档位折算（3 ⇒ 75%）", legacy.RestoreBrightnessPercent == 75, "restore=" + legacy.RestoreBrightnessPercent);

        // ⑨ 坏值不能让整份配置读崩
        foreach ((string raw, int expect) in new[] { ("-5", -1), ("250", 100), ("abc", -1) })
        {
            File.WriteAllLines(keyboardCfg, legacyLines.Append("brightness_pct=" + raw));
            LightingSettingsStore.TryLoad("Keyboard/Ctrl", out LightChannelSettings bad);
            Check("brightness_pct=" + raw + " ⇒ " + expect, bad.BrightnessPct == expect, "pct=" + bad.BrightnessPct);
        }
        File.WriteAllLines(keyboardCfg, legacyLines.Append("brightness_pct=87"));

        // ⑩ 百分比 ⇄ 档位映射（复用 KeyboardRgb 既有映射，⛔ 不另立一套）
        foreach ((int pct, int expect) in new[] { (0, 0), (12, 0), (13, 1), (25, 1), (50, 2), (87, 3), (100, 4) })
        {
            int level = KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(pct);
            Check("亮度 " + pct + "% ⇒ " + expect + " 档", level == expect, "level=" + level);
        }
        Check("档位 4 ⇒ 100%", KeyboardRgb.MapHardwareBrightnessLevel(4) == 100);
        // ⑥ 写不进去时不能崩、必须留错误日志（用户目录被占/无权限的真实场景）
        Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable,
            Path.Combine(dir, "logolight.cfg", "nested"));
        bool threw = false;
        try
        {
            LightingSettingsStore.Save("HidLightbar/Ctrl", s1);
        }
        catch (Exception ex)
        {
            threw = true;
            Console.WriteLine("异常类型=" + ex.GetType().Name);
        }
        Check("写入失败不抛异常（由 [light-cfg] 错误日志兜住）", !threw);

        Console.WriteLine(_fail == 0 ? "== 全绿：灯光记忆自检通过 ==" : "== 失败 " + _fail + " 条 ==");
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
        }
        return _fail == 0 ? 0 : 1;
    }
}
