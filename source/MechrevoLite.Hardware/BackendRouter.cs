using System;
using System.Collections.Generic;
using System.Text;

namespace MechrevoLite.Hardware;

/// <summary>
/// 路由结果：命中的规则序号/说明 + 选中的后端 + 当时的机型事实。
/// <para>每次路由都会落一行可解释日志：<c>[强化机型分流] backend=… | machine=… | rule=#n … | decl=…</c></para>
/// </summary>
internal sealed record BackendRouteDecision(int RuleIndex, string Rule, MechrevoBackendKind Backend, MachineIdentity Identity, bool DeclarationPassed);

/// <summary>
/// 后端能力声明（第 3 步）：每个后端声明「需要哪些事实 + 支持哪些机型类别」。
/// <para>路由统一判断：选中的后端若声明不支持当前机型，则落回兜底规则并记日志（不静默换后端）。</para>
/// </summary>
internal sealed record BackendDeclaration(
	MechrevoBackendKind Kind,
	string DisplayName,
	string[] RequiresFacts,
	Func<MachineIdentity, bool> Supports,
	string Note);

/// <summary>
/// 第二层「路由」：有序声明式规则表，首条命中即选后端；未命中落到兜底规则。
/// <para>规则只读 <see cref="MachineIdentity" />，不做后端探测、不“试一个失败再换一个”。</para>
/// <para>新增机型/后端 = 加一条规则、一个事实或一个能力声明，不改控制流。</para>
/// <para>⭐ 2026-10-27（task-41 机型分流审计）：按设计文档
/// <c>docs/notes/后端匹配-声明式架构-设计-20261020.md</c> 补齐
/// <b>LegacyMifs 规则</b>（原表只有 Baolongda / Gcu，无 GCU 的老接口机型会被声明成 GCU），
/// 并把 <c>gen</c> 规则收紧为「代际 + 已装 GCU」（代际本身不能证明 GCU 通路存在）。</para>
/// </summary>
internal static class BackendRouter
{
	/// <summary>规则只读事实项；条件表由数据组成，不写 lambda。</summary>
	internal enum RouteFact
	{
		BaolongdaWhitelisted,
		BaolongdaEnabled,
		GcuServiceInstalled,
        GcuPayloadPresent,
		GcuGenerationSupported,
		MifsLegacyCompatible,
		TongfangFamily,
		// ── 运行期可用性事实：同一张表在"能连上"的情况下才选得到对应后端 ──
		GcuReachable,
		NucLocalReady,
		XingyaoLocalReady,
		Xingyao14Family,
		EcLocalAvailable
	}

	/// <summary>
	/// 运行期可用性事实（连接证据）。声明层只有机型事实，实际层把这两类事实一起喂给同一张规则表，
	/// 由规则自己去匹配后端 —— 不再另起一条优先级链。
	/// </summary>
	internal sealed record RouteAvailability(
		bool GcuReachable,
		bool NucLocalReady,
		bool XingyaoLocalReady,
		bool EcLocalAvailable)
	{
		internal static readonly RouteAvailability None = new RouteAvailability(false, false, false, false);

		/// <summary>从当前硬件状态取运行期可用性；不触发机型采集。</summary>
		internal static RouteAvailability From(MechrevoHw? hw)
		{
			if (hw == null)
			{
				return None;
			}
			return new RouteAvailability(
				hw.IsGcuConnected,
				hw.NucLocalReady,
				hw.XingyaoLocalReady,
				GcuEcMode.IsEcMode);
		}
	}

	internal sealed record RuleCondition(RouteFact Fact, bool Expected = true);

	internal sealed record RuleSpec(int Index, string Description, MechrevoBackendKind Backend, RuleCondition[] Conditions);

