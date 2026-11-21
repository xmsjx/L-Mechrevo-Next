using MechrevoLite.Contracts;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// CPU 自定义调校的服务门面：窗口只把「要读哪些项 / 要写哪个值」交下来，
/// 参数判据、寄存器写入、存档落盘全在后端；窗口不再直接引用后端调校对象。
/// </summary>
internal static class CpuTuningService
{
    /// <summary>逐核自动跟随是否开启。</summary>
    internal static bool PerCoreAutoApplyEnabled => MechrevoLite.Hardware.RuntimeCpuTuning.PerCoreAutoApplyEnabled;

    /// <summary>逐核可保存的核数上限。</summary>
    internal static int PerCoreCapacity => MechrevoLite.Hardware.RuntimeCpuTuning.PerCoreCapacity;

    /// <summary>探查可调项（含逐核区段与 FMax 段）。</summary>
    internal static CpuTuningProbe Probe() => MechrevoLite.Hardware.RuntimeCpuTuning.Instance.Probe();

    /// <summary>核心偏移的可写范围。</summary>
    internal static (int Minimum, int Maximum) CoRange() => MechrevoLite.Hardware.RuntimeCpuTuning.CoRange();

    /// <summary>读回已保存的逐核偏移。</summary>
    internal static IReadOnlyList<int?> LoadSavedPerCore(int coreCount) =>
        MechrevoLite.Hardware.RuntimeCpuTuning.LoadSavedPerCore(coreCount);

    /// <summary>读回已保存的全核偏移。</summary>
    internal static int? LoadSavedAllCore() => MechrevoLite.Hardware.RuntimeCpuTuning.LoadSavedAllCore();

    /// <summary>保存逐核与全核偏移。</summary>
    internal static void SaveSettings(IReadOnlyList<int> perCore, int allCore) =>
        MechrevoLite.Hardware.RuntimeCpuTuning.SaveSettings(perCore, allCore);

    /// <summary>逐核自动跟随开关。</summary>
    internal static void SetAutoApply(bool enabled) => MechrevoLite.Hardware.RuntimeCpuTuning.SetAutoApply(enabled);

    /// <summary>下发逐核偏移。</summary>
    internal static CpuTuningResult ApplyPerCore(IReadOnlyList<int> margins) =>
        MechrevoLite.Hardware.RuntimeCpuTuning.Instance.ApplyPerCore(margins);

    /// <summary>下发单个可调项。</summary>
    internal static CpuTuningResult Apply(string id, decimal value) =>
        MechrevoLite.Hardware.RuntimeCpuTuning.Instance.Apply(id, value);

    /// <summary>恢复全部默认。</summary>
    internal static CpuTuningResult ResetAll() => MechrevoLite.Hardware.RuntimeCpuTuning.Instance.ResetAll();

    /// <summary>FMax 可写范围。</summary>
    internal static decimal FMaxMinimum => MechrevoLite.Hardware.RuntimeCpuTuning.FMaxMinimum;
    internal static decimal FMaxMaximum => MechrevoLite.Hardware.RuntimeCpuTuning.FMaxMaximum;

    /// <summary>本次会话 FMax 是否可写。</summary>
    internal static bool FMaxWritableCapability => MechrevoLite.Hardware.RuntimeCpuTuning.Instance.FMaxWritableCapability;

    /// <summary>读回已保存的 FMax。</summary>
    internal static int? LoadSavedFMax() => MechrevoLite.Hardware.RuntimeCpuTuning.LoadSavedFMax();

    /// <summary>保存 FMax。</summary>
    internal static bool SaveFMax(int mhz) => MechrevoLite.Hardware.RuntimeCpuTuning.SaveFMax(mhz);

    /// <summary>休眠/唤醒后重放已保存的逐核负压与 FMax（唤醒不是重启，走独立入口，不动开机守卫）。</summary>
    internal static CpuTuningResult ReapplyAfterResume() => MechrevoLite.Hardware.RuntimeCpuTuning.ReapplyAfterResume();

    /// <summary>下发 FMax。</summary>
    internal static CpuTuningResult ApplyFMax(decimal mhz) =>
        MechrevoLite.Hardware.RuntimeCpuTuning.Instance.ApplyFMax(mhz);
}
