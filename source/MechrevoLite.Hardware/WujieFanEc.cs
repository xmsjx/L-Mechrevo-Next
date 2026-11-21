using System;
using System.Text;

namespace MechrevoLite.Hardware;

/// <summary>
/// 2026-10-22（无界风扇曲线）：无界（WUJIE14XA）EC 风扇占空比直写后端 —— 走 PawnIO ACPI EC 0x92/0x93 通道，
/// 与宝龙达写风扇目标同一条路（不需要 inpoutx64）。
///
/// <para>依据：无界 EC 的占空比目标为主扇 <c>0x1804</c>、副扇 <c>0x1809</c>（0..200 = 0..100%）；
/// 无界转速 <c>0xC834/0xC835</c> 已用同一条 ACPI EC 0x92 通道读取（见 <see cref="MechrevoHw.TryReadWujieEcFanSpeeds" />）。
/// 温度 <c>0x043E</c>，转速 <c>0x0464/0x0465</c>、<c>0x046C/0x046D</c>（大端 16 位）。</para>
///
/// <para>⚠️ 本类当前提供：只读状态（诊断用）+ **单次**写占空比（带 0..200 白名单与回读校验）。
/// 25Hz 持续写入的曲线控制器不在本类（下一步接入）；任何写失败都会返回 false，由调用方交还 EC。</para>
/// </summary>
internal static class WujieFanEc
{
	internal const int RegisterMode = 0x200D;

	internal const int RegisterDutyFan1 = 0x1804;

	internal const int RegisterDutyFan2 = 0x1809;

	internal const int RegisterCpuTemp = 0x043E;

	internal const int RegisterFan1RpmHigh = 0x0464;

	internal const int RegisterFan2RpmHigh = 0x046C;

	// 曲线表：温度轴 0x0F00(CPU 升)/0x0F10(CPU 降)/0x0F30(GPU 升)/0x0F40(GPU 降)，
	// 占空比表 0x0F20(主扇 16 槽)/0x0F50(副扇 13 槽)，其后是最大占空比与控制量。
	// 0x07C5 bit7 = 主副扇输出隔离；按“出厂基线绝对写”避免 read-modify-write 撕裂事故。
	internal const int RegisterFanTableFan1 = 0x0F20;

	internal const int RegisterFanTableFan2 = 0x0F50;

	/// <summary>副扇占空比表只有 13 槽（0x0F50..0x0F5C），其后三个字节是控制量。</summary>
	internal const int FanTableGpuDutyLength = 13;

	/// <summary>最大占空比阈值 1（0x0F5D）。</summary>
	internal const int RegisterFanTableMax1 = 0x0F5D;

	/// <summary>最大占空比阈值 2（0x0F5E）。</summary>
	internal const int RegisterFanTableMax2 = 0x0F5E;

	/// <summary>表控制器（0x0F5F），写低于下限会让 EC 判为控制器复位。</summary>
	internal const int RegisterFanTableCtrl = 0x0F5F;

	/// <summary>表控制器下限（0xA0）。</summary>
	internal const int FanTableCtrlFloor = 160;

	/// <summary>降档温度相对升档温度的回差（℃），避免阈值附近反复横跳。</summary>
	internal const int TableDownHysteresisC = 3;

	/// <summary>占空比表量程上限（与实算占空比同口径）。</summary>
	internal const int FanTableDutyMax = 200;

	internal const int RegisterFanTableLatch = 0x07C6;

	/// <summary>表提交值：写 0x07C6 = 0x04 即提交并锁存（不是位索引）。</summary>
	internal const int TableLatchBit = 0x04;

	internal const int RegisterFanIsolate = 0x07C5;

	/// <summary>CPU 升档温度表基址（16 点）。</summary>
	internal const int RegisterCpuUpT = 0x0F00;

	/// <summary>CPU 降档温度表基址。</summary>
	internal const int RegisterCpuDownT = 0x0F10;

	/// <summary>GPU 升档温度表基址。</summary>
	internal const int RegisterGpuUpT = 0x0F30;

	/// <summary>GPU 降档温度表基址。</summary>
	internal const int RegisterGpuDownT = 0x0F40;

	/// <summary>自定义模式标志。</summary>
	internal const int RegisterCustomMode = 0x0706;

	/// <summary>转速爬升速率。</summary>
	internal const int RegisterRampRate = 0x0787;

	/// <summary>主扇软启动占空比槽（停转重启握手用）。</summary>
	internal const int RegisterFanSoftStart1 = 0x0F21;

	/// <summary>副扇软启动占空比槽。</summary>
	internal const int RegisterFanSoftStart2 = 0x0F51;

	/// <summary>主机→EC 风扇重启握手寄存器。</summary>
	internal const int RegisterRestartHandshake = 0x07A3;

	/// <summary>握手位（bit6）。</summary>
	internal const int RestartHandshakeBit = 0x40;

