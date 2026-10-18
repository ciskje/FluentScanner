using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentScanner;

/// Scan engine — shared intents with `macos/Sources/FluentScanner/Scanner.swift`
/// (identical port lists, ARP parsing and phases; keep them in sync).
internal static class Scanner
{
    // ponytail: fixed tuning knobs; editable settings intent is in TODO.md
    /// TTL guess from the ping reply (identical to the macOS ttlKind):
    /// only 128 (Windows) is distinctive — 255 is the default of many
    /// embedded stacks, not a router signature.
    internal static string? TtlKind(int ttl) => ttl switch
    {
        >= 118 and <= 130 => "Windows host",
        _ => null,
    };

    /// Always-probed ports for host discovery (ordered by LAN hit rate).
    /// Wide list: devices that only answer on ssh/https/RDP/print ports must
    /// still be found. Swapped 1:1 with the macOS `discoveryPorts`.
    internal static readonly int[] DiscoveryPorts = [445, 80, 8080, 22, 443, 3389, 9100, 631];
    /// Common-services list scanned for every live host (keep == macOS).
    internal static readonly int[] ScanPorts =
    [
        21, 22, 23, 25, 53, 80, 110, 111, 123, 135, 139, 143, 161, 389, 443,
        445, 465, 587, 631, 993, 995, 1433, 1521, 2049, 2375, 3260, 3306,
        3389, 5432, 5900, 5985, 6379, 8000, 8006, 8080, 8443, 8888, 9000,
        9100, 49152,
    ];
    internal static readonly Dictionary<int, string> PortNames = new()
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS",
        [80] = "HTTP", [110] = "POP3", [111] = "Rpcbind", [123] = "NTP",
        [135] = "MS-RPC", [139] = "NetBIOS", [143] = "IMAP", [161] = "SNMP",
        [389] = "LDAP", [443] = "HTTPS", [445] = "SMB", [465] = "SMTPS",
        [587] = "SMTP", [631] = "IPP", [993] = "IMAPS", [995] = "POP3S",
        [1433] = "MSSQL", [1521] = "Oracle", [2049] = "NFS", [2375] = "Docker",
        [3260] = "iSCSI", [3306] = "MySQL", [3389] = "RDP", [5432] = "PostgreSQL",
        [5900] = "VNC", [5985] = "WinRM", [6379] = "Redis", [8000] = "HTTP",
        [8006] = "HTTP", [8080] = "HTTP", [8443] = "HTTPS", [8888] = "HTTP",
        [9000] = "HTTP", [9100] = "JetDirect", [49152] = "WinRPC",
    };
    // Tuned for over-Wi-Fi RTT + ARP retries; short timeouts silently drop
    // slow devices (identical values to the macOS side).
    internal const int PingTimeoutMs = 1000;
    internal const int PingChunk = 64;
    internal const int DiscoveryTimeoutMs = 2000;
    internal const int PortTimeoutMs = 1500;
    internal const int MaxTargets = 4096;
    // ponytail: fixed concurrency caps; adaptive limiter if a /24+ scan
    // ever drops connects under load
    internal const int DiscoveryChunk = 64;
    internal const int PortChunk = 256;

    // MARK: OUI vendor lookup + device classification
    // oui.csv ("aabbcc<TAB>Org") shipped next to the exe; generated once from
    // the IEEE registry — same file as the macOS side (see windows/AGENTS.md).

    private static Dictionary<string, string>? _oui;
    internal static string? Vendor(string? mac)
    {
        // Randomized MAC (U/L bit): a fake prefix would blame the wrong
        // maker — same guard as the macOS Oui.vendor.
        if (mac is null || IsRandomized(mac)) return null;
        LoadOui();
        if (mac is not { Length: >= 8 } || _oui is null) return null;
        return _oui.TryGetValue(mac[..8].Replace(":", "").ToLowerInvariant(), out var org)
            ? org : null;
    }

    /// U/L bit set (first octet & 0x02): privacy randomization, no OUI.
    internal static bool IsRandomized(string mac)
    {
        var first = mac.Split([':', '-'], 2)[0];
        if (first.Length is 0 or > 2) return false;
        try { return (Convert.ToByte(first, 16) & 0x02) != 0; }
        catch { return false; }
    }

    private static void LoadOui()
    {
        if (_oui is not null) return;
        _oui = new Dictionary<string, string>();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "oui.csv");
            if (!File.Exists(path)) return; // missing table: no vendor column
            foreach (var line in File.ReadLines(path))
            {
                int sep = line.IndexOf('\t');
                if (sep > 0) _oui[line[..sep].ToLowerInvariant()] = line[(sep + 1)..];
            }
        }
        catch
        {
            // Table unreadable: vendor column stays empty, scan continues.
        }
    }

    /// Heuristic host type: strongest signal first — a Windows host with
    /// RDP/WinRM, a 9100 print server, NFS shares, then vendor-derived hints
    /// (identical to Scanner.classify on the macOS side).
    internal static string? Classify(string? vendor, IReadOnlySet<int> ports)
    {
        var hints = new List<string>();
        if (ports.Contains(3389) || ports.Contains(5985)) hints.Add("Windows host");
        if (ports.Contains(9100)) hints.Add("Printer");
        if (ports.Contains(2049)) hints.Add("NAS");
        if (vendor is not null)
        {
            if (vendor.Contains("Synology") || vendor.Contains("QNAP")) hints.Add("NAS");
            if (vendor.Contains("Raspberry")) hints.Add("Raspberry Pi");
            if (vendor.Contains("Espressif") || vendor.Contains("Tuya") ||
                vendor.Contains("Shenzhen") || vendor.Contains("Dreame") ||
                vendor.Contains("Xiaoyi") || vendor.Contains("AMPAK")) hints.Add("IoT device");
            if (vendor.Contains("Apple")) hints.Add("Apple device");
            if (vendor.Contains("Intel") || vendor.Contains("Realtek") ||
                vendor.Contains("ASUSTeK") || vendor.Contains("Gigabyte")) hints.Add("PC");
            if (vendor.Contains("Cisco") || vendor.Contains("Ubiquiti") ||
                vendor.Contains("Netgear") || vendor.Contains("TP-LINK") ||
                vendor.Contains("D-Link") || vendor.Contains("MikroTik") ||
                vendor.Contains("AVM") || vendor.Contains("Advanced Digital")) hints.Add("Router/AP");
        }
        return hints.Count == 0 ? null : string.Join(" · ", hints);
    }

    // MARK: Target parsing (same inputs/format as the Mac side)

    /// Accepts "10.0.0.5", "10.0.0.0/24", "10.0.0.1-10.0.0.254",
    /// "10.0.0.1-254" (last-octet shorthand). Empty list on anything else.
    internal static List<uint> Targets(string text)
    {
        var result = new List<uint>();
        var t = text.Trim();
        if (t.Length == 0) return result;

        int slash = t.IndexOf('/');
        if (slash >= 0)
        {
            // CIDR ("10.0.0.0/24"): host bits capped at 12 (ponytail
            // broadcast-size storm guard), base masked, IPs in order.
            if (!uint.TryParse(t[(slash + 1)..], out var bits) || bits > 32) return result;
            var net = ParseIp(t[..slash]);
            if (net is null) return result;
            int hostBits = 32 - (int)bits;
            if (hostBits > 12) return result;
            uint mask = hostBits == 0 ? uint.MaxValue : ~((1u << hostBits) - 1u);
            uint baseNet = net.Value & mask;
            // Network + broadcast are never probed (/31 has none per
            // RFC 3021, so both addresses stay).
            uint n = 1u << hostBits;
            if (n <= 2) { for (uint i = 0; i < n; i++) result.Add(baseNet + i); }
            else { for (uint i = 1; i < n - 1; i++) result.Add(baseNet + i); }
            return result;
        }

        int dash = t.IndexOf('-');
        if (dash >= 0)
        {
            var a = ParseIp(t[..dash]);
            if (a is null) return result;
            var tail = t[(dash + 1)..];
            uint b;
            var full = ParseIp(tail);
            if (full is not null) b = full.Value;
            else if (uint.TryParse(tail, out var last) && last <= 255)
            {
                // Last-octet shorthand: same first three octets as the start.
                if ((a.Value & 0xFF) > last) return result;
                b = (a.Value & 0xFFFFFF00) | last;
            }
            else return result;
            if (a.Value > b || b - a.Value >= MaxTargets) return result;
            for (uint v = a.Value; ; v++) { result.Add(v); if (v == b) break; }
            return result;
        }

        return ParseIp(t) is { } ip ? [ip] : result;
    }

    /// "192.168.1.10" → u32 with "a" as the most significant byte.
    internal static uint? ParseIp(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4) return null;
        uint v = 0;
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, out var b)) return null;
            v = (v << 8) | b;
        }
        return v;
    }

    internal static string Dotted(uint v) =>
        $"{v >> 24 & 0xFF}.{v >> 16 & 0xFF}.{v >> 8 & 0xFF}.{v & 0xFF}";

    /// Numeric IP order ("1.2.0.10" > "1.2.0.9").
    internal static int IpCompare(string a, string b)
    {
        var pa = ParseIp(a);
        var pb = ParseIp(b);
        if (pa is null || pb is null) return string.CompareOrdinal(a, b);
        return pa.Value.CompareTo(pb.Value);
    }

    // MARK: ARP (MAC identification) — `arp -a`, Windows form:
    // "192.168.1.1     f0-18-98-5b-83-d2     dynamic". Same regex-free-ish
    // line shape as the macOS side: first complete (IPv4, MAC) pair per line
    // wins; incomplete entries drop.

    private static readonly Regex MacRe = new(
        @"(?<mac>[0-9a-f]{1,2}([-:][0-9a-f]{1,2}){5})", RegexOptions.IgnoreCase);

    /// Raw "aa-bb-…"/"a:b:…" match → zero-padded colon form (macOS `arp`
    /// doesn't zero-pad single-digit groups — same fix as the Mac side).
    internal static string NormalizeMac(string raw)
    {
        var sep = raw.Contains(':') ? ':' : '-';
        return string.Join(":", raw.Split(sep).Select(g => g.PadLeft(2, '0').ToLowerInvariant()));
    }

    /// `arp -a` → ip → normalized "aa:bb:cc:dd:ee:ff" (empty on failure).
    internal static Dictionary<string, string> ArpTable()
    {
        var result = new Dictionary<string, string>();
        try
        {
            var psi = new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return result;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var line in text.Split('\n'))
            {
                var ip = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(t => t.Contains('.') && ParseIp(t) is not null);
                if (ip is null) continue;
                var m = MacRe.Match(line);
                if (m.Success && !result.ContainsKey(ip))
                    result[ip] = NormalizeMac(m.Groups["mac"].Value);
            }
        }
        catch
        {
            // No arp(8) or broken output: MACs stay empty, scan continues.
        }
        return result;
    }

    // MARK: TCP probing

    /// One connect attempt → wall-clock latency (ms) on success, else null.
    /// The abandoned connect (timeout path) gets a no-op observer so its
    /// failure never lands in UnobservedTaskException.
    internal static async Task<int?> ProbeAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        var t0 = Stopwatch.StartNew();
        using var client = new TcpClient();
        var connect = client.ConnectAsync(ip, port, ct).AsTask();
        try
        {
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)) != connect)
                return null; // timeout
            await connect;
            return (int)t0.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = connect.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    // MARK: Reverse DNS (best effort, capped) — never fatal

    internal static async Task<string?> ReverseDnsAsync(string ip, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip, ct);
            var done = await Task.WhenAny(lookup, Task.Delay(1500, ct));
            if (done != lookup) return null;
            var entry = await lookup;
            return string.Equals(entry.HostName, ip, StringComparison.OrdinalIgnoreCase)
                ? null : entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    // MARK: HTTP Server header (software hint for port-80/8080 hosts)

    /// First `Server:` header value of a response (null on anything
    /// unexpected). Pure — mirrors macOS parseServerHeader.
    internal static string? ParseServerHeader(string response)
    {
        int count = 0;
        foreach (var raw in response.Split('\n'))
        {
            if (++count > 17) break;
            var line = raw.Trim();
            if (line.Length == 0) break; // headers done
            if (line.Length > 7 && line[..7].Equals("server:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line[7..].Trim();
                return v.Length == 0 || v.Length > 80 ? null : v;
            }
        }
        return null;
    }

    /// GET / on an already-open HTTP port, read ≤2 KB: the `Server:` header
    /// names the software (Caddy, lighttpd…) when OUI says nothing.
    internal static async Task<string?> HttpServerAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(ip, port, ct).AsTask();
            if (await Task.WhenAny(connect, Task.Delay(1500, ct)) != connect) return null;
            await connect;
            using var stream = client.GetStream();
            var req = Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {ip}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            var buf = new byte[2048];
            var sb = new StringBuilder();
            try
            {
                while (sb.Length < 2048)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, 2048 - sb.Length)), cts.Token);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("\r\n\r\n") || sb.ToString().Contains("\n\n")) break;
                }
            }
            catch { }
            return ParseServerHeader(sb.ToString());
        }
        catch
        {
            return null;
        }
    }

    // MARK: mDNS harvest (hostnames from Bonjour)

    /// Service types probed with one mDNS PTR question each (`.local`
    /// suffix mandatory). Same list as macOS mdnsTypes.
    internal static readonly string[] MdnsTypes =
    [
        "_airplay._tcp.local", "_raop._tcp.local", "_googlecast._tcp.local",
        "_companion-link._tcp.local", "_device-info._tcp.local",
        "_workstation._tcp.local", "_smb._tcp.local", "_http._tcp.local",
        "_ipp._tcp.local", "_spotify-connect._tcp.local", "_hap._tcp.local",
        "_printer._tcp.local", "_pdl-datastream._tcp.local",
        "_androidtvremote._tcp.local", "_sonos._tcp.local", "_hue._tcp.local",
        "_homekit._tcp.local",
    ];

    /// One-question PTR query with the QU (unicast-response) class bit.
    internal static byte[] MdnsQuery(string domain)
    {
        var d = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            d.Add((byte)label.Length);
            d.AddRange(Encoding.UTF8.GetBytes(label));
        }
        d.Add(0);
        d.AddRange([0, 12, 0x80, 0x01]);
        return [.. d];
    }

    /// `(ip → owner name)` from the A records in answers + authority +
    /// additionals. RFC 1035 subset (labels + compression pointers).
    internal static Dictionary<string, string> ParseMdns(byte[] data)
    {
        var out_ = new Dictionary<string, string>();
        if (data.Length < 12) return out_;
        var b = data;
        int U16(int i) => i + 1 < b.Length ? (b[i] << 8) | b[i + 1] : 0;

        string ReadName(ref int i0)
        {
            int i = i0;
            var parts = new List<string>();
            void Push(int at, int len)
            {
                if (at + 1 + len <= b.Length)
                    parts.Add(Encoding.UTF8.GetString(b, at + 1, len));
            }
            while (true)
            {
                if (i >= b.Length || parts.Count > 128) { i0 = b.Length; return string.Join(".", parts); }
                int l = b[i];
                if ((l & 0xC0) == 0xC0)
                {
                    if (i + 1 >= b.Length) { i0 = b.Length; return string.Join(".", parts); }
                    i0 = i + 2;
                    int j = ((l & 0x3F) << 8) | b[i + 1];
                    int jumps = 0;
                    while (jumps <= 10)
                    {
                        if (j + 1 >= b.Length) break;
                        int m = b[j];
                        if ((m & 0xC0) == 0xC0) { jumps++; j = ((m & 0x3F) << 8) | b[j + 1]; continue; }
                        if (m <= 0) break;
                        Push(j, m);
                        j += 1 + m;
                    }
                    return string.Join(".", parts);
                }
                if (l <= 0) { i0 = i + 1; return string.Join(".", parts); }
                Push(i, l);
                i += 1 + l;
            }
        }

        int i = 12;
        for (int q = 0; q < Math.Min(U16(4), 64); q++) { ReadName(ref i); i += 4; }
        int total = Math.Min(U16(6) + U16(8) + U16(10), 256);
        for (int r = 0; r < total; r++)
        {
            var owner = ReadName(ref i);
            if (i + 10 > b.Length) break;
            int rdlen = U16(i + 8);
            int rd = i + 10;
            if (U16(i) == 1 && rdlen == 4 && rd + 4 <= b.Length)
            {
                var ip = $"{b[rd]}.{b[rd + 1]}.{b[rd + 2]}.{b[rd + 3]}";
                out_.TryAdd(ip, owner);
            }
            i = rd + rdlen;
        }
        return out_;
    }

    /// Send the queries, then collect the unicast answers in the window:
    /// `[ip → hostname]`. Unprivileged UDP — no special rights.
    internal static async Task<Dictionary<string, string>> MdnsHarvestAsync(double seconds = 2.5, CancellationToken ct = default)
    {
        var hosts = new Dictionary<string, string>();
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            var dst = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            foreach (var t in MdnsTypes)
            {
                try { await sock.SendToAsync(MdnsQuery(t), dst, ct); }
                catch { break; }
            }
            sock.ReceiveTimeout = 200;
            var end = DateTime.UtcNow.AddSeconds(seconds);
            var buf = new byte[65536];
            while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
            {
                try
                {
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n = sock.ReceiveFrom(buf, ref ep);
                    if (n <= 12) continue;
                    foreach (var kv in ParseMdns(buf[..n]))
                        hosts.TryAdd(kv.Key, kv.Value);
                }
                catch (SocketException) { }
            }
        }
        catch { }
        return hosts;
    }

    // MARK: NetBIOS name (Windows PCs announce no mDNS — NBSTAT over UDP 137)

    /// One-shot node-status query for the wildcard name.
    internal static byte[] NbnsQuery()
    {
        var q = new List<byte> { 0x12, 0x34, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 };
        void Enc(byte v) { q.Add((byte)(0x41 + (v >> 4))); q.Add((byte)(0x41 + (v & 0x0F))); }
        Enc(0x2A);
        for (int k = 0; k < 14; k++) Enc(0x00);
        Enc(0x00);
        q.AddRange([0x00, 0x00, 0x21, 0x00, 0x01]);
        return [.. q];
    }

    /// First machine name from a node-status response (null on garbage).
    internal static string? ParseNbns(byte[] data)
    {
        var b = data;
        if (b.Length <= 12) return null;
        int U16(int i) => i + 1 < b.Length ? (b[i] << 8) | b[i + 1] : 0;
        if (U16(6) < 1) return null;
        int i = 12;
        while (i < b.Length && b[i] != 0)
        {
            int l = b[i];
            if ((l & 0xC0) == 0xC0 || i + 1 + l > b.Length) return null;
            i += 1 + l;
        }
        i += 1 + 4;
        if (i + 1 < b.Length && (b[i] & 0xC0) == 0xC0) i += 2;
        else
        {
            while (i < b.Length && b[i] != 0)
            {
                int l = b[i];
                if ((l & 0xC0) == 0xC0 || i + 1 + l > b.Length) return null;
                i += 1 + l;
            }
            i += 1;
        }
        if (i + 10 > b.Length || U16(i) != 0x0021) return null;
        int rd = i + 10;
        if (rd + 18 > b.Length || b[rd] < 1) return null;
        var name = Encoding.ASCII.GetString(b, rd + 1, 15).Trim().Trim('\0');
        return name.Length == 0 ? null : name;
    }

    /// NetBIOS node-status lookup (UDP 137, 1 s).
    internal static async Task<string?> NbnsNameAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.ReceiveTimeout = 1000;
            var dst = new IPEndPoint(IPAddress.Parse(ip), 137);
            await sock.SendToAsync(NbnsQuery(), dst, ct);
            var buf = new byte[1024];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            int n = await Task.Run(() =>
            {
                try { return sock.ReceiveFrom(buf, ref ep); }
                catch { return 0; }
            }, ct);
            return n > 0 ? ParseNbns(buf[..n]) : null;
        }
        catch
        {
            return null;
        }
    }

    // MARK: Local interfaces (identify the scanning host itself)

    /// This machine's subnet as a scan range (network+1 … broadcast-1,
    /// clamped to MaxTargets hosts). Powers the Get IP button.
    internal static (string start, string end)? LocalSubnet()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                uint ip = ToUint(ua.Address);
                uint mask = ToUint(ua.IPv4Mask);
                if (mask == 0) continue;
                uint net = ip & mask;
                uint bcast = net | ~mask;
                if (bcast <= net + 1) continue;
                uint end = bcast - 1;
                if (end - (net + 1) >= (uint)MaxTargets) end = net + (uint)MaxTargets;
                return (Dotted(net + 1), Dotted(end));
            }
        }
        return null;
    }

    /// This machine's own IPv4 + MAC per up interface (loopback excluded).
    internal static List<(string ip, string? mac)> LocalAddrs()
    {
        var out_ = new List<(string, string?)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            string? mac = null;
            try
            {
                var bytes = ni.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6)
                    mac = string.Join(":", bytes.Select(x => x.ToString("x2")));
            }
            catch { }
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                out_.Add((ua.Address.ToString(), mac));
            }
        }
        return out_;
    }

    // MARK: Headless self-test (FSCAN_SELFTEST=1 — offline-safe: no network ops)

    internal static bool SelfTest()
    {
        bool ok = true;
        void Check(bool cond, string name)
        {
            if (!cond) ok = false;
            Console.Error.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}");
        }
        Check(ParseIp("10.0.0.1") == 0x0A000001, "ipv4 parse");
        Check(ParseIp("10.0.0.999") is null, "ipv4 rejects 999");
        var single = Targets("10.0.0.1");
        Check(single.Count == 1 && single[0] == 0x0A000001, "targets single");
        var c30 = Targets("10.0.0.0/30");
        Check(c30.Count == 2 && c30[0] == 0x0A000001 && c30[1] == 0x0A000002, "targets /30 skips net+broadcast");
        var c32 = Targets("10.0.0.5/32");
        Check(c32.Count == 1 && c32[0] == 0x0A000005, "targets /32 single");
        var c24 = Targets("10.0.0.0/24");
        Check(c24.Count == 254 && c24[0] == 0x0A000001 && c24[^1] == 0x0A0000FE, "targets /24 skips .0 + .255");
        Check(Targets("10.0.0.0/13").Count == 0, "targets cidr too wide rejected");
        var r4 = Targets("10.0.0.1-4");
        Check(r4.Count == 4 && r4[0] == 0x0A000001, "targets range last-octet");
        Check(Targets("10.0.0.4-10.0.0.2").Count == 0, "reversed range rejected");
        Check(Targets("10.0.2.30-10.0.2.31").Count == 2, "full-IP range");
        Check(Targets("").Count == 0 && Targets("abc").Count == 0, "garbage rejected");
        Check(Dotted(ParseIp("192.168.1.42")!.Value) == "192.168.1.42", "dotted");
        Check(NormalizeMac("f8:e9:3:0:4b:21") == "f8:e9:03:00:4b:21", "mac pads single digits");
        Check(NormalizeMac("F0-18-98-5B-83-D2") == "f0:18:98:5b:83:d2", "mac windows form");
        Check(TtlKind(128) == "Windows host", "ttl 128 windows");
        Check(TtlKind(64) is null, "ttl 64 too generic, dropped");
        Check(TtlKind(255) is null, "ttl 255 is embedded stacks, not routers");
        Check(ParseServerHeader("HTTP/1.0 200 OK\r\nServer: Caddy\r\n\r\n") == "Caddy", "http server header");
        Check(ParseServerHeader("HTTP/1.1 200 OK\nserver: lighttpd/1.4.28\n\n<body>") == "lighttpd/1.4.28", "http server lowercase + body guard");
        Check(ParseServerHeader("HTTP/1.0 200 OK\r\n\r\nno headers") is null, "http no server");
        Check(ParseServerHeader("") is null, "http empty");
        var q = MdnsQuery("_airplay._tcp.local");
        Check(q.Length > 12, "mdns query packet");
        var pkt = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 };
        void Dn(string s) { foreach (var l in s.Split('.')) { pkt.Add((byte)l.Length); pkt.AddRange(System.Text.Encoding.UTF8.GetBytes(l)); } pkt.Add(0); }
        Dn("Mac.local");
        pkt.AddRange([0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 192, 168, 1, 105]);
        pkt.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 192, 168, 1, 110]);
        var mp = ParseMdns([.. pkt]);
        Check(mp.TryGetValue("192.168.1.105", out var n1) && n1 == "Mac.local"
            && mp.TryGetValue("192.168.1.110", out var n2) && n2 == "Mac.local", "mdns parse + compression");
        Check(ParseMdns([0, 1, 2, 3]).Count == 0, "mdns short packet");
        Check(NbnsQuery().Length == 50, "nbns query size");
        var rp3 = new List<byte> { 0x12, 0x34, 0x84, 0x00, 0, 1, 0, 1, 0, 0, 0, 0 };
        rp3.AddRange(NbnsQuery()[12..]);
        rp3.AddRange([0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01, 0, 0, 0, 0]);
        var rd3 = new List<byte> { 0x01 };
        rd3.AddRange(System.Text.Encoding.ASCII.GetBytes("DESKTOP-ABC123 ".PadRight(15)));
        rd3.AddRange([0x00, 0x04, 0x00]);
        rp3.AddRange([(byte)0x00, (byte)rd3.Count]);
        rp3.AddRange(rd3);
        Check(ParseNbns([.. rp3]) == "DESKTOP-ABC123", "nbns parse");
        Check(ParseNbns([0, 1, 2, 3]) is null, "nbns garbage");
        Check(Classify("Dreame Technology", new HashSet<int>())?.Contains("IoT device") == true, "classify dreame iot");
        Check(Classify("Advanced Digital Broadcast SA", new HashSet<int>())?.Contains("Router/AP") == true, "classify adb router");
        Check(Classify(null, new HashSet<int> { 3389 })?.Contains("Windows host") == true, "classify rdp windows");
        Check(IsRandomized("4e:5e:ae:67:1f:98"), "randomized bit set");
        Check(!IsRandomized("f8:e9:03:00:4b:21"), "global mac");
        Check(!IsRandomized(""), "randomized empty");
        Check(Vendor("4e:5e:ae:67:1f:98") is null, "vendor skips randomized");
        if (LocalSubnet() is { } sub)
            Check(ParseIp(sub.start) < ParseIp(sub.end), "localSubnet range");
        foreach (var (ip, _) in LocalAddrs())
            Check(ParseIp(ip) is not null, "localAddrs valid");
        var row = new HostRow("1.2.3.4");
        row.AddPort(22);
        row.Server = "Caddy";
        row.AddPort(80);
        Check(row.Services == "SSH (22), HTTP/Caddy (80)", "services http server");
        Check(row.Links.Count == 1 && row.Links[0].Url == "http://1.2.3.4:80", "service link url");
        return ok;
    }

    private static uint ToUint(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
