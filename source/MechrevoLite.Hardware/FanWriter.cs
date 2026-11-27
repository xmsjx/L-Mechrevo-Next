// 2026-10-18（宝龙达 · 风扇接管）：⭐⭐ **风扇写入统一门面** `FanWriter` ⭐⭐
//
// ⭐⭐ 背景：WMI 写（20=1）会卡死；LPC 端口 I/O 读成功、写被 EC 拒绝；ACPI EC 读成功、写待验证。
//    机主批准：一次性把写入链路做完，但**默认不开放**（UI 隐藏，验证通过后才放开）。
//
// ⭐ 通道优先级（本类内部，顺序即优先级）：
//    ① `AcpiEc`   —— `EcPortIo`（0x62/0x66 · XRAM mailbox 0x93 写）：端口权由 PawnIO / ACPIDriver / inpout 提供，
//                   ⭐ 这条路上固件才真的接管风扇 ⇒ **默认首选** ✅
//    ② `SmuEcLpc` —— `SmuEcAccess`（= 7.3 `RyzenAccess` 的真实实现：LPC 0x4E/0x4F 端口 I/O；
//                   ⚠️ 不存在 SMU 邮箱命令，见 `SmuEcAccess.cs` 顶部来源说明）
//    ③ `VendorEc` —— `BaolongdaVendorEc`（0x2E/0x2F 间接窗口，仅宝龙达路径显式打开）：
//                   ⭐ 只在①②都不通时**兜底**；写前先做 0x1060 bit7 解锁与身份校验 ✅
//    ④ 若首选通道失败 ⇒ 自动尝试次选；成功过的通道记入 <see cref="PreferredChannel" />，下轮优先用。
//
// ⭐ 对外 API（只有这三个写入口，白名单/回读/恢复都封在内部）：
//    · <see cref="TrySetFanTargets" />（两路目标档 0..68）
//    · <see cref="TrySetManualMask" />（0xB20 的 bit1/bit3，读-改-写）
//    · <see cref="TryReleaseToFirmware" />（目标 0 + 清位，交回固件）
//    ⛔ 每次写后立刻回读比对；失败返回 false 并写日志；⛔ 不抛出异常。
//
// ⭐ 验证标记：`FanEcVerifyStore` 保存“已验证”标记（%APPDATA%\MechrevoLite\fan_ec_verified.cfg）；
//    ⭐ 2026-10-18（机主：30/40 系均已实测可用）⇒ **仅作诊断**：UI/写入路径不再依赖该 cfg ✅

using System;
using System.IO;
using System.Threading;

namespace MechrevoLite.Hardware;

/// <summary>⭐ 写通道（⛔ 不代表 SMU 邮箱命令；`SmuEcLpc` 实为 LPC 端口 I/O）✅</summary>
internal enum FanWriteChannel
{
	None,
	AcpiEc,
	SmuEcLpc,
	/// <summary>厂商驱动 + EC RAM 间接窗口（0x2E/0x2F，不碰 ACPI EC 0x66/0x62）。</summary>
	VendorEc
}

/// <summary>⭐⭐ 风扇写入统一门面（ACPI EC → LPC；回读校验/恢复都在内部）✅。</summary>
internal static class FanWriter
{
	private static FanWriteChannel _preferred = FanWriteChannel.None;

	/// <summary>交回后的转速观察窗口（只用于诊断日志，不参与成败判定）。</summary>
	private const int HealthyConfirmWindowMs = 3000;

	/// <summary>怠速下限：只用于日志里说明“采样到低于怠速”。</summary>
	private const int IdleFloorRpm = 800;

	/// <summary>交回前预热：转速低于它就先提一口气再交回去（RPM）。</summary>
	private const int PreSpinBelowRpm = 1200;

	/// <summary>交回前预热的临时占空比（%）。</summary>
	private const int PreSpinPercent = 50;

	/// <summary>预热保持时间（ms）：宝龙达风扇加减速都慢，给一点时间把转速带起来。</summary>
	private const int PreSpinHoldMs = 1200;

	/// <summary>低温停转判据：CPU 与 GPU 都不高于它时零转速属 EC 正常策略（℃）。</summary>
	private const int LowTempIdleC = 45;

	/// <summary>高温零转判据：任一温度不低于它却零转速 ⇒ 异常（℃）。</summary>
	private const int HotNoSpinC = 55;
	private static volatile bool _vendorEnabled;

