namespace MechrevoLite.Hardware.Architecture;

/// <summary>功能支持三态；Unknown 不等于不支持。</summary>
internal enum FeatureState
{
	Unknown = 0,
	Supported,
	Unsupported
}