	/// <summary>主扇占空比表槽数（0x0F20..0x0F2F，与 CPU 温度轴 16 点对齐）。</summary>
	internal const int FanTableActiveLength = 16;

	/// <summary>表后端心跳周期（ms）：先读目标，只有 EC 真的偏离（&gt; <see cref="TableDriftToleranceRaw" />）才重锁。</summary>
	internal const int TableHeartbeatMs = 10000;

	/// <summary>心跳允许的目标漂移（raw，0..200 满量程）。</summary>
	internal const int TableDriftToleranceRaw = 8;

	/// <summary>锁存脉冲间隔（ms）：先写 <c>base &amp; ~0x40</c> → 50ms → <c>base | 0x40</c>。</summary>
	internal const int TableLatchPulseMs = 50;

	/// <summary>低温交还后重新接管温度（℃）：45℃ 交还、48℃ 接管（3℃ 回差，避免阈值附近反复横跳）。</summary>
	internal const int HandoffResumeTemperatureC = 48;

	/// <summary>直写后端（direct）的副扇下限（%）：低于该值副扇可能被判堵转闭锁。</summary>
	internal const int FanFloorPercent = 20;

	/// <summary>table 后端下限（%）：旧版 WUJIE 实机验证 5% 即可，EC 自身约 1745 RPM 地板。</summary>
	internal const int TableFanFloorPercent = 5;

