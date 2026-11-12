namespace MechrevoLite.Hardware;

// 自检只验证规划器/功耗联动，不需要真实 WMI/SMU 探测。
internal static class WujieSmuGate
{
	internal static bool IsXingyaoAmdApu()
	{
		return false;
	}
}
