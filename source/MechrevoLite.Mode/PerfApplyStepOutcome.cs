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
		return cleaned;
	}
}
