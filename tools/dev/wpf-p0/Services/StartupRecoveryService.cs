// ⭐ task-23：P0 启动恢复补齐（与旧 Program deferredInitialization 对齐）。
// 只做后端就绪后的启动恢复调度；不创建/不修改任何窗口，不改 MainWindow/OriginalStyleWindow。
using System;
using System.Threading;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;

namespace L_Mechrevo.WpfP0.Services;

internal static class StartupRecoveryService
{
    private static int _scheduled;

    /// <summary>
    /// 启动后等待后端就绪，再依次执行旧 Program 的启动恢复项。
    /// ⚠️ 全部 try/catch 独立处理：某一项失败不阻塞其它项；每项都有日志。
    /// </summary>
    internal static void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
        {
            return;
        }
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        MechrevoHw? hw = await WaitForConnectedHwAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (hw == null)
        {
            Logger.WriteLine("[startup-recovery] 后端 60s 内未连接 ⇒ 跳过启动恢复（不重试）");
            return;
        }
        Logger.WriteLine("[startup-recovery] 后端已连接 ⇒ 开始启动恢复");

        // 隔离对齐放在最前面：EC 模式下如果官方 GCU 还开着，先关掉再继续后面的恢复与就绪。
        // GCU 模式下已开启隔离则只关界面，不动 GCU 服务。
        Try("OfficialConsoleIsolation.AlignBeforeReady", () =>
        {
            if (WpfBackend.EcModeActive)
            {
                (bool ok, string detail) = MechrevoLite.Helpers.OfficialConsoleIsolation.EnsureEcModeIsolation();
                Logger.WriteLine("[startup-recovery] EC 模式隔离对齐 ⇒ " + (ok ? "完成" : "未完全生效")
                    + " · " + detail);
                Logger.WriteLine("[startup-recovery] 按键上报=" + MechrevoLite.Hardware.GcuEcExperimental.TryEnableHotkeyNotify());
            }
            else
            {
                (bool ok, string detail) = MechrevoLite.Helpers.OfficialConsoleIsolation.EnsureGcuModeUiIsolation();
                Logger.WriteLine("[startup-recovery] GCU 模式界面隔离 ⇒ " + (ok ? "完成" : "未完成") + " · " + detail);
            }
        });
        StartIsolationGuard();

        Try("BatteryLimitRestore.RunOnce", () =>
        {
            // 宝龙达：正常启动时的限充核对（旧 Program.ScheduleIdleBackendWarmup 路径）；
            // 非宝龙达在 BatteryLimitPlatform 内部 0 读 0 写直接返回。
            BatteryLimitRestore.RunOnce("WPF 启动恢复");

        Try("ChargeGateKeepAlive.ResumeIfPinned", () =>
        {
            // 上次退出时门控仍处于钉住状态 ⇒ 接着守（只在开关打开时才会真的启动）。
            MechrevoLite.Hardware.ChargeGateKeepAlive.ResumeIfPinned();
        });
        });

        Try("RunDeferredFanStartupCleanup", () =>
        {
            // 旧 Program 启动 deferred 段的同一调用：宝龙达首次初始化风扇闸门/交回固件。
            hw.RunDeferredFanStartupCleanup();
        });

        Try("RuntimeCpuTuning.ApplySavedOnStartup", () =>
        {
            // 旧 Program.ScheduleCpuCoAutoApply 的启动入口；内部有 clean-exit/开关/能力门控。
            RuntimeCpuTuning.ApplySavedOnStartup();
            // 结论可见化：开关开着但被跳过（关机守卫/能力不支持）时提示一次，避免"以为已经自动应用"。
            if (RuntimeCpuTuning.SkippedByGuard)
            {
                try
                {
                    ToastService.Show("分核负压：上次非正常关机，本次未自动应用（自动应用开关已关闭）", failure: true);
                }
                catch
                {
                }
            }
        });

        Try("WujieFanCurveAutoApply.Schedule", () =>
        {
            // 无界 APU 自定义档开机自动曲线（内部等 4s 并判 WujieSmuApu/自定义档）。
            WujieFanCurveAutoApply.Schedule();
        });

