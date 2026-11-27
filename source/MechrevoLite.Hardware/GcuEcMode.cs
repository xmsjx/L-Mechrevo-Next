using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MechrevoLite.Hardware;

/// <summary>GCU 控制模式 / EC 控制模式切换（设置页使用）。</summary>
public static class GcuEcMode
{
    private const string DirectKey = "gcu_ec_direct";
    private static int _active;
    // GCU 服务装没装只问一次：判默认路由用；装/卸后在下次启动自然刷新。
    private static int _gcuServiceInstalled = -1;

    /// <summary>运行时已进入 EC 后端。</summary>
    public static bool IsRuntimeActive =>
        System.Threading.Volatile.Read(ref _active) != 0;

    /// <summary>用户在配置里明确选择 EC。</summary>
    public static bool IsDirectConfigured =>
        MechrevoLite.AppConfig.Get(DirectKey, 0) != 0;

    /// <summary>本机登记了 GCU 服务（GCUBridge / GCUService 任一；只读一份缓存）。</summary>
    /// <para>只认服务注册表本身，不看 ItemSupport 之类的历史快照：移除 GCU 后快照可能还留着，</para>
    /// <para>会让「没装 GCU 就默认 EC」判不出来。</para>
    private static bool GcuServiceInstalled
    {
        get
        {
            int cached = System.Threading.Volatile.Read(ref _gcuServiceInstalled);
            if (cached >= 0)
            {
                return cached == 1;
            }
            bool installed;
            try
            {
                installed = GcuServiceKeyExists("GCUBridge") || GcuServiceKeyExists("GCUService");
            }
            catch (Exception)
            {
                // 判不出来就当装了，维持旧行为。
                installed = true;
            }
            System.Threading.Volatile.Write(ref _gcuServiceInstalled, installed ? 1 : 0);
            return installed;
        }
    }

