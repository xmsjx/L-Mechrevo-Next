using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>同方老 MIFS 后端：当前没有 Logo/灯带下发路径。</summary>
internal sealed class TongfangMifsBackend : IBackendModule
{
	public BackendKind Kind => BackendKind.TongfangMifs;

	public string DisplayName => "TongfangMIFS";

	public BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe)
	{
		FeatureDeclaration logo = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "老 MIFS 后端没有 Logo 下发路径", new string[0]);
		FeatureDeclaration lightbar = new FeatureDeclaration(FeatureAvailability.Unknown,
			BackendChannel.None, "老 MIFS 未声明灯带通道，回退旧判据", new string[0]);
		FeatureDeclaration noOemDisplay = new FeatureDeclaration(FeatureAvailability.Unsupported,
			BackendChannel.None, "老 MIFS 后端没有 OEM 显示能力通道", new string[0]);
		return new BackendFeatureDeclaration(Kind,
			new Dictionary<FeatureId, FeatureDeclaration>
			{
				[FeatureId.LogoLight] = logo,
				[FeatureId.Lightbar] = lightbar,
				[FeatureId.LocalDimming] = noOemDisplay,
				[FeatureId.LcdOverdrive] = noOemDisplay,
				[FeatureId.ColorCalibration] = noOemDisplay
			});
	}
}
