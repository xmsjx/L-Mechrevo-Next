using System;
using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>
/// 声明表 → 统一门控：声明明文说"本机型没有这个功能"时，把后端的 Supported 降为 Unsupported。
/// <para>⚠️ 只降不升：Unknown 不动（Unknown 仍回退旧判据），声明表不能凭空把功能点亮。</para>
/// <para>⚠️ 只在本地 EC/本地通道后端调用；GCU 走 MQTT，不受本机 EC 声明影响。</para>
/// </summary>
internal static class DeclarationGate
{
	/// <summary>FeatureId → 声明表里的功能名 + 本版本已实现、可算"支持"的通道名。</summary>
	private static readonly (FeatureId Feature, string Channel, string[] Implemented)[] Map =
	{
		(FeatureId.ChargeLimit, "chargeLimit", new[] { "ec" }),
		(FeatureId.KeyboardLight, "keyboardBacklight", new[] { "ec", "ec-white", "gcu" }),
		(FeatureId.FanCurve, "fanCurve", new[] { "ec", "gcu" }),
		(FeatureId.FanBoost, "fanBoost", new[] { "ec", "gcu" }),
		(FeatureId.LogoLight, "logoLight", new[] { "ec", "hid", "mqtt" }),
		(FeatureId.Lightbar, "lightbar", new[] { "umi-hdram", "hid", "ec" }),
		(FeatureId.PerformanceMode, "performanceMode", new[] { "wujie-smu", "wmi-umi", "ec", "gcu" }),
		// 以下功能档案里还没有对应键：声明表不写就一直是 NotDeclared（行为不变）；
		// 要按机型关掉时写成 "none"，自带实现写成对象形式。
		(FeatureId.HingeLight, "hingeLight", new[] { "ec", "hid", "local" }),
		(FeatureId.SyncLight, "syncLight", new[] { "ec", "hid", "local" }),
		(FeatureId.LocalDimming, "localDimming", new[] { "ec", "hid", "local" }),
		(FeatureId.LcdOverdrive, "lcdOverdrive", new[] { "ec", "hid", "local" }),
		(FeatureId.DisplayEffect, "displayEffect", new[] { "ec", "hid", "local" }),
		(FeatureId.ColorCalibration, "colorCalibration", new[] { "ec", "hid", "local" }),
		(FeatureId.RefreshRate, "refreshRate", new[] { "local", "wmi", "ec" }),
		(FeatureId.GpuModeSwitch, "gpuModeSwitch", new[] { "local", "wmi", "ec" }),
		(FeatureId.GpuHotSwap, "gpuHotSwap", new[] { "local", "wmi", "ec" }),
		(FeatureId.IgpuOnly, "igpuOnly", new[] { "local", "wmi", "ec" }),
		(FeatureId.PerformanceProfile, "performanceProfile", new[] { "local", "ec", "gcu" }),
		(FeatureId.TurboMode, "turboMode", new[] { "local", "ec", "gcu" }),
		(FeatureId.SilentTurbo, "silentTurbo", new[] { "local", "ec", "gcu" }),
		(FeatureId.PerfTuning, "perfTuning", new[] { "local", "ec", "gcu" })
	};

	internal static (string Channel, string[] Implemented) Describe(FeatureId feature)
	{
		foreach ((FeatureId Feature, string Channel, string[] Implemented) item in Map)
		{
			if (item.Feature == feature)
			{
				return (item.Channel, item.Implemented);
			}
		}
		return (null, null);
	}

	/// <summary>纯函数版本（可测试）：按给定声明把不支持的功能降级。</summary>
	internal static Dictionary<FeatureId, FeatureDeclaration> Apply(IReadOnlyDictionary<FeatureId, FeatureDeclaration> features, MachineManifest manifest)
	{
		Dictionary<FeatureId, FeatureDeclaration> dictionary = new Dictionary<FeatureId, FeatureDeclaration>(features.Count);
		foreach (KeyValuePair<FeatureId, FeatureDeclaration> feature in features)
		{
			dictionary[feature.Key] = Apply(feature.Key, feature.Value, manifest);
		}
		return dictionary;
	}

	internal static FeatureDeclaration Apply(FeatureId feature, FeatureDeclaration declaration, MachineManifest manifest)
	{
		if (manifest == null || declaration == null || declaration.State != FeatureAvailability.Supported)
		{
			return declaration;
		}
		(string, string[]) tuple = Describe(feature);
		if (tuple.Item1 == null)
		{
			return declaration;
		}
		ChannelDecision channelDecision = manifest.Decide(tuple.Item1, tuple.Item2);
		if (!channelDecision.Blocks)
		{
			return declaration;
		}
		return new FeatureDeclaration(FeatureAvailability.Unsupported, BackendChannel.None,
			"声明表判定本机型无此功能（" + channelDecision + "）", new[] { "declaration:" + channelDecision.Kind.ToString().ToLowerInvariant() });
	}

	/// <summary>用运行期解析出的声明包装一个后端的完整声明。</summary>
	internal static BackendFeatureDeclaration Wrap(BackendFeatureDeclaration declaration)
	{
		if (declaration == null)
		{
			return null;
		}
		try
		{
			MachineManifest manifest = DeclarationRuntime.Manifest;
			if (manifest == null)
			{
				return declaration;
			}
			return declaration with { Features = Apply(declaration.Features, manifest) };
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[model-manifest] 门控覆盖失败，保持后端原声明：" + ex.Message);
			return declaration;
		}
	}
}
