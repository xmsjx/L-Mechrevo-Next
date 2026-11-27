param(
    [int]$Seconds = 100,
    [string]$Filter = '#',
    [string]$LogPath = 'D:\openLM\tmp-ota\ota-trigger.txt',
    [string]$ClientId = 'UWPClient_4',
    [string]$User = 'UWPClient_User_4',
    [string]$Pwd = 'UWPClient_Pwd888881772688_4',
    [switch]$NoTrigger
)
$src = @'
using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;

public class OtaTrigger2
{
    static Stream S;
    static StreamWriter Log;
    static void W(string s) { var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s; Log.WriteLine(line); Log.Flush(); Console.WriteLine(line); }
    static byte[] Enc(string str) { var b = Encoding.UTF8.GetBytes(str); var o = new List<byte>(); o.Add((byte)(b.Length / 256)); o.Add((byte)(b.Length % 256)); o.AddRange(b); return o.ToArray(); }
    static byte[] VI(int n) { var o = new List<byte>(); do { byte d = (byte)(n % 128); n /= 128; if (n > 0) d |= 0x80; o.Add(d); } while (n > 0); return o.ToArray(); }
    static int ReadVI() { int mult = 1, val = 0, b; do { b = S.ReadByte(); if (b < 0) throw new IOException("eof"); val += (b & 0x7F) * mult; mult *= 128; } while ((b & 0x80) != 0); return val; }
    static byte[] ReadExact(int n) { var buf = new byte[n]; int got = 0; while (got < n) { int r = S.Read(buf, got, n - got); if (r <= 0) throw new IOException("eof"); got += r; } return buf; }
    static string Clean(byte[] b, int max) { var sb = new StringBuilder(); for (int i = 0; i < Math.Min(b.Length, max); i++) { char ch = (char)b[i]; sb.Append(ch < 32 || ch > 126 ? '.' : ch); } return sb.ToString(); }

    public static void Run(int secs, string filter, string logPath, string cid, string user, string pwd, bool noTrigger)
    {
        Log = new StreamWriter(logPath, false, Encoding.UTF8);
        var c = new TcpClient();
        c.Connect("127.0.0.1", 13688);
        S = c.GetStream();
        S.ReadTimeout = 30000;
        var body = new List<byte>();
        body.AddRange(Enc("MQTT")); body.Add(4); body.Add(0xC2); body.Add(0); body.Add(60);
        body.AddRange(Enc(cid)); body.AddRange(Enc(user)); body.AddRange(Enc(pwd));
        var pkt = new List<byte>(); pkt.Add(0x10); pkt.AddRange(VI(body.Count)); pkt.AddRange(body);
        S.Write(pkt.ToArray(), 0, pkt.Count);
        int h0 = S.ReadByte(); int l0 = ReadVI(); byte[] a0 = ReadExact(l0);
        W("CONNACK rc=" + a0[1]);
        if (a0[1] != 0) { c.Close(); Log.Close(); return; }
        var sb = new List<byte>(); sb.Add(0); sb.Add(1); sb.AddRange(Enc(filter)); sb.Add(0);
        var sp = new List<byte>(); sp.Add(0x82); sp.AddRange(VI(sb.Count)); sp.AddRange(sb);
        S.Write(sp.ToArray(), 0, sp.Count);
        int h1 = S.ReadByte(); int l1 = ReadVI(); byte[] a1 = ReadExact(l1);
        W("SUBACK " + filter + " -> code=" + (a1.Length > 2 ? a1[2].ToString() : "?"));
        if (!noTrigger)
        {
            var pb = new List<byte>(); pb.AddRange(Enc("OTA/MRControl")); pb.AddRange(Encoding.UTF8.GetBytes("null"));
            var pp = new List<byte>(); pp.Add(0x30); pp.AddRange(VI(pb.Count)); pp.AddRange(pb);
            S.Write(pp.ToArray(), 0, pp.Count);
            W("TRIGGER OTA/MRControl payload=null");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int seen = 0;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            int h;
            try { h = S.ReadByte(); } catch { continue; }
            if (h < 0) break;
            int len = ReadVI();
            byte[] pl = ReadExact(len);
            int type = (h >> 4) & 0x0F;
            if (type == 3)
            {
                int tl = (pl[0] << 8) | pl[1];
                string topic = Encoding.UTF8.GetString(pl, 2, tl);
                int off = 2 + tl; int qos = (h >> 1) & 0x3; if (qos > 0) off += 2;
                var pay = new byte[pl.Length - off];
                Array.Copy(pl, off, pay, 0, pay.Length);
                seen++;
                W("PUB " + topic + " [" + pay.Length + "B] " + Clean(pay, 1500));
            }
        }
        W("=== end, published=" + seen + " ===");
        c.Close(); Log.Close();
    }
}
'@
Add-Type -TypeDefinition $src -Language CSharp
[OtaTrigger2]::Run($Seconds, $Filter, $LogPath, $ClientId, $User, $Pwd, [bool]$NoTrigger)
