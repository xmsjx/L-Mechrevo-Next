using System;
using System.Text;

namespace MechrevoLite.Hardware.Architecture;

internal sealed record NucUmiFacts(
	bool NucOnly,
	bool Umi,
	bool Wujie,
	bool EcOnly,
	bool Baolongda,
	bool GcuConnected,
	bool Keyboard,
	bool Lightbar,
	bool Logo,
	bool Hinge,
	bool FanTable,
	bool GpuMode,
	bool NucCharge,
	bool NucPerformance,
	bool NucPower)
{
	internal bool Family => NucOnly || Umi;
}

/// <summary>NUC/UMI 11 个目标实现的唯一判断入口。</summary>
internal static class NucUmiCapabilityMap
{
	internal static NucUmiFacts From(MechrevoHw hw)
	{
		if (hw == null)
		{
			return new NucUmiFacts(false, false, false, false, false, false,
				false, false, false, false, false, false, false, false, false);
		}
		bool ecOnly = GcuEcExperimental.Enabled && !hw.IsGcuConnected;
		return new NucUmiFacts(
			hw.IsNucOnly,
			hw.IsUmiMachine,
			hw.WujieSmuApu,
			ecOnly,
			hw.UsesBaolongda,
			hw.IsGcuConnected,
			hw.SupportsKeyboard,
			hw.SupportsLightbar,
			hw.SupportsLogoLight,
			hw.SupportsHingeLight,
			hw.FanCurveSeen,
			hw.IsNucUmiFamily,
			hw.IsNucUmiFamily || ecOnly,
			hw.IsNucUmiFamily || hw.WujieSmuApu || ecOnly,
			hw.IsNucUmiFamily || hw.WujieSmuApu || ecOnly || hw.UsesBaolongda || hw.IsGcuConnected);
	}

	internal static NucUmiOperationSupport Resolve(MechrevoHw hw, NucUmiOperation operation)
	{
		return Resolve(From(hw), operation);
	}

	internal static bool IsSupported(MechrevoHw hw, NucUmiOperation operation)
	{
		return Resolve(hw, operation) == NucUmiOperationSupport.Supported;
	}

	internal static NucUmiOperationSupport Resolve(NucUmiFacts facts, NucUmiOperation operation)
	{
		if (facts == null)
		{
			return NucUmiOperationSupport.Unknown;
		}
		return operation switch
		{
			NucUmiOperation.FanCurve => facts.Family || facts.Wujie || facts.EcOnly
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.FanBoost => facts.Family || facts.Wujie
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.ChargeLimit => facts.NucCharge
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.PerformanceMode => facts.NucPerformance
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.KeyboardLight => facts.Keyboard || facts.Family
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.Lightbar => facts.Lightbar || facts.Umi
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.LogoLight => facts.Logo
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.HingeLight => facts.Hinge
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.DisplayEffect => NucUmiOperationSupport.Supported,
			NucUmiOperation.GpuMode => facts.GpuMode
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			NucUmiOperation.PowerLimit => facts.NucPower
				? NucUmiOperationSupport.Supported : NucUmiOperationSupport.Unknown,
			_ => NucUmiOperationSupport.Unknown
		};
	}

	internal static string RunSelfTest()
	{
		StringBuilder sb = new StringBuilder();
		int pass = 0;
		int fail = 0;
		Check(sb, "nuc-only-fan", new NucUmiFacts(true, false, false, false, false, false,
			false, false, false, false, true, true, true, true, true),
			NucUmiOperation.FanCurve, NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "umi-lightbar", new NucUmiFacts(false, true, false, false, false, false,
			false, false, false, false, false, false, false, false, false),
			NucUmiOperation.Lightbar, NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-only-gpu", new NucUmiFacts(true, false, false, false, false, false,
			false, false, false, false, false, true, false, false, false),
			NucUmiOperation.GpuMode, NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "ec-charge", new NucUmiFacts(false, false, false, true, false, false,
			false, false, false, false, false, false, true, true, true),
			NucUmiOperation.ChargeLimit, NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "ec-power", new NucUmiFacts(false, false, false, true, false, false,
			false, false, false, false, false, false, false, true, true),
			NucUmiOperation.PowerLimit, NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "gcu-display", new NucUmiFacts(false, false, false, false, false, true,
			false, false, false, false, false, false, false, false, false),
			NucUmiOperation.DisplayEffect, NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "unknown-logo", new NucUmiFacts(false, false, false, false, false, false,
			false, false, false, false, false, false, false, false, false),
			NucUmiOperation.LogoLight, NucUmiOperationSupport.Unknown, ref pass, ref fail);
		Check(sb, "nuc-fanboost", F(nuc: true), NucUmiOperation.FanBoost,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-charge", F(nuc: true), NucUmiOperation.ChargeLimit,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-perf", F(nuc: true), NucUmiOperation.PerformanceMode,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-keyboard", F(nuc: true), NucUmiOperation.KeyboardLight,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-power", F(nuc: true), NucUmiOperation.PowerLimit,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "umi-fan", F(umi: true), NucUmiOperation.FanCurve,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "wujie-fan", F(wujie: true), NucUmiOperation.FanCurve,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "wujie-power", F(wujie: true), NucUmiOperation.PowerLimit,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "ec-perf", F(ec: true), NucUmiOperation.PerformanceMode,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "gcu-power", F(gcu: true), NucUmiOperation.PowerLimit,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "umi-power", F(umi: true), NucUmiOperation.PowerLimit,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-logo", F(nuc: true, logo: true), NucUmiOperation.LogoLight,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "nuc-hinge", F(nuc: true, hinge: true), NucUmiOperation.HingeLight,
			NucUmiOperationSupport.Supported, ref pass, ref fail);
		Check(sb, "unknown-power", F(), NucUmiOperation.PowerLimit,
			NucUmiOperationSupport.Unknown, ref pass, ref fail);
		Check(sb, "unknown-fan", F(), NucUmiOperation.FanCurve,
			NucUmiOperationSupport.Unknown, ref pass, ref fail);
		sb.Insert(0, "[nuc-umi 能力矩阵] PASS=" + pass + " FAIL=" + fail + Environment.NewLine);
		return sb.ToString().TrimEnd();
	}

	private static NucUmiFacts F(bool nuc = false, bool umi = false, bool wujie = false,
		bool ec = false, bool gcu = false, bool logo = false, bool hinge = false)
	{
		return new NucUmiFacts(nuc, umi, wujie, ec, false, gcu,
			false, false, logo, hinge, false, nuc || umi, nuc || umi || ec,
			nuc || umi || wujie || ec, nuc || umi || wujie || ec || gcu);
	}

	private static void Check(StringBuilder sb, string scenario, NucUmiFacts facts,
		NucUmiOperation operation, NucUmiOperationSupport expected, ref int pass, ref int fail)
	{
		NucUmiOperationSupport actual = Resolve(facts, operation);
		bool ok = actual == expected;
		if (ok) pass++; else fail++;
		sb.AppendLine((ok ? "  ✓ " : "  ✗ ") + scenario + " ⇒ " + actual + "（期望 " + expected + "）");
	}
}
