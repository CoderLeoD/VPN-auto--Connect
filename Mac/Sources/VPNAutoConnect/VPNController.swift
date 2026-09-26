import AppKit
import UserNotifications

enum VPNState: Equatable {
    case connected, disconnected, connecting, reconnecting, disconnecting, unknown, notInstalled
    /// Cisco 的后台服务没运行（通常是在「登录项与扩展」里被关闭，需要重新允许）
    case serviceUnavailable

    var title: String {
        switch self {
        case .connected: return "已连接"
        case .disconnected: return "未连接"
        case .connecting: return "正在连接…"
        case .reconnecting: return "Cisco 正在重连…"
        case .disconnecting: return "正在断开…"
        case .unknown: return "状态未知"
        case .notInstalled: return "未找到 Cisco Secure Client"
        case .serviceUnavailable: return "Cisco VPN 服务未运行"
        }
    }

    var symbol: String {
        switch self {
        case .connected: return "lock.shield.fill"
        case .connecting, .reconnecting, .disconnecting: return "arrow.triangle.2.circlepath"
        case .disconnected: return "shield.slash"
        case .unknown, .notInstalled, .serviceUnavailable: return "exclamationmark.shield"
        }
    }
}

/// 通过 Cisco 自带的命令行工具 `vpn` 查询状态、建立和断开连接
@MainActor
final class VPNController: ObservableObject {
    static let shared = VPNController()

    @Published private(set) var state: VPNState = .unknown
    @Published private(set) var busy = false
    @Published private(set) var lastMessage = ""
    @Published private(set) var lastConnectedAt: Date?
    @Published private(set) var logLines: [String] = []
    /// 用户在本程序里手动断开，或连续认证失败后，暂停自动重连
    @Published var autoPaused = false
    @Published private(set) var pauseReason = ""

    private let config = AppConfig.shared
    private var timer: Timer?
    private var consecutiveFailures = 0
    private var authFailures = 0
    private var nextAutoAttempt = Date.distantPast
    private var lastUsedTOTPCounter: UInt64?
    private var lastBlocker: String?

