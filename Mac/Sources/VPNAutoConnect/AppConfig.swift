import Foundation
import ServiceManagement

/// 非敏感配置存 UserDefaults；密码和 TOTP 密钥存钥匙串
final class AppConfig: ObservableObject {
    static let shared = AppConfig()

    private let defaults = UserDefaults.standard

    @Published var server: String { didSet { defaults.set(server, forKey: "server") } }
    @Published var group: String { didSet { defaults.set(group, forKey: "group") } }
    @Published var username: String { didSet { defaults.set(username, forKey: "username") } }
    @Published var autoReconnect: Bool { didSet { defaults.set(autoReconnect, forKey: "autoReconnect") } }
    @Published var quitCiscoGUI: Bool { didSet { defaults.set(quitCiscoGUI, forKey: "quitCiscoGUI") } }
    @Published var acceptBanner: Bool { didSet { defaults.set(acceptBanner, forKey: "acceptBanner") } }
    @Published var checkInterval: Int { didSet { defaults.set(checkInterval, forKey: "checkInterval") } }

    @Published private(set) var hasPassword = false
    @Published private(set) var totp: TOTPConfig?
    /// 钥匙串是否已成功读取过（读取可能要等用户在授权弹窗上点“允许”）
    @Published private(set) var secretsLoaded = false
    @Published private(set) var keychainError: String?
    private var cachedPassword: String?

    private init() {
        defaults.register(defaults: [
            "autoReconnect": true,
            "quitCiscoGUI": true,
            "acceptBanner": true,
            "checkInterval": 15,
        ])
        server = defaults.string(forKey: "server") ?? ""
        group = defaults.string(forKey: "group") ?? ""
        username = defaults.string(forKey: "username") ?? ""
        autoReconnect = defaults.bool(forKey: "autoReconnect")
        quitCiscoGUI = defaults.bool(forKey: "quitCiscoGUI")
        acceptBanner = defaults.bool(forKey: "acceptBanner")
        checkInterval = defaults.integer(forKey: "checkInterval")
    }

    /// 在后台线程读取钥匙串（可能弹出授权框，不能卡住主线程），成功后缓存在内存；失败则下次再试
    @MainActor
    func loadSecretsIfNeeded() async {
        guard !secretsLoaded else { return }
        let (pw, json) = await Task.detached { (Keychain.read("password"), Keychain.read("totp")) }.value
        let failed = [pw.status, json.status].first { $0 != errSecSuccess && $0 != errSecItemNotFound }
        if let failed {
            keychainError = "无法读取钥匙串（错误码 \(failed)），请在授权弹窗中点“始终允许”"
            return
        }
        keychainError = nil
        cachedPassword = pw.value
        hasPassword = !(pw.value ?? "").isEmpty
        if let data = json.value?.data(using: .utf8) {
            totp = try? JSONDecoder().decode(TOTPConfig.self, from: data)
        }
        secretsLoaded = true
    }

    var password: String? { cachedPassword }

    func setPassword(_ value: String) {
        Keychain.set(value, for: "password")
        cachedPassword = value
        hasPassword = !value.isEmpty
    }

    func setTOTP(_ config: TOTPConfig?) {
        if let config, let data = try? JSONEncoder().encode(config) {
            Keychain.set(String(decoding: data, as: UTF8.self), for: "totp")
        } else {
            Keychain.set(nil, for: "totp")
        }
        totp = config
    }

    /// Cisco 登录框会预填上次的用户名，并记录在 ~/.cisco/vpn/log/UIHistory_*.txt 中，从最新的日志里取出来
    static func detectCiscoUsername() -> String? {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".cisco/vpn/log")
        let files = ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.lastPathComponent.hasPrefix("UIHistory_") }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
        let regex = try! NSRegularExpression(pattern: #"<textbox id="username" value="([^"]+)""#)
        for file in files {
            guard let text = try? String(contentsOf: file, encoding: .utf8) else { continue }
            let matches = regex.matches(in: text, range: NSRange(text.startIndex..., in: text))
            if let m = matches.last, let r = Range(m.range(at: 1), in: text) {
                let name = String(text[r]).trimmingCharacters(in: .whitespaces)
                if !name.isEmpty { return name }
            }
        }
        return nil
    }

    /// 用户名为空时自动补上，返回是否补了
    @discardableResult
    func fillUsernameFromCiscoIfEmpty() -> Bool {
        guard username.trimmingCharacters(in: .whitespaces).isEmpty,
              let name = Self.detectCiscoUsername() else { return false }
        username = name
        return true
    }

    /// 缺少的配置项（用于提示为什么不能自动连接）
    var missingItems: [String] {
        var m: [String] = []
        if server.trimmingCharacters(in: .whitespaces).isEmpty { m.append("服务器") }
        if username.trimmingCharacters(in: .whitespaces).isEmpty { m.append("用户名") }
        if !hasPassword { m.append("密码") }
        if totp == nil { m.append("验证码密钥") }
        return m
    }

    var isComplete: Bool {
        !server.trimmingCharacters(in: .whitespaces).isEmpty
            && !username.trimmingCharacters(in: .whitespaces).isEmpty
            && hasPassword && totp != nil
    }

    // MARK: - 开机自启

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() }
                else { try SMAppService.mainApp.unregister() }
            } catch {
                NSLog("launchAtLogin error: \(error)")
            }
            objectWillChange.send()
        }
    }
}
