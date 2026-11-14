using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MechrevoLite.Hardware;

/// <summary>
/// 规则表加载：默认内置规则，可选 JSON 覆盖；任何非法配置整份回退内置规则。
/// </summary>
internal static class BackendRuleStore
{
	internal const string PathEnvVar = "LM_BACKEND_RULES_JSON";
	internal const string FileName = "backend_rules.json";

	internal sealed record RuleTable(
		BackendRouter.RuleSpec[] Rules,
		MechrevoBackendKind Fallback,
		string Source,
		string Path,
		string Error,
		long StampTicks = 0,
		long StampLength = -1);

	/// <summary>文件戳（末次写入 ticks + 长度）；用于判断是否需要热重载。</summary>
	internal static (long Ticks, long Length) Stamp(string path)
	{
		try
		{
			if (string.IsNullOrEmpty(path))
			{
				return (0L, -1L);
			}
			FileInfo info = new FileInfo(path);
			if (!info.Exists)
			{
				return (0L, -1L);
			}
			return (info.LastWriteTimeUtc.Ticks, info.Length);
		}
		catch
		{
			return (0L, -2L);
		}
	}

	/// <summary>当前生效表所对应的文件是否已变化（新增/删除/改写）。</summary>
	internal static bool HasChanged(RuleTable table)
	{
		(long ticks, long length) = Stamp(table.Path);
		return ticks != table.StampTicks || length != table.StampLength;
	}

	internal static RuleTable Load(BackendRouter.RuleSpec[] builtIn, MechrevoBackendKind builtInFallback)
	{
		string path;
		try
		{
			path = GetPath();
		}
		catch (Exception ex)
		{
			return Fallback(builtIn, builtInFallback, "", "路径解析失败：" + ex.GetType().Name + " " + ex.Message);
		}
		if (!File.Exists(path))
		{
			string error = HasCustomPath() ? "文件不存在：" + path : "";
			(long ticks0, long length0) = Stamp(path);
			return new RuleTable(builtIn, builtInFallback, "built-in", path, error, ticks0, length0);
		}
		try
		{
			return Parse(File.ReadAllText(path), builtIn, builtInFallback, path);
		}
		catch (Exception ex)
		{
			return Fallback(builtIn, builtInFallback, path, ex.GetType().Name + " " + ex.Message);
		}
	}

	/// <summary>JSON → 规则表（纯函数，可离线断言；任何非法内容整份回退内置规则）。</summary>
    internal static RuleTable Parse(string json, BackendRouter.RuleSpec[] builtIn, MechrevoBackendKind builtInFallback, string path)
	{
		(long ticks, long length) = Stamp(path);
		try
		{
			BackendRuleConfig? config = JsonSerializer.Deserialize<BackendRuleConfig>(json,
				new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true,
					ReadCommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true
				});
			if (config == null)
			{
				return Fallback(builtIn, builtInFallback, path, "反序列化结果为空");
			}
			if (config.Version != 1)
			{
				return Fallback(builtIn, builtInFallback, path, "version=" + config.Version + "（只支持 1）");
			}
			if (config.Rules == null || config.Rules.Count == 0)
			{
				return Fallback(builtIn, builtInFallback, path, "rules 为空");
			}
			MechrevoBackendKind fallback = builtInFallback;
			if (!string.IsNullOrWhiteSpace(config.Fallback))
			{
				if (!TryParseName(config.Fallback, out MechrevoBackendKind parsed)
					|| parsed == MechrevoBackendKind.None)
				{
					return Fallback(builtIn, builtInFallback, path, "fallback=" + config.Fallback + " 非法");
				}
				if (parsed != builtInFallback)
				{
					return Fallback(builtIn, builtInFallback, path, "fallback 只允许 " + builtInFallback);
				}
			}

			List<BackendRouter.RuleSpec> rules = new List<BackendRouter.RuleSpec>();
			HashSet<int> indexes = new HashSet<int>();
			for (int i = 0; i < config.Rules.Count; i++)
			{
				BackendRuleEntry entry = config.Rules[i];
				if (entry.Index <= 0 || !indexes.Add(entry.Index))
				{
					return Fallback(builtIn, builtInFallback, path, "第 " + (i + 1) + " 条 index 非法或重复");
				}
				if (!TryParseName(entry.Backend, out MechrevoBackendKind backend)
					|| backend == MechrevoBackendKind.None)
				{
					return Fallback(builtIn, builtInFallback, path, "第 " + (i + 1) + " 条 backend 非法");
				}
				if (entry.Conditions == null || entry.Conditions.Count == 0)
				{
					return Fallback(builtIn, builtInFallback, path, "第 " + (i + 1) + " 条 conditions 为空");
				}
				BackendRouter.RuleCondition[] conditions = new BackendRouter.RuleCondition[entry.Conditions.Count];
				for (int j = 0; j < entry.Conditions.Count; j++)
				{
					BackendRuleConditionEntry condition = entry.Conditions[j];
					if (!TryParseName(condition.Fact, out BackendRouter.RouteFact fact))
					{
						return Fallback(builtIn, builtInFallback, path,
							"第 " + (i + 1) + " 条第 " + (j + 1) + " 个 fact 非法：" + (condition.Fact ?? "(空)"));
					}
					conditions[j] = new BackendRouter.RuleCondition(fact, condition.Expected);
				}
				string description = string.IsNullOrWhiteSpace(entry.Description)
					? "config rule #" + entry.Index
					: entry.Description;
				rules.Add(new BackendRouter.RuleSpec(entry.Index, description, backend, conditions));
			}
			return new RuleTable(rules.ToArray(), fallback, "file", path, "", ticks, length);
		}
		catch (Exception ex)
		{
			return Fallback(builtIn, builtInFallback, path, ex.GetType().Name + " " + ex.Message);
		}
	}

	private static bool TryParseName<T>(string? text, out T value) where T : struct, Enum
	{
		value = default;
		string name = (text ?? string.Empty).Trim();
		if (name.Length == 0 || char.IsDigit(name[0]))
		{
			return false;
		}
		return Enum.TryParse(name, ignoreCase: true, out value)
			&& Enum.IsDefined(typeof(T), value);
	}

	private static RuleTable Fallback(BackendRouter.RuleSpec[] builtIn, MechrevoBackendKind fallback,
		string path, string error)
	{
		// 每次热重载检查都失败时不要刷屏：同一份内容只记一次。
        // 路径为空 = 路由自测的反向用例（没有真实文件），不要写进运行日志。
        if (path.Length != 0)
        {
            Logger.WriteLineIfChanged("route-rules-fallback", "[route] 规则文件不可用 ⇒ 回退内置规则：" + path + " · " + error);
        }
		(long ticks, long length) = Stamp(path);
		return new RuleTable(builtIn, fallback, "built-in", path, error, ticks, length);
	}

	private static bool HasCustomPath()
	{
		try
		{
			return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathEnvVar));
		}
		catch
		{
			return false;
		}
	}

	private static string GetPath()
	{
		try
		{
			string? custom = Environment.GetEnvironmentVariable(PathEnvVar);
			if (!string.IsNullOrWhiteSpace(custom))
			{
				return custom;
			}
		}
		catch
		{
		}
		string dir = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite");
		return System.IO.Path.Combine(dir, FileName);
	}
}
