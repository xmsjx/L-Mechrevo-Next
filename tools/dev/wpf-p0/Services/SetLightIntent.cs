namespace L_Mechrevo.WpfP0.Services;

/// <summary>灯光通道的完整意图：开/关、效果、亮度、速度、颜色。</summary>
internal sealed record SetLightIntent(
	string Topic,
	string Effect,
	int Light,
	int Speed,
	int ColorArgb,
	bool On);