	/// <summary>内置规则表（顺序 = 优先级；首条命中即选中）。</summary>
	private static readonly RuleSpec[] BuiltInRules =
	{
        // ① 板号白名单是硬机型证据（宝龙达不是同方系，GCU 装了也不参与判定）。
        new RuleSpec(1, "board in Baolongda whitelist -> Baolongda", MechrevoBackendKind.Baolongda,
            new[] { new RuleCondition(RouteFact.BaolongdaWhitelisted), new RuleCondition(RouteFact.BaolongdaEnabled) }),
        // ② 星耀家族：不是同方模具（板号 P916F），控制走本地 WMAA，GCU 不参与判定。
        new RuleSpec(2, "Xingyao family -> XingyaoPmc", MechrevoBackendKind.XingyaoPmc,
            new[] { new RuleCondition(RouteFact.Xingyao14Family) }),
        // ③ 同方系：代际佐证 + 服务已装 + 载荷真的在 + GCU 确实连得上，才走 GCU 控制模式。
        new RuleSpec(3, "gpu gen 30/40/50 && GCU service installed && payload present && GCU reachable -> Gcu", MechrevoBackendKind.Gcu,
            new[] { new RuleCondition(RouteFact.GcuServiceInstalled), new RuleCondition(RouteFact.GcuPayloadPresent), new RuleCondition(RouteFact.GcuGenerationSupported), new RuleCondition(RouteFact.GcuReachable) }),
        // ④ 同方系：服务 + 载荷 + 连得上（代际未知也走这一条）。
        new RuleSpec(4, "GCU service installed && payload present && GCU reachable -> Gcu", MechrevoBackendKind.Gcu,
            new[] { new RuleCondition(RouteFact.GcuServiceInstalled), new RuleCondition(RouteFact.GcuPayloadPresent), new RuleCondition(RouteFact.GcuReachable) }),
        // ⑤ NUC / UMI 本地就绪 ⇒ 走本地 ACPI/EC + HID（同方系内 GCU 不可用时的本地通道）。
        new RuleSpec(5, "NUC/UMI local ready -> NucUmiLocal", MechrevoBackendKind.NucUmiLocal,
            new[] { new RuleCondition(RouteFact.NucLocalReady) }),
        // ⑥ 同方系：GCU 不可达 + 已选 EC 直连 ⇒ 本地 ACPI/EC 接管（EC-only）。
        new RuleSpec(6, "Tongfang family && GCU unreachable && EC mode selected -> TongfangEc", MechrevoBackendKind.TongfangEc,
            new[] { new RuleCondition(RouteFact.TongfangFamily), new RuleCondition(RouteFact.GcuReachable, false), new RuleCondition(RouteFact.EcLocalAvailable) }),
        // ⑦ 老同方接口：MICommonInterface(legacy GUID) 且未装 GCU。
        new RuleSpec(7, "MICommonInterface(legacy GUID) && no GCU service -> LegacyMifs", MechrevoBackendKind.LegacyMifs,
            new[] { new RuleCondition(RouteFact.MifsLegacyCompatible), new RuleCondition(RouteFact.GcuServiceInstalled, false) }),
        // ⑧ 同方系（声明口径）：装没装 GCU 都归同方通道。
        new RuleSpec(8, "Tongfang family -> Gcu（无 GCU 时由本地 EC 接管）", MechrevoBackendKind.Gcu,
            new[] { new RuleCondition(RouteFact.TongfangFamily) })
	};
	/// <summary>当前生效规则表（可被 <see cref="ReloadRulesIfChanged" /> 原子替换）。</summary>
	private static BackendRuleStore.RuleTable _table = BackendRuleStore.Load(BuiltInRules, MechrevoBackendKind.Gcu);

	private static readonly object _reloadGate = new object();

	private static BackendRuleStore.RuleTable Table => System.Threading.Volatile.Read(ref _table);

	/// <summary>当前生效规则（内置或配置文件）。</summary>
	private static RuleSpec[] Rules => Table.Rules;

	/// <summary>规则来源：built-in / file。</summary>
	internal static string RuleSource => Table.Source;

	internal static string RulePath => Table.Path;

	internal static string RuleLoadError => Table.Error;

	internal static int RuleCount => Table.Rules.Length;

	/// <summary>规则文件是否已变化（新增/删除/改写）；变了才值得重载。</summary>
	internal static bool RulesFileChanged => BackendRuleStore.HasChanged(Table);

	/// <summary>
	/// 热重载：规则文件变化时重新读取并原子替换当前表；失败保留旧表（只记一次日志）。
	/// <para>调用点：路由入口（<see cref="DecideAndLog" />）、诊断采集。</para>
	/// </summary>
	/// <returns>本次是否真的重载了。</returns>
	internal static bool ReloadRulesIfChanged()
	{
		if (!RulesFileChanged)
		{
			return false;
		}
		lock (_reloadGate)
		{
			BackendRuleStore.RuleTable before = Table;
			if (!BackendRuleStore.HasChanged(before))
			{
				return false;
			}
			BackendRuleStore.RuleTable after = BackendRuleStore.Load(BuiltInRules, MechrevoBackendKind.Gcu);
			System.Threading.Volatile.Write(ref _table, after);
			Logger.WriteLineIfChanged("route-rules-reload",
				"[route] 规则已重载：来源=" + after.Source + " · 条数=" + after.Rules.Length
				+ (string.IsNullOrEmpty(after.Error) ? "" : " · 错误=" + after.Error)
				+ " · 文件=" + (string.IsNullOrEmpty(after.Path) ? "(无)" : after.Path));
			return true;
		}
	}

