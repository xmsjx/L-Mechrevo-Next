// EC 通道抽象：优先走 \.\ACPIDriver 的 IOCTL 通道，没有驱动时才回退 I2EC（inpoutx64）。
using System;
using System.Collections.Generic;
using System.Threading;

namespace MechrevoLite.Hardware;

/// <summary>EC 写入步骤（地址 / 值 / 可选延后）。</summary>
internal readonly record struct EcWriteStep(int Address, int Value, int DelayAfterMs = 0);

/// <summary>EC 写入后回读校验（地址 / 期望值）。</summary>
internal readonly record struct EcVerifyStep(int Address, int Expected);

/// <summary>EC 读写通道。</summary>
internal enum EcTransport
{
	None,
	AcpiDriver,
	I2ec
}

/// <summary>EC 字节读写：ACPIDriver 优先，I2EC 兜底。</summary>
internal static class EcIo
{
	internal static EcTransport Active { get; private set; } = EcTransport.None;

	internal static string Detail { get; private set; } = "未选择通道";

	/// <summary>选一次通道：ACPIDriver 打得开就用它，否则退 I2EC。</summary>
	internal static bool TrySelect(out string detail)
	{
		if (AcpiDriverEc.IsAvailable())
		{
			Active = EcTransport.AcpiDriver;
			Detail = "ACPIDriver（\\.\\ACPIDriver IOCTL）";
			detail = Detail;
			return true;
		}
		if (I2ecInpOut.IsAvailable)
		{
			Active = EcTransport.I2ec;
			Detail = "I2EC（inpoutx64）";
			detail = Detail;
			return true;
		}
		Active = EcTransport.None;
		Detail = "ACPIDriver 打不开且 inpoutx64 不可用";
		detail = Detail;
		return false;
	}

	internal static bool TryReadByte(int address, out int value, out string detail)
	{
		value = -1;
		if (Active == EcTransport.AcpiDriver)
		{
			if (AcpiDriverEc.TryReadByte(address, out byte raw))
			{
				value = raw;
				detail = "";
				return true;
			}
			detail = "ACPIDriver 读 0x" + address.ToString("X3") + " 失败";
			return false;
		}
		if (Active == EcTransport.I2ec)
		{
			return I2ecInpOut.TryReadEcByte(address, out value, out detail);
		}
		detail = "没有可用 EC 通道";
		return false;
	}

	internal static bool TryWriteByte(int address, int value, out string detail)
	{
		if (Active == EcTransport.AcpiDriver)
		{
			if (AcpiDriverEc.TryWriteByte(address, value))
			{
				detail = "";
				return true;
			}
			detail = "ACPIDriver 写 0x" + address.ToString("X3") + " 失败";
			return false;
		}
		if (Active == EcTransport.I2ec)
		{
			return I2ecInpOut.TryWriteEcByte(address, value, out detail);
		}
		detail = "没有可用 EC 通道";
		return false;
	}

	/// <summary>按步骤批量写，可选逐条回读校验。</summary>
	internal static bool TryWriteSequence(IReadOnlyList<EcWriteStep> steps, IReadOnlyList<EcVerifyStep> verifySteps, out string detail)
	{
		detail = "";
		if (Active == EcTransport.I2ec)
		{
			I2ecInpOut.I2ecWriteStep[] mapped = new I2ecInpOut.I2ecWriteStep[steps.Count];
			for (int i = 0; i < steps.Count; i++)
			{
				mapped[i] = new I2ecInpOut.I2ecWriteStep(steps[i].Address, steps[i].Value, steps[i].DelayAfterMs);
			}
			I2ecInpOut.I2ecVerifyStep[] mappedVerify = new I2ecInpOut.I2ecVerifyStep[verifySteps.Count];
			for (int i = 0; i < verifySteps.Count; i++)
			{
				mappedVerify[i] = new I2ecInpOut.I2ecVerifyStep(verifySteps[i].Address, verifySteps[i].Expected);
			}
			return I2ecInpOut.TryWriteEcSequence(mapped, mappedVerify, out detail);
		}
		if (Active != EcTransport.AcpiDriver)
		{
			detail = "没有可用 EC 通道";
			return false;
		}
		for (int i = 0; i < steps.Count; i++)
		{
			EcWriteStep step = steps[i];
			if (!AcpiDriverEc.TryWriteByte(step.Address, step.Value))
			{
				detail = "ACPIDriver 写 0x" + step.Address.ToString("X3") + " 失败（第 " + (i + 1) + " 步）";
				return false;
			}
			if (step.DelayAfterMs > 0)
			{
				Thread.Sleep(step.DelayAfterMs);
			}
		}
		for (int i = 0; i < verifySteps.Count; i++)
		{
			EcVerifyStep check = verifySteps[i];
			if (!AcpiDriverEc.TryReadByte(check.Address, out byte got))
			{
				detail = "ACPIDriver 回读 0x" + check.Address.ToString("X3") + " 失败";
				return false;
			}
			if (got != check.Expected)
			{
				detail = "回读不一致：0x" + check.Address.ToString("X3") + " 实际 0x" + got.ToString("X2") + " 期望 0x" + check.Expected.ToString("X2");
				return false;
			}
		}
		return true;
	}

	internal static void Reset()
	{
		Active = EcTransport.None;
		Detail = "未选择通道";
	}
}