	/// <summary>跨调用串行化：交回固件的"先清掩码→再写 0"必须在同一把锁内完成，
	/// 否则曲线/强冷在另一线程可插进两步之间重新置位掩码，出现"掩码置位 + 目标 0"的窗口。</summary>
	private static readonly object WriteGate = new object();

	/// <summary>宝龙达路径显式启用厂商驱动通道（其它机型不启用，避免无谓的驱动加载尝试）。</summary>
	internal static void EnableVendorChannel()
	{
		_vendorEnabled = true;
	}

	/// <summary>⭐ 上次成功的通道（None = 还没成功过）✅</summary>
	internal static FanWriteChannel PreferredChannel => _preferred;

	/// <summary>⭐ 两路目标档（0..68）；按通道优先级尝试，成功即记住通道 ✅。</summary>
	private static bool TrySetFanTargetsCore(int fan1Units, int fan2Units, out string detail, bool allowAcpiEc = true)
	{
		detail = "";
		int f1 = Math.Clamp(fan1Units, 0, 68);
		int f2 = Math.Clamp(fan2Units, 0, 68);
		// 目标写 0 而接管位仍置位 ⇒ EC 可能把风扇拉到 0 转：直接拒绝，必须先交回固件。
		if (f1 == 0 || f2 == 0)
		{
			if (TryReadMask(out int maskNow, out _) && (maskNow & 0x0A) != 0)
			{
				detail = "拒绝写 0 目标：接管掩码仍为 0x" + maskNow.ToString("X2") + "（先交回固件再写）";
				Logger.WriteError("[fan] " + detail);
				return false;
			}
		}
		foreach (FanWriteChannel channel in Candidates(allowAcpiEc))
		{
			if (TryWriteTargetsOn(channel, f1, f2, out string channelDetail))
			{
				_preferred = channel;
				detail = "通道=" + ChannelName(channel) + " · " + channelDetail;
				return true;
			}
			detail += (detail.Length == 0 ? "" : " | ") + ChannelName(channel) + "失败：" + channelDetail;
			// ⭐ 该通道失败：尝试把自己写过的部分恢复（best-effort，不抛）
			try
			{
				if (channel == FanWriteChannel.VendorEc)
				{
					// 厂商通道：先清掩码位再清目标（与交回固件同序），⛔ 不能拿 LPC 的恢复函数来收拾。
					if (BaolongdaVendorEc.TryReadByte(0xB20, out byte vm))
					{
						BaolongdaVendorEc.TryWriteByte(0xB20, (byte)(vm & ~0x0A));
						BaolongdaVendorEc.TryWriteByte(0xC83C, 0);
						BaolongdaVendorEc.TryWriteByte(0xC83D, 0);
					}
				}
				else if (channel == FanWriteChannel.AcpiEc)
				{
					EcPortIo.TryRestoreFanFirmwareAcpi(out _);
				}
				else
				{
					EcLpcIo.TryRestoreFanFirmware(out _);
				}
			}
			catch
			{
			}
		}
		return false;
	}

	/// <summary>读接管掩码 0xB20（按当前可用通道，只读；读不到返回 false）。</summary>
	internal static bool TryReadMask(out int mask, out string detail)
	{
		mask = -1;
		if (_vendorEnabled && BaolongdaVendorEc.IsReady && BaolongdaVendorEc.TryReadByte(0xB20, out byte vm))
		{
			mask = vm;
			detail = "厂商EC 0xB20=0x" + vm.ToString("X2");
			return true;
		}
		if (EcPortIo.TryReadByteStable(0xB20, out int acpiMask, out _))
		{
			mask = acpiMask;
			detail = "ACPI 0xB20=0x" + acpiMask.ToString("X2");
			return true;
		}
		if (EcLpcIo.TryReadByteStable(0xB20, out int lpcMask, out _))
		{
			mask = lpcMask;
			detail = "LPC 0xB20=0x" + lpcMask.ToString("X2");
			return true;
		}
		detail = "三个通道都读不到 0xB20";
		return false;
	}

