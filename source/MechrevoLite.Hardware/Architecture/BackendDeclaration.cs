using System.Collections.Generic;

namespace MechrevoLite.Hardware.Architecture;

/// <summary>单个功能的声明结果。</summary>
internal sealed record FeatureDeclaration(
	FeatureState State,
	BackendChannel Channel,
	string Reason,
	string[] Sources);

/// <summary>一个后端对功能集合的声明。</summary>
internal sealed record BackendDeclaration(
	BackendKind Kind,
	IReadOnlyDictionary<FeatureId, FeatureDeclaration> Features);
