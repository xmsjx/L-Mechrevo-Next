using System;
using System.Collections.Generic;
using System.Text;

namespace MechrevoLite.Hardware;

/// <summary>无界办公本按板号分支：X 系（同方 X 系板）、IP3（深圳 ARN39 板）、BLD（宝龙达 T140 板）。</summary>
internal enum WujieBoardKind
{
	None = 0,
	/// <summary>无界 14X/15X（同方 X 系板，多有 SMU 自定义档 + 独立风扇通道）。</summary>
	XSeries,
	/// <summary>无界 14（IP3 板，ARN39），三档、无自定义档。</summary>
	Ip3,
	/// <summary>无界 14 Pro（宝龙达 T140 板）：宝龙达的办公本，不是蛟龙。</summary>
	Bld
}

/// <summary>
/// 无界办公本的板号判据（DMI 板号 + 产品名，两维都要命中）。
/// <para>⚠️ 只做身份识别：不写任何寄存器、不改后端选择。</para>
/// <para>⚠️ 宝龙达办公本（T140 板）与宝龙达游戏本（蛟龙 MRID6 板）是两回事，不能混：
/// 本类只认 T140/PHX 这类办公本板号，MRID6 不在任何模式里。</para>
/// </summary>
internal static class WujieBoardProbe
{
	private static readonly string[] XSeriesBoards =
	{
		"WUJIE14-GX*", "WUJIE SERIES-X5*", "*GX4HRXL*", "*X5SP4NAG*",
		"*WUJIE*14X*", "*WUJIE*15X*"
	};

	private static readonly string[] XSeriesNames =
	{
		"WUJIE14X*", "WUJIE15X*",
		"WUJIE 14X*", "WUJIE 15X*", "WUJIE SERIES*"
	};

	private static readonly string[] Ip3Boards = { "IP3_ARN39*", "ARN39*" };
	private static readonly string[] Ip3Names = { "WUJIE 14*", "WUJIE14*" };

	private static readonly string[] BldBoards = { "WUJIE14 SERIES-PHX*", "T140*", "T140_PHX*" };
	private static readonly string[] BldNames = { "WUJIE14 PRO*", "WUJIE 14 PRO*" };

	/// <summary>按板号 + 产品名判定无界分支；未命中返回 <see cref="WujieBoardKind.None" />。</summary>
	internal static WujieBoardKind Classify(string board, string productName)
	{
		// 先判板号特征最明确的（T140 / ARN39 板），再判 X 系；产品名用来区分同板不同名。
		if (Matches(board, BldBoards) && Matches(productName, BldNames))
		{
			return WujieBoardKind.Bld;
		}
		if (Matches(board, Ip3Boards) && Matches(productName, Ip3Names))
		{
			return WujieBoardKind.Ip3;
		}
		if (Matches(board, XSeriesBoards) && Matches(productName, XSeriesNames))
		{
			return WujieBoardKind.XSeries;
		}
		return WujieBoardKind.None;
	}

	/// <summary>自测：板号模式表的样本用例（纯字符串，不碰硬件）。</summary>
	internal static string RunSelfTest()
	{
		List<string> pass = new List<string>();
		List<string> fail = new List<string>();
		Check("无界14X（WUJIE14-GX6HRXL / WUJIE 14X Pro）", "WUJIE14-GX6HRXL", "WUJIE 14X Pro",
			WujieBoardKind.XSeries, pass, fail);
		Check("无界15X（WUJIE Series-X5SP4NAG / WUJIE15X）", "WUJIE Series-X5SP4NAG", "WUJIE15X",
			WujieBoardKind.XSeries, pass, fail);
		Check("无界14 IP3（IP3_ARN39 / WUJIE 14）", "IP3_ARN39", "WUJIE 14",
			WujieBoardKind.Ip3, pass, fail);
		Check("无界14 Pro 宝龙达板（T140_PHX / WUJIE14 PRO）", "T140_PHX", "WUJIE14 PRO Series",
			WujieBoardKind.Bld, pass, fail);
		Check("蛟龙16Pro（MRID6-23）不得算无界办公本", "MRID6-23", "Jiaolong 16 Pro",
			WujieBoardKind.None, pass, fail);
		Check("同方游戏本（GM5HG0A）不得算无界", "GM5HG0A", "yilong15 Pro Series GM5HG0A",
			WujieBoardKind.None, pass, fail);
		string line = "      [无界板号判据] PASS=" + pass.Count + " FAIL=" + fail.Count;
		StringBuilder sb = new StringBuilder();
		sb.AppendLine(line);
		for (int i = 0; i < fail.Count; i++)
		{
			sb.AppendLine("        ✗ " + fail[i]);
		}
		return sb.ToString().TrimEnd();
	}

	private static void Check(string scenario, string board, string product, WujieBoardKind expected,
		List<string> pass, List<string> fail)
	{
		WujieBoardKind actual = Classify(board, product);
		string line = scenario + " ⇒ " + actual + "（期望 " + expected + "）";
		if (actual == expected)
		{
			pass.Add(line);
		}
		else
		{
			fail.Add(line);
		}
	}

	private static bool Matches(string target, string[] patterns)
	{
		if (patterns.Length == 0)
		{
			return true;
		}
		for (int i = 0; i < patterns.Length; i++)
		{
			if (MatchWildcard(target, patterns[i]))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>通配语义：<c>*x</c> 后缀、<c>x*</c> 前缀、<c>*x*</c> 包含、无通配为全等（大小写不敏感）。</summary>
	private static bool MatchWildcard(string target, string pattern)
	{
		string t = (target ?? "").Trim().ToUpperInvariant();
		string patternText = (pattern ?? "").Trim().ToUpperInvariant();
		if (patternText.Length == 0)
		{
			return true;
		}
		if (t.Length == 0)
		{
			return false;
		}
		bool startsWild = patternText.StartsWith("*", StringComparison.Ordinal);
		bool endsWild = patternText.EndsWith("*", StringComparison.Ordinal);
		string clean = patternText.Trim('*');
		if (clean.Length == 0)
		{
			return true;
		}
		if (startsWild && endsWild)
		{
			return t.Contains(clean, StringComparison.Ordinal);
		}
		if (startsWild)
		{
			return t.EndsWith(clean, StringComparison.Ordinal);
		}
		if (endsWild)
		{
			return t.StartsWith(clean, StringComparison.Ordinal);
		}
		return t == clean;
	}
}
