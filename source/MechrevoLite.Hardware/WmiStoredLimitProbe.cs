using System;
using System.Management;

namespace MechrevoLite.Hardware;

/// <summary>厂商 WMI EC mailbox（AcpiTest_MULong / GetSetULong）存储上限写入。</summary>
internal static class WmiStoredLimitProbe
{
	private const ulong ReadFlag = 0x0100UL << 32;

	private const uint ErrorSentinel = 0xFEFEFEFE;

	/// <summary>
	/// B 路写入（正式路径）：写 0x087F 存储上限并回读校验；成功返回 true。
	/// <para>percent ≥ 100 时写 0（= 取消存储上限/恢复默认充电）；40..99 写该值。</para>
	/// <para>只写 0x087F 一个地址，不碰门控/ROMID；失败不重试。</para>
	/// </summary>
	internal static bool TryWriteStoredLimit(int percent, out string detail)
	{
		detail = string.Empty;
		try
		{
			if (percent < 40 || percent > 100)
			{
				detail = "拒绝：percent 必须在 40..100（收到 " + percent + "）";
				return false;
			}
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM AcpiTest_MULong");
			ManagementObject instance = null;
			using (ManagementObjectCollection results = searcher.Get())
			{
				foreach (ManagementObject item in results)
				{
					instance = item;
					break;
				}
			}
			if (instance == null)
			{
				detail = "AcpiTest_MULong 类不存在";
				return false;
			}
			uint? probe = Read(instance, 0x0740);
			if (!probe.HasValue || probe.Value == ErrorSentinel)
			{
				detail = "mailbox 握手失败";
				return false;
			}
			int value = (percent >= 100) ? 0 : percent;
			Write(instance, 0x087F, (byte)value);
			uint? verify = Read(instance, 0x087F);
			int stored = verify.HasValue ? (int)(verify.Value & 0x7F) : -1;
			detail = "写 0x087F=" + value + " · 回读=" + stored;
			if (stored != value)
			{
				detail += " ⇒ 不一致";
				return false;
			}
			Logger.WriteError("[charge-limit] B 路写入存储上限成功（" + detail + "，项目号 0x" + (probe.Value & 0xFF).ToString("X2") + "）");
			return true;
		}
		catch (Exception ex)
		{
			detail = "异常：" + ex.GetType().Name + " " + ex.Message;
			return false;
		}
	}


	private static uint? Write(ManagementObject instance, ushort address, byte value)
	{
		try
		{
			ulong data = ((ulong)value << 16) | address;
			using ManagementBaseObject inParams = instance.GetMethodParameters("GetSetULong");
			inParams["Data"] = data;
			using ManagementBaseObject outParams = instance.InvokeMethod("GetSetULong", inParams, null);
			if (outParams == null || outParams["Return"] == null)
			{
				return null;
			}
			return Convert.ToUInt32(outParams["Return"]);
		}
		catch (Exception ex)
		{
			Logger.WriteError("[charge-limit] WMI mailbox 写 0x" + address.ToString("X4") + " 失败：" + ex.Message);
			return null;
		}
	}

	private static uint? Read(ManagementObject instance, ushort address)
	{
		try
		{
			ulong data = ReadFlag | address;
			using ManagementBaseObject inParams = instance.GetMethodParameters("GetSetULong");
			inParams["Data"] = data;
			using ManagementBaseObject outParams = instance.InvokeMethod("GetSetULong", inParams, null);
			if (outParams == null || outParams["Return"] == null)
			{
				return null;
			}
			return Convert.ToUInt32(outParams["Return"]);
		}
		catch (Exception ex)
		{
			Logger.WriteError("[charge-limit] WMI mailbox 读 0x" + address.ToString("X4") + " 失败：" + ex.Message);
			return null;
		}
	}
}

