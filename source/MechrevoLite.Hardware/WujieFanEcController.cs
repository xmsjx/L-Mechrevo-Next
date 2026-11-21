using System;
using System.Collections.Generic;
using System.Threading;
using MechrevoLite.Hardware.FanCurve;

namespace MechrevoLite.Hardware;

/// <summary>
/// 2026-10-22（无界风扇曲线）：无界（WUJIE14XA）EC 风扇曲线控制器。
///
/// <para>通道：ACPIDriver（\\.\ACPIDriver IOCTL）优先，没有驱动时退 I2EC（inpoutx64）。温度 0x043E，
/// 转速 0x0464/0x046C（大端）。</para>
///
/// <para>只走 **table 后端**：温度轴 0x0F00/0x0F10/0x0F30/0x0F40，占空比表 0x0F20（主扇 16 槽）/
/// 0x0F50（副扇 13 槽），加上 0x0F5D/0x0F5E/0x0F5F 与 0x07C6 写 0x04 锁存；
/// 目标不变时停写、EC 自己保持；旧的 25Hz 直写后端已删除。</para>
///
/// <para>安全：45℃ 以下不接管（EC 低温会停转）；副扇 5% 下限；≥85℃ 强制 100%；≥95℃ 停止交还 EC；
/// 温度采样连续失败、写表连续失败到上限时停止并交还 EC。⛔ 停止时不做渐进降速。</para>
/// </summary>
internal sealed class WujieFanEcController : IDisposable
{
	private const int WriteIntervalMs = 40;

	/// <summary>
	/// 控制后端 —— <see cref="Table" />（曲线表+锁存，唯一路径）。旧 25Hz 直写后端已删除。
	/// <para>⭐ table 优点：目标不变时稳态**零写入**（写表只在目标变化/10s 心跳漂移时），
	/// 撕裂写暴露窗口从 ~50 次/秒 降到“每次目标变化 21 次写”；缺点：受 EC 软着陆斜坡限制
	/// （目标变化时补写一次 0x1804/0x1809 跳坡）。</para>
	/// </summary>
	private enum ControlBackend
	{
		Table,
		/// <summary>写表不可用时的显式失败态：⛔ 不回退 direct 直写，曲线不下发，由 EC 按原生曲线跑。</summary>
		Unavailable
	}

	/// <summary>table 后端温度采样周期（ms）：25Hz 定时器里只每 500ms 读一次温度，减少端口读压力。</summary>
	private const int TableTempSampleMs = 500;

	/// <summary>table 后端温度连续读失败样本数（6 × 500ms ≈ 3s）⇒ 交还 EC。</summary>
	private const int TableTempFailSamples = 6;

	/// <summary>table 写表连续失败次数 ⇒ 记一条“连续失败”并放慢重试（⛔ 不回退 direct）。</summary>
	private const int TableWriteFailLimit = 3;

	/// <summary>写表失败后的首次重试间隔（ms）：不狂写，也不放弃。</summary>
	private const int TableWriteRetryMs = 5000;

	/// <summary>连续失败到上限后的重试间隔（ms）。</summary>
	private const int TableWriteRetrySlowMs = 30000;

	/// <summary>心跳连续偏离多少次判定“别的程序争用 EC”⇒ 放慢重锁间隔（不跟着加码狂写）。</summary>
	private const int TableContentionLimit = 5;

	/// <summary>判定争用后的心跳/重锁间隔（ms）。</summary>
	private const int TableContentionHeartbeatMs = 30000;


	private ControlBackend _backend = ControlBackend.Unavailable;

	// ---- table 后端状态（task-43）----
	private int _tableLatchBase;
	private int _tableIsolateBase;
	private bool _tableActive;
	private int _tableLastRaw1 = -1;

	private int _tableSignature;

	private long _lastZeroSlotTick;

	private int _zeroSlotRetries;

	private byte[] _tableCpuDuty = Array.Empty<byte>();

	private byte[] _tableGpuDuty = Array.Empty<byte>();
	private int _tableLastRaw2 = -1;
	private long _tableTempSampleTick;
	private long _tableLastWriteTick;
	private int _tableHeartbeatMs = WujieFanEc.TableHeartbeatMs;
	private int _tableContentionCount;
	private int _tableWriteFailCount;

	/// <summary>写表失败后的下一次重试时刻（TickCount64；0 = 立即可试）。</summary>
	private long _tableRetryNotBeforeTick;
	private bool _lowTempHandoff;

	/// <summary>低温交还后重新接管的温度（℃，= 45 + 3）✅</summary>
	internal const int HandoffResumeTemperatureC = WujieFanEc.HandoffResumeTemperatureC;

	/// <summary>2026-10-22（机主：45℃ 以下风扇会停转、写不进去）：低温区不写占空比，交还 EC 原生策略。</summary>
	/// <summary>表后端的最低工作温度。0 表示不设下限：低温段同样按曲线走，不再交还 EC。</summary>
	internal const int MinimumOperatingTemperatureC = 0;

	private readonly Timer _timer;

	private readonly object _gate = new object();

	private static WujieFanEcController _active;

	/// <summary>当前接管会话的参数指纹（用于判断"同参数重复启动"）。</summary>
	private static string? _activeSignature;

	private static string BuildSignature(byte[] cpuTemps, int[] cpuDuties, bool independent, byte[]? gpuTemps, int[]? gpuDuties)
	{
		return string.Join(",", cpuTemps) + "|" + string.Join(",", cpuDuties)
			+ "|" + (independent ? "1" : "0")
			+ "|" + (gpuTemps == null ? "" : string.Join(",", gpuTemps))
			+ "|" + (gpuDuties == null ? "" : string.Join(",", gpuDuties));
	}

	private byte[] _cpuTemps = Array.Empty<byte>();

	private int[] _cpuDuties = Array.Empty<int>();

	private int _validCpu;

	private byte[] _gpuTemps = Array.Empty<byte>();

	private int[] _gpuDuties = Array.Empty<int>();

	private int _validGpu;

	private bool _independent;

	private bool _running;


	private int _tempFailTicks;

	/// <summary>本次接管是否已置自定义模式标志。</summary>
	private bool _customModeSet;

	/// <summary>上次停转握手时间（限流）。</summary>
	private long _lastHandshakeTick;

	private int _lastTempC = -1;

	private long _lastSampleTick;

	private int _lastTargetPercent = -1;

	internal WujieFanEcController()
	{
		_timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
	}

