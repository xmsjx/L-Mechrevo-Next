using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MechrevoLite.Helpers;

namespace MechrevoLite.Update;

internal static class UpdateHttp
{
	internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10L);

	internal static readonly HttpClient Check = Create(UpdateChecker.HttpTimeout);

	internal static readonly HttpClient Download = Create(Timeout.InfiniteTimeSpan);

	internal static bool IsRedirect(HttpStatusCode status)
	{
		if (status >= HttpStatusCode.MultipleChoices)
		{
			return status < HttpStatusCode.BadRequest;
		}
		return false;
	}

	internal static string RedirectRefusedReason(HttpStatusCode status)
	{
		return $"HTTP {(int)status}：拒绝跟随重定向";
	}

	/// <summary>下载 GitHub Release 资产时跟随 302，但只允许跳到允许列表内 HTTPS 主机。</summary>
	internal static async Task<HttpResponseMessage> DownloadFollowingRedirectsAsync(Uri uri, CancellationToken ct)
	{
		const int maxRedirects = 5;
		Uri current = uri;
		for (int i = 0; i <= maxRedirects; i++)
		{
			using HttpRequestMessage request = new(HttpMethod.Get, current);
			HttpResponseMessage response = await Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			if (!IsRedirect(response.StatusCode))
			{
				return response;
			}
			Uri? next = null;
			try
			{
				string? location = response.Headers.Location?.ToString();
				if (!string.IsNullOrWhiteSpace(location))
				{
					next = new Uri(current, location);
				}
			}
			catch
			{
			}
			response.Dispose();
			if (next == null)
			{
				throw new HttpRequestException("重定向响应缺少有效的 Location");
			}
			if (!UpdatePolicy.IsAcceptableDownloadUri(next) || !UpdatePolicy.IsAllowedDownloadHost(next))
			{
				throw new HttpRequestException("重定向目标不在允许列表内：" + next.Host);
			}
			current = next;
		}
		throw new HttpRequestException("重定向次数过多");
	}

	private static HttpClient Create(TimeSpan timeout)
	{
		HttpClient httpClient = new HttpClient(new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			ConnectCallback = ConnectWithDnsFallbackAsync
		});
		httpClient.Timeout = timeout;
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("L-Mechrevo/" + Program.ReleaseLabel);
		return httpClient;
	}

	/// <summary>先按常规方式连接；域名解析不到时改用备用 DNS 查地址再连，TLS 仍按原域名校验。</summary>
	private static async ValueTask<Stream> ConnectWithDnsFallbackAsync(SocketsHttpConnectionContext context, CancellationToken ct)
	{
		DnsEndPoint endPoint = context.DnsEndPoint;
		Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true
		};
		try
		{
			await socket.ConnectAsync(endPoint, ct).ConfigureAwait(continueOnCapturedContext: false);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData || ex.SocketErrorCode == SocketError.TryAgain)
		{
			socket.Dispose();
		}
		catch
		{
			socket.Dispose();
			throw;
		}
		IPAddress? address = await DohResolver.ResolveAsync(endPoint.Host, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (address == null)
		{
			throw new HttpRequestException("域名解析失败：" + endPoint.Host);
		}
		Logger.WriteLine("更新域名系统解析失败，改用备用 DNS 解析 " + endPoint.Host + "=" + address);
		Socket byAddress = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
		{
			NoDelay = true
		};
		try
		{
			await byAddress.ConnectAsync(new IPEndPoint(address, endPoint.Port), ct).ConfigureAwait(continueOnCapturedContext: false);
			return new NetworkStream(byAddress, ownsSocket: true);
		}
		catch
		{
			byAddress.Dispose();
			throw;
		}
	}
}