	/// <summary>⭐ 0xB20 接管掩码（只改 bit1/bit3，保留其它位）；回读确认 ✅。</summary>
	private static bool TrySetManualMaskCore(bool bit1, bool bit3, out string detail, bool allowAcpiEc = true)
	{
		detail = "";
		foreach (FanWriteChannel channel in Candidates(allowAcpiEc))
		{
			bool ok;
			string channelDetail;
			if (channel == FanWriteChannel.VendorEc)
			{
				int target = -1;
				bool read = BaolongdaVendorEc.TryReadByte(0xB20, out byte maskNow);
				string takeover = "";
				if (read)
				{
					byte want = (byte)((maskNow & ~0x0A) | (bit1 ? 0x02 : 0x00) | (bit3 ? 0x08 : 0x00));
					target = want;
					ok = BaolongdaVendorEc.TryWriteByte(0xB20, want)
						&& BaolongdaVendorEc.TryReadByte(0xB20, out byte rb) && (rb & 0x0A) == (want & 0x0A);
					if (ok)
					{
						// 接管位写进去不代表固件认账：隔一拍再读，被收回就判失败并交给后面的通道。
						System.Threading.Thread.Sleep(120);
						bool kept = BaolongdaVendorEc.TryReadByte(0xB20, out byte again) && (again & 0x0A) == (want & 0x0A);
						takeover = kept ? "" : " · 接管位被收回";
						ok = kept;
					}
				}
				else
				{
					ok = false;
				}
				channelDetail = "厂商EC 0xB20 目标=0x" + (target < 0 ? "读不到" : target.ToString("X2"))
					+ " · 回读确认=" + ok + takeover;
			}
			else if (channel == FanWriteChannel.AcpiEc)
			{
				ok = EcPortIo.TryWriteAcpiFanManualMask(bit1, bit3, out int readBack, out channelDetail);
				channelDetail = "ACPI 0xB20 回读=0x" + (readBack < 0 ? "读不到" : readBack.ToString("X2")) + " · " + channelDetail;
			}
			else
			{
				ok = SmuEcAccess.TryWriteManualMask(bit1, bit3, out channelDetail);
			}
			if (ok)
			{
				_preferred = channel;
				detail = "通道=" + ChannelName(channel) + " · " + channelDetail;
				return true;
			}
			detail += (detail.Length == 0 ? "" : " | ") + ChannelName(channel) + "失败：" + channelDetail;
		}
		return false;
	}

	/// <summary>
	/// ⭐⭐ 交回固件（**安全顺序**）：⭐ 先清 `0xB20` bit1/bit3（交出控制权并回读确认）→ **再**清目标 0 ✅。
	/// <para>⚠️⚠️ 2026-10-18（机主安全提问）：⛔ 绝不在掩码仍置位时写 0 目标 —— 否则 EC 可能把风扇拉到 0 转 ✅</para>
	/// <para>⭐ 交回前若转速偏低，先把风扇提到一个中间目标保持气流，再交回（宝龙达加减速都慢）✅</para>
	/// <para>⭐ 交回后只按 EC 侧状态判定（掩码位已清 + 目标 0）；转速此后归 EC 自己的策略，低温降速/停转属正常，⛔ 不再据此抢回控制权 ✅</para>
	/// </summary>
	private static bool TryReleaseToFirmwareCore(out string detail, int cpuTempC = -1, int gpuTempC = -1)
	{
		detail = "";
		// ⭐ 记录交回前状态（低转速保护用）
		int preMask = -1;
		int preFan1 = -1;
		int preFan2 = -1;
		if (EcPortIo.TryReadByteStable(0xB20, out int mask, out _))
		{
			preMask = mask;
			EcPortIo.TryReadByteStable(0xC83C, out preFan1, out _);
			EcPortIo.TryReadByteStable(0xC83D, out preFan2, out _);
		}
		// ⭐ 交回前预热：掩码还置位（说明是我们接管中）且转速偏低 ⇒ 先提一口气，别让风扇在交回瞬间就空掉。
		PreSpinBeforeRelease(preMask);
		foreach (FanWriteChannel channel in Candidates())
		{
			bool ok;
			string channelDetail;
			if (channel == FanWriteChannel.VendorEc)
			{
				// 安全顺序：先清掩码位并回读确认，再写 0 目标（⛔ 不反过来）。
				bool maskCleared = BaolongdaVendorEc.TryReadByte(0xB20, out byte maskBefore)
					&& BaolongdaVendorEc.TryWriteByte(0xB20, (byte)(maskBefore & ~0x0A))
					&& BaolongdaVendorEc.TryReadByte(0xB20, out byte maskAfter)
					&& (maskAfter & 0x0A) == 0;
				bool zeroed = maskCleared
					&& BaolongdaVendorEc.TryWriteByte(0xC83C, 0)
					&& BaolongdaVendorEc.TryWriteByte(0xC83D, 0);
				ok = maskCleared && zeroed;
				channelDetail = "厂商EC 先清掩码 0xB20" + (maskCleared ? "已清" : "清除失败")
					+ " · 再清目标 0xC83C/0xC83D" + (zeroed ? "已清" : "清除失败");
			}
			else if (channel == FanWriteChannel.AcpiEc)
			{
				ok = EcPortIo.TryRestoreFanFirmwareAcpi(out channelDetail);
			}
			else
			{
				ok = EcLpcIo.TryRestoreFanFirmware(out channelDetail);
			}
			if (!ok)
			{
				detail += (detail.Length == 0 ? "" : " | ") + ChannelName(channel) + "失败：" + channelDetail;
				continue;
			}
			_preferred = channel;
			bool safe = VerifyReleased(channel, cpuTempC, gpuTempC, out string verifyDetail, out int minRpm, out int maxRpm, out double waitedSeconds);
			detail = "通道=" + ChannelName(channel) + " · " + channelDetail
				+ " · 13=" + (minRpm < 0 ? "读不到" : minRpm + "~" + maxRpm + " RPM")
				+ "（等待 " + waitedSeconds.ToString("0.0") + "s）· " + verifyDetail;
			Logger.WriteLine("[fan] 交回固件：先清掩码（" + ExtractMaskPart(channelDetail)
				+ "）· 再清目标（" + ExtractTargetPart(channelDetail) + "）· 13="
				+ (minRpm < 0 ? "读不到" : minRpm + "~" + maxRpm + " RPM")
				+ " · 回读确认=" + (safe ? "✅" : "⚠️ 未确认"));
			if (!safe)
			{
				Logger.WriteError("[fan] ⚠️ 交回固件回读未确认（掩码位/目标没清干净 · 观察 "
					+ waitedSeconds.ToString("0.0") + "s）⇒ 恢复接管（回写原 0xB20/目标）");
				bool recovered = false;
				try
				{
					if (preFan1 >= 0 && preFan2 >= 0)
					{
						bool t = TrySetFanTargets(preFan1, preFan2, out _);
						bool m = TrySetManualMask((preMask & 0x02) != 0, (preMask & 0x08) != 0, out _);
						recovered = t && m;
					}
				}
				catch
				{
				}
				detail += " · 低转速保护恢复=" + recovered;
			}
			return safe;
		}
		return false;
	}