    static let cliCandidates = [
        "/opt/cisco/secureclient/bin/vpn",
        "/opt/cisco/anyconnect/bin/vpn",
    ]
    var cliPath: String? {
        Self.cliCandidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    // MARK: - 生命周期

    func start() {
        if config.fillUsernameFromCiscoIfEmpty() {
            log("用户名为空，已从 Cisco Secure Client 的记录中自动填入")
        }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }

        let nc = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification,
                     NSWorkspace.screensDidWakeNotification,
                     NSWorkspace.sessionDidBecomeActiveNotification] {
            nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.handleWake() }
            }
        }
        rescheduleTimer()
        Task { await tick() }
    }

    func rescheduleTimer() {
        timer?.invalidate()
        let interval = TimeInterval(max(5, config.checkInterval))
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.tick() }
        }
    }

    private func handleWake() {
        log("检测到唤醒/解锁，稍后检查 VPN 状态")
        consecutiveFailures = 0
        nextAutoAttempt = .distantPast
        // 刚唤醒时网络往往还没就绪，分两次检查
        for delay in [4.0, 12.0] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                Task { @MainActor in await self?.tick() }
            }
        }
    }

    // MARK: - 定时检查 + 自动重连

    func tick() async {
        guard !busy else { return }
        await config.loadSecretsIfNeeded()
        await refreshState()
        guard state == .disconnected else { lastBlocker = nil; return }
        // 未连接却不能自动连时，把原因写进日志（原因变化时才记一次）
        let blocker = autoConnectBlocker()
        if blocker != lastBlocker, let blocker { log("未连接，但不自动重连：\(blocker)") }
        lastBlocker = blocker
        guard blocker == nil, Date() >= nextAutoAttempt else { return }
        log("检测到 VPN 未连接，自动重连")
        await connect(manual: false)
    }

    func refreshState() async {
        guard let cli = cliPath else { state = .notInstalled; return }
        let out = await Shell.run(cli, args: ["state"], input: nil, timeout: 20).output
        let newState = Self.parseState(out)
        if newState == .connected && state != .connected { lastConnectedAt = Date() }
        if newState != state {
            log("状态：\(state.title) → \(newState.title)")
            if newState == .serviceUnavailable {
                lastMessage = "Cisco VPN 服务被系统禁用，请在「登录项与扩展」中允许它在后台运行"
                log(lastMessage + "\n" + Self.tail(out, lines: 6))
                notify(title: "Cisco VPN 服务未运行", body: lastMessage)
            } else if state == .serviceUnavailable {
                lastMessage = ""
            }
        }
        state = newState
    }

    private func autoConnectBlocker() -> String? {
        if !config.autoReconnect { return "自动重连已在设置中关闭" }
        if autoPaused { return pauseReason.isEmpty ? "自动重连已暂停" : pauseReason }
        if let e = config.keychainError { return e }
        if !config.secretsLoaded { return "尚未读取到钥匙串" }
        if !config.isComplete { return "配置不完整，缺少" + config.missingItems.joined(separator: "、") }
        return nil
    }

    static func parseState(_ output: String) -> VPNState {
        let lower = output.lowercased()
        if lower.contains("needs user approval") || lower.contains("cannot contact the vpn service") {
            return .serviceUnavailable
        }
        // 输出中会有多行 ">> state: xxx"，以最后一行为准
        let states = output.components(separatedBy: .newlines).compactMap { line -> String? in
            guard let r = line.range(of: "state:") else { return nil }
            return line[r.upperBound...].trimmingCharacters(in: .whitespaces).lowercased()
        }
        guard let s = states.last(where: { $0 != "unknown" }) ?? states.last else { return .unknown }
        if s.hasPrefix("connected") { return .connected }
        if s.hasPrefix("disconnected") { return .disconnected }
        if s.hasPrefix("connecting") { return .connecting }
        if s.hasPrefix("reconnecting") { return .reconnecting }
        if s.hasPrefix("disconnecting") { return .disconnecting }
        return .unknown
    }

    // MARK: - 连接 / 断开

    func connect(manual: Bool) async {
        guard !busy else { return }
        guard let cli = cliPath else { state = .notInstalled; return }
        if state == .serviceUnavailable {
            lastMessage = "Cisco VPN 服务未运行，无法连接。请先在系统设置中允许它。"
            log(lastMessage)
            return
        }
        await config.loadSecretsIfNeeded()
        guard config.isComplete, let password = config.password, let totp = config.totp else {
            lastMessage = config.keychainError ?? ("请先在设置中补全：" + config.missingItems.joined(separator: "、"))
            log(lastMessage)
            return
        }
        if manual {
            autoPaused = false
            pauseReason = ""
            authFailures = 0
        }

        busy = true
        state = .connecting
        defer { busy = false }

        if config.quitCiscoGUI { await quitCiscoGUI() }

        // 验证码快过期时等下一个周期，避免提交时刚好失效；同一个码不重复使用
        var now = Date()
        var counter = UInt64(now.timeIntervalSince1970) / UInt64(totp.period)
        if TOTP.secondsRemaining(period: totp.period, at: now) < 8 || counter == lastUsedTOTPCounter {
            let wait = TOTP.secondsRemaining(period: totp.period, at: now) + 1
            lastMessage = "等待新的验证码（\(wait) 秒）…"
            log(lastMessage)
            try? await Task.sleep(nanoseconds: UInt64(wait) * 1_000_000_000)
            now = Date()
            counter = UInt64(now.timeIntervalSince1970) / UInt64(totp.period)
        }
        guard let code = TOTP.code(for: totp, at: now) else {
            lastMessage = "验证码密钥无效，请重新配置"
            return
        }
        lastUsedTOTPCounter = counter

        // 与 Cisco GUI 的登录顺序一致：[组] → 用户名 → 密码 → Second Password(验证码) → 接受 banner
        var lines = ["connect \(config.server.trimmingCharacters(in: .whitespaces))"]
        if !config.group.trimmingCharacters(in: .whitespaces).isEmpty { lines.append(config.group) }
        lines.append(config.username.trimmingCharacters(in: .whitespaces))
        lines.append(password)
        lines.append(code)
        if config.acceptBanner { lines.append("y") }
        lines.append("exit")
        let input = lines.joined(separator: "\n") + "\n"

        lastMessage = "正在连接 \(config.server)…"
        log(lastMessage)
        let result = await Shell.run(cli, args: ["-s"], input: input, timeout: 90)
        let output = Self.redact(result.output, secrets: [password, code])
        log("vpn 输出：\n" + Self.tail(output, lines: 25))

        await refreshState()
        if state == .connected {
            consecutiveFailures = 0
            authFailures = 0
            lastConnectedAt = Date()
            lastMessage = "连接成功"
            log(lastMessage)
            return
        }

        consecutiveFailures += 1
        let lower = output.lowercased()
        let isAuthFailure = ["login failed", "login denied", "authentication failed",
                             "access denied"].contains { lower.contains($0) }
        if isAuthFailure {
            authFailures += 1
            lastMessage = "认证失败（第 \(authFailures) 次）"
            // 连续两次认证失败就停，防止账号被锁
            if authFailures >= 2 {
                autoPaused = true
                pauseReason = "连续认证失败，已暂停自动重连。请检查密码/验证码是否已在 Cisco 中更换。"
                notify(title: "VPN 自动连接已暂停", body: pauseReason)
            }
        } else if result.timedOut {
            lastMessage = "连接超时"
        } else {
            lastMessage = "连接失败：" + (Self.lastMeaningfulLine(output) ?? "未知原因")
        }
        log(lastMessage)

        let backoff = min(300.0, 15.0 * pow(2.0, Double(consecutiveFailures - 1)))
        nextAutoAttempt = Date().addingTimeInterval(backoff)
        if !manual, consecutiveFailures == 3 {
            notify(title: "VPN 自动重连失败", body: lastMessage)
        }
    }

    func disconnect() async {
        guard !busy, let cli = cliPath else { return }
        busy = true
        state = .disconnecting
        autoPaused = true
        pauseReason = "你手动断开了连接，自动重连已暂停（点“连接”即可恢复）"
        let out = await Shell.run(cli, args: ["disconnect"], input: nil, timeout: 30).output
        log("断开：\n" + Self.tail(out, lines: 5))
        busy = false
        await refreshState()
        lastMessage = "已断开"
    }

    func toggleAutoPause() {
        autoPaused.toggle()
        pauseReason = autoPaused ? "已手动暂停自动重连" : ""
        if !autoPaused {
            authFailures = 0
            consecutiveFailures = 0
            nextAutoAttempt = .distantPast
            Task { await tick() }
        }
    }

    /// Cisco 的图形界面运行时会占用连接能力，命令行无法发起连接，因此先退出它（VPN 服务本身不受影响）
    private func quitCiscoGUI() async {
        let apps = NSRunningApplication.runningApplications(withBundleIdentifier: "com.cisco.secureclient.gui")
            + NSRunningApplication.runningApplications(withBundleIdentifier: "com.cisco.anyconnect.gui")
        guard !apps.isEmpty else { return }
        log("退出 Cisco Secure Client 界面以便命令行连接")
        apps.forEach { $0.terminate() }
        for _ in 0..<10 {
            if apps.allSatisfy({ $0.isTerminated }) { break }
            try? await Task.sleep(nanoseconds: 300_000_000)
        }
        apps.filter { !$0.isTerminated }.forEach { $0.forceTerminate() }
        try? await Task.sleep(nanoseconds: 500_000_000)
    }

    // MARK: - 工具

    func log(_ s: String) {
        let f = DateFormatter()
        f.dateFormat = "MM-dd HH:mm:ss"
        let line = "[\(f.string(from: Date()))] \(s)"
        logLines.append(line)
        Self.appendToLogFile(line)
        if logLines.count > 400 { logLines.removeFirst(logLines.count - 400) }
    }

    func clearLog() { logLines.removeAll() }

    static let logFileURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/VPNAutoConnect.log")

    private static func appendToLogFile(_ line: String) {
        let url = logFileURL
        let fm = FileManager.default
        // 超过 1MB 就轮转一次
        if let size = (try? fm.attributesOfItem(atPath: url.path))?[.size] as? Int, size > 1_000_000 {
            try? fm.removeItem(at: url.appendingPathExtension("old"))
            try? fm.moveItem(at: url, to: url.appendingPathExtension("old"))
        }
        if !fm.fileExists(atPath: url.path) { fm.createFile(atPath: url.path, contents: nil) }
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile()
            h.write(Data((line + "\n").utf8))
            try? h.close()
        }
    }

    /// 打开「系统设置 → 通用 → 登录项与扩展」
    func openLoginItemsSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension") {
            NSWorkspace.shared.open(url)
        }
    }

    private func notify(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        UNUserNotificationCenter.current().add(
            UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    static func redact(_ s: String, secrets: [String]) -> String {
        secrets.filter { !$0.isEmpty }.reduce(s) { $0.replacingOccurrences(of: $1, with: "******") }
    }

    static func tail(_ s: String, lines n: Int) -> String {
        s.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && $0 != "VPN>" }
            .suffix(n).joined(separator: "\n")
    }

    static func lastMeaningfulLine(_ s: String) -> String? {
        s.components(separatedBy: .newlines)
            .map { $0.replacingOccurrences(of: "VPN>", with: "").trimmingCharacters(in: .whitespaces) }
            .filter { $0.hasPrefix(">> error") || $0.hasPrefix(">> notice") || $0.hasPrefix(">> warning") }
            .last?
            .replacingOccurrences(of: ">> ", with: "")
    }
}

