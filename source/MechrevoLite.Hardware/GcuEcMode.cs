using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MechrevoLite.Hardware;

/// <summary>GCU 控制模式 / EC 控制模式切换（设置页使用）。</summary>
public static class GcuEcMode
{
    private const string DirectKey = "gcu_ec_direct";
    private static int _active;

    /// <summary>运行时已进入 EC 后端。</summary>
    public static bool IsRuntimeActive =>
        System.Threading.Volatile.Read(ref _active) != 0;

    /// <summary>用户在配置里明确选择 EC。</summary>
    public static bool IsDirectConfigured =>
        MechrevoLite.AppConfig.Get(DirectKey, 0) != 0;

    /// <summary>EC 控制模式是否生效：运行时已进入 EC，或用户明确选择 EC。</summary>
    public static bool IsEcMode => IsRuntimeActive || IsDirectConfigured;

    /// <summary>EC 模式来源，供日志/诊断区分“已接管”和“用户强制”。</summary>
    public static string Reason => IsRuntimeActive
        ? (IsDirectConfigured ? "runtime+direct" : "runtime")
        : (IsDirectConfigured ? "direct" : "none");

    /// <summary>进入 EC 后端时标记，供灯光/校准/风扇等本地通道判断。</summary>
    public static void MarkActive() => System.Threading.Volatile.Write(ref _active, 1);

    public static void ClearActive() => System.Threading.Volatile.Write(ref _active, 0);

    public static async Task<(bool Ok, string Detail)> SetModeAsync(bool ecMode)
    {
        try
        {
            if (ecMode)
            {
                // 先停 GCU 服务端，避免它继续覆盖 EC。
                Run("net", "stop GCUBridge");
                Run("net", "stop GCUService");
                Run("taskkill", "/IM GCUService.exe /F");
                MarkActive();
                MechrevoLite.AppConfig.Remove("gcu_ec_experiment");
                MechrevoLite.AppConfig.Set(DirectKey, 1);
                MechrevoLite.AppConfig.Flush();
                await Task.Run(() => IteLightingLocal.RefreshFromHid());
                // 若当前处于隔离状态：EC 模式下把 GCU 服务启动类型一并禁用，开机不再自启。
                try { System.ValueTuple<bool, string> pause = MechrevoLite.Helpers.OfficialConsoleIsolation.PauseGcuServicesForEcMode(); Logger.WriteLine("[gcu-svc] 切 EC 模式 ⇒ " + pause.Item2); } catch { }
                try { bool notify = GcuEcExperimental.TryEnableHotkeyNotify(); Logger.WriteLine("[hotkey] 切 EC 模式 ⇒ 按键上报=" + notify); } catch { }
                return (true, "已切换 EC 控制模式：GCUService/GCUBridge 已停止");
            }

            ClearActive();
            MechrevoLite.AppConfig.Remove("gcu_ec_experiment");
            MechrevoLite.AppConfig.Set(DirectKey, 0);
            MechrevoLite.AppConfig.Flush();
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
            // 回 GCU 模式：隔离状态下把 GCU 服务启动类型恢复回去。
            try { System.ValueTuple<bool, string> resume = MechrevoLite.Helpers.OfficialConsoleIsolation.ResumeGcuServicesForEcMode(); Logger.WriteLine("[gcu-svc] 回 GCU 模式 ⇒ " + resume.Item2); } catch { }
            return (true, "已切换 GCU 模式：已请求启动 GCUService / GCUBridge");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + " " + ex.Message);
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
