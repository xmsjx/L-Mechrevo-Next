using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MechrevoLite.Hardware;

/// <summary>规则文件根对象。</summary>
internal sealed class BackendRuleConfig
{
	[JsonPropertyName("version")]
	public int Version { get; set; }

	[JsonPropertyName("fallback")]
	public string? Fallback { get; set; }

	[JsonPropertyName("rules")]
	public List<BackendRuleEntry>? Rules { get; set; }
}

internal sealed class BackendRuleEntry
{
	[JsonPropertyName("index")]
	public int Index { get; set; }

	[JsonPropertyName("backend")]
	public string? Backend { get; set; }

	[JsonPropertyName("description")]
	public string? Description { get; set; }

	[JsonPropertyName("conditions")]
	public List<BackendRuleConditionEntry>? Conditions { get; set; }
}

internal sealed class BackendRuleConditionEntry
{
	[JsonPropertyName("fact")]
	public string? Fact { get; set; }

	[JsonPropertyName("expected")]
	public bool Expected { get; set; } = true;
}