	/// <summary>是否无界（APU 无独显）机型；诊断/UI 只在这个平台启用。</summary>
	internal static bool IsWujiePlatform()
	{
		try
		{
			return Program.hw?.WujieSmuApu == true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>只读：模式位、双扇占空比、CPU 温度、双扇转速。</summary>
	internal static bool TryReadState(out byte mode, out int duty1, out int duty2, out int tempC, out int fan1Rpm, out int fan2Rpm, out string detail)
	{
		mode = 0;
		duty1 = -1;
		duty2 = -1;
		tempC = -1;
		fan1Rpm = -1;
		fan2Rpm = -1;
		try
		{
			// 2026-10-22：控制器正在走 I2EC 时，ACPI-EC 0x92 可能被占用而读失败；
			// 诊断优先用 I2EC（与写同一通道），不可用时再回退 ACPI-EC。
			bool okMode = ReadByteAny(RegisterMode, out int modeValue, out string modeNote);
			bool okDuty1 = ReadByteAny(RegisterDutyFan1, out int duty1Value, out string duty1Note);
			bool okDuty2 = ReadByteAny(RegisterDutyFan2, out int duty2Value, out string duty2Note);
			bool okTemp = ReadByteAny(RegisterCpuTemp, out int tempValue, out _);
			int[] rpm1Bytes = new int[2];
			int[] rpm2Bytes = new int[2];
			bool okRpm1 = ReadByteAny(RegisterFan1RpmHigh, out rpm1Bytes[0], out _) && ReadByteAny(RegisterFan1RpmHigh + 1, out rpm1Bytes[1], out _);
			bool okRpm2 = ReadByteAny(RegisterFan2RpmHigh, out rpm2Bytes[0], out _) && ReadByteAny(RegisterFan2RpmHigh + 1, out rpm2Bytes[1], out _);
			mode = okMode ? (byte)modeValue : (byte)0;
			duty1 = okDuty1 && duty1Value >= 0 ? duty1Value : -1;
			duty2 = okDuty2 && duty2Value >= 0 ? duty2Value : -1;
			tempC = okTemp && tempValue >= 0 && tempValue < 128 ? tempValue : -1;
			if (okRpm1 && rpm1Bytes.Length == 2)
			{
				fan1Rpm = ((rpm1Bytes[0] & 0xFF) << 8) | (rpm1Bytes[1] & 0xFF);
			}
			if (okRpm2 && rpm2Bytes.Length == 2)
			{
				fan2Rpm = ((rpm2Bytes[0] & 0xFF) << 8) | (rpm2Bytes[1] & 0xFF);
			}
			detail = "mode=" + (okMode ? "0x" + modeValue.ToString("X2") : "读取失败(" + modeNote + ")")
				+ " · duty1=" + (okDuty1 ? duty1Value.ToString() : "读取失败(" + duty1Note + ")")
				+ " · duty2=" + (okDuty2 ? duty2Value.ToString() : "读取失败(" + duty2Note + ")");
			return okMode && okDuty1 && okDuty2;
		}
		catch (Exception ex)
		{
			detail = ex.GetType().Name + " " + ex.Message;
			return false;
		}
	}

	/// <summary>读一个 EC 字节（ACPIDriver 优先，其次 I2EC）：供诊断与状态读取使用。</summary>
	private static bool ReadByteAny(int address, out int value, out string note)
	{
		if (EcIo.Active == EcTransport.None)
		{
			EcIo.TrySelect(out _);
		}
		if (EcIo.TryReadByte(address, out value, out string ecNote))
		{
			note = EcIo.Detail;
			return true;
		}
		if (EcPortIo.TryReadByteStable(address, out value, out string portNote))
		{
			note = "ACPI-EC";
			return true;
		}
		note = ecNote + "；ACPI-EC：" + portNote;
		return false;
	}

	/// <summary>2026-10-22：宝龙达同款 LPC EC-RAM 通道只读探测（PawnIO LpcIO）。返回一段诊断文本。</summary>
	private static string ProbeLpcPath()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("[无界风扇 · LPC EC-RAM 只读探测（宝龙达同款通道，仅读）]");
		try
		{
			sb.AppendLine("LPC EC-RAM 基准（温度 0x043E / 转速 0x0464）："
				+ DescribeLpc(0x043E) + " · " + DescribeLpc(0x0464));
			sb.AppendLine("无界占空比（0x1800/0x1801/0x1804/0x1806/0x1809）："
				+ DescribeLpc(0x1800) + " · " + DescribeLpc(0x1801) + " · " + DescribeLpc(0x1804) + " · " + DescribeLpc(0x1806) + " · " + DescribeLpc(0x1809));
			sb.AppendLine("模式/转速（0x200D / 0xC834 / 0xC835）：" + DescribeLpc(0x200D) + " · " + DescribeLpc(0xC834) + " · " + DescribeLpc(0xC835));
			bool dutyOk = EcLpcIo.TryReadByte(0x1804, out int duty1) && duty1 >= 0 && duty1 <= 200
				&& EcLpcIo.TryReadByte(0x1809, out int duty2) && duty2 >= 0 && duty2 <= 200;
			bool tempOk = EcLpcIo.TryReadByte(0x043E, out int temp) && temp > 0 && temp < 120;
			sb.AppendLine(dutyOk && tempOk
				? "结论：LPC 通道可读无界占空比/温度 ⇒ 写入也可尝试改走 PawnIO（不依赖 inpoutx64）。"
				: "结论：LPC 通道读不到（或值不合理）⇒ 写入仍需 inpoutx64（I2EC）或自研 PawnIO I2EC 模块。");
		}
		catch (Exception ex)
		{
			sb.AppendLine("LPC 探测异常（已忽略）：" + ex.GetType().Name + " " + ex.Message);
		}
		return sb.ToString().TrimEnd();
	}

	private static string DescribeLpc(int address)
	{
		return EcLpcIo.TryReadByte(address, out int value)
			? "0x" + address.ToString("X4") + "=0x" + value.ToString("X2")
			: "0x" + address.ToString("X4") + "=读不到";
	}

	/// <summary>诊断包用：无界风扇 EC 状态（只读）。</summary>
	internal static string Describe()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("[无界风扇 EC（只读探针）]");
		try
		{
			sb.AppendLine("平台识别：" + (IsWujiePlatform() ? "命中（WujieSmuApu）" : "未命中（本机不是无界 APU，跳过）"));
			if (!IsWujiePlatform())
			{
				return sb.ToString();
			}
			_ = EcIo.TrySelect(out string ecChannelDetail);
			sb.AppendLine("EC 通道：" + ecChannelDetail + " · ACPIDriver=" + (AcpiDriverEc.IsAvailable() ? "在" : "不在") + " · I2EC=" + (I2ecInpOut.IsAvailable ? "可用" : "不可用"));
			if (!TryReadState(out byte mode, out int duty1, out int duty2, out int tempC, out int fan1Rpm, out int fan2Rpm, out string detail))
			{
				sb.AppendLine("读取失败：" + detail);
				sb.AppendLine("结论：通道/地址不可用（未写入任何占空比；不据此判定不支持）");
				return sb.ToString();
			}
			sb.AppendLine($"模式 0x200D=0x{mode:X2}（bit0/1={(((mode & 0x03) == 0x03) ? "读写模式" : "非读写模式")}）");
			sb.AppendLine($"占空比：主扇={duty1}/200 · 副扇={duty2}/200 · CPU={tempC}℃ · 主扇={fan1Rpm}rpm · 副扇={fan2Rpm}rpm");
			sb.AppendLine("参考语义：占空比 0..200 → 0..100%；低于 20% 副扇可能被判堵转闭锁；"
				+ "写曲线只走 table 后端（0x0F20/0x0F50 前 8 字节 + 0x07C6 写 0x04 锁存 + 0x07C5 bit7 隔离），写表失败即交还 EC 原生曲线。");
			sb.AppendLine("边界：本段只读。");
			// 2026-10-22（机主：无界能不能走宝龙达那条 PawnIO 路子）：只读探测 LPC EC-RAM 通道是否能摸到无界的
			// 温度/转速/占空比地址。若能读到合理值，就可以把写入也挪到 PawnIO（不再依赖 inpoutx64）。
			sb.AppendLine(ProbeLpcPath());
		}
		catch (Exception ex)
		{
			sb.AppendLine("风扇 EC 探针异常（已忽略）：" + ex.GetType().Name + " " + ex.Message);
		}
		sb.AppendLine();
		return sb.ToString();
	}
}
