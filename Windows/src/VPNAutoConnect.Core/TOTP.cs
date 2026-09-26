using System.Security.Cryptography;
using System.Text;

namespace VPNAutoConnect.Core;

/// 一个 TOTP 账户（与 Google Authenticator 中的一条对应）
public sealed record TOTPConfig
{
    public string Secret { get; init; } = "";      // Base32
    public int Digits { get; init; } = 6;
    public int Period { get; init; } = 30;
    public string Algorithm { get; init; } = "SHA1";
    public string? Issuer { get; init; }
    public string? Account { get; init; }

    public string DisplayName
    {
        get
        {
            var parts = new[] { Issuer, Account }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            return parts.Length == 0 ? "未命名账户" : string.Join(" : ", parts);
        }
    }
}

public static class TOTP
{
    // MARK: - 生成验证码

    public static string? Code(TOTPConfig config, DateTimeOffset? at = null)
    {
        var key = Base32Decode(config.Secret);
        if (key == null || key.Length == 0 || config.Period <= 0) return null;
        var t = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var counter = (ulong)t / (ulong)config.Period;
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xff); counter >>= 8; }

        byte[] mac = config.Algorithm.ToUpperInvariant() switch
        {
            "SHA256" => HMACSHA256.HashData(key, msg),
            "SHA512" => HMACSHA512.HashData(key, msg),
            _ => HMACSHA1.HashData(key, msg),
        };

        int offset = mac[^1] & 0x0f;
        uint bin = ((uint)(mac[offset] & 0x7f) << 24)
            | ((uint)mac[offset + 1] << 16)
            | ((uint)mac[offset + 2] << 8)
            | mac[offset + 3];
        int digits = Math.Clamp(config.Digits, 6, 8);
        uint mod = 1;
        for (int i = 0; i < digits; i++) mod *= 10;
        return (bin % mod).ToString().PadLeft(digits, '0');
    }

    public static long Counter(int period, DateTimeOffset? at = null) =>
        (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / Math.Max(1, period);

    public static int SecondsRemaining(int period, DateTimeOffset? at = null)
    {
        int p = Math.Max(1, period);
        return p - (int)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() % p);
    }

    // MARK: - Base32

    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[]? Base32Decode(string input)
    {
        var cleaned = new string(input.ToUpperInvariant()
            .Where(c => !char.IsWhiteSpace(c) && c != '=' && c != '-').ToArray());
        if (cleaned.Length == 0) return null;
        uint buffer = 0;
        int bits = 0;
        var output = new List<byte>();
        foreach (var c in cleaned)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | (uint)v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return output.ToArray();
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        uint buffer = 0;
        int bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(int)((buffer >> bits) & 0x1f)]);
            }
        }
        if (bits > 0) sb.Append(Alphabet[(int)((buffer << (5 - bits)) & 0x1f)]);
        return sb.ToString();
    }

    // MARK: - 解析用户输入

    /// 支持三种输入：
    /// 1. otpauth://totp/...?secret=...   （绑定时的原始二维码）
    /// 2. otpauth-migration://offline?data=...  （Google Authenticator「导出账户」生成的二维码）
    /// 3. 纯 Base32 密钥
    public static List<TOTPConfig> ParseAny(string text)
    {
        var t = text.Trim();
        var lower = t.ToLowerInvariant();
        if (lower.StartsWith("otpauth-migration://")) return ParseMigration(t);
        if (lower.StartsWith("otpauth://"))
            return ParseOtpauth(t) is { } c ? [c] : [];
        var secret = t.Replace(" ", "").ToUpperInvariant();
        if (Base32Decode(secret) is { Length: >= 10 }) return [new TOTPConfig { Secret = secret }];
        return [];
    }

    /// 拆出 scheme://host/path?query 中的 host、path 与查询参数（参数名小写）
    static (string host, string path, Dictionary<string, string> query) SplitUri(string uri)
    {
        var rest = uri[(uri.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var q = rest.IndexOf('?');
        var query = new Dictionary<string, string>();
        if (q >= 0)
        {
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var name = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair).ToLowerInvariant();
                var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : "";
                query.TryAdd(name, value);
            }
            rest = rest[..q];
        }
        var slash = rest.IndexOf('/');
        var host = slash >= 0 ? rest[..slash] : rest;
        var path = slash >= 0 ? rest[(slash + 1)..] : "";
        return (host, path, query);
    }

    public static TOTPConfig? ParseOtpauth(string uri)
    {
        var (host, path, query) = SplitUri(uri);
        if (!host.Equals("totp", StringComparison.OrdinalIgnoreCase)) return null;
        if (!query.TryGetValue("secret", out var secret) || Base32Decode(secret) == null) return null;

        var label = Uri.UnescapeDataString(path);
        query.TryGetValue("issuer", out var issuer);
        string? account = label;
        var idx = label.IndexOf(':');
        if (idx >= 0)
        {
            issuer ??= label[..idx];
            account = label[(idx + 1)..].Trim();
        }
        return new TOTPConfig
        {
            Secret = secret.ToUpperInvariant(),
            Digits = int.TryParse(query.GetValueOrDefault("digits"), out var d) ? d : 6,
            Period = int.TryParse(query.GetValueOrDefault("period"), out var p) ? p : 30,
            Algorithm = (query.GetValueOrDefault("algorithm") ?? "SHA1").ToUpperInvariant(),
            Issuer = issuer,
            Account = account,
        };
    }

    /// 解析 Google Authenticator 导出格式（protobuf）
    public static List<TOTPConfig> ParseMigration(string uri)
    {
        var result = new List<TOTPConfig>();
        if (!SplitUri(uri).query.TryGetValue("data", out var b64)) return result;
        b64 = b64.Replace('-', '+').Replace('_', '/').Replace(' ', '+');
        while (b64.Length % 4 != 0) b64 += "=";
        byte[] data;
        try { data = Convert.FromBase64String(b64); } catch (FormatException) { return result; }

        var reader = new ProtoReader(data);
        while (reader.Next() is (int field, ProtoReader.Value value))
        {
            if (field != 1 || value.Bytes == null) continue;
            var r = new ProtoReader(value.Bytes);
            byte[] secret = [];
            string name = "", issuer = "";
            ulong algorithm = 1, digits = 1, type = 2;
            while (r.Next() is (int f, ProtoReader.Value v))
            {
                switch (f)
                {
                    case 1 when v.Bytes != null: secret = v.Bytes; break;
                    case 2 when v.Bytes != null: name = Encoding.UTF8.GetString(v.Bytes); break;
                    case 3 when v.Bytes != null: issuer = Encoding.UTF8.GetString(v.Bytes); break;
                    case 4 when v.Bytes == null: algorithm = v.Varint; break;
                    case 5 when v.Bytes == null: digits = v.Varint; break;
                    case 6 when v.Bytes == null: type = v.Varint; break;
                }
            }
            if ((type != 2 && type != 0) || secret.Length == 0) continue; // 只要 TOTP
            var algo = algorithm switch { 2 => "SHA256", 3 => "SHA512", _ => "SHA1" };
            var account = name;
            var idx = name.IndexOf(':');
            if (idx >= 0)
            {
                account = name[(idx + 1)..].Trim();
                if (issuer.Length == 0) issuer = name[..idx];
            }
            result.Add(new TOTPConfig
            {
                Secret = Base32Encode(secret),
                Digits = digits == 2 ? 8 : 6,
                Period = 30,
                Algorithm = algo,
                Issuer = issuer.Length == 0 ? null : issuer,
                Account = account.Length == 0 ? null : account,
            });
        }
        return result;
    }
}

/// 极简 protobuf 读取器（只支持 varint 与 length-delimited，足够解析 GA 导出数据）
internal sealed class ProtoReader(byte[] data)
{
    public readonly record struct Value(ulong Varint, byte[]? Bytes);
    int pos;

    ulong? ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) return null;
        }
        return null;
    }

    public (int field, Value value)? Next()
    {
        while (pos < data.Length)
        {
            if (ReadVarint() is not { } key) return null;
            int field = (int)(key >> 3);
            switch (key & 0x7)
            {
                case 0:
                    if (ReadVarint() is not { } v) return null;
                    return (field, new Value(v, null));
                case 2:
                    if (ReadVarint() is not { } len || (ulong)pos + len > (ulong)data.Length) return null;
                    var bytes = data[pos..(pos + (int)len)];
                    pos += (int)len;
                    return (field, new Value(0, bytes));
                case 1: pos += 8; break;
                case 5: pos += 4; break;
                default: return null;
            }
        }
        return null;
    }
}
