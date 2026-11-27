using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MechrevoLite.Hardware;

public sealed class ModelRegistryData
{
	public const string ResourceName = "LMechrevo.model-registry.json";

	public const int SupportedSchemaVersion = 1;

	public const int PlatformCodeCount = 24;

	private static readonly IReadOnlyDictionary<string, int> ExpectedVendorListCounts = new Dictionary<string, int>(StringComparer.Ordinal)
	{
		["commercial"] = 28,
		["commercialHave20Db"] = 14,
		["singleColorKeyboard"] = 9,
		["nonNumpad"] = 3,
		["idpIdy"] = 3
	};

	private static readonly IReadOnlyDictionary<string, int> ExpectedGpuSkuCounts = new Dictionary<string, int>(StringComparer.Ordinal)
	{
		["gn20"] = 10,
		["gn21"] = 19
	};

	private static readonly HashSet<string> ForbiddenFieldNames = new HashSet<string>(StringComparer.Ordinal) { "capabilities", "capability", "capabilitybits", "generation", "dgpugeneration", "generationsource" };

	private static readonly int[] ExpectedDgpuGenerations = new int[3] { 30, 40, 50 };

	public IReadOnlyList<PlatformCodeIdentity> PlatformCodes { get; }

	public IReadOnlyDictionary<int, string> BiosProjectBytes { get; }

	public IReadOnlyDictionary<string, IReadOnlyList<string>> VendorLists { get; }

	public IReadOnlyDictionary<string, IReadOnlyList<string>> GpuSkus { get; }

	public IReadOnlyList<FanTableGrouping> FanTableGroups { get; }

	public IReadOnlyList<int> DgpuGenerations { get; }

	public IReadOnlySet<string> PlatformCodeSet { get; }

	/// <summary>共享通道档案（<c>channelProfiles</c>）：同方系通用的读/写位置只在这里写一次。</summary>
	public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>> ChannelProfiles { get; }

	/// <summary>机型声明（<c>machineManifests</c>）：按声明顺序匹配，首条命中即用。</summary>
	public IReadOnlyList<MachineManifest> Manifests { get; }

	/// <summary>同方通用默认档案名：一般同方机型不写声明，直接用它。</summary>
	public const string GenericProfileName = "tongfang.ec.core";

	/// <summary>一般同方（同方家族、且没有专门声明）使用的默认通道；没有该档案时为 null。</summary>
	public MachineManifest? GenericManifest { get; }

	private ModelRegistryData(IReadOnlyList<PlatformCodeIdentity> platformCodes, IReadOnlyDictionary<int, string> biosProjectBytes, IReadOnlyDictionary<string, IReadOnlyList<string>> vendorLists, IReadOnlyDictionary<string, IReadOnlyList<string>> gpuSkus, IReadOnlyList<FanTableGrouping> fanTableGroups, IReadOnlyList<int> dgpuGenerations, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>> channelProfiles, IReadOnlyList<MachineManifest> manifests)
	{
		PlatformCodes = platformCodes;
		BiosProjectBytes = biosProjectBytes;
		VendorLists = vendorLists;
		GpuSkus = gpuSkus;
		FanTableGroups = fanTableGroups;
		DgpuGenerations = dgpuGenerations;
		PlatformCodeSet = new HashSet<string>(platformCodes.Select((PlatformCodeIdentity code) => code.Code), StringComparer.Ordinal);
		ChannelProfiles = channelProfiles;
		Manifests = manifests;
		GenericManifest = channelProfiles.TryGetValue("tongfang.ec.core", out IReadOnlyDictionary<string, ChannelBinding> value)
			? new MachineManifest("tongfang-generic", "tongfang.ec.core", new ManifestMatch(), null, value)
			: null;
	}

	public static ModelRegistryData Load()
	{
		using Stream stream = typeof(ModelRegistryData).Assembly.GetManifestResourceStream("LMechrevo.model-registry.json");
		if (stream == null)
		{
			throw new ModelRegistryDataException("embedded resource 'LMechrevo.model-registry.json' is missing");
		}
		using StreamReader streamReader = new StreamReader(stream);
		ModelRegistryData modelRegistryData = Parse(streamReader.ReadToEnd());
		try
		{
			Logger.WriteLine("[model-manifest] " + modelRegistryData.DescribeManifests());
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[model-manifest] 摘要写入失败：" + ex.Message);
		}
		return modelRegistryData;
	}

