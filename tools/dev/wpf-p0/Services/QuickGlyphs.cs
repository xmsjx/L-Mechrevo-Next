using System.Windows;
using System.Windows.Media;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 快捷开关图标的字体选择：单字符走图标字体（Segoe MDL2 Assets），
/// 多字符（如 Num / Fn / Aa）走正文粗体，保证"文字图标"不会被图标字体画成空白方块。
/// </summary>
internal static class QuickGlyphs
{
	internal static bool IsText(string? glyph)
	{
		if (string.IsNullOrEmpty(glyph) || glyph.Length <= 1)
		{
			return false;
		}
		char first = glyph[0];
		return char.IsLetterOrDigit(first) || first == '+';
	}

	/// <summary>文字图标的字号（图标字体固定 16）。</summary>
	internal static double Size(string? glyph)
	{
		if (!IsText(glyph))
		{
			return 16;
		}
		string text = glyph ?? "";
		return text.Length >= 3 ? 10.5 : 12.5;
	}

	internal static FontFamily Font(string? glyph)
	{
		if (IsText(glyph))
		{
			return Application.Current?.TryFindResource("AppFontFamily") as FontFamily
				?? new FontFamily("Segoe UI");
		}
		return new FontFamily("Segoe MDL2 Assets");
	}

	internal static FontWeight Weight(string? glyph)
	{
		return IsText(glyph) ? FontWeights.SemiBold : FontWeights.Normal;
	}
}
