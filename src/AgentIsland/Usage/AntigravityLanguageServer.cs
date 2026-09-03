using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentIsland.Usage;

/// Client for Antigravity's local language server — the only place its quota
/// is actually readable.
///
/// The cloud path other tools document is a dead end (verified on a real
/// signed-in account, 2026-08-08): `retrieveUserQuota` answers with legacy
/// Gemini Code Assist buckets, `loadCodeAssist` says UNSUPPORTED_CLIENT, and
/// `retrieveUserQuotaSummary` 403s for a plain Bearer caller. The CLI
/// reaches the same method through its own language server, which does hold
/// the real numbers.
///
/// That server is embedded in the `agy` process rather than spawned
/// separately, so quota is readable exactly while Antigravity is running.
/// There is no on-disk cache to fall back on, so the store keeps the last
/// good snapshot and the UI dates it.
public static class AntigravityLanguageServer
{
    private const string ServicePrefix = "/exa.language_server_pb.LanguageServerService/";
    private const string Host = "127.0.0.1";

    public sealed record Reply(int Status, byte[] Body);

    // The server speaks HTTPS with a self-signed certificate. The trust
    // override is scoped to loopback so it can never soften validation for
    // a real host.
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            request.RequestUri?.Host == Host,
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private static int _cachedPort;

    public static async Task<Reply?> Call(
        string method,
        int port,
        string body = "{}",
        string? csrfToken = null,
        double timeoutSeconds = 6)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{Host}:{port}{ServicePrefix}{method}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        // The CLI's own server rejects nothing and wants no token; the
        // desktop IDE gates on one. Sending an empty header to the CLI would
        // be worse than sending none, so it stays absent unless we have one.
        if (!string.IsNullOrEmpty(csrfToken))
        {
            request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return new Reply((int)response.StatusCode, data);
        }
        catch
        {
            return null;
        }
    }

    /// Ports are assigned at launch and written nowhere, so they are found
    /// by walking the running Antigravity processes' listening sockets —
    /// GetExtendedTcpTable in-process; shelling out to netstat on every
    /// refresh tick would fork twice a minute for the life of the app.
    public static async Task<int?> Discover()
    {
        var cached = _cachedPort;
        if (cached > 0 && await IsAlive(cached).ConfigureAwait(false)) return cached;
        foreach (var pid in AntigravityProcessIds())
        {
            foreach (var port in ListeningPorts(pid))
            {
                if (port == cached) continue;
                if (await IsAlive(port).ConfigureAwait(false))
                {
                    _cachedPort = port;
                    return port;
                }
            }
        }
        _cachedPort = 0;
        return null;
    }

    /// `GetUnleashData` is the cheapest method that proves this is the RPC
    /// port rather than a sibling plain-HTTP port the process also opens.
    /// 401 counts as alive: the port is right and only the token is missing.
    private static async Task<bool> IsAlive(int port)
    {
        var reply = await Call(
            "GetUnleashData", port, "{\"wrapper_data\":{}}", timeoutSeconds: 2)
            .ConfigureAwait(false);
        return reply is { Status: 200 or 401 };
    }

    /// Matches the CLI (whose real binary is `antigravity`, reached through
    /// an `agy` shim), the desktop app, and a separately spawned
    /// `language_server*`. Process names on Windows drop the .exe extension.
    public static bool IsAntigravityName(string name) =>
        name.Equals("agy", StringComparison.OrdinalIgnoreCase)
        || name.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
        || name.Equals("antigravity-cli", StringComparison.OrdinalIgnoreCase)
        || name.Contains("antigravity", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("language_server", StringComparison.OrdinalIgnoreCase);

    public static List<int> AntigravityProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (IsAntigravityName(process.ProcessName)) ids.Add(process.Id);
            }
            catch
            {
                // Access denied on system processes — not ours anyway.
            }
            finally
            {
                process.Dispose();
            }
        }
        return ids;
    }

    // MARK: - Listening-port enumeration (iphlpapi)

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int ipVersion,
        int tableClass,
        uint reserved);

    public static List<int> ListeningPorts(int pid)
    {
        var ports = new List<int>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) return ports;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                return ports;
            }
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);
                if (row.OwningPid != (uint)pid) continue;
                // LocalPort is in network byte order, low 16 bits.
                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                if (port > 0 && !ports.Contains(port)) ports.Add(port);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return ports;
    }

    /// The CLI needs no token and must not be sent one. The desktop IDE
    /// passes its own on the command line, so it is lifted from the process
    /// command line only after a 401 says the plain call was refused —
    /// a strictly additive retry on a path that already failed.
    public static string? CsrfToken(int pid)
    {
        var args = ProcessCommandLine(pid);
        if (args is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            args, "--csrf_token[=\\s]+([^\\s\"]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ProcessCommandLine(int pid)
    {
        // WMI without a System.Management dependency: wmic is gone on
        // modern Windows, so query via PowerShell's CIM cmdlet. This runs
        // only on the rare desktop-IDE 401 retry path, never per tick.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId = "
                    + pid + "').CommandLine\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
