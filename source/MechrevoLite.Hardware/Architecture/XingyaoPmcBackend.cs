using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>星耀 PMC 后端：当前没有独立 Logo 灯通道。</summary>
internal sealed class XingyaoPmcBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.XingyaoPmc;

	public string DisplayName => "XingyaoPMC";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "星耀后端没有独立 Logo 灯通道", new string[0]);
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
