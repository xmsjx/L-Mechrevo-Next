using System.Text.RegularExpressions;

namespace MechrevoLite.Mode;

public sealed record PerfApplyStepOutcome(PerfApplyStep Step, PerfApplyResult Result, string Detail = "")
{
	public override string ToString()
	{
		return Friendly(Step?.ToString() + " -> " + Result.ToString() + (string.IsNullOrEmpty(Detail) ? "" : (" (" + Detail + ")")));
	}

	/// <summary>
	/// 面向用户的文本：去掉寄存器/地址这类内部细节，只留"做了什么、成没成"。
	/// </summary>
	internal static string Friendly(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		string cleaned = Regex.Replace(text, @"\s*[\(\[]?\s*0x[0-9A-Fa-f]{1,4}(\s*[/,·]\s*0x[0-9A-Fa-f]{1,4})*\s*[\)\]]?", " ");
		cleaned = Regex.Replace(cleaned, @"\bEC\s*(RAM|寄存器|地址)\b", "EC");
		cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
		cleaned = cleaned.Replace("->", "⇒");
		// 结果词统一成用户口径：能回读验证的才算"已应用"，只发出、无法回读的标"未验证"。
		cleaned = cleaned.Replace("Confirmed", "已应用")
			.Replace("Sent", "已应用（未验证）")
			.Replace("Failed", "未应用")
			.Replace("Stale", "已应用（未验证）");
		return cleaned;
	}
}
