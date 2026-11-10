namespace MechrevoLite.Hardware.Architecture;

/// <summary>一次功能门控的最终结果；UI 与服务只消费该结果。</summary>
internal sealed record FeatureDecision(
	FeatureId Feature,
	FeatureState State,
	BackendKind Backend,
	BackendChannel Channel,
	string Reason,
	string[] Sources)
{
	internal bool IsSupported => State == FeatureState.Supported;
}