        Try("EcFanCurveAutoApply.Schedule", () =>
        {
            // 同方 EC 模式的自定义档曲线：EC 风扇表重启会回固件默认，这里补一次重写（内部判 EC 模式/自定义档/开关）。
            EcFanCurveAutoApply.Schedule();
        });

        Try("DisplayToggleRestore.RunOnce", () =>
        {
            // 分区控光 / LCD Overdrive 在 EC 模式下没有回读通道，按上次保存的选择重放一次。
            DisplayToggleRestore.RunOnce();
        });

        Try("PowerWallRestore.RunOnce", () =>
        {
            // 自定义档的 PL1/PL2/fPPT 随档位保存，但开机回档不重写功耗墙；这里补一次重放。
            PowerWallRestore.RunOnce();
        });

        Try("WaterCoolerRestore.RunOnce", () =>
        {
            // 水冷档位固件不保存：把用户最后选的档位在开机后补发一次（没有保存值则跳过）。
            if (!WaterCoolerRestore.HasSaved())
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000).ConfigureAwait(false);
                    WaterCoolerRoutingService? routing = WpfBackend.CreateWaterCoolerRouting();
                    if (routing == null)
                    {
                        return;
                    }
                    await routing.InitializeAsync().ConfigureAwait(false);
                    await WaterCoolerRestore.RunOnce(routing).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.WriteError("[water-cooler] 启动重放异常：" + ex.Message);
                }
            });
        });

        Try("Pl4UnitAutoApply.ApplyIfNeeded", () =>
        {
            // 用户记录过 PL4 口径时，开机对齐一次 EC；EC 已一致则不重复下发。
            Pl4UnitAutoApply.ApplyIfNeeded("启动");
        });

        Try("OfficialConsoleIsolation.SyncServicesForMode", () =>
        {
            // 后面这一步只做"模式↔服务启动类型"的对账（隔离对齐已在最前面做过一次）。
            bool ecMode = L_Mechrevo.WpfP0.Services.WpfBackend.EcModeActive;
            if (ecMode)
            {
                Logger.WriteLine("[startup-recovery] EC 模式 ⇒ " + MechrevoLite.Helpers.OfficialConsoleIsolation.PauseGcuServicesForEcMode().Item2);
            }
            else
            {
                Logger.WriteLine("[startup-recovery] GCU 模式 ⇒ " + MechrevoLite.Helpers.OfficialConsoleIsolation.ResumeGcuServicesForEcMode().Item2);
            }
        });

        Logger.WriteLine("[startup-recovery] 启动恢复调度结束");
    }

    private static int _guardStarted;

    /// <summary>
    /// 运行期守卫：每 20 秒核对一次官方控制台状态。
    /// EC 模式 ⇒ 界面与服务都要关（官方会自己重启，所以反复核对）；GCU 模式 ⇒ 开了隔离只关界面。
    /// </summary>
    private static void StartIsolationGuard()
    {
        if (Interlocked.Exchange(ref _guardStarted, 1) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    try
                    {
                        string detail = WpfBackend.EcModeActive
                            ? MechrevoLite.Helpers.OfficialConsoleIsolation.GuardEcModeIsolation()
                            : MechrevoLite.Helpers.OfficialConsoleIsolation.GuardGcuModeUiIsolation();
                        if (detail.Length > 0)
                        {
                            Logger.WriteLine("[startup-recovery] 隔离守卫 ⇒ " + detail);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine("[startup-recovery] 隔离守卫生效异常：" + ex.Message);
                    }
                }
            }
            finally
            {
                timer.Dispose();
            }
        });
    }
    private static async Task<MechrevoHw?> WaitForConnectedHwAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            MechrevoHw? hw = WpfBackend.Hw;
            if (hw != null && hw.IsConnected)
            {
                return hw;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        return null;
    }

    private static void Try(string name, Action action)
    {
        try
        {
            action();
            Logger.WriteLine("[startup-recovery] " + name + " ⇒ 已执行（成功/由内部判据跳过）");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("[startup-recovery] " + name + " 失败（已吞掉，不重试）："
                + ex.GetType().Name + " " + ex.Message);
        }
    }
}
