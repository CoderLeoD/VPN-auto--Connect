import XCTest
@testable import VPNAutoConnect

final class TOTPTests: XCTestCase {
    // RFC 6238 附录 B 测试向量
    let rfcSecret = TOTP.base32Encode(Data("12345678901234567890".utf8))

    func testRFC6238SHA1() {
        let c = TOTPConfig(secret: rfcSecret, digits: 8)
        XCTAssertEqual(TOTP.code(for: c, at: Date(timeIntervalSince1970: 59)), "94287082")
        XCTAssertEqual(TOTP.code(for: c, at: Date(timeIntervalSince1970: 1111111109)), "07081804")
        XCTAssertEqual(TOTP.code(for: c, at: Date(timeIntervalSince1970: 20000000000)), "65353130")
    }

    func testSixDigits() {
        let c = TOTPConfig(secret: rfcSecret)
        XCTAssertEqual(TOTP.code(for: c, at: Date(timeIntervalSince1970: 59)), "287082")
    }

    func testBase32RoundTrip() {
        let d = Data((0..<37).map { UInt8($0 * 7 % 256) })
        XCTAssertEqual(TOTP.base32Decode(TOTP.base32Encode(d)), d)
        XCTAssertEqual(TOTP.base32Decode("jbsw y3dp ehpk 3pxp"), TOTP.base32Decode("JBSWY3DPEHPK3PXP"))
    }

    func testParseOtpauth() {
        let r = TOTP.parseAny("otpauth://totp/ACME%20Co:john@example.com?secret=JBSWY3DPEHPK3PXP&issuer=ACME%20Co&period=30")
        XCTAssertEqual(r.count, 1)
        XCTAssertEqual(r[0].secret, "JBSWY3DPEHPK3PXP")
        XCTAssertEqual(r[0].issuer, "ACME Co")
        XCTAssertEqual(r[0].account, "john@example.com")
    }

    func testParseRawSecret() {
        XCTAssertEqual(TOTP.parseAny("jbsw y3dp ehpk 3pxp").first?.secret, "JBSWY3DPEHPK3PXP")
        XCTAssertTrue(TOTP.parseAny("hello world!").isEmpty)
    }

    func testParseMigration() {
        // 手工构造 GA 导出 protobuf：otp_parameters{secret, name, issuer, algo=SHA1, digits=SIX, type=TOTP}
        let secret: [UInt8] = Array("12345678901234567890".utf8)
        let name: [UInt8] = Array("VPN:max".utf8)
        let issuer: [UInt8] = Array("Corp".utf8)
        var otp: [UInt8] = [0x0a, UInt8(secret.count)] + secret
        otp += [0x12, UInt8(name.count)] + name
        otp += [0x1a, UInt8(issuer.count)] + issuer
        otp += [0x20, 1, 0x28, 1, 0x30, 2]
        let payload: [UInt8] = [0x0a, UInt8(otp.count)] + otp + [0x10, 1]
        let b64 = Data(payload).base64EncodedString()
            .addingPercentEncoding(withAllowedCharacters: .alphanumerics)!
        let r = TOTP.parseAny("otpauth-migration://offline?data=\(b64)")
        XCTAssertEqual(r.count, 1)
        XCTAssertEqual(r[0].secret, rfcSecret)
        XCTAssertEqual(r[0].issuer, "Corp")
        XCTAssertEqual(r[0].account, "max")
        XCTAssertEqual(TOTP.code(for: r[0], at: Date(timeIntervalSince1970: 59)), "287082")
    }

    func testQRDecodeGenerated() {
        let text = "otpauth://totp/Test?secret=JBSWY3DPEHPK3PXP"
        let filter = CIFilter(name: "CIQRCodeGenerator")!
        filter.setValue(Data(text.utf8), forKey: "inputMessage")
        let img = filter.outputImage!.transformed(by: CGAffineTransform(scaleX: 10, y: 10))
        XCTAssertEqual(QRDecoder.decode(ciImage: img), [text])
    }

    @MainActor func testParseState() {
        let out = """
          >> state: Unknown
          >> state: Disconnected
          >> notice: Ready to connect.
          >> state: Connected
        VPN>
        """
        XCTAssertEqual(VPNController.parseState(out), .connected)
        XCTAssertEqual(VPNController.parseState("  >> state: Unknown\n  >> state: Disconnected"), .disconnected)
        let approval = """
          >> state: Unknown
          >> warning: VPN service needs user approval.
        VPN>   >> error: Unable to proceed.
        Cannot contact the VPN service.
        """
        XCTAssertEqual(VPNController.parseState(approval), .serviceUnavailable)
    }
}