	/// <summary>⭐ 交回后校验：掩码位已清 + 目标 0 + WMI 13 观察 ≤3 s；低于 800 RPM ⇒ 不安全 ✅。</summary>
	private static bool VerifyReleased(FanWriteChannel channel, int cpuTempC, int gpuTempC, out string verifyDetail, out int minRpm, out int maxRpm, out double waitedSeconds)
	{
		minRpm = -1;
		maxRpm = -1;
		bool maskClear = false;
		bool targetsZero = false;
		if (channel == FanWriteChannel.VendorEc)
		{
			maskClear = BaolongdaVendorEc.TryReadByte(0xB20, out byte vm) && (vm & 0x0A) == 0;
			bool vf1 = BaolongdaVendorEc.TryReadByte(0xC83C, out byte va);
			bool vf2 = BaolongdaVendorEc.TryReadByte(0xC83D, out byte vb);
			targetsZero = vf1 && vf2 && va == 0 && vb == 0;
		}
		else if (channel == FanWriteChannel.AcpiEc)
		{
			maskClear = EcPortIo.TryReadByteStable(0xB20, out int m, out _) && (m & 0x0A) == 0;
			bool f1 = EcPortIo.TryReadByteStable(0xC83C, out int a, out _);
			bool f2 = EcPortIo.TryReadByteStable(0xC83D, out int b, out _);
			targetsZero = f1 && f2 && a == 0 && b == 0;
		}
		else
		{
			maskClear = EcLpcIo.TryReadByteStable(0xB20, out int m, out _) && (m & 0x0A) == 0;
			bool f1 = EcLpcIo.TryReadByteStable(0xC83C, out int a, out _);
			bool f2 = EcLpcIo.TryReadByteStable(0xC83D, out int b, out _);
			targetsZero = f1 && f2 && a == 0 && b == 0;
		}

		long start = Environment.TickCount64;
		bool rpmReadOk = false;
		int lowSamples = 0;
		int lastRpm = -1;
		// 转速只作诊断：交回是否成立由掩码位/目标回读判定。
		while (Environment.TickCount64 - start <= HealthyConfirmWindowMs)
		{
			int rpm = ReadWmiRpmForRelease();
			if (rpm >= 0)
			{
				rpmReadOk = true;
				if (minRpm < 0 || rpm < minRpm)
				{
					minRpm = rpm;
				}
				if (rpm > maxRpm)
				{
					maxRpm = rpm;
				}
				lastRpm = rpm;
				if (rpm < IdleFloorRpm)
				{
					lowSamples++;
				}
			}
			Thread.Sleep(500);
		}
		waitedSeconds = (Environment.TickCount64 - start) / 1000.0;
		// 交回成立与否只看 EC 侧状态：掩码位已清 + 目标归零 ⇒ 风扇已归 EC 管。
		// 此后的转速由 EC 自己的策略决定（低温本来就会降到怠速以下甚至停转），不再据此抢回控制权。
		verifyDetail = "掩码位已清=" + maskClear + " · 目标0=" + targetsZero
			+ " · 13=" + (rpmReadOk
				? minRpm + "~" + maxRpm + " RPM（低于怠速采样 " + lowSamples + " 次，归 EC 策略）"
				: "读不到")
			+ JudgeZeroSpin(lastRpm, cpuTempC, gpuTempC);
		return maskClear && targetsZero;
	}

