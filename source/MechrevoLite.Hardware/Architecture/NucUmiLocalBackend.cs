using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>NUC/UMI 本地后端：当前没有独立 Logo 灯通道。</summary>
internal sealed class NucUmiLocalBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.NucUmiLocal;

	public string DisplayName => "NucUmiLocal";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Unknown,
			BackendChannel.None, "NUC/UMI 没有独立 Logo 灯通道", new string[0]);
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration> { [FeatureId.LogoLight] = logo });
	}
}