	// 后端能力声明（第 3 步）：事实需求 + 支持的机型类别
	private static readonly BackendDeclaration[] Declarations =
	{
		new BackendDeclaration(MechrevoBackendKind.Baolongda, "BaolongDa",
			new[] { "board(product)", "boardWhitelist" },
			identity => identity.BaolongdaWhitelisted,
			"仅限板号白名单机型（MRID6 / MRID6-*）；无 GCU，走宝龙达 WMI"),
		new BackendDeclaration(MechrevoBackendKind.Gcu, "GCU",
			new[] { "gcuServiceInstalled", "gpuGeneration" },
			identity => !identity.BaolongdaWhitelisted,
			"同方机型；宝龙达白名单机型不走 GCU。未识别任何机型时也落到这里（未连接则如实提示）"),
		new BackendDeclaration(MechrevoBackendKind.LegacyMifs, "LegacyMifs",
			new[] { "mifsClass" },
			identity => !identity.BaolongdaWhitelisted && identity.MifsLegacyCompatible && !identity.GcuServiceInstalled,
			"老同方 MICommonInterface（GUID 与 LegacyMifsWmi 一致）且未装 GCU 服务；内置性能档、不走 MQTT"),
		new BackendDeclaration(MechrevoBackendKind.NucUmiLocal, "NucUmiLocal",
			new[] { "nucLocalReady" },
			identity => true,
			"NUC / UMI（Intel 30 系）：本地 ACPI/EC + HID；需要运行期探测就绪才走这条"),
		new BackendDeclaration(MechrevoBackendKind.XingyaoPmc, "XingyaoPmc",
			new[] { "xingyaoLocalReady" },
			identity => true,
			"星耀：本地 WMAA（ACPI WMI）；不是同方模具，不走同方 EC 地址"),
		new BackendDeclaration(MechrevoBackendKind.TongfangEc, "TongfangEc",
			new[] { "tongfangFamily", "ecMode" },
			identity => identity.TongfangFamily && !identity.BaolongdaWhitelisted,
			"同方家族 + GCU 不可达 + 已选 EC 直连：由本地 ACPI/EC 接管")
	};

	/// <summary>兜底后端（默认 GCU；配置文件只允许与内置一致）。</summary>
	private static MechrevoBackendKind Fallback => Table.Fallback;

	// ⭐ 运行期最近一次路由结果（供 [gate] 机型分类日志 / 诊断包 / ActiveRoute 对比复用同一份事实）。
	private static BackendRouteDecision? _lastDecision;

	/// <summary>
	/// ⭐ 本进程最近一次路由结果（未路由过 = null）。
	/// <para>日志、诊断包、<see cref="MechrevoHw.RouteDeclarationMatchesActual" /> 都读它，
	/// 保证「运行判定」与「诊断重算」用的是同一份 <see cref="MachineIdentity" /> 实例（设计文档 §3.4）。</para>
	/// </summary>
	internal static BackendRouteDecision? LastDecision => System.Threading.Volatile.Read(ref _lastDecision);

	internal static BackendDeclaration DeclarationFor(MechrevoBackendKind kind)
	{
		for (int i = 0; i < Declarations.Length; i++)
		{
			if (Declarations[i].Kind == kind)
			{
				return Declarations[i];
			}
		}
		return null;
	}

