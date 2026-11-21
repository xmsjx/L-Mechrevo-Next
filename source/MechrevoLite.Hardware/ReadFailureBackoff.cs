using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware
{
    /// <summary>
    /// 读取失败阶梯退避。
    /// <para>某些硬件读取通道在设备缺失/驱动未就绪时会**每次调用都付一次失败的代价**
    /// （例如每次 CreateFile + IOCTL 各几百毫秒），而采样泵每 2.5 秒就会调它们一次。
    /// 这类通道连续失败后不再每拍重试，而是按阶梯暂停一段时间；任意一次成功立即复位。</para>
    /// <para>阶梯：连续失败 3 次 ⇒ 暂停 5 分钟 ⇒（期满只试一次，失败即升级）20 分钟 ⇒ 60 分钟
    /// ⇒ 第 4 档"本进程内停用"（重启后重新尝试）。第 4 档用进程内静态集合记录，
    /// 这样上层对象重建（采样健康维护会重建监视器）也不会让阶梯白做。</para>
    /// </summary>
    internal static class ReadFailureBackoff
    {
        /// <summary>连续失败多少次进入第一档。</summary>
        private const int FailureThreshold = 3;

        /// <summary>各档暂停时长（毫秒）：第 1 档 5 分钟、第 2 档 20 分钟、第 3 档 60 分钟。</summary>
        private static readonly long[] StagePausesMs =
        {
            5L * 60 * 1000,
            20L * 60 * 1000,
            60L * 60 * 1000
        };

        /// <summary>超过档位数量即"本进程内停用"。</summary>
        private const int DisabledStage = 4;

        private sealed class ChannelState
        {
            public int ConsecutiveFailures;
            public int Stage;
            public long NextAttemptAt;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ChannelState> States = new Dictionary<string, ChannelState>(StringComparer.Ordinal);

        /// <summary>时钟（毫秒、单调递增）。自检注入假时钟，运行期用 TickCount64。</summary>
        internal static Func<long> Now = () => Environment.TickCount64;

        /// <summary>true = 该通道处于退避中，本次不要读。</summary>
        internal static bool ShouldSkip(string channel)
        {
            if (string.IsNullOrEmpty(channel))
            {
                return false;
            }
            lock (Gate)
            {
                if (!States.TryGetValue(channel, out ChannelState? state) || state == null)
                {
                    return false;
                }
                if (state.Stage >= DisabledStage)
                {
                    return true;
                }
                if (state.Stage <= 0)
                {
                    return false;
                }
                return Now() < state.NextAttemptAt;
            }
        }

        /// <summary>读成功：清零并（若是从退避里出来）记一行恢复日志。</summary>
        internal static void NoteSuccess(string channel)
        {
            if (string.IsNullOrEmpty(channel))
            {
                return;
            }
            bool recovered;
            lock (Gate)
            {
                if (!States.TryGetValue(channel, out ChannelState? state) || state == null)
                {
                    return;
                }
                recovered = state.ConsecutiveFailures > 0 || state.Stage > 0;
                state.ConsecutiveFailures = 0;
                state.Stage = 0;
                state.NextAttemptAt = 0;
            }
            if (recovered)
            {
                Logger.WriteLine("[backoff] 通道=" + channel + " 读取恢复 ⇒ 退避清零");
            }
        }

        /// <summary>读失败：累计；达到阈值进入/升级退避档。</summary>
        internal static void NoteFailure(string channel)
        {
            if (string.IsNullOrEmpty(channel))
            {
                return;
            }
            int stage;
            long pauseMs = 0;
            int failures;
            lock (Gate)
            {
                if (!States.TryGetValue(channel, out ChannelState? state) || state == null)
                {
                    state = new ChannelState();
                    States[channel] = state;
                }
                if (state.Stage >= DisabledStage)
                {
                    return;
                }
                state.ConsecutiveFailures++;
                failures = state.ConsecutiveFailures;
                if (state.Stage > 0)
                {
                    // 期满那一次试探仍失败 ⇒ 升档
                    state.Stage++;
                }
                else if (state.ConsecutiveFailures >= FailureThreshold)
                {
                    state.Stage = 1;
                }
                stage = state.Stage;
                if (stage >= DisabledStage)
                {
                    state.NextAttemptAt = long.MaxValue;
                }
                else if (stage > 0)
                {
                    pauseMs = StagePausesMs[Math.Min(stage, StagePausesMs.Length) - 1];
                    state.NextAttemptAt = Now() + pauseMs;
                }
            }
            if (stage >= DisabledStage)
            {
                Logger.WriteLine("[backoff] 通道=" + channel + " 连续失败 " + failures
                    + " 次 ⇒ 本进程内停用该通道（重启后重新尝试）");
            }
            else if (stage > 0 && pauseMs > 0)
            {
                Logger.WriteLine("[backoff] 通道=" + channel + " 连续失败 " + failures
                    + " 次 ⇒ 第 " + stage + " 档：暂停 " + (pauseMs / 60000) + " 分钟（到期只试一次，仍失败即升档）");
            }
        }

        /// <summary>
        /// 探针统一入口：退避期内直接返回 false（不再付一次失败的代价）；失败按阶梯记账并落一行去重错误；
        /// 成功立即复位。探针侧只写这一句，⛔ 不再各写一份「暂停多久 / 记几次 / 什么时候复位」的重复代码。
        /// </summary>
        /// <param name="channel">通道名（同一物理通道用同一个名字，跨对象重建也能接上阶梯）。</param>
        /// <param name="failNote">失败时给人看的一句话（可含命令字等信息）。</param>
        /// <param name="probe">真正去读硬件的委托；返回 true 表示这一拍读成功。</param>
        internal static bool TryRun(string channel, string failNote, Func<bool> probe)
        {
            if (probe == null)
            {
                return false;
            }
            if (ShouldSkip(channel))
            {
                return false;
            }
            bool ok;
            try
            {
                ok = probe();
            }
            catch (Exception ex)
            {
                NoteFailure(channel);
                LogProbeFailure(channel, failNote + "：" + ex.GetType().Name + " " + ex.Message);
                return false;
            }
            if (ok)
            {
                NoteSuccess(channel);
                return true;
            }
            NoteFailure(channel);
            LogProbeFailure(channel, failNote);
            return false;
        }

        /// <summary>失败只在该通道该档位第一次出现时落一行（默认日志级别可见），档位变化才再记。</summary>
        private static void LogProbeFailure(string channel, string note)
        {
            Logger.WriteErrorIfChanged("probe-backoff:" + channel + ":" + note,
                "[probe] 通道=" + channel + " " + note + " ⇒ " + Describe(channel));
        }

        /// <summary>清空所有通道的退避（仅"手动恢复本地采样"这个显式入口可以调用）。</summary>
        internal static void ResetAll()
        {
            lock (Gate)
            {
                States.Clear();
            }
            Logger.WriteLine("[backoff] 手动恢复 ⇒ 已清空全部通道退避");
        }

        /// <summary>当前状态描述（诊断用，不参与逻辑）。</summary>
        internal static string Describe(string channel)
        {
            lock (Gate)
            {
                if (!States.TryGetValue(channel, out ChannelState? state) || state == null)
                {
                    return "正常";
                }
                if (state.Stage >= DisabledStage)
                {
                    return "已停用（重启后重试）";
                }
                if (state.Stage <= 0)
                {
                    return "正常（连续失败 " + state.ConsecutiveFailures + "）";
                }
                long remain = Math.Max(0, state.NextAttemptAt - Now());
                return "第 " + state.Stage + " 档（剩余 " + (remain / 1000) + " 秒）";
            }
        }

        /// <summary>状态机自检（纯内存、可注入时钟；不碰任何硬件）。</summary>
        internal static void RunSelfTest(List<string> log)
        {
            Func<long> previous = Now;
            long clock = 1_000_000;
            try
            {
                ResetAll();
                Now = () => clock;

                // ① 未达阈值不暂停
                for (int i = 0; i < FailureThreshold - 1; i++)
                {
                    NoteFailure("selftest");
                }
                log.Add("未达阈值不暂停: " + (ShouldSkip("selftest") == false ? "OK" : "FAIL"));

                // ② 达到阈值 ⇒ 第一档暂停 5 分钟
                NoteFailure("selftest");
                log.Add("第一档进入: " + (ShouldSkip("selftest") ? "OK" : "FAIL"));
                clock += StagePausesMs[0] - 1000;
                log.Add("第一档期内仍跳过: " + (ShouldSkip("selftest") ? "OK" : "FAIL"));
                clock += 2000;
                log.Add("第一档期满允许试一次: " + (ShouldSkip("selftest") == false ? "OK" : "FAIL"));

                // ③ 试探失败 ⇒ 第二档 20 分钟
                NoteFailure("selftest");
                clock += StagePausesMs[1] - 1000;
                log.Add("第二档期内跳过: " + (ShouldSkip("selftest") ? "OK" : "FAIL"));
                clock += 2000;

                // ④ 再失败 ⇒ 第三档 60 分钟
                NoteFailure("selftest");
                log.Add("第三档期内跳过: " + (ShouldSkip("selftest") ? "OK" : "FAIL"));
                clock += StagePausesMs[2] + 1000;

                // ⑤ 再失败 ⇒ 停用到进程重启（期满也不再试）
                NoteFailure("selftest");
                clock += StagePausesMs[2] * 10;
                log.Add("第四档停用: " + (ShouldSkip("selftest") ? "OK" : "FAIL"));

                // ⑥ 成功清零
                NoteSuccess("selftest");
                log.Add("成功后清零: " + (ShouldSkip("selftest") == false ? "OK" : "FAIL"));

                // ⑦ ResetAll 也能清掉停用态
                for (int i = 0; i < FailureThreshold + 3; i++)
                {
                    clock += StagePausesMs[2] + 1000;
                    NoteFailure("selftest2");
                }
                bool disabled = ShouldSkip("selftest2");
                ResetAll();
                log.Add("停用态可被手动恢复清掉: " + (disabled && ShouldSkip("selftest2") == false ? "OK" : "FAIL"));
            }
            finally
            {
                Now = previous;
                ResetAllSilent();
            }
        }

        private static void ResetAllSilent()
        {
            lock (Gate)
            {
                States.Clear();
            }
        }
    }
}