	/// <summary>
	/// 零转速定性：同一个“0 RPM”既可能是 EC 按低温正常停转，也可能是没被接住或堵转。
	/// CPU 与 GPU 都不高于 <see cref="LowTempIdleC" /> ⇒ 低温停转（EC 的既定策略，不必干预）；
	/// 任一温度不低于 <see cref="HotNoSpinC" /> ⇒ 异常，记 error；温度读不到/中间区间只记录不下结论。
	/// </summary>
	private static string JudgeZeroSpin(int lastRpm, int cpuTempC, int gpuTempC)
	{
		if (lastRpm != 0)
		{
			return "";
		}
		bool cpuKnown = cpuTempC > 0;
		bool gpuKnown = gpuTempC > 0;
		int hottest = Math.Max(cpuKnown ? cpuTempC : -1, gpuKnown ? gpuTempC : -1);
		string tempText = "CPU " + (cpuKnown ? cpuTempC + "℃" : "读不到")
			+ " / GPU " + (gpuKnown ? gpuTempC + "℃" : "读不到");
		bool allCool = cpuKnown && gpuKnown
			? (cpuTempC <= LowTempIdleC && gpuTempC <= LowTempIdleC)
			: (hottest >= 0 && hottest <= LowTempIdleC);
		if (allCool)
		{
			return " · 零转速＝低温停转（" + tempText + "，EC 正常策略）";
		}
		if (hottest >= HotNoSpinC)
		{
			Logger.WriteError("[fan] ⚠️ 交回后零转速但温度不低（" + tempText + "）⇒ 疑似未接管或风扇堵转；仅记录，⛔ 未自动抢回控制权");
			return " · ⚠️ 零转速但温度 " + hottest + "℃ ⇒ 异常（未接管/堵转）";
		}
		return " · 零转速 · " + tempText + "（中间区间，仅记录）";
	}

	/// <summary>
	/// 交回前的预热：掩码仍置位（我们正接管）且转速低于 1200 RPM 时，先写一个 50% 目标把风扇带起来，
	/// 保持 PreSpinHoldMs 后再走交回流程。掩码本来就没置位（风扇一直在 EC 手上）⇒ 一个字节都不写。
	/// </summary>
	private static void PreSpinBeforeRelease(int preMask)
	{
		try
		{
			if (preMask < 0 || (preMask & 0x0A) == 0)
			{
				return;
			}
			int rpm = ReadWmiRpmForRelease();
			if (rpm < 0 || rpm >= PreSpinBelowRpm)
			{
				return;
			}
			int units = PreSpinPercent * 68 / 100;
			if (!TrySetFanTargetsCore(units, units, out string spinDetail))
			{
				Logger.WriteLine("[fan] 交回前预热跳过：写目标失败（" + spinDetail + "）");
				return;
			}
			Logger.WriteLine("[fan] 交回前预热：13=" + rpm + " RPM ⇒ 提到 " + PreSpinPercent + "% 保持气流，" + PreSpinHoldMs + "ms 后再交回（" + spinDetail + "）");
			Thread.Sleep(PreSpinHoldMs);
		}
		catch (Exception ex)
		{
			Logger.WriteLine("[fan] 交回前预热失败（已忽略，继续交回）：" + ex.Message);
		}
	}

