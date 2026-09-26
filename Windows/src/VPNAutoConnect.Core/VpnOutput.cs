namespace VPNAutoConnect.Core;

public enum VPNState
{
    Connected, Disconnected, Connecting, Reconnecting, Disconnecting, Unknown, NotInstalled,
    /// Cisco 的后台服务（Cisco Secure Client Agent）没运行
    ServiceUnavailable,
}

public static class VPNStateExtensions
{
    public static string Title(this VPNState s) => s switch
    {
        VPNState.Connected => "已连接",
        VPNState.Disconnected => "未连接",
        VPNState.Connecting => "正在连接…",
        VPNState.Reconnecting => "Cisco 正在重连…",
        VPNState.Disconnecting => "正在断开…",
        VPNState.NotInstalled => "未找到 Cisco Secure Client",
        VPNState.ServiceUnavailable => "Cisco VPN 服务未运行",
        _ => "状态未知",
    };
}

/// 解析 Cisco 命令行 vpncli.exe 的输出
public static class VpnOutput
{
    static readonly string[] ServiceUnavailableMarks =
    [
        "needs user approval",
        "cannot contact the vpn service",
        "vpn service is not available",
        "vpn service is unavailable",
    ];

    static readonly string[] AuthFailureMarks =
        ["login failed", "login denied", "authentication failed", "access denied"];

    public static VPNState ParseState(string output)
    {
        var lower = output.ToLowerInvariant();
        if (ServiceUnavailableMarks.Any(lower.Contains)) return VPNState.ServiceUnavailable;
        // 输出中会有多行 ">> state: xxx"，以最后一行为准
        var states = Lines(output)
            .Select(line => line.IndexOf("state:", StringComparison.Ordinal) is var i and >= 0
                ? line[(i + 6)..].Trim().ToLowerInvariant() : null)
            .OfType<string>()
            .ToList();
        var s = states.LastOrDefault(x => x != "unknown") ?? states.LastOrDefault();
        if (s == null) return VPNState.Unknown;
        if (s.StartsWith("connected")) return VPNState.Connected;
        if (s.StartsWith("disconnected")) return VPNState.Disconnected;
        if (s.StartsWith("connecting")) return VPNState.Connecting;
        if (s.StartsWith("reconnecting")) return VPNState.Reconnecting;
        if (s.StartsWith("disconnecting")) return VPNState.Disconnecting;
        return VPNState.Unknown;
    }

    public static bool IsAuthFailure(string output)
    {
        var lower = output.ToLowerInvariant();
        return AuthFailureMarks.Any(lower.Contains);
    }

    public static string Redact(string s, IEnumerable<string> secrets) =>
        secrets.Where(x => !string.IsNullOrEmpty(x)).Aggregate(s, (acc, x) => acc.Replace(x, "******"));

    public static string Tail(string s, int lines) =>
        string.Join("\n", Lines(s).Select(l => l.Trim()).Where(l => l.Length > 0 && l != "VPN>").TakeLast(lines));

    public static string? LastMeaningfulLine(string s) =>
        Lines(s)
            .Select(l => l.Replace("VPN>", "").Trim())
            .LastOrDefault(l => l.StartsWith(">> error") || l.StartsWith(">> notice") || l.StartsWith(">> warning"))
            ?.Replace(">> ", "");

    static string[] Lines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
