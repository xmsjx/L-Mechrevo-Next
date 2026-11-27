using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MechrevoLite.Helpers;

/// <summary>系统解析不到域名时，用 HTTPS 形式的公共 DNS 查询 A 记录；结果按域名缓存。</summary>
internal static class DohResolver
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Endpoints =
    {
        "https://dns.alidns.com/resolve?name=",
        "https://doh.pub/dns-query?name="
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    /// <summary>只查备用 DNS，不走系统解析；查到返回地址，查不到返回 null。</summary>
    internal static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        IPAddress? literal = null;
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out literal))
        {
            return literal;
        }
        if (Cache.TryGetValue(host, out string cached) && IPAddress.TryParse(cached, out IPAddress? hit))
        {
            return hit;
        }
        foreach (string endpoint in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string query = endpoint + Uri.EscapeDataString(host) + "&type=A";
                using HttpResponseMessage resp = await Http.GetAsync(query, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("Answer", out JsonElement answers))
                {
                    continue;
                }
                foreach (JsonElement answer in answers.EnumerateArray())
                {
                    if (answer.TryGetProperty("type", out JsonElement type)
                        && type.ValueKind == JsonValueKind.Number
                        && type.GetInt32() != 1)
                    {
                        continue;
                    }
                    if (!answer.TryGetProperty("data", out JsonElement data))
                    {
                        continue;
                    }
                    string? text = data.GetString();
                    if (!string.IsNullOrWhiteSpace(text)
                        && IPAddress.TryParse(text, out IPAddress? address)
                        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        Cache[host] = address.ToString();
                        return address;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
        return null;
    }
}
