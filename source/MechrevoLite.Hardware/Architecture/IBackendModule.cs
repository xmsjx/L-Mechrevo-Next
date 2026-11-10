namespace MechrevoLite.Hardware.Architecture;

/// <summary>后端模块：只负责声明能力与提供通道，不参与 UI 判定。</summary>
internal interface IBackendModule
{
	BackendKind Kind { get; }

	string DisplayName { get; }

	BackendFeatureDeclaration Declare(MachineFacts facts, FeatureProbe probe);
}