    /// <summary>本机是不走 GCU 的机型（NUC-UMI 系列）。判定读机器事实，失败按「需要 GCU」处理。</summary>
    private static bool MachineNeedsNoGcu()
    {
        try
        {
            MechrevoHw? hw = MechrevoLite.Program.hw;
            return hw != null && hw.IsNucUmiFamily;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool GcuServiceKeyExists(string name)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                "SYSTEM\\CurrentControlSet\\Services\\" + name);
            return key != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>本来就不用 GCU 的机型（NUC-UMI）默认走 EC。</summary>
    /// <para>其余机型一律等用户在界面上选（导入 GCU / 改用 EC）：连不上 GCU 不再静默切 EC。</para>
    private static bool ImplicitDirect =>
        !MechrevoLite.AppConfig.Exists(DirectKey) && MachineNeedsNoGcu();

    /// <summary>EC 控制模式是否生效：运行时已进入 EC、用户明确选择 EC，或没装 GCU 时默认 EC。</summary>
    public static bool IsEcMode => IsRuntimeActive || IsDirectConfigured || ImplicitDirect;

    /// <summary>EC 模式来源，供日志/诊断区分“已接管”和“用户强制”。</summary>
    public static string Reason => IsRuntimeActive
        ? (IsDirectConfigured ? "runtime+direct" : "runtime")
        : (IsDirectConfigured ? "direct" : (ImplicitDirect ? "implicit" : "none"));

    /// <summary>进入 EC 后端时标记，供灯光/校准/风扇等本地通道判断。</summary>
    public static void MarkActive()
    {
        System.Threading.Volatile.Write(ref _active, 1);
        // EC 模式下把「GCU 又起来了」的守护一并挂上（开机就是 EC 模式时也走这里）。
        EnsureGcuGuard();
    }

    public static void ClearActive() => System.Threading.Volatile.Write(ref _active, 0);

    private static int _connectRetrySuppressed;

    /// <summary>本次运行是否已放弃重试连接 GCU（重启程序后自动恢复）。</summary>
    internal static bool ConnectRetrySuppressed => System.Threading.Volatile.Read(ref _connectRetrySuppressed) != 0;

    /// <summary>用户选择保持 GCU 模式但不再重试：本次运行不再发起连接，直到下次启动。</summary>
    internal static void SuppressConnectRetryForRun(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _connectRetrySuppressed, 1) == 0)
        {
            Logger.WriteLine("[gcu-svc] 本次运行不再重试连接 GCU（" + reason + "）");
        }
    }

    /// <summary>
    /// GCU 不可用（服务没装 / 连不上 / 端口不通）时落到 EC 模式，并把这次选择<b>记住</b>：
    /// 下次启动 <see cref="IsDirectConfigured" /> 直接成立，开机就是 EC 就绪，不用再等一轮探测。
    /// <para>幂等：已切过就只补运行时状态，不重复写配置、不重复记日志。</para>
    /// </summary>
    /// <returns>本次是否真的改变了状态（用于调用方决定要不要记日志）。</returns>
    public static bool AutoEnterEcModeWhenGcuMissing(string reason)
    {
        bool alreadyDirect = IsDirectConfigured;
        bool alreadyActive = IsRuntimeActive;
        if (alreadyDirect && alreadyActive)
        {
            return false;
        }
        MarkActive();
        if (!alreadyDirect)
        {
            MechrevoLite.AppConfig.Remove("gcu_ec_experiment");
            MechrevoLite.AppConfig.Set(DirectKey, 1);
            MechrevoLite.AppConfig.Flush();
        }
        Logger.WriteError("[gcu-svc] GCU 不可用（" + reason + "）⇒ 已切 EC 模式"
            + (alreadyDirect ? "（配置已是 EC，仅补运行时状态）" : "并记住选择，下次启动直接 EC 就绪"));
        return true;
    }

    public static async Task<(bool Ok, string Detail)> SetModeAsync(bool ecMode)
    {
        try
        {
            if (ecMode)
            {
                // 先停 GCU 服务端，避免它继续覆盖 EC；⭐ 停完必须核实，停不掉要如实说明。
                (bool gcuStopped, string stopDetail) = StopGcuNow();
                MarkActive();
                MechrevoLite.AppConfig.Remove("gcu_ec_experiment");
                MechrevoLite.AppConfig.Set(DirectKey, 1);
                MechrevoLite.AppConfig.Flush();
                await Task.Run(() => IteLightingLocal.RefreshFromHid());
                // EC 模式下 GCU 服务停机并把启动类型改为禁用，开机不再自启。
                try { System.ValueTuple<bool, string> pause = MechrevoLite.Helpers.OfficialConsoleIsolation.EnsureEcModeIsolation(); Logger.WriteLine("[gcu-svc] 切 EC 模式 ⇒ " + pause.Item2); } catch { }
                try { bool notify = GcuEcExperimental.TryEnableHotkeyNotify(); Logger.WriteLine("[hotkey] 切 EC 模式 ⇒ 按键上报=" + notify); } catch { }
                return (true, "已切换 EC 控制模式：" + (gcuStopped
                    ? "GCUService/GCUBridge 已停止"
                    : "⚠️ GCU 仍在运行（" + stopDetail + "），后台守护每 8 秒会再停一次"));
            }

            ClearActive();
            StopGcuGuard();
            // 刚切回 GCU：服务端才拉起，开预热窗口避免误判「GCU 不可用」。
            GcuWarmup.BeginAfterEcSwitchBack();
            MechrevoLite.AppConfig.Remove("gcu_ec_experiment");
            MechrevoLite.AppConfig.Set(DirectKey, 0);
            MechrevoLite.AppConfig.Flush();
            // 必须先把启动类型恢复回来，否则服务仍是禁用状态，net start 会被 1058 拒掉。
            try { System.ValueTuple<bool, string> resumeFirst = MechrevoLite.Helpers.OfficialConsoleIsolation.ResumeGcuServicesForEcMode(); Logger.WriteLine("[gcu-svc] 回 GCU 模式 ⇒ 恢复服务启动类型 " + resumeFirst.Item2); } catch { }
            // EC 模式曾停止/结束 GCUService；回 GCU 必须先拉起服务端，再拉起 Bridge。
            Run("net", "start GCUService");
            Run("net", "start GCUBridge");
            // 等 GCU 端口就绪再返回：否则紧随其后的自动重启可能过早判定 GCU 不可用，落回 EC-only。
            bool ready = await Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    if (ProbeGcuPort())
                    {
                        return true;
                    }
                    System.Threading.Thread.Sleep(500);
                }
                return false;
            });
            Logger.WriteLine("[gcu-svc] GCU 端口 13688 就绪=" + ready);
            IteLightingLocal.Invalidate();
            // 回 GCU 模式：已开启隔离时只关官方界面进程，服务保持运行。
            try { MechrevoLite.Helpers.OfficialConsoleIsolation.EnsureGcuModeUiIsolation(); } catch { }
            return (true, "已切换 GCU 模式：已请求启动 GCUService / GCUBridge");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 停 GCU 服务/进程并**核实**：sc 停服务（服务名可能不存在，返回码记进日志）→ taskkill 补刀 → 复查进程。
    /// <para>⛔ 不再像旧实现那样"发完命令就当成功"：GCU 是手动启动的服务，会被别的程序按需拉起。</para>
    /// </summary>
    private static (bool Ok, string Detail) StopGcuNow()
    {
        List<string> steps = new();
        steps.Add("sc stop GCUBridge=" + RunCapture("sc", "stop GCUBridge").Code);
        steps.Add("sc stop GCUService=" + RunCapture("sc", "stop GCUService").Code);
        System.Threading.Thread.Sleep(600);
        steps.Add("kill GCUService=" + RunCapture("taskkill", "/IM GCUService.exe /F").Code);
        steps.Add("kill GCUBridge=" + RunCapture("taskkill", "/IM GCUBridge.exe /F").Code);
        System.Threading.Thread.Sleep(300);
        bool running = MechrevoLite.Helpers.OfficialConsoleIsolation.GcuRuntimeRunning();
        string detail = (running ? "仍在运行" : "已停止") + " · " + string.Join(" ", steps);
        Logger.WriteLine("[gcu-svc] 停 GCU ⇒ " + detail);
        return (!running, detail);
    }

    private static (int Code, string Output) RunCapture(string file, string arguments)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null)
            {
                return (-1, "启动失败");
            }
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
                return (-2, "超时");
            }
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.GetType().Name);
        }
    }

    private static System.Threading.Timer? _gcuGuard;
    private static int _guardBusy;
    private static readonly Stopwatch _guardClock = new();

    /// <summary>探测窗口：只在进入 EC 模式后的前 2 分钟内密集核对（GCU 通常就是这阵子被拉起）。</summary>
    private const int GuardWindowSeconds = 120;

    /// <summary>窗口内的核对节拍。</summary>
    private const int GuardStepMs = 8000;

    /// <summary>
    /// EC 模式守护：GCUBridge 是「手动」服务，会被官方控制台/其它组件按需拉起，一次 stop 收不住。
    /// <para>进入 EC 模式后每 8 秒核对一次，**2 分钟窗口结束就停止探测**，常驻不再占开销。</para>
    /// </summary>
    private static void EnsureGcuGuard()
    {
        if (_gcuGuard != null)
        {
            return;
        }
        _guardClock.Restart();
        _gcuGuard = new System.Threading.Timer(_ => GuardTick(), null, 0, System.Threading.Timeout.Infinite);
        Logger.WriteLine("[gcu-svc] EC 模式守护已启动（每 8 秒核对一次，2 分钟后自动停止探测）");
    }

    /// <summary>
    /// 启动时按当前模式挂守护：挂在"一定会跑"的入口（键盘灯分流探测），不依赖某个 UI 启动分支是否执行到。
    /// </summary>
    public static void EnsureGuardIfEcMode()
    {
        if (!IsEcMode)
        {
            return;
        }
        EnsureGcuGuard();
    }

    private static void StopGcuGuard()
    {
        System.Threading.Timer? guard = _gcuGuard;
        _gcuGuard = null;
        try { guard?.Dispose(); } catch { }
        Logger.WriteLine("[gcu-svc] EC 模式守护已停止（切回 GCU 模式）");
    }

    private static void GuardTick()
    {
        try
        {
            if (!IsEcMode
                || System.Threading.Interlocked.Exchange(ref _guardBusy, 1) != 0)
            {
                return;
            }
            try
            {
                if (!MechrevoLite.Helpers.OfficialConsoleIsolation.GcuRuntimeRunning())
                {
                    return;
                }
                (bool ok, string detail) = StopGcuNow();
                Logger.WriteError("[gcu-svc] EC 模式守护：GCU 又起来了 ⇒ 再次停止 " + (ok ? "成功" : "失败（" + detail + "）"));
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _guardBusy, 0);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteError("[gcu-svc] EC 模式守护异常：" + ex.GetType().Name);
        }
        finally
        {
            // 窗口内：自己回填下一拍；窗口结束：彻底停掉，之后不再有任何探测。
            double elapsed = _guardClock.Elapsed.TotalSeconds;
            if (elapsed < GuardWindowSeconds)
            {
                try { _gcuGuard?.Change(GuardStepMs, System.Threading.Timeout.Infinite); } catch { }
            }
            else
            {
                System.Threading.Timer? guard = _gcuGuard;
                _gcuGuard = null;
                try { guard?.Dispose(); } catch { }
                Logger.WriteLine("[gcu-svc] EC 模式守护：2 分钟探测窗口结束 ⇒ 停止探测（已运行 " + (int)elapsed + "s）");
            }
        }
    }

    private static void Run(string file, string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(5000);
        }
        catch
        {
        }
    }

    /// <summary>探测 GCU 服务端口是否已可连接（仅本地连接尝试，不发送报文）。</summary>
    private static bool ProbeGcuPort()
    {
        try
        {
            using System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient();
            System.Threading.Tasks.Task task = client.ConnectAsync("127.0.0.1", 13688);
            return task.Wait(300) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
