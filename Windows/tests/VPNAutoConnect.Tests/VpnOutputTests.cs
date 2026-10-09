using VPNAutoConnect.Core;
using Xunit;

namespace VPNAutoConnect.Tests;

public class VpnOutputTests
{
    // Taken from a real failure: a vpn CLI left behind by a crashed instance held the lock
    const string BusyOutput =
        ">> contacting host (vpn.example.com:443) for login information...\r\n" +
        ">> state: Disconnected\r\n" +
        ">> error: Connect capability is unavailable. Another Cisco Secure Client application acquired it. " +
        "Terminating the other application and restarting this application may resolve this issue.\r\n" +
        "VPN> someone\r\n";

    [Fact]
    public void DetectsConnectCapabilityBusy()
    {
        Assert.True(VpnOutput.IsConnectCapabilityBusy(BusyOutput));
        Assert.True(VpnOutput.IsConnectCapabilityBusy("CONNECT CAPABILITY IS UNAVAILABLE"));
        Assert.False(VpnOutput.IsConnectCapabilityBusy(">> state: Connected"));
        Assert.False(VpnOutput.IsConnectCapabilityBusy(">> Login failed."));
    }

    [Fact]
    public void BusyIsNotAuthFailure()
    {
        Assert.False(VpnOutput.IsAuthFailure(BusyOutput));
        Assert.Equal(VPNState.Disconnected, VpnOutput.ParseState(BusyOutput));
    }
}
