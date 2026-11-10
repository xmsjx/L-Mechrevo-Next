namespace L_Mechrevo.WpfP0.Services;

/// <summary>UI 只表达“要把哪个灯光通道开/关”，不构造硬件命令。</summary>
internal sealed record SetLightPowerIntent(string Topic, bool On);

/// <summary>意图处理结果；UI 只负责显示。</summary>
internal readonly record struct IntentResult(bool Ok, string Detail = "")
{
	internal static IntentResult Success(string detail = "") => new(true, detail);

	internal static IntentResult Fail(string detail) => new(false, detail);
}

/// <summary>应用层命令总线：UI 发意图，服务层负责能力判定与下发。</summary>
internal interface IIntentDispatcher
{
	Task<IntentResult> DispatchAsync(SetLightPowerIntent intent);

	Task<IntentResult> DispatchAsync(SetLightIntent intent);
}

/// <inheritdoc />
internal sealed class IntentDispatcher : IIntentDispatcher
{
	private readonly ILightingService _lighting;

	internal IntentDispatcher(ILightingService lighting)
	{
		_lighting = lighting;
	}

	public async Task<IntentResult> DispatchAsync(SetLightPowerIntent intent)
	{
		ServiceResult result = await _lighting.SetLightPowerAsync(intent.Topic, intent.On).ConfigureAwait(false);
		return new IntentResult(result.Ok, result.Detail);
	}

	public async Task<IntentResult> DispatchAsync(SetLightIntent intent)
	{
		ServiceResult result = await _lighting.ApplyLightIntentAsync(intent).ConfigureAwait(false);
		return new IntentResult(result.Ok, result.Detail);
	}
}
