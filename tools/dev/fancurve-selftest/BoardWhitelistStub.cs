namespace MechrevoLite.Hardware;

// 自检只跑曲线/SMBIOS 无关逻辑，不引入 System.Management 依赖。
internal static class BoardWhitelist
{
	internal static bool Matches(string? baseBoardProduct)
	{
		return false;
	}
}
