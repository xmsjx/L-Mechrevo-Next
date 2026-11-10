namespace MechrevoLite.Hardware.Architecture;

/// <summary>功能下发实际使用的通道。</summary>
internal enum BackendChannel
{
	None = 0,
	Gcu,
	Ec,
	AcpiWmi,
	WmiMailbox,
	Local
}
