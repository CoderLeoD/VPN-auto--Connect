import XCTest
@testable import VPNAutoConnect

final class VPNOutputTests: XCTestCase {
    // Taken from a real failure: another `vpn -s` left behind by a crashed instance held the lock
    let busyOutput = """
    >> contacting host (vpn.example.com:443) for login information...
    >> state: Disconnected
    >> error: Connect capability is unavailable. Another Cisco Secure Client application acquired it. Terminating the other application and restarting this application may resolve this issue. Otherwise, contact your system administrator.
    VPN> someone
    """

    func testDetectsConnectCapabilityBusy() {
        XCTAssertTrue(VPNController.isConnectCapabilityBusy(busyOutput))
        XCTAssertFalse(VPNController.isConnectCapabilityBusy(">> state: Connected"))
        XCTAssertFalse(VPNController.isConnectCapabilityBusy(">> Login failed."))
    }

    func testStrayCLIPidsPicksOnlyCiscoCLI() {
        let ps = """
          101 /opt/cisco/secureclient/bin/vpn -s
          102 /opt/cisco/secureclient/bin/vpn state
          103 /opt/cisco/secureclient/bin/vpn
          104 /opt/cisco/secureclient/bin/vpnagentd
          105 /Applications/VPN AutoConnect.app/Contents/MacOS/VPNAutoConnect
          106 /opt/cisco/anyconnect/bin/vpn -s
          107 grep /opt/cisco/secureclient/bin/vpn
        garbage line
        """
        let pids = VPNController.strayCLIPids(psOutput: ps, cliPaths: VPNController.cliCandidates,
                                              excluding: [102])
        XCTAssertEqual(pids, [101, 103, 106])
    }

    func testStrayCLIPidsEmpty() {
        XCTAssertEqual(VPNController.strayCLIPids(psOutput: "", cliPaths: VPNController.cliCandidates,
                                                  excluding: []), [])
    }

    func testShellStopWhenEndsProcessEarly() async {
        let start = Date()
        let r = await Shell.run("/bin/sh", args: ["-c", "echo 'Connect capability is unavailable'; sleep 30"],
                                input: nil, timeout: 20, stopWhen: VPNController.isConnectCapabilityBusy)
        XCTAssertLessThan(Date().timeIntervalSince(start), 5)
        XCTAssertFalse(r.timedOut)
        XCTAssertTrue(r.output.contains("Connect capability"))
        XCTAssertTrue(Shell.runningPids().isEmpty)
    }

    func testShellTimeoutKillsProcess() async {
        let r = await Shell.run("/bin/sleep", args: ["30"], input: nil, timeout: 1)
        XCTAssertTrue(r.timedOut)
        XCTAssertTrue(Shell.runningPids().isEmpty)
    }
}
