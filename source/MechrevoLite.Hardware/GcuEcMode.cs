using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MechrevoLite.Hardware;

/// <summary>GCU 控制模式 / EC 控制模式切换（设置页使用）。</summary>
public static class GcuEcMode
{
    private const string ExperimentKey = "gcu_ec_experiment";
    private const string DirectKey = "gcu_ec_direct";

    public static bool IsEcMode =>
        MechrevoLite.AppConfig.Get(ExperimentKey, 0) != 0
        || MechrevoLite.AppConfig.Get(DirectKey, 0) != 0;

    public static async Task<(bool Ok, string Detail)> SetModeAsync(bool ecMode)
    {
        try
        {
            if (ecMode)
            {
                // 先停 GCU 服务端，避免它继续覆盖 EC。
                Run("taskkill", "/IM GCUService.exe /F");
                Run("net", "stop GCUBridge");
                MechrevoLite.AppConfig.Set(ExperimentKey, 1);
                MechrevoLite.AppConfig.Set(DirectKey, 1);
                MechrevoLite.AppConfig.Flush();
                return (true, "已切换 EC 控制模式：GCUService/GCUBridge 已停止");
            }

            MechrevoLite.AppConfig.Set(ExperimentKey, 0);
            MechrevoLite.AppConfig.Set(DirectKey, 0);
            MechrevoLite.AppConfig.Flush();
            Run("net", "start GCUBridge");
            return (true, "已切换 GCU 模式：GCUBridge 已启动");
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
}
