using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>宝龙达 WMI 后端：Logo 走功能号 15 的开关通道。</summary>
internal sealed class BaolongdaWmiBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.BaolongdaWmi;

	public string DisplayName => "BaolongdaWMI";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Supported,
			BackendChannel.WmiMailbox, "宝龙达功能号 15 提供 Logo 开关",
			new[] { "wmi:Baolongda.Function15" });
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
