namespace MechrevoLite.Hardware;

/// <summary>
/// 分核负压（Curve Optimizer）可写的取值区间：SMU 侧下限为 −50，上限 0（不偏移）。
/// <para>界面、写入门控与参数编码共用这一份定义，避免三处各写一个数字。</para>
/// </summary>
internal static class CpuCoRange
{
	internal const int Minimum = -50;

	internal const int Maximum = 0;

	internal static bool IsValid(int margin)
	{
		return margin >= Minimum && margin <= Maximum;
	}
}
