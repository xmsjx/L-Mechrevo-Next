namespace MechrevoLite.Hardware.Architecture;

/// <summary>控制后端类别：模具 + 传输通道。</summary>
internal enum BackendKind
{
	None = 0,
	TongfangGcu,
	TongfangEc,
	TongfangMifs,
	BaolongdaWmi,
	NucUmiLocal,
	XingyaoPmc
}