	internal static string DescribeDeclarations()
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < Declarations.Length; i++)
		{
			BackendDeclaration declaration = Declarations[i];
			sb.AppendLine("  - " + declaration.DisplayName + "：需要[" + string.Join(", ", declaration.RequiresFacts) + "] · " + declaration.Note);
		}
		return sb.ToString().TrimEnd();
	}

	/// <summary>路由结果的一行可解释描述（日志 / 诊断共用，避免两处格式漂移）。</summary>
	internal static string DescribeDecision(BackendRouteDecision decision)
	{
		if (decision == null)
		{
			return "(未运行路由)";
		}
		BackendDeclaration declaration = DeclarationFor(decision.Backend);
		return "backend=" + decision.Backend
			+ " | machine=" + decision.Identity.Summary
			+ " | rule=#" + decision.RuleIndex + " " + decision.Rule
			+ " | decl=" + (declaration == null ? "(无)" : declaration.DisplayName + (decision.DeclarationPassed ? "=pass" : "=fail"));
	}

	/// <summary>当前生效规则的逐条描述（诊断用）。</summary>
	internal static string DescribeRules()
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < Rules.Length; i++)
		{
			RuleSpec rule = Rules[i];
			string conditions = string.Join(" && ", Array.ConvertAll(rule.Conditions,
				c => (c.Expected ? "" : "!") + c.Fact));
			sb.AppendLine("  #" + rule.Index + " " + rule.Backend + " ← " + conditions
				+ " · " + rule.Description);
		}
		return sb.ToString().TrimEnd();
	}

	/// <summary>声明层判定：只用机型事实（不看连接证据）。</summary>
	internal static BackendRouteDecision Decide(MachineIdentity identity)
	{
		BackendRuleStore.RuleTable table = Table;
		return Decide(identity, table.Rules, table.Fallback, RouteAvailability.None);
	}

	/// <summary>
	/// 实际层判定：同一张规则表，额外把"运行期可用性事实"喂进去，由规则自己匹配后端。
	/// <para>不是"声明一个后端、不行再排队退回下一个" —— 不可用的后端在条件上就不成立。</para>
	/// </summary>
	internal static BackendRouteDecision Decide(MachineIdentity identity, RouteAvailability availability)
	{
		BackendRuleStore.RuleTable table = Table;
		return Decide(identity, table.Rules, table.Fallback, availability ?? RouteAvailability.None);
	}

	private static BackendRouteDecision Decide(MachineIdentity identity, RuleSpec[] rules, MechrevoBackendKind fallback,
		RouteAvailability availability)
	{
		if (identity == null)
		{
			throw new ArgumentNullException("identity");
		}
		for (int i = 0; i < rules.Length; i++)
		{
			RuleSpec rule = rules[i];
			if (!Matches(identity, rule, availability))
			{
				continue;
			}
			bool declarationPassed = DeclarationFor(rule.Backend)?.Supports(identity) ?? true;
			if (declarationPassed)
			{
				return new BackendRouteDecision(rule.Index, rule.Description, rule.Backend, identity, DeclarationPassed: true);
			}
			// 声明不支持 ⇒ 不静默换后端，记下原因并落到兜底规则（不是“探测失败重试”）。
			Logger.WriteLine("[强化机型分流] 规则 #" + rule.Index + " 命中 " + rule.Backend + "，但该后端声明不支持当前机型 ⇒ 落兜底规则");
			break;
		}
		return new BackendRouteDecision(rules.Length + 1, "fallback -> GCU（未识别机型；未连接则如实提示）", fallback, identity,
			DeclarationPassed: DeclarationFor(fallback)?.Supports(identity) ?? true);
	}

	/// <summary>单条事实在当前机型下的取值（路由判定与诊断解释共用同一实现，避免两套判据）。</summary>
	internal static bool Evaluate(MachineIdentity identity, RouteFact fact)
	{
		return Evaluate(identity, fact, RouteAvailability.None);
	}

	/// <summary>单条事实取值：机型事实 + 运行期可用性事实（同一实现，判定与解释共用）。</summary>
	internal static bool Evaluate(MachineIdentity identity, RouteFact fact, RouteAvailability availability)
	{
		RouteAvailability av = availability ?? RouteAvailability.None;
		return fact switch
		{
			RouteFact.BaolongdaWhitelisted => identity.BaolongdaWhitelisted,
			RouteFact.BaolongdaEnabled => !BaolongdaBackend.IsDisabledByEnv,
			RouteFact.GcuServiceInstalled => identity.GcuServiceInstalled,
            RouteFact.GcuPayloadPresent => identity.GcuPayloadPresent,
			RouteFact.GcuGenerationSupported => IsGcuGeneration(identity.GpuGeneration),
			RouteFact.MifsLegacyCompatible => identity.MifsLegacyCompatible,
			RouteFact.TongfangFamily => identity.TongfangFamily,
			// 声明层（未提供可用性）按"会连上"处理，运行层按真实连接证据；NUC/星耀/EC 只在运行层成立。
			RouteFact.GcuReachable => availability == null || av.GcuReachable,
			RouteFact.NucLocalReady => av.NucLocalReady,
			RouteFact.XingyaoLocalReady => av.XingyaoLocalReady,
			RouteFact.Xingyao14Family => identity.Xingyao14Family,
			RouteFact.EcLocalAvailable => av.EcLocalAvailable,
			_ => false
		};
	}

	private static bool Matches(MachineIdentity identity, RuleSpec rule, RouteAvailability availability)
	{
		for (int i = 0; i < rule.Conditions.Length; i++)
		{
			RuleCondition condition = rule.Conditions[i];
			if (Evaluate(identity, condition.Fact, availability) != condition.Expected)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// 只读可视化：逐条规则列出**每个条件的实际取值**、是否命中（★ = 最终采用）、以及兜底后端。
	/// <para>⛔ 只读：不路由、不探测、不写任何东西；诊断包与 UI 只读页共用这一份文本。</para>
	/// </summary>
	internal static string Explain(MachineIdentity identity, RouteAvailability availability = null)
	{
		if (identity == null)
		{
			return "  (无机型事实，无法解释规则命中)";
		}
		BackendRuleStore.RuleTable table = Table;
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("  规则表：来源=" + table.Source + " · 条数=" + table.Rules.Length
			+ " · 文件=" + (string.IsNullOrEmpty(table.Path) ? "(无)" : table.Path)
			+ (string.IsNullOrEmpty(table.Error) ? "" : " · 错误=" + table.Error));
		bool matched = false;
		for (int i = 0; i < table.Rules.Length; i++)
		{
			RuleSpec rule = table.Rules[i];
			bool hit = Matches(identity, rule, availability);
			bool declarationPassed = DeclarationFor(rule.Backend)?.Supports(identity) ?? true;
			string conditions = string.Join(" && ", Array.ConvertAll(rule.Conditions,
				c => (c.Expected ? "" : "!") + c.Fact + "=" + (Evaluate(identity, c.Fact, availability) ? "真" : "假")));
			string verdict = !hit
				? "未命中"
				: (!declarationPassed ? "命中但声明不支持 ⇒ 落兜底" : (matched ? "命中（已被更前面的规则抢先）" : "★ 采用"));
			sb.AppendLine("    #" + rule.Index + " " + rule.Backend + " ← " + conditions + " · " + verdict);
			if (hit && declarationPassed && !matched)
			{
				matched = true;
			}
		}
		sb.AppendLine("    兜底 → " + table.Fallback + (matched ? "" : "（★ 采用）"));
		return sb.ToString().TrimEnd();
	}

	/// <summary>仅 UI 探针/离线预览用：把合成机型事实落到 LastDecision，让 UI 按 NUC 渲染；不触碰真实后端。</summary>
	internal static void SetPreviewDecision(MachineIdentity identity)
	{
		System.Threading.Volatile.Write(ref _lastDecision,
			new BackendRouteDecision(0, "ui-probe -> Gcu", MechrevoBackendKind.Gcu, identity, DeclarationPassed: true));
	}

	internal static BackendRouteDecision DecideAndLog()
	{
		ReloadRulesIfChanged();
		BackendRouteDecision decision = Decide(MachineIdentity.Collect());
		System.Threading.Volatile.Write(ref _lastDecision, decision);
		Logger.WriteLine("[强化机型分流] 规则来源=" + RuleSource + " · 条数=" + RuleCount
			+ (string.IsNullOrEmpty(RuleLoadError) ? "" : " · 错误=" + RuleLoadError));
		Logger.WriteLine("[强化机型分流] " + DescribeDecision(decision));
		// 同方机没装 GCU：只记日志，不在这里静默切 EC ——
		// 改由界面弹一次「导入 GCU / 改用 EC」让用户选，选完才落模式（见 GcuStartupChoice）。
		if (decision.Backend == MechrevoBackendKind.Gcu
			&& decision.Identity.TongfangFamily
			&& !decision.Identity.GcuServiceInstalled
			&& !GcuWarmup.IsActive)
		{
			Logger.WriteLine("[gcu-svc] GCU 后端机型但本机没装 GCU 服务 ⇒ 等用户在界面上选择导入或改用 EC");
		}
		return decision;
	}

	/// <summary>后端枚举 → 连接侧路由枚举（声明/实际的对比用；不改变 <see cref="MechrevoHw.ActiveRoute" /> 的语义）。</summary>
	internal static BackendRoute ToRoute(MechrevoBackendKind kind)
	{
		return kind switch
		{
			MechrevoBackendKind.Gcu => BackendRoute.Gcu,
			MechrevoBackendKind.Baolongda => BackendRoute.Baolongda,
			MechrevoBackendKind.LegacyMifs => BackendRoute.LegacyMifs,
			MechrevoBackendKind.TongfangEc => BackendRoute.AcpiEc,
			_ => BackendRoute.None
		};
	}

	private static bool IsGcuGeneration(string generation)
	{
		return string.Equals(generation, "Gen30", StringComparison.Ordinal)
			|| string.Equals(generation, "Gen40", StringComparison.Ordinal)
			|| string.Equals(generation, "Gen50", StringComparison.Ordinal);
	}

	/// <summary>
	/// ⭐ 离线自测（只读、零硬件）：用合成 <see cref="MachineIdentity" /> 跑规则矩阵，断言
	/// 每条设计规则（含新增 LegacyMifs / 代际收紧 / 兜底）都命中预期后端。
	/// <para>输出格式：首行 <c>[机型分流自测] PASS=n FAIL=m</c>，逐行 ✓/✗。诊断包与探针都会调用。</para>
	/// <para>⛔ 不读真实硬件、不写任何东西；纯函数。</para>
	/// </summary>
	internal static string RunSelfTest()
	{
		List<string> pass = new List<string>();
		List<string> fail = new List<string>();

		// ① 宝龙达白名单：GCU 服务同时存在也必须优先宝龙达（设计文档 §2 蛟龙事故）
		Check("白名单机型（同时装了 GCU）⇒ Baolongda", MechrevoBackendKind.Baolongda, 1,
			MachineIdentity.ForTest(board: "MRID6-23", whitelisted: true, generation: "Gen40", gcuInstalled: true, mifsClass: true), pass, fail);
		// ② 星耀家族（板号 P916F）⇒ 星耀后端，装了 GCU 也不抢
		Check("星耀家族 ⇒ XingyaoPmc（GCU 不参与）", MechrevoBackendKind.XingyaoPmc, 2,
			MachineIdentity.ForTest(board: "P916F", generation: "Gen50", gcuInstalled: true) with { Xingyao14Family = true }, pass, fail);
		// ② 星耀15：身份单独识别，但暂时挂靠 GCU 控制（有机主未反馈、带 GCU 载荷）
		Check("星耀15（暂挂 GCU）⇒ Gcu", MechrevoBackendKind.Gcu, 3,
			MachineIdentity.ForTest(board: "GM6XY15", generation: "Gen50", gcuInstalled: true) with { Model = "星耀15 Pro Series GM6XY15", Xingyao15Family = true }, pass, fail);
		// ② LNL 无界：身份单独识别，暂时挂靠 GCU 控制（与无界 APU 分开）
		Check("无界 LNL（暂挂 GCU）⇒ Gcu", MechrevoBackendKind.Gcu, 3,
			MachineIdentity.ForTest(board: "GM6LNL", generation: "Gen50", gcuInstalled: true) with { WujieLnlFamily = true }, pass, fail);		// ② 40 系 + 已装 GCU ⇒ Gcu（代际佐证）
		Check("Gen40 + 已装 GCU ⇒ Gcu", MechrevoBackendKind.Gcu, 3,
			MachineIdentity.ForTest(board: "GM5HG0A", generation: "Gen40", gcuInstalled: true), pass, fail);
		// ③ 代际未知但已装 GCU ⇒ Gcu（规则 3）
		Check("代际未知 + 已装 GCU ⇒ Gcu", MechrevoBackendKind.Gcu, 4,
			MachineIdentity.ForTest(board: "GM5HG0A", generation: "Unknown", gcuInstalled: true), pass, fail);
		// ④ 老接口：MICommonInterface(legacy GUID) + 未装 GCU ⇒ LegacyMifs（本轮新增规则）
		Check("MICommonInterface + 未装 GCU ⇒ LegacyMifs", MechrevoBackendKind.LegacyMifs, 7,
			MachineIdentity.ForTest(board: "GM6IX8B", generation: "Gen30", gcuInstalled: false, mifsClass: true), pass, fail);
		// ⑤ 装了 GCU 时 MICommonInterface 在也不能抢老接口（设计文档 §2：同方通用类新机型也可能带）
		Check("MICommonInterface + 已装 GCU ⇒ Gcu（老接口不得抢）", MechrevoBackendKind.Gcu, 4,
			MachineIdentity.ForTest(board: "GM5HG0A", generation: "Unknown", gcuInstalled: true, mifsClass: true), pass, fail);
		// ⑥ 同方板号、没有 GCU、没有 MIFS ⇒ 同方族规则（本地 EC 回退）
        // ⑥ 服务键在、但载荷 exe 缺失（登记残留）⇒ 不得声明 GCU，交给同方/回落通道
        Check("已装 GCU 但载荷缺失 ⇒ 不选 GCU 规则", MechrevoBackendKind.Gcu, 8,
            MachineIdentity.ForTest(board: "GM7ZG8S", generation: "Gen30", gcuInstalled: true, gcuPayload: false), pass, fail);
        Check("已装 GCU 且载荷在 ⇒ 仍选规则 3（不误降级）", MechrevoBackendKind.Gcu, 3,
            MachineIdentity.ForTest(board: "GM7ZG8S", generation: "Gen30", gcuInstalled: true, gcuPayload: true), pass, fail);
		Check("GM 板号 + 无 GCU/MIFS ⇒ 同方族 Gcu", MechrevoBackendKind.Gcu, 8,
			MachineIdentity.ForTest(board: "GM5HG0A", generation: "Gen40", gcuInstalled: false), pass, fail);
		// ⑦ 什么都没识别到 ⇒ 兜底 Gcu（不误判成宝龙达/老接口）
		Check("无任何事实 ⇒ 兜底 Gcu", MechrevoBackendKind.Gcu, 9,
			MachineIdentity.ForTest(), pass, fail);
		// ⑧ 白名单但 MICommonInterface 缺失：路由仍按机型证据声明 Baolongda（探测失败由候选循环兜底并记日志）
		Check("白名单 + MIFS 缺失 ⇒ 仍声明 Baolongda", MechrevoBackendKind.Baolongda, 1,
			MachineIdentity.ForTest(board: "MRID6", whitelisted: true, generation: "Gen30", gcuInstalled: false, mifsClass: false), pass, fail);
		// ⑨ 声明一致性：所有命中规则的声明都应为 pass（能力声明是第二道保险）
		CheckDeclaration(MachineIdentity.ForTest(board: "MRID6-23", whitelisted: true, mifsClass: true), pass, fail);
		CheckDeclaration(MachineIdentity.ForTest(board: "GM5HG0A", generation: "Gen40", gcuInstalled: true), pass, fail);
		CheckDeclaration(MachineIdentity.ForTest(board: "GM6IX8B", generation: "Gen30", mifsClass: true), pass, fail);
		// ⑩ 规则文件解析（热重载的解析层）：合法 ⇒ file；version 非 1 / fact 非法 / backend 非法 ⇒ 整份回退内置
		CheckRuleParse("规则文件：单条合法 ⇒ file", ValidRuleJson, expectFile: true, null, 1, pass, fail);
		CheckRuleParse("规则文件：condition expected=false 可解析", NegatedRuleJson, expectFile: true, null, 1, pass, fail);
		CheckRuleParse("规则文件：version=2 ⇒ 回退内置", "{\"version\":2,\"rules\":[]}", expectFile: false, "version=", 8, pass, fail);
		CheckRuleParse("规则文件：fact 非法 ⇒ 回退内置", "{\"version\":1,\"rules\":[{\"index\":1,\"backend\":\"Gcu\",\"conditions\":[{\"fact\":\"NoSuchFact\"}]}]}",
			expectFile: false, "fact 非法", 8, pass, fail);
		CheckRuleParse("规则文件：backend 非法 ⇒ 回退内置", "{\"version\":1,\"rules\":[{\"index\":1,\"backend\":\"Nope\",\"conditions\":[{\"fact\":\"TongfangFamily\"}]}]}",
			expectFile: false, "backend 非法", 8, pass, fail);
		CheckRuleParse("规则文件：空 rules ⇒ 回退内置", "{\"version\":1,\"rules\":[]}", expectFile: false, "rules 为空", 8, pass, fail);
		// ⑩a 无界办公本板号判据（X 系 / IP3 / BLD；蛟龙 MRID6 不得算无界）
		{
			string wujieLines = MechrevoLite.Hardware.WujieBoardProbe.RunSelfTest();
			if (wujieLines.Contains("FAIL=0")) { pass.Add(wujieLines); } else { fail.Add(wujieLines); }
		}
		// ⑪ 只读可视化：当前事实下恰好一条「★ 采用」，并列出条件实际取值
		CheckExplain("规则解释：白名单机型", MachineIdentity.ForTest(board: "MRID6-23", whitelisted: true, gcuInstalled: true), pass, fail);
		CheckExplain("规则解释：同方无 GCU", MachineIdentity.ForTest(board: "GM5HG0A", generation: "Gen40", gcuInstalled: false), pass, fail);
		CheckExplain("规则解释：无任何事实", MachineIdentity.ForTest(), pass, fail);

		StringBuilder sb = new StringBuilder();
		sb.AppendLine("[机型分流自测] PASS=" + pass.Count + " FAIL=" + fail.Count);
		for (int i = 0; i < pass.Count; i++)
		{
			sb.AppendLine("  ✓ " + pass[i]);
		}
		for (int j = 0; j < fail.Count; j++)
		{
			sb.AppendLine("  ✗ " + fail[j]);
		}
		return sb.ToString().TrimEnd();
	}

	private static void Check(string scenario, MechrevoBackendKind expectedBackend, int expectedRule, MachineIdentity identity, List<string> pass, List<string> fail)
	{
		BackendRouteDecision decision = Decide(identity, BuiltInRules, MechrevoBackendKind.Gcu, null);
		bool ok = decision.Backend == expectedBackend && decision.RuleIndex == expectedRule && decision.DeclarationPassed;
		string line = scenario + " ⇒ 实际 " + decision.Backend + "(rule=#" + decision.RuleIndex + ", decl=" + (decision.DeclarationPassed ? "pass" : "fail") + ")";
		if (ok)
		{
			pass.Add(line + " · 期望 " + expectedBackend + "(rule=#" + expectedRule + ")");
		}
		else
		{
			fail.Add(line + " · 期望 " + expectedBackend + "(rule=#" + expectedRule + ")");
		}
	}

	private static void CheckDeclaration(MachineIdentity identity, List<string> pass, List<string> fail)
	{
		BackendRouteDecision decision = Decide(identity, BuiltInRules, MechrevoBackendKind.Gcu, null);
		if (decision.DeclarationPassed)
		{
			pass.Add("能力声明 pass：" + decision.Backend + " rule=#" + decision.RuleIndex);
		}
		else
		{
			fail.Add("能力声明 fail：" + decision.Backend + " rule=#" + decision.RuleIndex + " machine=" + identity.Summary);
		}
	}

	private const string ValidRuleJson =
		"{\"version\":1,\"rules\":[{\"index\":1,\"backend\":\"Baolongda\",\"description\":\"t\",\"conditions\":[{\"fact\":\"BaolongdaWhitelisted\"}]}]}";

	private const string NegatedRuleJson =
		"{\"version\":1,\"rules\":[{\"index\":1,\"backend\":\"Gcu\",\"conditions\":[{\"fact\":\"GcuServiceInstalled\",\"expected\":false}]}]}";

	private static void CheckRuleParse(string scenario, string json, bool expectFile, string expectErrorFragment,
		int expectRuleCount, List<string> pass, List<string> fail)
	{
		BackendRuleStore.RuleTable table = BackendRuleStore.Parse(json, BuiltInRules, MechrevoBackendKind.Gcu, "");
		string expectedSource = expectFile ? "file" : "built-in";
		bool ok = string.Equals(table.Source, expectedSource, StringComparison.Ordinal)
			&& table.Rules.Length == expectRuleCount
			&& (expectErrorFragment == null || (table.Error ?? "").Contains(expectErrorFragment, StringComparison.Ordinal));
		string line = scenario + " ⇒ 来源=" + table.Source + " 条数=" + table.Rules.Length
			+ (string.IsNullOrEmpty(table.Error) ? "" : " 错误=" + table.Error)
			+ " · 期望 来源=" + expectedSource + " 条数=" + expectRuleCount;
		if (ok)
		{
			pass.Add(line);
		}
		else
		{
			fail.Add(line);
		}
	}

	private static void CheckExplain(string scenario, MachineIdentity identity, List<string> pass, List<string> fail)
	{
		string text = Explain(identity);
		int stars = (text.Length - text.Replace("★ 采用", "").Length) / "★ 采用".Length;
		bool factValuesShown = text.Contains("BaolongdaWhitelisted=", StringComparison.Ordinal)
			&& text.Contains("GcuServiceInstalled=", StringComparison.Ordinal);
		string line = scenario + " ⇒ 采用标记=" + stars + " 次 · 条件取值=" + (factValuesShown ? "有" : "缺")
			+ " · 期望 采用标记=1 次 · 条件取值=有";
		if (stars == 1 && factValuesShown)
		{
			pass.Add(line);
		}
		else
		{
			fail.Add(line + " · 实际文本=" + text.Replace("\r", "").Replace("\n", " / "));
		}
	}
}