	private static int ReadWmiRpmForRelease()
	{
		try
		{
			using BaolongdaWmi wmi = BaolongdaWmi.TryCreate();
			if (wmi != null && wmi.TryReadFanSpeeds(out BaolongdaWmi.FanSpeeds speeds))
			{
				return Math.Max(speeds.First, speeds.Second);
			}
		}
		catch
		{
		}
		return -1;
	}

	private static string ExtractMaskPart(string channelDetail)
	{
		int idx = channelDetail.IndexOf("先清掩码", StringComparison.Ordinal);
		if (idx < 0)
		{
			return channelDetail;
		}
		int end = channelDetail.IndexOf("· 再清目标", idx, StringComparison.Ordinal);
		return end < 0 ? channelDetail.Substring(idx) : channelDetail.Substring(idx, end - idx).TrimEnd(' ');
	}

	private static string ExtractTargetPart(string channelDetail)
	{
		int idx = channelDetail.IndexOf("再清目标", StringComparison.Ordinal);
		return idx < 0 ? channelDetail : channelDetail.Substring(idx);
	}

	/// <summary>⭐ 只读：两路转速字节 0xC834/0xC835（≈RPM/100，稳定读；ACPI 优先）✅。</summary>
	internal static bool TryReadRpmUnits(out int fan1Units, out int fan2Units, out string detail)
	{
		fan1Units = -1;
		fan2Units = -1;
		bool acpi1 = EcPortIo.TryReadByteStable(0xC834, out int a1, out string an1);
		bool acpi2 = EcPortIo.TryReadByteStable(0xC835, out int a2, out string an2);
		if (acpi1 && acpi2)
		{
			fan1Units = a1;
			fan2Units = a2;
			detail = "ACPI 0xC834=0x" + a1.ToString("X2") + "（" + an1 + "）· 0xC835=0x" + a2.ToString("X2") + "（" + an2 + "）";
			return true;
		}
		bool lpc1 = EcLpcIo.TryReadByteStable(0xC834, out int l1, out string ln1);
		bool lpc2 = EcLpcIo.TryReadByteStable(0xC835, out int l2, out string ln2);
		if (lpc1 && lpc2)
		{
			fan1Units = l1;
			fan2Units = l2;
			detail = "LPC 0xC834=0x" + l1.ToString("X2") + "（" + ln1 + "）· 0xC835=0x" + l2.ToString("X2") + "（" + ln2 + "）";
			return true;
		}
		detail = "ACPI 读失败（" + (acpi1 ? "0xC834 ok" : "0xC834 " + an1) + "）· LPC 读失败（" + (lpc1 ? "ok" : ln1) + "）";
		return false;
	}

	private static bool TryWriteTargetsOn(FanWriteChannel channel, int fan1Units, int fan2Units, out string detail)
	{
		detail = "";
		if (channel == FanWriteChannel.VendorEc)
		{
			// 先解锁：身份不符或解锁不成功就不动风扇寄存器（否则写进去也只是存了个值）。
			if (!BaolongdaVendorEc.TryUnlockEcRam(out string unlockNote))
			{
				detail = "厂商EC 解锁失败（" + unlockNote + "）";
				return false;
			}
			bool v1 = BaolongdaVendorEc.TryWriteByte(0xC83C, (byte)fan1Units)
				&& BaolongdaVendorEc.TryReadByte(0xC83C, out byte vb1) && vb1 == (byte)fan1Units;
			bool v2 = BaolongdaVendorEc.TryWriteByte(0xC83D, (byte)fan2Units)
				&& BaolongdaVendorEc.TryReadByte(0xC83D, out byte vb2) && vb2 == (byte)fan2Units;
			detail = "厂商EC 0xC83C=" + fan1Units + "（回读=" + (v1 ? "一致" : "不一致/读不到") + "）"
				+ " · 0xC83D=" + fan2Units + "（回读=" + (v2 ? "一致" : "不一致/读不到") + "）"
				+ " · " + unlockNote;
			return v1 && v2;
		}
		if (channel == FanWriteChannel.AcpiEc)
		{
			bool w1 = EcPortIo.TryWriteAcpiFanTarget(EcPortIo.AcpiFan1TargetAddress, fan1Units, out int rb1);
			string t1 = EcPortIo.LastAcpiWriteTrace;
			bool w2 = EcPortIo.TryWriteAcpiFanTarget(EcPortIo.AcpiFan2TargetAddress, fan2Units, out int rb2);
			string t2 = EcPortIo.LastAcpiWriteTrace;
			detail = "ACPI 0xC83C=" + fan1Units + "（回读 " + (rb1 < 0 ? "读不到" : rb1.ToString()) + " · " + t1 + "）"
				+ " · 0xC83D=" + fan2Units + "（回读 " + (rb2 < 0 ? "读不到" : rb2.ToString()) + " · " + t2 + "）";
			return w1 && w2;
		}
		bool l1 = SmuEcAccess.TryWriteEc(0xC83C, (byte)fan1Units, out string d1);
		bool l2 = SmuEcAccess.TryWriteEc(0xC83D, (byte)fan2Units, out string d2);
		detail = "LPC 0xC83C=" + fan1Units + "（" + d1 + "）· 0xC83D=" + fan2Units + "（" + d2 + "）";
		return l1 && l2;
	}