	internal bool Running => _running;

	// EC 通道后装/驱动自愈完成是常态（驱动被卸载、控制台重装、开机时设备还没就绪）⇒ 记下这次下发意图，
	// 通道回来后自动重新接管，⛔ 不让用户自己去切一次档位才恢复。
	private sealed class PendingStart
	{
		internal byte[] CpuTemps = Array.Empty<byte>();
		internal int[] CpuDuties = Array.Empty<int>();
		internal bool Independent;
		internal byte[] GpuTemps = Array.Empty<byte>();
		internal int[] GpuDuties = Array.Empty<int>();
	}

	private static readonly object RecoveryGate = new();
	private static PendingStart? _pendingStart;
	private static Timer? _recoveryTimer;
	private static int _recoveryAttempts;
	private const int RecoveryIntervalMs = 60000;
	private const int RecoveryAttemptLimit = 30;

	internal string LastDetail { get; private set; } = "";

	/// <summary>启动曲线控制：CPU/GPU 两套温度与占空比数组（长度 16，0xFF 表示无效点）；
	/// independent=true 时主扇按 CPU 曲线、副扇按 GPU 曲线分别输出。</summary>
	/// <summary>无界默认温度轴（16 点，从 45℃ 起）。</summary>
	internal static byte[] DefaultTemperatures()
	{
		// 从 45℃ 起（控制器 45℃ 是硬门槛），与占空比同序对齐；返回副本，调用方改动不影响内置表。
		return (byte[])FanCurveDefaults.WujieCpuTemperatures.Clone();
	}

	/// <summary>无界 GPU 默认温度轴（同一套表，尾部 255 表示无效点）。</summary>
	internal static byte[] DefaultGpuTemperatures()
	{
		return (byte[])FanCurveDefaults.WujieGpuTemperatures.Clone();
	}

	internal bool Start(byte[] cpuTemps, int[] cpuDuties, bool independent, byte[] gpuTemps, int[] gpuDuties, out string detail)
	{
		if (!WujieFanEc.IsWujiePlatform())
		{
			detail = "不是无界机型";
			return false;
		}
		if (cpuTemps == null || cpuDuties == null || cpuTemps.Length == 0 || cpuDuties.Length == 0)
		{
			detail = "CPU 曲线数据为空";
			return false;
		}
		// 无界只有一路温度输入，独立输出已退役：始终按单路执行，GPU 曲线不再单独下发。
		independent = false;
		// 同一套参数重复启动时复用现有会话，避免"交还 EC / 再接管"来回抖动。
		string signature = BuildSignature(cpuTemps, cpuDuties, independent, gpuTemps, gpuDuties);
		if (_active != null && _activeSignature == signature)
		{
			detail = "已接管（复用现有会话）";
			return true;
		}
		// 签名等真正接管成功后再记：中途失败时旧会话可能还在跑，提前记会让下次同参数误判「已在复用」。
		// 通道：ACPIDriver 优先，inpout 兜底；两者都不在时才尝试装一次 inpout。
		if (!AcpiDriverEc.IsAvailable() && !I2ecInpOut.IsAvailable)
		{
			string installDetail = InpOutDriverInstaller.TryEnsureInstalled(out string installResult) ? "已安装" : installResult;
			Logger.WriteLine("[fan] 无界 EC 曲线：驱动自动安装结果=" + installDetail);
		}
		int temp = -1;
		if (!EcIo.TrySelect(out string transportDetail))
		{
			detail = "无可用 EC 通道（" + transportDetail + "）";
			return false;
		}
		if (!EcIo.TryReadByte(WujieFanEc.RegisterCpuTemp, out temp, out string tempDetail) || temp < 0 || temp > 120)
		{
			detail = "读取 CPU 温度失败（0x043E，" + EcIo.Detail + "）：" + tempDetail;
			return false;
		}
Logger.WriteLine("[fan] 无界 EC 通道：" + EcIo.Detail + " · 当前 " + temp + "℃");
		// 选择后端：只走 table（曲线表+锁存）。
		ControlBackend backend = ControlBackend.Unavailable;
		int latchBase = 0;
		int isolateBase = 0;
		string backendDetail = "";
		SelectBackend(out backend, out latchBase, out isolateBase, out backendDetail);
		if (backend == ControlBackend.Unavailable)
		{
			// 写表这条路走不通 ⇒ 如实报失败（机主要求：⛔ 不退回 direct 25Hz 直写，那是会被固件打乱的路子）。
			Logger.WriteErrorIfChanged("wujie-fan-unavailable", "[fan] 无界 EC 曲线：未启用（" + backendDetail + "）");
			ArmChannelRecovery(cpuTemps, cpuDuties, independent, gpuTemps, gpuDuties);
			detail = backendDetail;
			return false;
		}
		StopActive("被新的曲线控制器替换");
		lock (_gate)
		{
			_independent = independent;
			_cpuTemps = (byte[])cpuTemps.Clone();
			_cpuDuties = (int[])cpuDuties.Clone();
			_validCpu = CountValidPoints(_cpuTemps, _cpuDuties);
			if (independent)
			{
				_gpuTemps = (byte[])gpuTemps.Clone();
				_gpuDuties = (int[])gpuDuties.Clone();
				_validGpu = CountValidPoints(_gpuTemps, _gpuDuties);
			}
			else
			{
				_gpuTemps = (byte[])_cpuTemps.Clone();
				_gpuDuties = (int[])_cpuDuties.Clone();
				_validGpu = _validCpu;
			}
			_tempFailTicks = 0;
			_lastTempC = temp;
			_lastSampleTick = Environment.TickCount64;
			_lastTargetPercent = -1;
			_backend = backend;
			_tableLatchBase = latchBase;
			_tableIsolateBase = isolateBase;
			_tableActive = false;
			_tableLastRaw1 = -1;
			_tableLastRaw2 = -1;
			_tableTempSampleTick = Environment.TickCount64;
			_tableLastWriteTick = Environment.TickCount64;
			_tableHeartbeatMs = WujieFanEc.TableHeartbeatMs;
			_tableContentionCount = 0;
			_tableWriteFailCount = 0;
			_lowTempHandoff = false;
			_running = true;
			_active = this;
			_timer.Change(WriteIntervalMs, WriteIntervalMs);
		}
		ClearChannelRecovery("已重新接管");
		_activeSignature = signature;
		detail = "已启动（" + EcIo.Detail + " · " + backendDetail + " · " + (independent ? "独立控制" : "同步控制")
			+ " · CPU " + _validCpu + " 点 / GPU " + _validGpu + " 点 · 当前 " + temp + "℃）";
		Logger.WriteLine("[fan] 无界 EC 曲线启动：" + detail);
		return true;
	}

