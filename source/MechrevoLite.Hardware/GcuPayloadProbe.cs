using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MechrevoLite.Hardware;

/// <summary>
/// 后端控制组件的"可执行载荷"是否真的落地。
/// <para>⭐ 服务键存在 ≠ 载荷可用：服务登记残留时，内核对象还在、exe 已被删/挪走，
/// 此时写入方永远等不到应答。判定只看服务键里的 ImagePath 指向的文件是否存在。</para>
/// <para>⚠️ 解析不到 ImagePath（未登记/无权限）时按"存在"处理：宁可维持现状，也不误降级。</para>
/// </summary>
internal static class GcuPayloadProbe
{
	/// <summary>探测控制组件载荷。</summary>
	/// <param name="present">载荷是否可用（解析不到 ⇒ true）。</param>
	/// <param name="detail">给日志用的说明。</param>
	internal static bool TryDetect(out bool present, out string detail)
	{
		present = true;
		detail = "";
		try
		{
			foreach (string name in new[] { "GCUBridge", "GCUService" })
			{
				using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
				if (key == null)
				{
					continue;
				}
				if (key.GetValue("ImagePath") is not string raw || string.IsNullOrWhiteSpace(raw))
				{
					detail = name + " 服务键无 ImagePath（按存在处理）";
					return true;
				}
				string path = ResolvePath(raw);
				if (path.Length == 0)
				{
					detail = name + " ImagePath 无法解析（按存在处理）：" + raw;
					return true;
				}
				if (System.IO.File.Exists(path))
				{
					detail = name + " 载荷存在：" + path;
					return true;
				}
				present = false;
				detail = name + " 载荷缺失：" + path;
				return false;
			}
			detail = "没有 GCUBridge/GCUService 服务键";
			return true;
		}
		catch (Exception ex)
		{
			detail = "载荷探测失败（按存在处理）：" + ex.Message;
			return true;
		}
	}

    /// <summary>本地控制端口的连通性（只做一次 TCP 连接，200 ms 超时，不发任何数据）。</summary>
    internal static bool IsPortReachable(string host, int port, int timeoutMs = 200)
    {
        // ⚠️ 连接过程必须全程被观察：超时放弃时若留下带异常的任务，终结器线程会把它重抛成崩溃记录。
        try
        {
            Task<bool> probe = ProbePortAsync(host, port, timeoutMs);
            if (!probe.Wait(timeoutMs + 100))
            {
                return false;
            }
            return probe.Result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>只做一次连接尝试；任何异常都在内部转成 false，保证任务不携带未观察异常。</summary>
    private static async Task<bool> ProbePortAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient();
            using CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

	/// <summary>把 ImagePath 里的引号/环境变量/参数去掉，得到可判断存在的文件路径。</summary>
	private static string ResolvePath(string imagePath)
	{
		string text = imagePath.Trim();
		if (text.StartsWith('"'))
		{
			int end = text.IndexOf('"', 1);
			text = end > 0 ? text.Substring(1, end - 1) : text.Trim('"');
		}
		else
		{
			int space = text.IndexOf(' ');
			if (space > 0)
			{
				text = text.Substring(0, space);
			}
		}
		try
		{
			text = Environment.ExpandEnvironmentVariables(text);
		}
		catch
		{
		}
		if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
		{
			text = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + text.Substring(@"\SystemRoot".Length);
		}
		else if (text.StartsWith(@"\??\", StringComparison.Ordinal))
		{
			text = text.Substring(4);
		}
		return text;
	}
}
