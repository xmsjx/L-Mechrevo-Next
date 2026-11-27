param(
    [int]$Seconds = 45,
    [string]$LogPath = 'D:\openLM\tmp-ota\ota-trace.txt'
)
$pub = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Net.Sockets;
public class OtaPub
{
    public static string Fire(string cid, string user, string pwd, string topic, string payload)
    {
        var c = new TcpClient(); c.Connect("127.0.0.1", 13688);
        var S = c.GetStream(); S.ReadTimeout = 8000;
        var body = new List<byte>();
        body.AddRange(E("MQTT")); body.Add(4); body.Add(0xC2); body.Add(0); body.Add(60);
        body.AddRange(E(cid)); body.AddRange(E(user)); body.AddRange(E(pwd));
        var pkt = new List<byte>(); pkt.Add(0x10); pkt.AddRange(VI(body.Count)); pkt.AddRange(body);
        S.Write(pkt.ToArray(), 0, pkt.Count);
        var ack = new byte[4]; int g = 0; while (g < 4) { int r = S.Read(ack, g, 4 - g); if (r <= 0) break; g += r; }
        if (ack[3] != 0) { c.Close(); return "connack rc=" + ack[3]; }
        var pb = new List<byte>(); pb.AddRange(E(topic)); pb.AddRange(Encoding.UTF8.GetBytes(payload));
        var pp = new List<byte>(); pp.Add(0x30); pp.AddRange(VI(pb.Count)); pp.AddRange(pb);
        S.Write(pp.ToArray(), 0, pp.Count);
        System.Threading.Thread.Sleep(300);
        c.Close();
        return "published " + topic + "=" + payload;
    }
    static byte[] E(string s) { var b = Encoding.UTF8.GetBytes(s); var o = new List<byte>(); o.Add((byte)(b.Length / 256)); o.Add((byte)(b.Length % 256)); o.AddRange(b); return o.ToArray(); }
    static byte[] VI(int n) { var o = new List<byte>(); do { byte d = (byte)(n % 128); n /= 128; if (n > 0) d |= 0x80; o.Add(d); } while (n > 0); return o.ToArray(); }
}
'@
Add-Type -TypeDefinition $pub -Language CSharp

$log = New-Object System.Collections.Generic.List[string]
function W($s) { $line = (Get-Date).ToString('HH:mm:ss.fff') + '  ' + $s; $log.Add($line); }

W ([OtaPub]::Fire('UWPClient_4','UWPClient_User_4','UWPClient_Pwd888881772688_4','OTA/MRControl','null'))
$end = (Get-Date).AddSeconds($Seconds)
$seenPids = @{}
while ((Get-Date) -lt $end) {
    $procs = Get-Process -Name 'BIOS_OTA' -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        if (-not $seenPids.ContainsKey($p.Id)) {
            $seenPids[$p.Id] = $true
            W ("BIOS_OTA pid=" + $p.Id + " title='" + $p.MainWindowTitle + "' start=" + $p.StartTime.ToString('HH:mm:ss.fff') + " path=" + $p.Path)
            $conns = Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue
            foreach ($cn in $conns) { W ("  TCP " + $cn.LocalAddress + ':' + $cn.LocalPort + ' -> ' + $cn.RemoteAddress + ':' + $cn.RemotePort + ' ' + $cn.State) }
        }
    }
    Start-Sleep -Milliseconds 400
}
W ('=== done, pids seen: ' + ($seenPids.Keys -join ',') + ' ===')
$log | Set-Content -Path $LogPath -Encoding UTF8
$log -join [char]10
