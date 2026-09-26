using System.Text;
using VPNAutoConnect.Core;
using Xunit;
using ZXing;
using ZXing.QrCode;

namespace VPNAutoConnect.Tests;

public class TOTPTests
{
    // RFC 6238 附录 B 测试向量
    static readonly string RfcSecret = TOTP.Base32Encode(Encoding.UTF8.GetBytes("12345678901234567890"));

    static DateTimeOffset At(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    [Fact]
    public void RFC6238SHA1()
    {
        var c = new TOTPConfig { Secret = RfcSecret, Digits = 8 };
        Assert.Equal("94287082", TOTP.Code(c, At(59)));
        Assert.Equal("07081804", TOTP.Code(c, At(1111111109)));
        Assert.Equal("65353130", TOTP.Code(c, At(20000000000)));
    }

    [Fact]
    public void SixDigits()
    {
        var c = new TOTPConfig { Secret = RfcSecret };
        Assert.Equal("287082", TOTP.Code(c, At(59)));
    }

    [Fact]
    public void Base32RoundTrip()
    {
        var d = Enumerable.Range(0, 37).Select(i => (byte)(i * 7 % 256)).ToArray();
        Assert.Equal(d, TOTP.Base32Decode(TOTP.Base32Encode(d)));
        Assert.Equal(TOTP.Base32Decode("JBSWY3DPEHPK3PXP"), TOTP.Base32Decode("jbsw y3dp ehpk 3pxp"));
    }

    [Fact]
    public void ParseOtpauth()
    {
        var r = TOTP.ParseAny("otpauth://totp/ACME%20Co:john@example.com?secret=JBSWY3DPEHPK3PXP&issuer=ACME%20Co&period=30");
        Assert.Single(r);
        Assert.Equal("JBSWY3DPEHPK3PXP", r[0].Secret);
        Assert.Equal("ACME Co", r[0].Issuer);
        Assert.Equal("john@example.com", r[0].Account);
    }

    [Fact]
    public void ParseRawSecret()
    {
        Assert.Equal("JBSWY3DPEHPK3PXP", TOTP.ParseAny("jbsw y3dp ehpk 3pxp").FirstOrDefault()?.Secret);
        Assert.Empty(TOTP.ParseAny("hello world!"));
    }

    [Fact]
    public void ParseMigration()
    {
        // 手工构造 GA 导出 protobuf：otp_parameters{secret, name, issuer, algo=SHA1, digits=SIX, type=TOTP}
        var secret = Encoding.UTF8.GetBytes("12345678901234567890");
        var name = Encoding.UTF8.GetBytes("VPN:max");
        var issuer = Encoding.UTF8.GetBytes("Corp");
        var otp = new List<byte> { 0x0a, (byte)secret.Length };
        otp.AddRange(secret);
        otp.AddRange([0x12, (byte)name.Length]); otp.AddRange(name);
        otp.AddRange([0x1a, (byte)issuer.Length]); otp.AddRange(issuer);
        otp.AddRange([0x20, 1, 0x28, 1, 0x30, 2]);
        var payload = new List<byte> { 0x0a, (byte)otp.Count };
        payload.AddRange(otp);
        payload.AddRange([0x10, 1]);
        var b64 = Uri.EscapeDataString(Convert.ToBase64String(payload.ToArray()));

        var r = TOTP.ParseAny($"otpauth-migration://offline?data={b64}");
        Assert.Single(r);
        Assert.Equal(RfcSecret, r[0].Secret);
        Assert.Equal("Corp", r[0].Issuer);
        Assert.Equal("max", r[0].Account);
        Assert.Equal("287082", TOTP.Code(r[0], At(59)));
    }

    [Fact]
    public void QRDecodeGenerated()
    {
        const string text = "otpauth://totp/Test?secret=JBSWY3DPEHPK3PXP";
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions { Width = 300, Height = 300, Margin = 2 },
        };
        var px = writer.Write(text);
        Assert.Equal([text], QRDecoder.Decode(px.Pixels, px.Width, px.Height));
    }

    [Fact]
    public void ParseState()
    {
        const string output = """
              >> state: Unknown
              >> state: Disconnected
              >> notice: Ready to connect.
              >> state: Connected
            VPN>
            """;
        Assert.Equal(VPNState.Connected, VpnOutput.ParseState(output));
        Assert.Equal(VPNState.Disconnected, VpnOutput.ParseState("  >> state: Unknown\r\n  >> state: Disconnected\r\n"));
        const string noService = """
            Cisco Secure Client (version 5.1.2.42) .

              >> error: The VPN Service is not available. Exiting.
            """;
        Assert.Equal(VPNState.ServiceUnavailable, VpnOutput.ParseState(noService));
    }

    [Fact]
    public void RedactAndTail()
    {
        var s = VpnOutput.Redact("Password: hunter2\nAnswer: 123456\nVPN>\n", ["hunter2", "123456", ""]);
        Assert.DoesNotContain("hunter2", s);
        Assert.DoesNotContain("123456", s);
        Assert.Equal("Answer: ******", VpnOutput.Tail(s, 1));
        Assert.Equal("error: Login failed.", VpnOutput.LastMeaningfulLine("VPN>  >> notice: x\n  >> error: Login failed.\n"));
    }
}
