using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MechrevoLite.Hardware.FanCurve;

namespace MechrevoLite.Hardware
{
    /// <summary>
    /// 风扇曲线下发请求。温度轴是**可空**的：
    /// <list type="bullet">
    /// <item>EC 整表类（NUC·UMI / 同方 EC 直连）必须给，否则用 EC 当前表的轴兜底；</item>
    /// <item>无界只写占空比，缺轴时用固定阶梯；</item>
    /// <item>同方 GCU 由固件表管轴、宝龙达换算 RPM，给了也不用。</item>
    /// </list>
    /// </summary>
    internal readonly record struct FanCurveRequest(
        IReadOnlyList<int>? CpuDuties,
        IReadOnlyList<int>? GpuDuties,
        IReadOnlyList<byte>? CpuTemps,
        IReadOnlyList<byte>? GpuTemps,
        bool Independent,
        bool RampEnabled,
        int RampMs);

    /// <summary>风扇曲线下发结果：Applied=真的写进去了；Writer=走的哪条通道（日志用）。</summary>
    internal readonly record struct FanCurveOutcome(bool Applied, string Writer, string Detail);

    /// <summary>
    /// 风扇曲线的**单一入口**：调用方只说"按这份曲线下发"，由本类按
    /// <see cref="FanCurveBackendFactory" /> 的分类选通道，不再各自判断机型。
    /// <para>四个写手：无界 EC 表（只写占空比 + 锁存/隔离/心跳）、EC 整表（NucFanTable）、
    /// 同方 GCU（Fan/Control 曲线）、宝龙达（由档位应用侧走本机 RPM 闭环，本类只如实回报）。</para>
    /// </summary>
    internal static class FanCurveChannel
    {
        internal static readonly FanCurveOutcome NotHandled =
            new FanCurveOutcome(false, "宝龙达本机", "走占空比→RPM 本机闭环，由档位应用侧处理");

        /// <summary>下发一份曲线（本地通道同步完成，GCU 通道走异步发布）。</summary>
        internal static async Task<FanCurveOutcome> ApplyAsync(MechrevoHw? hw, FanCurveRequest request, CancellationToken ct)
        {
            if (hw == null)
            {
                return new FanCurveOutcome(false, "无后端", "硬件后端未就绪");
            }
            int[]? cpuDuty = ToDuty16(request.CpuDuties);
            if (cpuDuty == null)
            {
                return new FanCurveOutcome(false, "无数据", "没有可下发的 CPU 曲线点位");
            }
            int[] gpuDuty = ToDuty16(request.GpuDuties) ?? cpuDuty;
            byte[]? cpuTemps = ToTemps(request.CpuTemps);
            byte[]? gpuTemps = ToTemps(request.GpuTemps);

            FanCurveBackendKind kind = FanCurveBackendFactory.Resolve(hw);
            try
            {
                switch (kind)
                {
                    case FanCurveBackendKind.Baolongda:
                        // 宝龙达没有固件风扇表：曲线是"占空比→RPM 存本机 + 3 秒闭环"，由档位应用侧负责。
                        return NotHandled;

                    case FanCurveBackendKind.Wujie:
                    {
                        // 无界只写 EC 表前 8 字节（EC 取前 8 字节最大值），温度轴只用于软件侧选点。
                        byte[] axis = cpuTemps is { Length: 16 } ? cpuTemps : WujieFanEcController.DefaultTemperatures();
                        byte[] gpuAxis = gpuTemps is { Length: 16 } ? gpuTemps : axis;
                        WujieFanEcController controller = new WujieFanEcController();
                        bool ok = controller.Start(axis, cpuDuty, request.Independent, gpuAxis, gpuDuty, out string detail);
                        return new FanCurveOutcome(ok, "无界 EC 表", detail);
                    }

                    case FanCurveBackendKind.NucUmi:
                    case FanCurveBackendKind.Ec:
                    {
                        if (cpuTemps is not { Length: 16 } || gpuTemps is not { Length: 16 })
                        {
                            // 没存过温度轴 ⇒ 用 EC 当前表的轴（重启后是固件默认轴）。
                            if (NucFanTable.TryReadCurrent(out byte[] currentCpu, out _, out byte[] currentGpu, out _))
                            {
                                if (cpuTemps is not { Length: 16 })
                                {
                                    cpuTemps = currentCpu;
                                }
                                if (gpuTemps is not { Length: 16 })
                                {
                                    gpuTemps = currentGpu;
                                }
                            }
                        }
                        if (cpuTemps is not { Length: 16 } || gpuTemps is not { Length: 16 })
                        {
                            return new FanCurveOutcome(false, "EC 整表", "温度轴缺失（既没保存也读不到当前表）");
                        }
                        bool ok = NucFanTable.TryApply(cpuTemps, cpuDuty, gpuTemps, gpuDuty, out string detail);
                        bool isolated = NucFanTable.TrySetFanIsolated(true, out _);
                        bool ramp = NucFanTable.TrySetFanRamp(request.RampEnabled, request.RampMs, out _);
                        string writer = kind == FanCurveBackendKind.NucUmi ? "NUC/UMI EC 整表" : "同方 EC 整表";
                        return new FanCurveOutcome(ok && isolated, writer,
                            detail + (ramp ? "" : " · 切换速度未写入"));
                    }

                    case FanCurveBackendKind.Gcu:
                    {
                        await hw.SetFanCurve(0, cpuDuty).ConfigureAwait(continueOnCapturedContext: false);
                        if (!ReferenceEquals(gpuDuty, cpuDuty))
                        {
                            await hw.SetFanCurve(1, gpuDuty).ConfigureAwait(continueOnCapturedContext: false);
                        }
                        return new FanCurveOutcome(true, "同方 GCU 曲线", "已发布 CPU/GPU 曲线");
                    }

                    default:
                        return new FanCurveOutcome(false, "未选择", "当前没有可用的风扇曲线通道");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new FanCurveOutcome(false, kind.ToString(), "下发异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>曲线后端分类（供日志/界面显示，不参与逻辑判断）。</summary>
        internal static string DescribeBackend(MechrevoHw? hw)
        {
            return FanCurveBackendFactory.Resolve(hw) switch
            {
                FanCurveBackendKind.Baolongda => "宝龙达本机闭环",
                FanCurveBackendKind.Wujie => "无界 EC 表",
                FanCurveBackendKind.NucUmi => "NUC/UMI EC 整表",
                FanCurveBackendKind.Ec => "同方 EC 整表",
                FanCurveBackendKind.Gcu => "同方 GCU",
                _ => "无"
            };
        }

        /// <summary>把任意长度的点位扩成 16 点（不足用最后一点补齐，超出截断）。</summary>
        private static int[]? ToDuty16(IReadOnlyList<int>? values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }
            int[] result = new int[16];
            for (int i = 0; i < result.Length; i++)
            {
                int value = i < values.Count ? values[i] : values[values.Count - 1];
                result[i] = Math.Clamp(value, 0, 100);
            }
            return result;
        }

        private static byte[]? ToTemps(IReadOnlyList<byte>? values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }
            if (values.Count == 16)
            {
                byte[] same = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    same[i] = values[i];
                }
                return same;
            }
            return null;
        }
    }
}
