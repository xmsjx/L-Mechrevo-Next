using System;
using System.Threading.Tasks;
using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Hardware.Architecture;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 屏幕开关（分区控光 / LCD Overdrive）的记忆重放。
/// <para>这两项在多数同方机型上只有下发、没有回读，固件开机回自己的默认值 ⇒ 只能记住上次选择并在启动时重放。</para>
/// <para>⭐ 记忆写在硬件层（<see cref="MechrevoService.SaveDisplayToggleIntent" />），这里只把记忆重新下发一次；
/// "开"的边沿由 <see cref="WpfBackend.ApplyLocalDimming" /> 内部先关再开完成（固件只在状态变化时真正生效）。</para>
/// <para>⛔ 本机没有记忆（配置里没有这个键）时不动固件状态；失败只补发一次，不做无界重试。</para>
/// </summary>
internal static class DisplayToggleRestore
{
    /// <summary>补发前的等待：只补一次，避免开机阶段反复打硬件。</summary>
    private const int RetryDelayMs = 1000;

    internal static async Task RunOnceAsync()
    {
        try
        {
            // 只对"同方系"重放：星耀走 WMAA、宝龙达走 WMI 邮箱、NUC/UMI 走本地 HID，
            // 那些机型的寄存器位置与同方通道不同，不能套同一组命令。
            var backend = WpfBackend.ActiveBackendKind;
            bool ecLocal = backend == BackendKind.TongfangEc || backend == BackendKind.TongfangGcu;
            if (!ecLocal)
            {
                Logger.WriteLine("[display-restore] 当前后端=" + backend + " ⇒ 屏幕开关由该后端自行恢复，跳过");
                return;
            }

            // 分区控光：-1 = 本机从没记录过 ⇒ 不动固件状态（别把官方控制台的设置改掉）。
            int ld = AppConfig.Get(MechrevoService.LocalDimmingSavedKey, -1);
            if (ld >= 0)
            {
                bool want = ld != 0;
                bool ok = await SendAsync(want, localDimming: true).ConfigureAwait(false);
                Logger.WriteError("[display-restore] 分区控光按记忆重放=" + (want ? "开" : "关")
                    + " ⇒ " + (ok ? "成功" : "失败（补发一次仍失败）"));
            }
            else
            {
                Logger.WriteLine("[display-restore] 本机没有分区控光记忆 ⇒ 不改固件状态");
            }

            // LCD Overdrive：同一条通道判据与同一个下发入口，避免重放和实际点击走两套。
            int od = AppConfig.Get(MechrevoService.LcdOverdriveSavedKey, -1);
            if (od >= 0)
            {
                bool want = od != 0;
                bool ok = await SendAsync(want, localDimming: false).ConfigureAwait(false);
                Logger.WriteError("[display-restore] LCD Overdrive 按记忆重放=" + (want ? "开" : "关")
                    + " ⇒ " + (ok ? "成功" : "失败（补发一次仍失败）"));
            }
            else
            {
                Logger.WriteLine("[display-restore] 本机没有 OD 记忆 ⇒ 不改固件状态");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteError("[display-restore] 恢复屏幕开关失败：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            // 给界面一个"重放已经有结论"的信号：没生效时提示用户点一下，而不是让按钮默默显示已开。
            WpfBackend.MarkDisplayToggleReplayDone();
        }
    }

    /// <summary>按记忆下发一次，失败补发一次（开机早期 ACPI 桥可能还没排队好）。</summary>
    private static async Task<bool> SendAsync(bool on, bool localDimming)
    {
        bool ok = localDimming
            ? await WpfBackend.SwitchLocalDimmingAsync(on).ConfigureAwait(false)
            : await WpfBackend.SwitchLcdOverdriveAsync(on).ConfigureAwait(false);
        if (!ok)
        {
            await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            ok = localDimming
                ? await WpfBackend.SwitchLocalDimmingAsync(on).ConfigureAwait(false)
                : await WpfBackend.SwitchLcdOverdriveAsync(on).ConfigureAwait(false);
        }
        return ok;
    }
}
