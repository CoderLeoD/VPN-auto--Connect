import Foundation
import CryptoKit

/// 一个 TOTP 账户（与 Google Authenticator 中的一条对应）
struct TOTPConfig: Codable, Equatable {
    var secret: String          // Base32
    var digits: Int = 6
    var period: Int = 30
    var algorithm: String = "SHA1"
    var issuer: String? = nil
    var account: String? = nil

    var displayName: String {
        let parts = [issuer, account].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? "未命名账户" : parts.joined(separator: " : ")
    }
}

enum TOTP {
    // MARK: - 生成验证码

    static func code(for config: TOTPConfig, at date: Date = Date()) -> String? {
        guard let key = base32Decode(config.secret), !key.isEmpty, config.period > 0 else { return nil }
        let counter = UInt64(date.timeIntervalSince1970) / UInt64(config.period)
        var bigCounter = counter.bigEndian
        let msg = Data(bytes: &bigCounter, count: 8)
        let symKey = SymmetricKey(data: key)

        let mac: [UInt8]
        switch config.algorithm.uppercased() {
        case "SHA256": mac = Array(HMAC<SHA256>.authenticationCode(for: msg, using: symKey))
        case "SHA512": mac = Array(HMAC<SHA512>.authenticationCode(for: msg, using: symKey))
        default:       mac = Array(HMAC<Insecure.SHA1>.authenticationCode(for: msg, using: symKey))
        }

        let offset = Int(mac[mac.count - 1] & 0x0f)
        let bin = (UInt32(mac[offset] & 0x7f) << 24)
            | (UInt32(mac[offset + 1]) << 16)
            | (UInt32(mac[offset + 2]) << 8)
            | UInt32(mac[offset + 3])
        let digits = max(6, min(8, config.digits))
        var mod: UInt32 = 1
        for _ in 0..<digits { mod *= 10 }
        let value = bin % mod
        return String(format: "%0\(digits)u", value)
    }

    static func secondsRemaining(period: Int, at date: Date = Date()) -> Int {
        let p = max(1, period)
        return p - Int(date.timeIntervalSince1970) % p
    }

    // MARK: - Base32

    static func base32Decode(_ input: String) -> Data? {
        let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")
        var map: [Character: UInt8] = [:]
        for (i, c) in alphabet.enumerated() { map[c] = UInt8(i) }

        let cleaned = input.uppercased().filter { !$0.isWhitespace && $0 != "=" && $0 != "-" }
        guard !cleaned.isEmpty else { return nil }
        var buffer: UInt32 = 0
        var bits = 0
        var out = Data()
        for c in cleaned {
            guard let v = map[c] else { return nil }
            buffer = (buffer << 5) | UInt32(v)
            bits += 5
            if bits >= 8 {
                bits -= 8
                out.append(UInt8((buffer >> UInt32(bits)) & 0xff))
            }
        }
        return out
    }

    static func base32Encode(_ data: Data) -> String {
        let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")
        var out = ""
        var buffer: UInt32 = 0
        var bits = 0
        for byte in data {
            buffer = (buffer << 8) | UInt32(byte)
            bits += 8
            while bits >= 5 {
                bits -= 5
                out.append(alphabet[Int((buffer >> UInt32(bits)) & 0x1f)])
            }
        }
        if bits > 0 {
            out.append(alphabet[Int((buffer << UInt32(5 - bits)) & 0x1f)])
        }
        return out
    }

    // MARK: - 解析用户输入

    /// 支持三种输入：
    /// 1. otpauth://totp/...?secret=...   （绑定时的原始二维码）
    /// 2. otpauth-migration://offline?data=...  （Google Authenticator「导出账户」生成的二维码）
    /// 3. 纯 Base32 密钥
    static func parseAny(_ text: String) -> [TOTPConfig] {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = t.lowercased()
        if lower.hasPrefix("otpauth-migration://") {
            return parseMigration(t)
        }
        if lower.hasPrefix("otpauth://") {
            return parseOtpauth(t).map { [$0] } ?? []
        }
        let secret = t.replacingOccurrences(of: " ", with: "").uppercased()
        if let key = base32Decode(secret), key.count >= 10 {
            return [TOTPConfig(secret: secret)]
        }
        return []
    }