	/// <summary>记下这次下发意图：EC 通道不可用导致没能接管时，等通道回来后自动重新接管。</summary>
	private static void ArmChannelRecovery(byte[] cpuTemps, int[] cpuDuties, bool independent, byte[] gpuTemps, int[] gpuDuties)
	{
		lock (RecoveryGate)
		{
			_pendingStart = new PendingStart
			{
				CpuTemps = (byte[])cpuTemps.Clone(),
				CpuDuties = (int[])cpuDuties.Clone(),
				Independent = independent,
				GpuTemps = gpuTemps == null ? Array.Empty<byte>() : (byte[])gpuTemps.Clone(),
				GpuDuties = gpuDuties == null ? Array.Empty<int>() : (int[])gpuDuties.Clone()
			};
			_recoveryAttempts = 0;
			_recoveryTimer ??= new Timer(_ => RetryChannelRecovery(), null, RecoveryIntervalMs, RecoveryIntervalMs);
		}
	}

	/// <summary>取消自动恢复（已接管成功、或用户明确交还 EC 时就不要再抢回来）。</summary>
	private static void ClearChannelRecovery(string reason)
	{
		lock (RecoveryGate)
		{
			if (_pendingStart == null && _recoveryTimer == null)
			{
				return;
			}
			_pendingStart = null;
			_recoveryTimer?.Dispose();
			_recoveryTimer = null;
			Logger.WriteLine("[fan] 无界 EC 曲线：取消通道恢复重试（" + reason + "）");
		}
	}

	/// <summary>一分钟看一次通道：回来了就按上次下发重新接管；一直没回来最多等 30 轮。</summary>
	private static void RetryChannelRecovery()
	{
		PendingStart? pending;
		lock (RecoveryGate)
		{
			pending = _pendingStart;
			if (pending == null || ++_recoveryAttempts > RecoveryAttemptLimit)
			{
				_pendingStart = null;
				_recoveryTimer?.Dispose();
				_recoveryTimer = null;
				if (pending != null)
				{
					Logger.WriteError("[fan] 无界 EC 曲线：EC 通道 " + RecoveryAttemptLimit + " 分钟仍未恢复 ⇒ 停止自动重试");
				}
				return;
			}
			if (!AcpiDriverEc.IsAvailable() && !I2ecInpOut.IsAvailable)
			{
				// 通道还没回来：先借自愈安装再试一次（6 小时限频由 AcpiDriverInstaller 自己管），仍不行就静默等下一轮。
				AcpiDriverInstaller.EnsureInstalled(force: false, out _);
				if (!AcpiDriverEc.IsAvailable() && !I2ecInpOut.IsAvailable)
				{
					return;
				}
			}
			_pendingStart = null;
			_recoveryTimer?.Dispose();
			_recoveryTimer = null;
		}
		WujieFanEcController controller = new();
		bool ok = controller.Start(pending.CpuTemps, pending.CpuDuties, pending.Independent, pending.GpuTemps, pending.GpuDuties, out string detail);
		Logger.WriteLine("[fan] 无界 EC 曲线：EC 通道恢复 ⇒ 按上次下发重新接管「" + (ok ? "成功" : "仍失败") + "」（" + detail + "）");
	}

	/// <summary>
	/// 后端选择：只认 table（曲线表+锁存）。表初始化失败就如实报失败，⛔ 不回退直写。
	/// </summary>
	private static void SelectBackend(out ControlBackend backend, out int latchBase, out int isolateBase, out string backendDetail)
	{
		backend = ControlBackend.Unavailable;
		latchBase = 0;
		isolateBase = 0;
		string configured = (MechrevoLite.AppConfig.GetString("wujie_fan_backend") ?? "").Trim();
		if (configured.Equals("direct", StringComparison.OrdinalIgnoreCase)
			|| configured.Equals("duty", StringComparison.OrdinalIgnoreCase))
		{
			Logger.WriteError("[fan] 无界 EC：wujie_fan_backend=" + configured + " 是已删除的直写后端，按 table 执行");
		}
		if (EcIo.Active == EcTransport.None)
		{
			backendDetail = "曲线未启用（没有可用 EC 通道）";
			Logger.WriteError("[fan] 无界 EC：写表通道不可用 ⇒ " + backendDetail + "，风扇交还 EC 原生曲线");
			return;
		}
		if (configured.Length > 0
			&& !configured.Equals("table", StringComparison.OrdinalIgnoreCase)
			&& !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			Logger.WriteError("[fan] 无界 EC：未知 wujie_fan_backend=" + configured + " ⇒ 按默认策略尝试 table");
		}
		if (!EcIo.TryReadByte(WujieFanEc.RegisterFanTableLatch, out int latch, out string latchDetail))
		{
			backendDetail = "曲线未启用（table 初始化失败：读 0x07C6 失败 " + latchDetail + "；⛔ 不回退 direct 直写）";
			Logger.WriteError("[fan] 无界 EC：table 初始化失败 ⇒ " + latchDetail + "，风扇交还 EC 原生曲线");
			return;
		}
		if (!EcIo.TryReadByte(WujieFanEc.RegisterFanIsolate, out int isolate, out string isolateDetail))
		{
			backendDetail = "曲线未启用（table 初始化失败：读 0x07C5 失败 " + isolateDetail + "；⛔ 不回退 direct 直写）";
			Logger.WriteError("[fan] 无界 EC：table 初始化失败 ⇒ " + isolateDetail + "，风扇交还 EC 原生曲线");
			return;
		}
		latchBase = latch & ~WujieFanEc.TableLatchBit;
		isolateBase = isolate & ~0x80;
		backend = ControlBackend.Table;
		backendDetail = "table 后端（0x0F20/0x0F50 前 8 字节 + 0x07C6 写 0x04 锁存 + 0x07C5 bit7 隔离；latch_base=0x"
			+ latchBase.ToString("X2") + " isolate_base=0x" + isolateBase.ToString("X2") + "）";
		bool tableSelfCheck = RunTableSelfCheck(out string tableSelfCheckDetail);
		Logger.WriteLine("[fan] 无界 EC：选择 table 后端优先（latch_base=0x" + latchBase.ToString("X2")
			+ " isolate_base=0x" + isolateBase.ToString("X2") + "；目标不变时稳态零写入，10s 心跳校验）"
			+ " · 计划自检=" + tableSelfCheck + "（" + tableSelfCheckDetail + "）");
	}