enum Shell {
    struct Result { let output: String; let status: Int32; let timedOut: Bool }

    static func run(_ path: String, args: [String], input: String?, timeout: TimeInterval) async -> Result {
        await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .userInitiated).async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: path)
                p.arguments = args
                let outPipe = Pipe(), inPipe = Pipe()
                p.standardOutput = outPipe
                p.standardError = outPipe
                p.standardInput = inPipe

                var data = Data()
                let lock = NSLock()
                outPipe.fileHandleForReading.readabilityHandler = { h in
                    let d = h.availableData
                    lock.lock(); data.append(d); lock.unlock()
                }
                let done = DispatchSemaphore(value: 0)
                p.terminationHandler = { _ in done.signal() }
                do {
                    try p.run()
                } catch {
                    outPipe.fileHandleForReading.readabilityHandler = nil
                    cont.resume(returning: Result(output: "无法启动 \(path): \(error)", status: -1, timedOut: false))
                    return
                }
                if let input {
                    inPipe.fileHandleForWriting.write(Data(input.utf8))
                }
                try? inPipe.fileHandleForWriting.close()

                var timedOut = false
                if done.wait(timeout: .now() + timeout) == .timedOut {
                    timedOut = true
                    p.terminate()
                    _ = done.wait(timeout: .now() + 3)
                }
                outPipe.fileHandleForReading.readabilityHandler = nil
                let rest = (try? outPipe.fileHandleForReading.readToEnd()) ?? nil
                lock.lock()
                if let rest { data.append(rest) }
                let text = String(decoding: data, as: UTF8.self)
                lock.unlock()
                cont.resume(returning: Result(output: text, status: p.isRunning ? -1 : p.terminationStatus, timedOut: timedOut))
            }
        }
    }
}
