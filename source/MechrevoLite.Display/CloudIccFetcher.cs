using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using MechrevoLite.Helpers;

namespace MechrevoLite.Display;

/// <summary>从厂商 ICC 云端按“面板 ID + 序列号/BIOS 序列号/MAC”匹配并下载 ICC。</summary>
public static class CloudIccFetcher
{
    private const string AistoneServer = "http://iccprofile.aistoneglobal.com:8001/api/iccprofile/";
    private const string AistoneServerAlt = "http://iccprofile1.aistoneglobal.com:3081/api/iccprofile/";
    private const string UniwillServer = "http://iccprofile.uniwill.com.tw/api/iccprofile/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // 主站给 50 系的序列号包；tw 只覆盖 40 系；3081 两边都做兜底（慢，单次 10 秒上下）。
    private static readonly string[] ServersSerialFirst = { AistoneServer, AistoneServerAlt, UniwillServer };
    private static readonly string[] ServersMacFirstGen50 = { AistoneServerAlt, AistoneServer, UniwillServer };
    private static readonly string[] ServersMacFirstLegacy = { UniwillServer, AistoneServerAlt, AistoneServer };
    private static readonly string[] ServersFallback = { AistoneServer, AistoneServerAlt, UniwillServer };

    private static readonly string[] AllSuffixes = { "", ".zip", ".icm" };
    private static readonly string[] PlainSuffixOnly = { "" };

    public sealed record Result(bool Ok, string? FileName, string Detail, IReadOnlyList<string>? FileNames = null);

    private sealed record MonitorId(string Pnp, string Friendly, string Serial);

    private sealed record Hit(IReadOnlyList<string> FileNames, string Url, int Bytes);

    public static async Task<Result> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            string biosSerial = ReadBiosSerial();
            List<MonitorId> monitors = ReadMonitorIds();
            List<string> macs = ReadMacAddresses();
            if (monitors.Count == 0)
            {
                return new Result(false, null, "云端获取校色失败：未读到面板 ID");
            }
            Logger.WriteError("[icc-cloud] monitors=" + monitors.Count + " macs=" + macs.Count);

            // 本机已有官方 ICC（GCU 下载的副本、系统颜色目录里的标准档）时直接用，不再走网络。
            List<string> localImported = new();
            foreach (string localFile in CollectLocalOfficialIccFiles(monitors, macs))
            {
                string? name = IccHelper.ImportOfficialIcc(localFile);
                if (!string.IsNullOrWhiteSpace(name) && !localImported.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    localImported.Add(name);
                }
            }
            if (localImported.Count > 0)
            {
                Logger.WriteError("[icc-cloud] 使用本机已有 ICC " + string.Join(",", localImported));
                string localPrimary = localImported.FirstOrDefault(IccHelper.IsDefaultIccName) ?? localImported[0];
                return new Result(true, localPrimary,
                    "已使用本机已有的官方 ICC：" + string.Join("、", localImported), localImported);
            }

            // 50 系优先序列号（aistone）；40 系/未知优先 MAC（uniwill）。
            bool gen50 = MechrevoLite.Program.hw?.DgpuGeneration
                == MechrevoLite.Gpu.DgpuGenerationKind.Gen50;
            // 逐个候选尝试；服务器响应慢时给整轮设上限，避免界面长时间无响应。
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            CancellationToken token = deadline.Token;
            Hit? hit;
            if (gen50)
            {
                hit = await TryFetchAsync(
                    BuildSerialCandidates(monitors, biosSerial),
                    ServersSerialFirst,
                    token).ConfigureAwait(false);
                if (hit == null)
                {
                    hit = await TryFetchAsync(
                        BuildMacCandidates(monitors, macs),
                        ServersMacFirstGen50,
                        token).ConfigureAwait(false);
                }
            }
            else
            {
                hit = await TryFetchAsync(
                    BuildMacCandidates(monitors, macs),
                    ServersMacFirstLegacy,
                    token).ConfigureAwait(false);
                if (hit == null)
                {
                    hit = await TryFetchAsync(
                        BuildSerialCandidates(monitors, biosSerial),
                        ServersSerialFirst,
                        token).ConfigureAwait(false);
                }
            }

