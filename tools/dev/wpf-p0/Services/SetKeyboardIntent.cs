namespace L_Mechrevo.WpfP0.Services;

/// <summary>键盘灯通道意图：开关、效果、亮度、速度、颜色。</summary>
internal sealed record SetKeyboardIntent(
	string Effect,
	int Light,
	int Speed,
	int ColorArgb,
	bool On);