	/// <summary>停止写入，交还 EC（EC 会在 6~8 秒内接管；不做渐进降速）。</summary>
	internal void Stop(string reason)
	{
		if (_customModeSet)
		{
			// 交还时清自定义模式标志（0x40），让固件按自己的策略接管风扇。
			EcIo.TryWriteByte(WujieFanEc.RegisterCustomMode, 0x40, out _);
			_customModeSet = false;
		}
		bool wasRunning;
		bool releaseTable;
		lock (_gate)
		{
			wasRunning = _running;
			releaseTable = _backend == ControlBackend.Table && _tableActive;
			_running = false;
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			_tempFailTicks = 0;
			if (reason.Length > 0)
			{
				LastDetail = reason;
			}
		}
		// ⭐ task-43：table 后端停止/交还前必须**清锁存+清隔离**（绝对写）；否则 EC 会一直按最后一张表跑。
		if (releaseTable)
		{
			ReleaseTable("停止/" + reason);
		}
		if (wasRunning)
		{
			Logger.WriteLine("[fan] 无界 EC 曲线停止：" + reason + "（交还 EC）");
		}
		if (ReferenceEquals(_active, this))
		{
			_active = null;
		}
	}

	public void Dispose()
	{
		Stop("窗口关闭");
		_timer.Dispose();
	}

	/// <summary>停掉当前活动的无界风扇曲线控制器（例如强冷开启时；强冷本身仍走 GCU）。</summary>
	internal static void StopActive(string reason)
	{
		// 明确交还/停掉曲线时，不要再用后台重试把它抢回来。
		ClearChannelRecovery(reason);
		WujieFanEcController active = _active;
		if (active != null)
		{
			active.Stop(reason);
		}
	}

	private void OnTick(object state)
	{
		lock (_gate)
		{
			if (!_running)
			{
				return;
			}
		}
		try
		{
			// table 后端：25Hz 定时器只用于采样与心跳校验，写表只在目标变化或表体漂移时发生。
			if (_backend == ControlBackend.Unavailable)
			{
				// 曲线未启用：⛔ 不用 25Hz 直写兜底（无界固件会把表打乱，直写无意义还会覆盖用户预期）。
				if (Logger.TryMarkChanged("wujie-fan-unavailable", "1"))
				{
					Logger.WriteError("[fan] 无界 EC：曲线后端未启用（table 初始化失败），已交还 EC 原生曲线；⛔ 不回退 direct");
				}
				return;
			}
			OnTickTable();
		}
		catch (Exception ex)
		{
			Stop("控制器异常（" + ex.GetType().Name + "），交还 EC");
			LastDetail = "控制器异常已停止";
		}
	}