	public static ModelRegistryData Parse(string json)
	{
		JsonDocument jsonDocument;
		try
		{
			jsonDocument = JsonDocument.Parse(json);
		}
		catch (JsonException ex)
		{
			throw new ModelRegistryDataException("registry is not valid JSON: " + ex.Message);
		}
		using (jsonDocument)
		{
			JsonElement rootElement = jsonDocument.RootElement;
			if (rootElement.ValueKind != JsonValueKind.Object)
			{
				throw new ModelRegistryDataException("registry root must be an object");
			}
			RejectForbiddenFields(rootElement, "$");
			int num = RequireInt(rootElement, "schemaVersion");
			if (num != 1)
			{
				throw new ModelRegistryDataException($"unsupported schemaVersion {num} (want {1})");
			}
			IReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>> channelProfiles = ReadChannelProfiles(rootElement);
			IReadOnlyList<MachineManifest> manifests = ReadMachineManifests(rootElement, channelProfiles);
			return new ModelRegistryData(ReadPlatformCodes(rootElement), ReadBiosProjectBytes(rootElement), ReadStringLists(rootElement, "vendorLists", ExpectedVendorListCounts), ReadStringLists(rootElement, "gpuSkus", ExpectedGpuSkuCounts), ReadFanTableGroups(rootElement), ReadDgpuGenerations(rootElement), channelProfiles, manifests);
		}
	}

	public bool IsPlatformCode(string code)
	{
		return PlatformCodeSet.Contains(code);
	}