	/// <summary>两路目标档（0..68）：整段加锁，避免与交回固件/掩码写入交错。</summary>
	internal static bool TrySetFanTargets(int fan1Units, int fan2Units, out string detail)
	{
		lock (WriteGate)
		{
			return TrySetFanTargetsCore(fan1Units, fan2Units, out detail);
		}
	}

	/// <summary>强冷专用：只走厂商驱动 / PawnIO 通道，⛔ 不回退 ACPI-EC（那路写不进去还会假成功）。</summary>
	internal static bool TrySetFanTargetsForBoost(int fan1Units, int fan2Units, out string detail)
	{
		lock (WriteGate)
		{
			return TrySetFanTargetsCore(fan1Units, fan2Units, out detail, allowAcpiEc: false);
		}
	}

	/// <summary>强冷专用掩码写入：同样不回退 ACPI-EC。</summary>
	internal static bool TrySetManualMaskForBoost(bool bit1, bool bit3, out string detail)
	{
		lock (WriteGate)
		{
			return TrySetManualMaskCore(bit1, bit3, out detail, allowAcpiEc: false);
		}
	}

	/// <summary>0xB20 接管掩码：整段加锁。</summary>
	internal static bool TrySetManualMask(bool bit1, bool bit3, out string detail)
	{
		lock (WriteGate)
		{
			return TrySetManualMaskCore(bit1, bit3, out detail);
		}
	}

	/// <summary>交回固件（先清掩码→再清目标）：整段加锁，保证两步之间不会被插写。</summary>
	internal static bool TryReleaseToFirmware(out string detail, int cpuTempC = -1, int gpuTempC = -1)
	{
		lock (WriteGate)
		{
			return TryReleaseToFirmwareCore(out detail, cpuTempC, gpuTempC);
		}
	}
	private static System.Collections.Generic.IEnumerable<FanWriteChannel> Candidates(bool allowAcpiEc = true)
	{
		// 顺序即优先级：宝龙达（蛟龙 30/40 系）优先厂商驱动通道 —— 该机型由本程序自己
		// 创建/启动驱动服务，不需要 PawnIO。厂商通道不可用则回退 PawnIO（LPC 模块）通道，
		// ACPI-EC 排最后：这台模具上它常读不到身份、写进去也留不住，只作最后兜底。
		if (_vendorEnabled)
		{
			if (BaolongdaVendorEc.EnsureReady())
			{
				yield return FanWriteChannel.VendorEc;
			}
			yield return FanWriteChannel.SmuEcLpc;
			if (allowAcpiEc)
			{
				yield return FanWriteChannel.AcpiEc;
			}
			yield break;
		}
		if (_preferred == FanWriteChannel.SmuEcLpc)
		{
			yield return FanWriteChannel.SmuEcLpc;
			yield return FanWriteChannel.AcpiEc;
		}
		else
		{
			yield return FanWriteChannel.AcpiEc;
			yield return FanWriteChannel.SmuEcLpc;
		}
	}

	internal static string ChannelName(FanWriteChannel channel)
	{
		return channel switch
		{
			FanWriteChannel.AcpiEc => "ACPI-EC",
			FanWriteChannel.SmuEcLpc => "SMU-EC(=LPC)",
			FanWriteChannel.VendorEc => "厂商驱动(0x2E/0x2F)",
			_ => "None"
		};
	}
}