            // 兜底：<pnp> / <友好名>，三个服务器都试。
            if (hit == null)
            {
                hit = await TryFetchAsync(
                    BuildFallbackCandidates(monitors),
                    ServersFallback,
                    token).ConfigureAwait(false);
            }

            if (hit == null)
            {
                Logger.WriteError("[icc-cloud] 未命中（monitors=" + monitors.Count + " macs=" + macs.Count + "）");
                return new Result(false, null, "云端获取校色失败：未找到匹配当前面板的 ICC（已尝试序列号、MAC 和兜底路径）");
            }
            Logger.WriteError("[icc-cloud] 命中 " + hit.Url + " files=" + hit.FileNames.Count);
            // 多文件包的默认项固定是 default.icc；单文件包只有屏幕厂商+序列号/MAC 那个文件。
            string primary = hit.FileNames.FirstOrDefault(IccHelper.IsDefaultIccName)
                ?? hit.FileNames.FirstOrDefault()
                ?? "";
            return new Result(true, primary, $"已从云端获取 {hit.FileNames.Count} 个 ICC：{hit.Url}（{hit.Bytes} 字节）", hit.FileNames);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, null, ct.IsCancellationRequested
                ? "云端获取校色失败：已取消"
                : "云端获取校色失败：服务器响应超时");
        }
        catch (Exception ex)
        {
            return new Result(false, null, "云端获取校色失败：" + ex.GetType().Name);
        }
    }

    private static async Task<Hit?> TryFetchAsync(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> servers,
        CancellationToken ct)
    {
        foreach (string server in servers)
        {
            foreach (string name in candidates)
            {
                // 备用站只响应无后缀的单文件，其它后缀要么 404 要么长时间不回，不试。
                foreach (string suffix in string.Equals(server, AistoneServerAlt, StringComparison.OrdinalIgnoreCase)
                    ? PlainSuffixOnly
                    : AllSuffixes)
                {
                    ct.ThrowIfCancellationRequested();
                    string url = server + Uri.EscapeDataString(name + suffix);
                    try
                    {
                        byte[]? data = await TryGetBytesAsync(url, ct).ConfigureAwait(false);
                        if (data == null)
                        {
                            continue;
                        }
                        IReadOnlyList<string> imported = await TryImportAsync(data, name + suffix, ct).ConfigureAwait(false);
                        if (imported.Count > 0)
                        {
                            return new Hit(imported, url, data.Length);
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
            }
        }
        return null;
    }

    /// <summary>网络请求：直连失败（多为系统域名解析不到）时改用备用 DNS 解析出地址，再带 Host 头重试一次。</summary>
    private static async Task<byte[]?> TryGetBytesAsync(string url, CancellationToken ct)
    {
        bool timedOut = false;
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 单次请求自身超时：不换路径重试，直接交给下一个候选，避免拖长总耗时。
            timedOut = true;
        }
        catch
        {
        }
        if (timedOut)
        {
            return null;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            return null;
        }
        string? ip = await ResolveHostAsync(uri.Host, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(ip))
        {
            return null;
        }
        try
        {
            UriBuilder builder = new(uri) { Host = ip };
            using HttpRequestMessage request = new(HttpMethod.Get, builder.Uri);
            request.Headers.Host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString();
            using HttpResponseMessage resp = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static readonly ConcurrentDictionary<string, string> ResolvedHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>先走系统解析；解析不到时用备用 DNS 查询一次 A 记录（结果按域名缓存）。</summary>
    private static async Task<string?> ResolveHostAsync(string host, CancellationToken ct)
    {
        if (ResolvedHosts.TryGetValue(host, out string cached))
        {
            return cached;
        }
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            foreach (IPAddress address in addresses)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    ResolvedHosts[host] = address.ToString();
                    return address.ToString();
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
        IPAddress? fallback = await DohResolver.ResolveAsync(host, ct).ConfigureAwait(false);
        if (fallback != null)
        {
            string text = fallback.ToString();
            Logger.WriteError("[icc-cloud] 系统域名解析失败，改用备用 DNS 解析 " + host + "=" + text);
            ResolvedHosts[host] = text;
            return text;
        }
        return null;
    }

    private static async Task<IReadOnlyList<string>> TryImportAsync(byte[] data, string safeName, CancellationToken ct)
    {
        List<string> imported = new();
        if (IsZip(data))
        {
            try
            {
                using MemoryStream zipStream = new(data);
                using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
                foreach (ZipArchiveEntry entry in archive.Entries
                    .Where(e => e.Name.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)
                        || e.Name.EndsWith(".icc", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] icc;
                    await using (Stream stream = entry.Open())
                    {
                        using MemoryStream copy = new();
                        await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
                        icc = copy.ToArray();
                    }
                    if (!IsIcc(icc))
                    {
                        continue;
                    }
                    string? name = await ImportAsync(icc, Path.GetFileNameWithoutExtension(entry.Name) + ".icm", ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(name) && !imported.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        imported.Add(name);
                    }
                }
                return imported;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return imported;
            }
        }
        if (!IsIcc(data))
        {
            return imported;
        }
        string? single = await ImportAsync(data, Path.GetFileNameWithoutExtension(safeName) + ".icm", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(single))
        {
            imported.Add(single);
        }
        return imported;
    }

    private static async Task<string?> ImportAsync(byte[] data, string fileName, CancellationToken ct)
    {
        // 保留云端原始文件名；不能再用 lm-cloud-<hash> 临时名导入，否则下拉框会显示随机名。
        string safe = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "cloud.icm";
        }
        if (!safe.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)
            && !safe.EndsWith(".icc", StringComparison.OrdinalIgnoreCase))
        {
            safe += ".icm";
        }
        string dir = Path.Combine(Path.GetTempPath(), "lm-icc-" + Guid.NewGuid().ToString("N"));
        string temp = Path.Combine(dir, safe);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            return IccHelper.ImportOfficialIcc(temp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static bool IsIcc(byte[] data)
    {
        return data.Length >= 40 && data[36] == (byte)'a' && data[37] == (byte)'c'
            && data[38] == (byte)'s' && data[39] == (byte)'p';
    }

    private static bool IsZip(byte[] data)
    {
        return data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B;
    }

    private static List<string> BuildSerialCandidates(IReadOnlyList<MonitorId> monitors, string biosSerial)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorId monitor in monitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Pnp))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(monitor.Serial))
            {
                result.Add(monitor.Pnp + "_" + monitor.Serial);
            }
            if (!string.IsNullOrWhiteSpace(biosSerial))
            {
                result.Add(monitor.Pnp + "_" + biosSerial);
            }
        }
        return result.ToList();
    }

    private static List<string> BuildMacCandidates(IReadOnlyList<MonitorId> monitors, IReadOnlyList<string> macs)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorId monitor in monitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Pnp))
            {
                continue;
            }
            foreach (string mac in macs)
            {
                if (!string.IsNullOrWhiteSpace(mac))
                {
                    result.Add(monitor.Pnp + "_" + mac);
                }
            }
        }
        return result.ToList();
    }

    private static List<string> BuildFallbackCandidates(IReadOnlyList<MonitorId> monitors)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorId monitor in monitors)
        {
            if (!string.IsNullOrWhiteSpace(monitor.Pnp))
            {
                result.Add(monitor.Pnp);
            }
            if (!string.IsNullOrWhiteSpace(monitor.Friendly))
            {
                result.Add(monitor.Friendly);
            }
        }
        return result.ToList();
    }

    private static string ReadBiosSerial()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT SerialNumber FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                string value = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch { }
        return "";
    }

    private const string SpoolColorDir = @"C:\Windows\System32\spool\drivers\color";

    /// <summary>本机已有的官方 ICC：GCU 目录里下载好的副本，以及系统颜色目录里的标准档文件。</summary>
    private static List<string> CollectLocalOfficialIccFiles(IReadOnlyList<MonitorId> monitors, IReadOnlyList<string> macs)
    {
        List<string> dirs = new();
        string gcuRoot = Path.Combine(AppContext.BaseDirectory, "GCU");
        try
        {
            if (Directory.Exists(gcuRoot))
            {
                foreach (string dir in Directory.EnumerateDirectories(gcuRoot, "ICCProfile", SearchOption.AllDirectories))
                {
                    dirs.Add(dir);
                }
            }
        }
        catch { }
        dirs.Add(SpoolColorDir);

        List<string> result = new();
        foreach (string dir in dirs)
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.ic*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                continue;
            }
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                bool recognized = IccHelper.StandardLevelFor(file) > 0;
                if (!recognized)
                {
                    // 面板文件要能对上本机网卡，避免把同型号别的机器的校色一起拿进来。
                    recognized = monitors.Any(m => !string.IsNullOrWhiteSpace(m.Pnp)
                            && name.StartsWith(m.Pnp, StringComparison.OrdinalIgnoreCase))
                        && macs.Any(mac => !string.IsNullOrWhiteSpace(mac)
                            && name.EndsWith("_" + mac, StringComparison.OrdinalIgnoreCase));
                }
                if (!recognized)
                {
                    continue;
                }
                try
                {
                    FileInfo info = new(file);
                    if (info.Length < 1024)
                    {
                        continue;
                    }
                    byte[] head = new byte[40];
                    using (FileStream stream = File.OpenRead(file))
                    {
                        if (stream.Read(head, 0, 40) < 40)
                        {
                            continue;
                        }
                    }
                    if (head[36] != (byte)'a' || head[37] != (byte)'c'
                        || head[38] != (byte)'s' || head[39] != (byte)'p')
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                if (!result.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(file);
                }
            }
        }
        return result;
    }

    private static List<MonitorId> ReadMonitorIds()
    {
        List<MonitorId> ids = new();
        try
        {
            using ManagementObjectSearcher searcher = new(
                @"root\wmi",
                "SELECT InstanceName, UserFriendlyName, ManufacturerName, ProductCodeID, SerialNumberID FROM WmiMonitorID");
            foreach (ManagementObject obj in searcher.Get())
            {
                string instance = obj["InstanceName"]?.ToString() ?? "";
                string pnp = "";
                int start = instance.IndexOf("DISPLAY\\", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    string rest = instance[(start + "DISPLAY\\".Length)..];
                    int slash = rest.IndexOf('\\');
                    if (slash > 0) pnp = rest[..slash];
                }
                if (string.IsNullOrWhiteSpace(pnp))
                {
                    string mfg = DecodeMonitorString(obj["ManufacturerName"]);
                    string code = DecodeMonitorString(obj["ProductCodeID"]);
                    pnp = (mfg + code).Trim();
                }
                string friendly = DecodeMonitorString(obj["UserFriendlyName"]);
                string serial = DecodeMonitorString(obj["SerialNumberID"]);
                if (string.Equals(serial, "0", StringComparison.OrdinalIgnoreCase))
                {
                    serial = "";
                }
                if (!string.IsNullOrWhiteSpace(pnp) || !string.IsNullOrWhiteSpace(friendly))
                {
                    ids.Add(new MonitorId(pnp, friendly, serial));
                }
            }
        }
        catch { }
        return ids
            .GroupBy(m => m.Pnp + "|" + m.Friendly + "|" + m.Serial, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string DecodeMonitorString(object? raw)
    {
        try
        {
            if (raw is ushort[] values)
            {
                return new string(values.Where(v => v > 0).Select(v => (char)v).ToArray()).Trim('\0', ' ');
            }
            if (raw is byte[] bytes)
            {
                return System.Text.Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
            }
        }
        catch { }
        return "";
    }

    private static readonly string[] VirtualNicKeywords =
    {
        "Virtual", "VMware", "Hyper-V", "TAP", "Loopback", "Pseudo", "Bluetooth", "Wi-Fi Direct",
        "WAN Miniport", "Npcap", "VPN"
    };

    /// <summary>本机网卡 MAC：物理有线优先、无线其次，虚拟网卡放最后，避免候选顺序被打乱。</summary>
    private static List<string> ReadMacAddresses()
    {
        List<string> wired = new();
        List<string> wireless = new();
        List<string> virtualNics = new();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                {
                    continue;
                }
                string mac = nic.GetPhysicalAddress().ToString();
                if (string.IsNullOrWhiteSpace(mac))
                {
                    continue;
                }
                mac = mac.ToUpperInvariant();
                string text = nic.Name + " " + nic.Description;
                bool isVirtual = VirtualNicKeywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                List<string> target = isVirtual
                    ? virtualNics
                    : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? wired : wireless;
                if (!target.Contains(mac, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(mac);
                }
            }
        }
        catch { }
        return wired.Concat(wireless).Concat(virtualNics).ToList();
    }
}