	private static void RejectForbiddenFields(JsonElement element, string path)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty item in element.EnumerateObject())
			{
				string text = item.Name.ToLowerInvariant();
				bool flag = text.Contains("bios") && text.Contains("generation");
				if (ForbiddenFieldNames.Contains(text) | flag)
				{
					throw new ModelRegistryDataException($"forbidden field '{item.Name}' at {path}: the identity registry carries no capability bits and no per-code generation");
				}
				RejectForbiddenFields(item.Value, path + "." + item.Name);
			}
			return;
		}
		if (element.ValueKind != JsonValueKind.Array)
		{
			return;
		}
		foreach (JsonElement item2 in element.EnumerateArray())
		{
			RejectForbiddenFields(item2, path + "[]");
		}
	}

	private static List<PlatformCodeIdentity> ReadPlatformCodes(JsonElement root)
	{
		JsonElement jsonElement = Require(root, "platformCodes", JsonValueKind.Array);
		List<PlatformCodeIdentity> list = new List<PlatformCodeIdentity>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		HashSet<int> hashSet2 = new HashSet<int>();
		foreach (JsonElement item in jsonElement.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				throw new ModelRegistryDataException("platformCodes entries must be objects");
			}
			string text = ((item.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : null);
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new ModelRegistryDataException("platformCodes entry has no 'code'");
			}
			if (!hashSet.Add(text))
			{
				throw new ModelRegistryDataException("duplicate platform code '" + text + "'");
			}
			int? projectId = null;
			if (item.TryGetProperty("projectId", out var value2) && value2.ValueKind != JsonValueKind.Null)
			{
				if (value2.ValueKind != JsonValueKind.Number || !value2.TryGetInt32(out var value3))
				{
					throw new ModelRegistryDataException("platformCodes '" + text + "' has a non-integer projectId");
				}
				if (!hashSet2.Add(value3))
				{
					throw new ModelRegistryDataException($"duplicate projectId {value3}");
				}
				projectId = value3;
			}
			list.Add(new PlatformCodeIdentity(text, projectId));
		}
		// 至少覆盖这 24 个带风扇表的机型编码；允许追加新机型（如只做本地 EC 的商用机），
		// 但删减仍然报错 —— 这条断言的本意是防止声明表被误删。
		if (list.Count < 24)
		{
			throw new ModelRegistryDataException($"platformCodes must cover at least the {24} fan-table codes, got {list.Count}");
		}
		return list;
	}

	private static IReadOnlyDictionary<int, string> ReadBiosProjectBytes(JsonElement root)
	{
		JsonElement jsonElement = Require(root, "biosProjectBytes", JsonValueKind.Array);
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		foreach (JsonElement item in jsonElement.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				throw new ModelRegistryDataException("biosProjectBytes entries must be objects");
			}
			if (!item.TryGetProperty("masked", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var value2))
			{
				throw new ModelRegistryDataException("biosProjectBytes entry has no integer 'masked'");
			}
			string value3 = ((item.TryGetProperty("name", out var value4) && value4.ValueKind == JsonValueKind.String) ? value4.GetString() : null);
			if (string.IsNullOrWhiteSpace(value3))
			{
				throw new ModelRegistryDataException($"biosProjectBytes entry {value2} has no 'name'");
			}
			if (!dictionary.TryAdd(value2, value3))
			{
				throw new ModelRegistryDataException($"duplicate biosProjectBytes masked value {value2}");
			}
		}
		if (dictionary.Count == 0)
		{
			throw new ModelRegistryDataException("biosProjectBytes is empty");
		}
		return dictionary;
	}

	private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadStringLists(JsonElement root, string name, IReadOnlyDictionary<string, int> expectedCounts)
	{
		JsonElement parent = Require(root, name, JsonValueKind.Object);
		Dictionary<string, IReadOnlyList<string>> dictionary = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		foreach (var (text2, num2) in expectedCounts)
		{
			if (!parent.TryGetProperty(text2, out var _))
			{
				throw new ModelRegistryDataException(name + "." + text2 + " is missing");
			}
			List<string> list = ReadStringArray(parent, text2, name);
			if (list.Count != num2)
			{
				throw new ModelRegistryDataException($"{name}.{text2} must have {num2} members, got {list.Count}");
			}
			dictionary[text2] = list;
		}
		return dictionary;
	}

	private static List<FanTableGrouping> ReadFanTableGroups(JsonElement root)
	{
		JsonElement jsonElement = Require(root, "fanTableGroups", JsonValueKind.Array);
		List<FanTableGrouping> list = new List<FanTableGrouping>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		int num = 0;
		foreach (JsonElement item in jsonElement.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				throw new ModelRegistryDataException("fanTableGroups entries must be objects");
			}
			string text = ((item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : null);
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new ModelRegistryDataException("fanTableGroups entry has no 'id'");
			}
			if (!hashSet.Add(text))
			{
				throw new ModelRegistryDataException("duplicate fanTableGroups id '" + text + "'");
			}
			List<string> list2 = ReadStringArray(item, "keys", "fanTableGroups[" + text + "]");
			List<string> list3 = ReadStringArray(item, "models", "fanTableGroups[" + text + "]");
			if (list2.Count == 0 || list3.Count == 0)
			{
				throw new ModelRegistryDataException("fanTableGroups[" + text + "] must have keys and models");
			}
			if (list2.Distinct(StringComparer.Ordinal).Count() != list2.Count)
			{
				throw new ModelRegistryDataException("fanTableGroups[" + text + "] has duplicate keys");
			}
			num += list3.Count;
			list.Add(new FanTableGrouping(text, list2, list3));
		}
		if (list.Count == 0)
		{
			throw new ModelRegistryDataException("fanTableGroups is empty");
		}
		if (num != 24)
		{
			throw new ModelRegistryDataException($"fanTableGroups models must sum to {24}, got {num}");
		}
		return list;
	}

	/// <summary>
	/// 读 <c>channelProfiles</c>：同方系各机型共用的通道定义写一份，机型只引用。
	/// 没有该节点时返回空表（老声明表照样能加载）。
	/// </summary>
	private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>> ReadChannelProfiles(JsonElement root)
	{
		Dictionary<string, IReadOnlyDictionary<string, ChannelBinding>> dictionary =
			new Dictionary<string, IReadOnlyDictionary<string, ChannelBinding>>(StringComparer.Ordinal);
		if (!root.TryGetProperty("channelProfiles", out var value))
		{
			return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>>(dictionary);
		}
		if (value.ValueKind != JsonValueKind.Object)
		{
			throw new ModelRegistryDataException("channelProfiles must be an object");
		}
		foreach (JsonProperty item in value.EnumerateObject())
		{
			string name = item.Name;
			if (string.IsNullOrWhiteSpace(name))
			{
				throw new ModelRegistryDataException("channelProfiles contains an empty profile name");
			}
			if (dictionary.ContainsKey(name))
			{
				throw new ModelRegistryDataException("duplicate channelProfiles entry '" + name + "'");
			}
			dictionary[name] = ReadChannelMap(item.Value, "channelProfiles." + name);
		}
		return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>>(dictionary);
	}

	private static IReadOnlyDictionary<string, ChannelBinding> ReadChannelMap(JsonElement element, string context)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new ModelRegistryDataException(context + " must be an object");
		}
		Dictionary<string, ChannelBinding> dictionary = new Dictionary<string, ChannelBinding>(StringComparer.Ordinal);
		foreach (JsonProperty item in element.EnumerateObject())
		{
			if (dictionary.ContainsKey(item.Name))
			{
				throw new ModelRegistryDataException(context + " has duplicate feature '" + item.Name + "'");
			}
			dictionary[item.Name] = ReadChannelBinding(item.Value, context + "." + item.Name);
		}
		return new ReadOnlyDictionary<string, ChannelBinding>(dictionary);
	}

	private static ChannelBinding ReadChannelBinding(JsonElement element, string context)
	{
		if (element.ValueKind == JsonValueKind.String)
		{
			string text = element.GetString();
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new ModelRegistryDataException(context + " has an empty channel name");
			}
			return new ChannelBinding(text.Trim(), null, declaredAsString: true);
		}
		if (element.ValueKind != JsonValueKind.Object)
		{
			throw new ModelRegistryDataException(context + " must be a string or an object");
		}
		string channel = Require(element, "channel", JsonValueKind.String).GetString();
		if (string.IsNullOrWhiteSpace(channel))
		{
			throw new ModelRegistryDataException(context + " has an empty channel name");
		}
		Dictionary<string, string> parameters = new Dictionary<string, string>(StringComparer.Ordinal);
		if (element.TryGetProperty("params", out var value))
		{
			if (value.ValueKind != JsonValueKind.Object)
			{
				throw new ModelRegistryDataException(context + ".params must be an object");
			}
			foreach (JsonProperty item in value.EnumerateObject())
			{
				if (item.Value.ValueKind != JsonValueKind.String)
				{
					throw new ModelRegistryDataException(context + ".params." + item.Name + " must be a string");
				}
				parameters[item.Name] = item.Value.GetString() ?? "";
			}
		}
		return new ChannelBinding(channel.Trim(), new ReadOnlyDictionary<string, string>(parameters));
	}

	private static IReadOnlyList<MachineManifest> ReadMachineManifests(JsonElement root, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ChannelBinding>> channelProfiles)
	{
		List<MachineManifest> list = new List<MachineManifest>();
		if (!root.TryGetProperty("machineManifests", out var value))
		{
			return list;
		}
		if (value.ValueKind != JsonValueKind.Array)
		{
			throw new ModelRegistryDataException("machineManifests must be an array");
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				throw new ModelRegistryDataException("machineManifests entries must be objects");
			}
			string text = Require(item, "id", JsonValueKind.String).GetString();
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new ModelRegistryDataException("machineManifests has an empty id");
			}
			if (!hashSet.Add(text))
			{
				throw new ModelRegistryDataException("duplicate machineManifests id '" + text + "'");
			}
			string profile = null;
			if (item.TryGetProperty("profile", out var value2))
			{
				if (value2.ValueKind != JsonValueKind.String)
				{
					throw new ModelRegistryDataException("machineManifests '" + text + "' has a non-string profile");
				}
				profile = value2.GetString();
				if (string.IsNullOrWhiteSpace(profile) || !channelProfiles.ContainsKey(profile))
				{
					throw new ModelRegistryDataException("machineManifests '" + text + "' references unknown profile '" + profile + "'");
				}
			}
			ManifestMatch manifestMatch = ReadManifestMatch(item, text);
			IReadOnlyDictionary<string, ChannelBinding> readOnlyDictionary = null;
			if (item.TryGetProperty("channels", out var value3))
			{
				readOnlyDictionary = ReadChannelMap(value3, "machineManifests '" + text + "'.channels");
			}
			if (profile == null && readOnlyDictionary == null)
			{
				throw new ModelRegistryDataException("machineManifests '" + text + "' declares neither profile nor channels");
			}
			Dictionary<string, ChannelBinding> dictionary = new Dictionary<string, ChannelBinding>(StringComparer.Ordinal);
			IReadOnlyDictionary<string, ChannelBinding> value4 = null;
			if (profile != null && channelProfiles.TryGetValue(profile, out value4))
			{
				foreach (KeyValuePair<string, ChannelBinding> item2 in value4)
				{
					dictionary[item2.Key] = item2.Value;
				}
			}
			if (readOnlyDictionary != null)
			{
				foreach (KeyValuePair<string, ChannelBinding> item3 in readOnlyDictionary)
				{
					// 覆盖项要么是档案里已有的功能（改走别的通道），要么是显式写 none（本机型没有这个功能）。
					// 其余情况多半是功能名拼错，直接报错而不是悄悄多出一个没人读的键。
					if (value4 != null && !value4.ContainsKey(item3.Key) && !item3.Value.IsNone && item3.Value.DeclaredAsString)
					{
						throw new ModelRegistryDataException("machineManifests '" + text + "'.channels." + item3.Key + " is not in profile '" + profile + "' (use an object { \"channel\": \"…\" } for an own implementation, or \"none\" to declare the feature absent)");
					}
					// 参数合并：覆盖项只写要改的参数，其余沿用档案（否则少写一个参数就会悄悄回默认值）。
					if (value4 != null && value4.TryGetValue(item3.Key, out ChannelBinding profileBinding))
					{
						dictionary[item3.Key] = MachineManifest.Merge(profileBinding, item3.Value);
					}
					else
					{
						dictionary[item3.Key] = item3.Value;
					}
				}
			}
			list.Add(new MachineManifest(text, profile, manifestMatch, readOnlyDictionary, new ReadOnlyDictionary<string, ChannelBinding>(dictionary)));
		}
		return list;
	}

	private static ManifestMatch ReadManifestMatch(JsonElement item, string id)
	{
		if (!item.TryGetProperty("match", out var value))
		{
			throw new ModelRegistryDataException("machineManifests '" + id + "' must declare match");
		}
		if (value.ValueKind != JsonValueKind.Object)
		{
			throw new ModelRegistryDataException("machineManifests '" + id + "'.match must be an object");
		}
		ManifestMatch manifestMatch = new ManifestMatch(ReadOptionalStringArray(value, "platformCodes", id), ReadOptionalStringArray(value, "boardPrefixes", id), ReadOptionalStringArray(value, "modelKeywords", id));
		if (manifestMatch.IsEmpty)
		{
			throw new ModelRegistryDataException("machineManifests '" + id + "'.match is empty");
		}
		return manifestMatch;
	}

	private static IReadOnlyList<string> ReadOptionalStringArray(JsonElement parent, string name, string id)
	{
		if (!parent.TryGetProperty(name, out var value))
		{
			return null;
		}
		if (value.ValueKind != JsonValueKind.Array)
		{
			throw new ModelRegistryDataException("machineManifests '" + id + "'.match." + name + " must be an array");
		}
		List<string> list = new List<string>();
		foreach (JsonElement item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
			{
				throw new ModelRegistryDataException("machineManifests '" + id + "'.match." + name + " must contain non-empty strings");
			}
			list.Add(item.GetString());
		}
		return list;
	}

	/// <summary>按声明顺序匹配机型；命中返回该条声明（含合并后的通道），未命中返回 null（= 走各功能自己的实时探测）。</summary>
	public MachineManifest MatchManifest(string board, string model, string platformCode, out string reason)
	{
		reason = null;
		foreach (MachineManifest manifest in Manifests)
		{
			reason = manifest.Match.Hit(board, model, platformCode);
			if (reason != null)
			{
				return manifest;
			}
		}
		return null;
	}

	public MachineManifest MatchManifest(string board, string model, string platformCode)
	{
		return MatchManifest(board, model, platformCode, out string _);
	}

	/// <summary>
	/// 取本机该用哪份通道定义。
	/// <para>顺序：先排除宝龙达白名单与非同方产品线（星耀 14/15、无界 LNL）→ 按声明顺序找专门声明
	/// → 同方家族且确实开放本地 EC 才回落到 <c>tongfang.ec.core</c> 通用默认 → 否则 null。</para>
	/// </summary>
	public MachineManifest? MatchOrDefault(ManifestQuery query, out string reason)
	{
		if (query.BaolongdaWhitelisted)
		{
			reason = "baolongda-whitelist";
			return null;
		}
		if (query.NonTongfangFamily)
		{
			reason = "non-tongfang-family";
			return null;
		}
		MachineManifest machineManifest = MatchManifest(query.Board, query.Model, query.PlatformCode, out reason);
		if (machineManifest != null)
		{
			return machineManifest;
		}
		if (!query.TongfangFamily)
		{
			reason = "not-tongfang";
			return null;
		}
		if (!query.LocalEcAvailable)
		{
			reason = "local-ec-unavailable";
			return null;
		}
		if (GenericManifest == null)
		{
			reason = "no-generic-profile";
			return null;
		}
		reason = "tongfang-generic";
		return GenericManifest;
	}

	/// <summary>声明表解析结果的可读摘要（写日志/诊断用）。</summary>
	public string DescribeManifests()
	{
		List<string> list = new List<string>();
		foreach (MachineManifest manifest in Manifests)
		{
			list.Add(manifest.ToString());
		}
		return "profiles=" + ChannelProfiles.Count + " manifests=" + Manifests.Count + (list.Count == 0 ? "" : " | " + string.Join(" ; ", list));
	}

	private static IReadOnlyList<int> ReadDgpuGenerations(JsonElement root)
	{
		return ReadIntArray(root, "dgpuGenerations");
	}

	private static IReadOnlyList<int> ReadIntArray(JsonElement root, string name)
	{
		JsonElement jsonElement = Require(root, name, JsonValueKind.Array);
		List<int> list = new List<int>();
		foreach (JsonElement item in jsonElement.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var value))
			{
				throw new ModelRegistryDataException(name + " must contain only integers");
			}
			list.Add(value);
		}
		if (!list.OrderBy((int result) => result).SequenceEqual(ExpectedDgpuGenerations))
		{
			throw new ModelRegistryDataException($"{name} must be the axis-2 domain {{{string.Join(",", ExpectedDgpuGenerations)}}}, got {{{string.Join(",", list)}}}");
		}
		return list;
	}

	private static List<string> ReadStringArray(JsonElement parent, string name, string context)
	{
		JsonElement jsonElement = Require(parent, name, JsonValueKind.Array);
		List<string> list = new List<string>();
		foreach (JsonElement item in jsonElement.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String)
			{
				throw new ModelRegistryDataException(context + "." + name + " must contain only strings");
			}
			string text = item.GetString();
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new ModelRegistryDataException(context + "." + name + " contains an empty entry");
			}
			list.Add(text);
		}
		return list;
	}

	private static int RequireInt(JsonElement parent, string name)
	{
		if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var value2))
		{
			throw new ModelRegistryDataException("'" + name + "' is missing or not an integer");
		}
		return value2;
	}

	private static JsonElement Require(JsonElement parent, string name, JsonValueKind kind)
	{
		if (!parent.TryGetProperty(name, out var value))
		{
			throw new ModelRegistryDataException("'" + name + "' is missing");
		}
		if (value.ValueKind != kind)
		{
			throw new ModelRegistryDataException($"'{name}' must be a {kind}, got {value.ValueKind}");
		}
		return value;
	}
}