/// <summary>
/// ⭐⭐ 闸门配置：`%APPDATA%\MechrevoLite\fan_ec_verified.cfg` ✅。
/// <para>⭐ 默认无文件 ⇒ 未验证 ⇒ UI 隐藏；只有 `--fan-smu-ec-test` 验证通过才写 `fan_ec_verified_30=1`/`_40=1`（或通用 `fan_ec_verified=1`）✅</para>
/// </summary>
internal static class FanEcVerifyStore
{
	// ⭐ 进程内缓存（⭐ 属性/闭环每 tick 都会查闸门 ⇒ ⛔ 不能每次读文件）✅
	private static bool? _cached30;

	private static bool? _cached40;

	private static string ConfigPath()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"MechrevoLite", "fan_ec_verified.cfg");
	}

	/// <summary>⭐ 读“已验证”标记（默认 false；文件不存在/读失败都按 false；进程内缓存）✅</summary>
	internal static bool Load(bool is30Series)
	{
		bool? cached = is30Series ? _cached30 : _cached40;
		if (cached.HasValue)
		{
			return cached.Value;
		}
		bool result = false;
		try
		{
			string path = ConfigPath();
			if (!File.Exists(path))
			{
				result = false;
			}
			else
			{
				foreach (string raw in File.ReadAllLines(path))
				{
					string line = raw.Trim();
					if (line.Length == 0 || line.StartsWith('#'))
					{
						continue;
					}
					int eq = line.IndexOf('=');
					if (eq <= 0)
					{
						continue;
					}
					string key = line.Substring(0, eq).Trim();
					string value = line.Substring(eq + 1).Trim();
					if (value != "1")
					{
						continue;
					}
					if (key == "fan_ec_verified")
					{
						result = true;
						break;
					}
					if (is30Series && key == "fan_ec_verified_30")
					{
						result = true;
						break;
					}
					if (!is30Series && key == "fan_ec_verified_40")
					{
						result = true;
						break;
					}
				}
			}
		}
		catch
		{
			result = false;
		}
		if (is30Series)
		{
			_cached30 = result;
		}
		else
		{
			_cached40 = result;
		}
		return result;
	}

	/// <summary>⭐ 写“已验证”标记（仅验证命令调用；⛔ 产品运行期不写）✅</summary>
	internal static bool Save(bool is30Series, bool verified, out string detail)
	{
		detail = "";
		try
		{
			string path = ConfigPath();
			string dir = Path.GetDirectoryName(path) ?? "";
			if (dir.Length > 0)
			{
				Directory.CreateDirectory(dir);
			}
			string key = is30Series ? "fan_ec_verified_30" : "fan_ec_verified_40";
			File.WriteAllText(path,
				"# L-Mechrevo fan EC verification (written by a verification command only)\n"
				+ key + "=" + (verified ? "1" : "0") + "\n");
			// ⭐ 更新进程内缓存（保存后当前进程立即可见；UI 仍需重启才重新读闸门）✅
			if (is30Series)
			{
				_cached30 = verified;
			}
			else
			{
				_cached40 = verified;
			}
			detail = path + " ⇒ " + key + "=" + (verified ? "1" : "0");
			return true;
		}
		catch (Exception ex)
		{
			detail = "写配置失败：" + ex.GetType().Name + " " + ex.Message;
			return false;
		}
	}

	/// <summary>⭐ 验证标记文件路径（⭐ `--fan-gate-status` 只读展示用）✅</summary>
	internal static string GetConfigPath()
	{
		return ConfigPath();
	}

	/// <summary>⭐ 标记文件是否存在（⭐ 只读）✅</summary>
	internal static bool MarkerExists()
	{
		try
		{
			return File.Exists(ConfigPath());
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// ⭐⭐ 2026-10-18（机主：`--fan-ec-test` 成功也要放行 UI）：⭐ **两个验证命令共用的写标记函数** ✅。
	/// <para>⛔ 只在验证判定 True 时由命令调用；⛔ 产品运行期不写；⭐ 日志固定一行 ✅</para>
	/// </summary>
	internal static bool MarkVerifiedFromTest(bool is30Series, out string detail)
	{
		bool ok = Save(is30Series, verified: true, out detail);
		if (ok)
		{
			Logger.WriteLine("[fan] ✅ 已写入验证标记（重启 LM 后放开风扇曲线/强冷 UI）· " + detail);
		}
		else
		{
			Logger.WriteLine("[fan] ⚠️ 验证标记写入失败（UI 仍保持隐藏）· " + detail);
		}
		return ok;
	}
}