	/// <summary>
	/// 只读：回读表体并与当前目标比对（固件偶发把表打乱 ⇒ 靠校验发现后重锁）。
	/// <para>期望值 = 目标 raw × 255 / 200（与写表同一换算，表量程 0..255）；只比前 
	/// <see cref="WujieFanEc.FanTableActiveLength" /> 个字节。</para>
	/// </summary>
	private bool TableContentDrift(out string detail)
	{
		detail = "";
		if (_tableCpuDuty.Length < WujieFanEc.FanTableActiveLength
			|| _tableGpuDuty.Length < WujieFanEc.FanTableGpuDutyLength)
		{
			return false;
		}
		for (int i = 0; i < WujieFanEc.FanTableActiveLength; i++)
		{
			if (ReadEc(WujieFanEc.RegisterFanTableFan1 + i, out int v1) && v1 != _tableCpuDuty[i])
			{
				detail = "表1[" + i + "]=0x" + v1.ToString("X2") + "≠0x" + _tableCpuDuty[i].ToString("X2");
				return true;
			}
		}
		for (int i = 0; i < WujieFanEc.FanTableGpuDutyLength; i++)
		{
			if (ReadEc(WujieFanEc.RegisterFanTableFan2 + i, out int v2) && v2 != _tableGpuDuty[i])
			{
				detail = "表2[" + i + "]=0x" + v2.ToString("X2") + "≠0x" + _tableGpuDuty[i].ToString("X2");
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// ⭐⭐ 2026-10-27（task-43）：table 后端的一步。只在目标变化时写表+锁存；稳态只做 10s 心跳校验，
	/// EC 偏离目标（&gt; 8 raw）才重锁。低温 &lt;45℃ 释放表交还 EC，≥48℃ 重新接管（3℃ 回差）。
	/// </summary>
	private void OnTickTable()
	{
		long now = Environment.TickCount64;
		int temp = _lastTempC;
		if (now - _tableTempSampleTick >= TableTempSampleMs)
		{
			_tableTempSampleTick = now;
			bool tempOk = ReadEc(WujieFanEc.RegisterCpuTemp, out int freshTemp)
				&& freshTemp >= 0 && freshTemp <= 120;
			if (!tempOk)
			{
				_tempFailTicks++;
				if (_tempFailTicks >= TableTempFailSamples)
				{
					Stop("连续读 CPU 温度失败（table，约 3 秒），交还 EC");
					LastDetail = "I/O 失败已停止";
					return;
				}
			}
			else
			{
				_tempFailTicks = 0;
				temp = freshTemp;
				_lastTempC = freshTemp;
			}
		}
		if (temp < 0)
		{
			return;
		}
		if (temp >= 95)
		{
			Stop("温度 " + temp + "℃ ≥ 95℃，交还 EC 自动控速");
			LastDetail = "温度过高已交还 EC";
			return;
		}
		// 低温段同样按曲线执行：不再把控制权交还 EC（曲线温度轴可以低于 45℃）。
		int cpuTarget = (temp >= 85) ? 100 : ResolveDuty(_cpuTemps, _cpuDuties, _validCpu, temp);
		int secondaryTemp = temp;
		if (_independent && ReadEc(0x044F, out int gpuTemp) && gpuTemp > 0 && gpuTemp <= 120)
		{
			secondaryTemp = gpuTemp;
		}
		int gpuTarget = (secondaryTemp >= 85) ? 100 : ResolveDuty(_gpuTemps, _gpuDuties, _validGpu, secondaryTemp);
		// 5% 下限：EC 自身约 1745 RPM 地板，低于此值副扇可能被判堵转。
		if (cpuTarget < WujieFanEc.TableFanFloorPercent)
		{
			cpuTarget = WujieFanEc.TableFanFloorPercent;
		}
		if (gpuTarget < WujieFanEc.TableFanFloorPercent)
		{
			gpuTarget = WujieFanEc.TableFanFloorPercent;
		}
		int raw1 = Math.Max(1, Math.Min(EcPortIo.WujieFanDutyMaxRaw, (int)Math.Round(cpuTarget * 2.0)));
		int raw2 = Math.Max(1, Math.Min(EcPortIo.WujieFanDutyMaxRaw, (int)Math.Round(gpuTarget * 2.0)));
		if (BuildCurveSignature() != _tableSignature)
		{
			if (!ApplyTable("曲线变化"))
			{
				return;
			}
			_tableSignature = BuildCurveSignature();
			_tableLastWriteTick = now;
			_tableContentionCount = 0;
			_tableWriteFailCount = 0;
			int combined = _independent ? (cpuTarget * 1000 + gpuTarget) : cpuTarget;
			if (combined != _lastTargetPercent)
			{
				_lastTargetPercent = combined;
				Logger.WriteLineIfChanged("wujie-fan-target", $"[fan] 无界 table：{temp}℃ → CPU {cpuTarget}% (raw={raw1}) · 副扇 {gpuTarget}% (raw={raw2}) · {(_independent ? "独立" : "同步")}（0x0F20/0x0F50 曲线表 + 0x07C6 锁存）");
			}
		}
		else if (now - _tableLastWriteTick >= _tableHeartbeatMs)
		{
			// 心跳只回读表体：表内容被别的程序改写才重写整表，稳态零写入。
			_tableLastWriteTick = now;
			bool drift = TableContentDrift(out string tableDriftDetail);
			if (drift)
			{
				_tableContentionCount++;
				if (!ApplyTable("心跳漂移重锁"))
				{
					return;
				}
				if (_tableContentionCount >= TableContentionLimit)
				{
					_tableHeartbeatMs = TableContentionHeartbeatMs;
					Logger.WriteError("[fan] 无界 table：心跳连续 " + _tableContentionCount + " 次发现表体被改（"
						+ tableDriftDetail + "）⇒ 疑似别的程序争用 EC；重锁间隔放慢到 30s（⛔ 不跟着加码狂写）");
				}
				else
				{
					Logger.WriteLine("[fan] 无界 table：心跳发现表体被改（" + tableDriftDetail + "）⇒ 重锁一次");
				}
			}
			else
			{
				_tableContentionCount = 0;
			}
		}
		// 首档占空比为 0 时 EC 停在那里不会自己再起转：温度已过首档阈值就重新提交整表，
		// 每个温度区间最多补 4 次（补完还是回到 0 档说明不是这个原因，⛔ 不无限重写抢选档权）。
		if (_cpuTemps.Length > 1 && temp < _cpuTemps[1])
		{
			_zeroSlotRetries = 0;
		}
		if (_tableCpuDuty.Length == WujieFanEc.FanTableActiveLength && _tableCpuDuty[0] == 0
			&& _cpuTemps.Length > 1 && temp >= _cpuTemps[1] && _zeroSlotRetries < 4
			&& now - _lastZeroSlotTick >= 15000)
		{
			_zeroSlotRetries++;
			_lastZeroSlotTick = now;
			if (ApplyTable("回差落 0 档重新提交"))
			{
				Logger.WriteLineIfChanged("wujie-fan-zero-slot", "[fan] 无界 table：" + temp + "℃ 已过首档（"
					+ _cpuTemps[0] + "℃→" + _cpuTemps[1] + "℃）⇒ 重新提交整表 + 握手，避免停在 0 档");
				TryWakeFan(TableDutyAt(1), TableDutyAt(1));
			}
		}
	}

	/// <summary>
	/// 温度轮廓写入计划：CPU/GPU 各写整张升档温度表与降档温度表。
	/// 降档温度 = 升档温度 - 回差（不低于 0），给 EC 留回差。
	/// </summary>
	/// <summary>
	/// 停转重启握手：0x0F21/0x0F51 就是两张表的 Duty[1] 槽，写进去的必须是表里第 2 档的占空比，
	/// 不能塞当前目标（否则第 2 档被改坏）→ 再置 0x07A3 bit6（EC 消费后不自清，回执看转速）。
	/// </summary>
	private void TryWakeFan(int duty1, int duty2)
	{
		if (Environment.TickCount64 - _lastHandshakeTick < 5000)
		{
			return;
		}
		_lastHandshakeTick = Environment.TickCount64;
		if (!EcIo.TryWriteByte(WujieFanEc.RegisterFanSoftStart1, (byte)Math.Clamp(duty1, 0, 255), out _)
			|| !EcIo.TryWriteByte(WujieFanEc.RegisterFanSoftStart2, (byte)Math.Clamp(duty2, 0, 255), out _))
		{
			return;
		}
		int current = EcIo.TryReadByte(WujieFanEc.RegisterRestartHandshake, out int cur, out _) ? cur : 0;
		EcIo.TryWriteByte(WujieFanEc.RegisterRestartHandshake, (byte)(current | WujieFanEc.RestartHandshakeBit), out _);
		Logger.WriteLine("[fan] 无界 EC 停转握手：Duty[1]=" + duty1 + "/" + duty2 + " → 置 0x07A3 bit6（等转速起转确认）");
	}

	/// <summary>表里第 index 档的占空比原始值（主扇表为准，越界取首/末档）。</summary>
	private int TableDutyAt(int index)
	{
		if (_tableCpuDuty.Length == 0)
		{
			return 0;
		}
		return _tableCpuDuty[Math.Clamp(index, 0, _tableCpuDuty.Length - 1)];
	}

	private static List<EcWriteStep> BuildThresholdPlan(byte[] cpuTemps, byte[] gpuTemps)
	{
		List<EcWriteStep> list = new(WujieFanEc.FanTableActiveLength * 4);
		AppendThreshold(list, WujieFanEc.RegisterCpuUpT, WujieFanEc.RegisterCpuDownT, cpuTemps);
		AppendThreshold(list, WujieFanEc.RegisterGpuUpT, WujieFanEc.RegisterGpuDownT, gpuTemps);
		return list;
	}

	private static void AppendThreshold(List<EcWriteStep> list, int upBase, int downBase, byte[] temps)
	{
		if (temps == null || temps.Length < WujieFanEc.FanTableActiveLength)
		{
			return;
		}
		for (int i = 0; i < WujieFanEc.FanTableActiveLength; i++)
		{
			list.Add(new EcWriteStep(upBase + i, temps[i]));
		}
		// 降档阈值取上一档的升档阈值：只减固定回差时，相邻档间隔一旦大于回差，温度会同时满足
		// 「从下一档升档」和「从本档降档」，档位在两个槽之间来回横跳。
		for (int i = 0; i < WujieFanEc.FanTableActiveLength; i++)
		{
			int limit = temps[i] > 0 ? temps[i] - 1 : 0;
			int previous = i > 0 ? temps[i - 1] : limit;
			int down = Math.Min(limit, previous);
			if (down < 0)
			{
				down = 0;
			}
			list.Add(new EcWriteStep(downBase + i, down));
		}
	}

	/// <summary>
	/// ⭐ task-43：构造 table 写入计划（纯函数，便于离线自检）；顺序 = 表1 前 8 → 表2 前 8 → 隔离 → 清锁存 → 置锁存(50ms) → 补写占空比。
	/// </summary>
	private static void BuildTableWritePlan(byte[] cpuDuty, byte[] gpuDuty, int latchBase, int isolateBase,
		out List<EcWriteStep> steps, out List<EcVerifyStep> verify, out bool needIsolate)
	{
		// 副扇与主扇占空比完全一致时不需要分通道输出，清隔离位让两把扇共用主扇链路。
		bool same = true;
		for (int i = 0; i < WujieFanEc.FanTableGpuDutyLength; i++)
		{
			if (cpuDuty[i] != gpuDuty[i])
			{
				same = false;
				break;
			}
		}
		needIsolate = !same;
		int isolateWant = needIsolate ? (isolateBase | 0x80) : (isolateBase & ~0x80);
		steps = new List<EcWriteStep>(WujieFanEc.FanTableActiveLength + WujieFanEc.FanTableGpuDutyLength + 6);
		int tableMax = WujieFanEc.FanTableCtrlFloor;
		int v;
		for (int i = 0; i < WujieFanEc.FanTableActiveLength; i++)
		{
			v = Math.Clamp((int)cpuDuty[i], 0, WujieFanEc.FanTableDutyMax);
			tableMax = Math.Max(tableMax, v);
			steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableFan1 + i, v));
		}
		// 副扇占空比表只有 13 槽；0x0F5D..0x0F5F 是阈值与控制量，按占空比写会覆盖它们。
		for (int i = 0; i < WujieFanEc.FanTableGpuDutyLength; i++)
		{
			v = Math.Clamp((int)gpuDuty[i], 0, WujieFanEc.FanTableDutyMax);
			tableMax = Math.Max(tableMax, v);
			steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableFan2 + i, v));
		}
		steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableMax1, tableMax));
		steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableMax2, tableMax));
		steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableCtrl, tableMax));
		// 锁存前先摆好隔离位（0→1 跳变时 EC 就看到最终状态），再清锁存 → 50ms → 置锁存。
		steps.Add(new EcWriteStep(WujieFanEc.RegisterFanIsolate, isolateWant, 20));
		steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableLatch, 0x00));
		steps.Add(new EcWriteStep(WujieFanEc.RegisterFanTableLatch, WujieFanEc.TableLatchBit, WujieFanEc.TableLatchPulseMs));
		// 不补写 0x1804/0x1809：fanctl 的 table 语义是让 EC 自己沿软着陆斜坡走到表目标；
		// 直接写目标寄存器会和 EC 的斜坡/表状态互相争用，且回读可能永远不相等（导致误判写表失败）。
		verify = new List<EcVerifyStep>
		{
			new(WujieFanEc.RegisterFanIsolate, isolateWant),
			new(WujieFanEc.RegisterFanTableLatch, WujieFanEc.TableLatchBit)
		};
	}

	/// <summary>
	/// ⭐ task-43 离线自检（⛔ 不访问 EC/不写任何端口）：校验常量与写入计划形状
	/// （8+8 表字节、隔离位、锁存脉冲 50ms、斜坡补写、同步时清隔离）。供启动日志/离线 harness 调用。
	/// </summary>
	internal static bool RunTableSelfCheck(out string detail)
	{
		try
		{
			// 用局部变量比较，避免编译器把“常量相等”折叠成恒假条件（CS0162 不可达代码）。
			int expectedTableLength = 16;
			int expectedHeartbeatMs = 10000;
			int expectedDriftRaw = 8;
			int expectedPulseMs = 50;
			int expectedResumeC = MinimumOperatingTemperatureC + 3;
			if (WujieFanEc.FanTableActiveLength != expectedTableLength
				|| WujieFanEc.TableHeartbeatMs != expectedHeartbeatMs
				|| WujieFanEc.TableDriftToleranceRaw != expectedDriftRaw
				|| WujieFanEc.TableLatchPulseMs != expectedPulseMs
				|| WujieFanEc.HandoffResumeTemperatureC != expectedResumeC)
			{
				detail = "常量与设计不一致（table 长度/心跳/漂移/脉冲/回差）";
				return false;
			}
			byte[] cpuDuty = new byte[WujieFanEc.FanTableActiveLength];
			byte[] gpuDuty = new byte[WujieFanEc.FanTableGpuDutyLength];
			for (int i = 0; i < cpuDuty.Length; i++)
			{
				cpuDuty[i] = (byte)(i * 10);
			}
			for (int i = 0; i < gpuDuty.Length; i++)
			{
				gpuDuty[i] = (byte)(i * 12);
			}
			BuildTableWritePlan(cpuDuty, gpuDuty, 0x00, 0x00, out List<EcWriteStep> steps,
				out List<EcVerifyStep> verify, out bool needIsolate);
			if (!needIsolate || steps.Count != 35 || verify.Count != 2)
			{
				detail = "计划形状不对：steps=" + steps.Count + " verify=" + verify.Count + " isolate=" + needIsolate;
				return false;
			}
			int expectMax = Math.Max(Math.Max((int)cpuDuty[cpuDuty.Length - 1], (int)gpuDuty[gpuDuty.Length - 1]),
				WujieFanEc.FanTableCtrlFloor);
			if (steps[0].Address != WujieFanEc.RegisterFanTableFan1 || steps[0].Value != cpuDuty[0]
				|| steps[15].Address != WujieFanEc.RegisterFanTableFan1 + 15 || steps[7].Value != cpuDuty[7])
			{
				detail = "主扇占空比表计划不对";
				return false;
			}
			int gpuLast = 16 + WujieFanEc.FanTableGpuDutyLength - 1;
			if (steps[16].Address != WujieFanEc.RegisterFanTableFan2 || steps[16].Value != gpuDuty[0]
				|| steps[gpuLast].Address != WujieFanEc.RegisterFanTableFan2 + WujieFanEc.FanTableGpuDutyLength - 1
				|| steps[gpuLast].Value != gpuDuty[gpuDuty.Length - 1])
			{
				detail = "副扇占空比表计划不对（应为 13 槽）";
				return false;
			}
			int ctrlIdx = 16 + WujieFanEc.FanTableGpuDutyLength;
			if (steps[ctrlIdx].Address != WujieFanEc.RegisterFanTableMax1 || steps[ctrlIdx].Value != expectMax
				|| steps[ctrlIdx + 1].Address != WujieFanEc.RegisterFanTableMax2 || steps[ctrlIdx + 1].Value != expectMax
				|| steps[ctrlIdx + 2].Address != WujieFanEc.RegisterFanTableCtrl || steps[ctrlIdx + 2].Value != expectMax)
			{
				detail = "最大占空比阈值/表控制器计划不对";
				return false;
			}
			if (steps[32].Address != WujieFanEc.RegisterFanIsolate || steps[32].Value != 0x80)
			{
				detail = "副扇隔离位置位计划不对";
				return false;
			}
			if (steps[33].Address != WujieFanEc.RegisterFanTableLatch || steps[33].Value != 0x00
				|| steps[34].Address != WujieFanEc.RegisterFanTableLatch || steps[34].Value != WujieFanEc.TableLatchBit
				|| steps[34].DelayAfterMs != WujieFanEc.TableLatchPulseMs)
			{
				detail = "锁存脉冲计划不对（应先 &~0x40 → 50ms → |0x40）";
				return false;
			}
			byte[] shared = new byte[WujieFanEc.FanTableActiveLength];
			byte[] sharedGpu = new byte[WujieFanEc.FanTableGpuDutyLength];
			for (int i = 0; i < shared.Length; i++)
			{
				shared[i] = 120;
			}
			for (int i = 0; i < sharedGpu.Length; i++)
			{
				sharedGpu[i] = 120;
			}
			BuildTableWritePlan(shared, sharedGpu, 0x02, 0x10, out steps, out _, out bool sameTargetIsolate);
			if (sameTargetIsolate || steps[32].Value != 0x10)
			{
				detail = "主副扇目标相同时应清 0x07C5 bit7（并保留基线其它位）";
				return false;
			}
			detail = "table 计划自检 OK（表1/2 各 8 字节 + 隔离 + 锁存 50ms 脉冲；同目标清隔离）";
			return true;
		}
		catch (Exception ex)
		{
			detail = "自检异常：" + ex.GetType().Name + " " + ex.Message;
			return false;
		}
	}

	/// <summary>由当前曲线算出两张占空比表（百分比 → 0..200 原始量程，单调不减）。</summary>
	private bool BuildCurveDuties(out byte[] cpuDuty, out byte[] gpuDuty, out string detail)
	{
		cpuDuty = new byte[WujieFanEc.FanTableActiveLength];
		gpuDuty = new byte[WujieFanEc.FanTableGpuDutyLength];
		detail = "";
		if (!FillDuties(_cpuTemps, _cpuDuties, _validCpu, cpuDuty))
		{
			detail = "主扇曲线不完整";
			return false;
		}
		if (_independent && _validGpu >= WujieFanEc.FanTableActiveLength)
		{
			byte[] full = new byte[WujieFanEc.FanTableActiveLength];
			if (FillDuties(_gpuTemps, _gpuDuties, _validGpu, full))
			{
				Array.Copy(full, gpuDuty, gpuDuty.Length);
				return true;
			}
		}
		// 副扇没有独立温度源时与主扇共用一张曲线。
		Array.Copy(cpuDuty, gpuDuty, gpuDuty.Length);
		return true;
	}

	/// <summary>把一条曲线转成占空比表；超量程截断，并保证单调不减。</summary>
	private static bool FillDuties(byte[] temps, int[] duties, int valid, byte[] target)
	{
		if (valid < target.Length || temps.Length < target.Length || duties.Length < target.Length)
		{
			return false;
		}
		int running = 0;
		for (int i = 0; i < target.Length; i++)
		{
			if (temps[i] > 120)
			{
				return false;
			}
			int raw = (int)Math.Round(duties[i] * 2.0);
			raw = Math.Clamp(raw, 0, WujieFanEc.FanTableDutyMax);
			if (raw < running)
			{
				raw = running;
			}
			running = raw;
			target[i] = (byte)raw;
		}
		return true;
	}

	/// <summary>曲线指纹：温度轴与占空比都参与，内容变了才需要重写整表。</summary>
	private int BuildCurveSignature()
	{
		int hash = 17;
		for (int i = 0; i < _cpuTemps.Length; i++)
		{
			hash = hash * 31 + _cpuTemps[i];
		}
		for (int i = 0; i < _cpuDuties.Length; i++)
		{
			hash = hash * 31 + _cpuDuties[i];
		}
		if (_independent)
		{
			for (int i = 0; i < _gpuTemps.Length; i++)
			{
				hash = hash * 31 + _gpuTemps[i];
			}
			for (int i = 0; i < _gpuDuties.Length; i++)
			{
				hash = hash * 31 + _gpuDuties[i];
			}
		}
		return hash;
	}

	/// <summary>占空比表摘要（起转档 / 次档 / 末档），便于日志核对。</summary>
	private static string TableSummary(byte[] duty)
	{
		if (duty.Length == 0)
		{
			return "空表";
		}
		int second = duty.Length > 1 ? duty[1] : duty[0];
		return "首档 raw=" + duty[0] + " 次档 raw=" + second + " 末档 raw=" + duty[duty.Length - 1];
	}

	/// <summary>
	/// 写整张曲线的占空比表 → 设/清隔离位 → 锁存脉冲（base&amp;~0x40 → 50ms → base|0x40）；
	/// 不写 0x1804/0x1809，由 EC 沿软着陆斜坡走到表目标；整组序列在 I2EC 跨进程锁内一次完成。
	/// </summary>
	private bool ApplyTable(string reason)
	{
		lock (_gate)
		{
		if (!_running)
		{
			// Stop 已把控制器置停（另一个线程正在交还 EC）⇒ ⛔ 不再重锁表，避免 Stop 之后又被锁上。
			return false;
		}
		if (Environment.TickCount64 < _tableRetryNotBeforeTick)
		{
			// 刚失败过：等退避窗口过去再试，⛔ 不在 25Hz 里连打 EC。
			return false;
		}
		if (!BuildCurveDuties(out byte[] cpuDuty, out byte[] gpuDuty, out string curveDetail))
		{
			Logger.WriteError("[fan] 无界 table：曲线无效，本次不下发（" + reason + "）：" + curveDetail);
			return false;
		}
		BuildTableWritePlan(cpuDuty, gpuDuty, _tableLatchBase, _tableIsolateBase, out List<EcWriteStep> steps,
			out List<EcVerifyStep> verify, out bool needIsolate);
		// 温度轮廓先于占空比写入：升档温度表 + 降档温度表（降档 = 升档 -2℃，避免阈值附近反复横跳）。
		steps.InsertRange(0, BuildThresholdPlan(_cpuTemps, _gpuTemps));
		if (!_customModeSet)
		{
			// 自定义模式标志（开机值 0x41；退出时写 0x40 交还）。
			_customModeSet = EcIo.TryWriteByte(WujieFanEc.RegisterCustomMode, 0x41, out string customDetail);
			Logger.WriteLine("[fan] 无界 EC 自定义模式：" + (_customModeSet ? "已置位（0x41）" : "置位失败：" + customDetail));
		}
		bool ok = EcIo.TryWriteSequence(steps, verify, out string detail);
		if (!ok)
		{
			_tableWriteFailCount++;
			bool slowRetry = _tableWriteFailCount >= TableWriteFailLimit;
			_tableRetryNotBeforeTick = Environment.TickCount64 + (slowRetry ? TableWriteRetrySlowMs : TableWriteRetryMs);
			Logger.WriteError("[fan] 无界 table：写表失败（" + reason + "）：" + detail
				 + " ⇒ 第 " + _tableWriteFailCount + " 次，" + (slowRetry ? "30s" : "5s") + " 后重试"
				 + (slowRetry ? "；连续失败已达上限，暂停下发（⛔ 不回退 direct 直写，EC 继续按原生曲线/旧表跑）"
					: "（⛔ 不回退 direct 直写）"));
			return false;
		}
		_tableActive = true;
		_tableWriteFailCount = 0;
		_tableRetryNotBeforeTick = 0;
		_tableCpuDuty = cpuDuty;
		_tableGpuDuty = gpuDuty;
		Logger.WriteLineIfChanged("wujie-fan-table", "[fan] 无界 table：已写曲线表+锁存（" + reason + " · "
			+ TableSummary(cpuDuty) + " · 隔离=" + (needIsolate ? "置" : "清") + " · 回读确认 ok）");
		return true;
		}
	}
	/// <summary>
	/// ⭐ task-43：交还 EC —— 绝对写入清 0x0727 bit6 与 0x07C5 bit7（&amp; 基线，**绝不做 read-modify-write**），
	/// 带一次回读校验；失败重试一次并如实记日志（不假装成功）。
	/// </summary>
	private bool ReleaseTable(string reason, bool force = false)
	{
		if (!_tableActive && !force)
		{
			return true;
		}
		int latchWant = _tableLatchBase & ~WujieFanEc.TableLatchBit;
		int isolateWant = _tableIsolateBase & ~0x80;
		List<EcWriteStep> steps = new()
		{
			new(WujieFanEc.RegisterFanTableLatch, latchWant),
			new(WujieFanEc.RegisterFanIsolate, isolateWant)
		};
		List<EcVerifyStep> verify = new()
		{
			new(WujieFanEc.RegisterFanTableLatch, latchWant),
			new(WujieFanEc.RegisterFanIsolate, isolateWant)
		};
		bool ok = EcIo.TryWriteSequence(steps, verify, out string detail);
		if (!ok)
		{
			Thread.Sleep(20);
			ok = EcIo.TryWriteSequence(steps, verify, out detail);
		}
		_tableActive = false;
		_tableLastRaw1 = -1;
		_tableLastRaw2 = -1;
		if (ok)
		{
			Logger.WriteLine("[fan] 无界 table：已清锁存+隔离并交还 EC（" + reason + "）");
		}
		else
		{
			Logger.WriteError("[fan] 无界 table：⚠️ 交还 EC 失败（" + reason + "）：" + detail
				+ " ⇒ EC 可能仍按旧表运行，下次启动写表时会重新锁存");
		}
		return ok;
	}


	private static int CountValidPoints(byte[] temps, int[] duties)
	{
		int valid = 0;
		for (int i = 0; i < temps.Length && i < duties.Length; i++)
		{
			if (temps[i] == byte.MaxValue)
			{
				break;
			}
			valid++;
		}
		return Math.Max(1, valid);
	}

	private static int ResolveDuty(byte[] temps, int[] duties, int points, int temp)
	{
		if (points <= 0)
		{
			return 100;
		}
		byte[] effectiveTemps = new byte[points];
		Array.Copy(temps, effectiveTemps, points);
		for (int i = 0; i < points; i++)
		{
			if (effectiveTemps[i] < MinimumOperatingTemperatureC)
			{
				effectiveTemps[i] = (byte)MinimumOperatingTemperatureC;
			}
		}
		temp = Math.Max(temp, MinimumOperatingTemperatureC);
		if (temp <= effectiveTemps[0])
		{
			return duties[0];
		}
		for (int i = 1; i < points; i++)
		{
			int current = effectiveTemps[i];
			if (current == byte.MaxValue)
			{
				break;
			}
			if (temp <= current)
			{
				int previousTemp = effectiveTemps[i - 1];
				int span = Math.Max(1, current - previousTemp);
				int deltaDuty = duties[i] - duties[i - 1];
				return duties[i - 1] + (int)Math.Round(deltaDuty * (double)(temp - previousTemp) / span);
			}
		}
		return duties[Math.Max(0, points - 1)];
	}

	private bool ReadEc(int address, out int value)
	{
		return EcIo.TryReadByte(address, out value, out _);
	}

	private bool WriteDuty(int address, int raw, out string detail)
	{
		return EcIo.TryWriteByte(address, raw, out detail);
	}

}