    static func parseOtpauth(_ uri: String) -> TOTPConfig? {
        guard let comps = URLComponents(string: uri),
              comps.host?.lowercased() == "totp" else { return nil }
        let items = comps.queryItems ?? []
        func q(_ name: String) -> String? {
            items.first { $0.name.lowercased() == name }?.value
        }
        guard let secret = q("secret"), base32Decode(secret) != nil else { return nil }

        var label = comps.path
        if label.hasPrefix("/") { label.removeFirst() }
        label = label.removingPercentEncoding ?? label
        var issuer = q("issuer")
        var account: String? = label
        if let idx = label.firstIndex(of: ":") {
            if issuer == nil { issuer = String(label[..<idx]) }
            account = String(label[label.index(after: idx)...]).trimmingCharacters(in: .whitespaces)
        }
        return TOTPConfig(
            secret: secret.uppercased(),
            digits: Int(q("digits") ?? "") ?? 6,
            period: Int(q("period") ?? "") ?? 30,
            algorithm: (q("algorithm") ?? "SHA1").uppercased(),
            issuer: issuer,
            account: account
        )
    }

    /// 解析 Google Authenticator 导出格式（protobuf）
    static func parseMigration(_ uri: String) -> [TOTPConfig] {
        guard let comps = URLComponents(string: uri),
              var b64 = comps.queryItems?.first(where: { $0.name == "data" })?.value else { return [] }
        b64 = b64.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        b64 = b64.replacingOccurrences(of: " ", with: "+")
        while b64.count % 4 != 0 { b64.append("=") }
        guard let data = Data(base64Encoded: b64) else { return [] }

        var result: [TOTPConfig] = []
        var reader = ProtoReader(data: [UInt8](data))
        while let (field, value) = reader.next() {
            guard field == 1, case .bytes(let otpBytes) = value else { continue }
            var r = ProtoReader(data: otpBytes)
            var secret = Data(), name = "", issuer = ""
            var algorithm = 1, digits = 1, type = 2
            while let (f, v) = r.next() {
                switch (f, v) {
                case (1, .bytes(let b)): secret = Data(b)
                case (2, .bytes(let b)): name = String(decoding: b, as: UTF8.self)
                case (3, .bytes(let b)): issuer = String(decoding: b, as: UTF8.self)
                case (4, .varint(let n)): algorithm = Int(n)
                case (5, .varint(let n)): digits = Int(n)
                case (6, .varint(let n)): type = Int(n)
                default: break
                }
            }
            guard type == 2 || type == 0, !secret.isEmpty else { continue } // 只要 TOTP
            let algo = [2: "SHA256", 3: "SHA512"][algorithm] ?? "SHA1"
            var account = name
            if let idx = name.firstIndex(of: ":") {
                account = String(name[name.index(after: idx)...]).trimmingCharacters(in: .whitespaces)
                if issuer.isEmpty { issuer = String(name[..<idx]) }
            }
            result.append(TOTPConfig(
                secret: base32Encode(secret),
                digits: digits == 2 ? 8 : 6,
                period: 30,
                algorithm: algo,
                issuer: issuer.isEmpty ? nil : issuer,
                account: account.isEmpty ? nil : account
            ))
        }
        return result
    }
}

/// 极简 protobuf 读取器（只支持 varint 与 length-delimited，足够解析 GA 导出数据）
private struct ProtoReader {
    enum Value { case varint(UInt64), bytes([UInt8]) }
    let data: [UInt8]
    var pos = 0

    init(data: [UInt8]) { self.data = data }

    mutating func readVarint() -> UInt64? {
        var result: UInt64 = 0
        var shift: UInt64 = 0
        while pos < data.count {
            let b = data[pos]; pos += 1
            result |= UInt64(b & 0x7f) << shift
            if b & 0x80 == 0 { return result }
            shift += 7
            if shift > 63 { return nil }
        }
        return nil
    }

    mutating func next() -> (Int, Value)? {
        while pos < data.count {
            guard let key = readVarint() else { return nil }
            let field = Int(key >> 3)
            switch key & 0x7 {
            case 0:
                guard let v = readVarint() else { return nil }
                return (field, .varint(v))
            case 2:
                guard let len = readVarint(), pos + Int(len) <= data.count else { return nil }
                let bytes = Array(data[pos..<pos + Int(len)])
                pos += Int(len)
                return (field, .bytes(bytes))
            case 1: pos += 8
            case 5: pos += 4
            default: return nil
            }
        }
        return nil
    }
}
