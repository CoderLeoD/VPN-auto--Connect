import SwiftUI
import AppKit
import UniformTypeIdentifiers
import Combine

enum SettingsTab: Hashable { case account, totp, general, log }

@MainActor
final class SettingsWindow {
    private static var window: NSWindow?
    private static let tabModel = TabModel()

    final class TabModel: ObservableObject { @Published var tab: SettingsTab = .account }

    static func show(tab: SettingsTab? = nil) {
        if let tab { tabModel.tab = tab }
        if window == nil {
            let view = SettingsView(tabModel: tabModel)
                .environmentObject(AppConfig.shared)
                .environmentObject(VPNController.shared)
            let w = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 560, height: 460),
                styleMask: [.titled, .closable, .miniaturizable, .resizable],
                backing: .buffered, defer: false)
            w.title = "VPN AutoConnect 设置"
            w.contentView = NSHostingView(rootView: view)
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

struct SettingsView: View {
    @ObservedObject var tabModel: SettingsWindow.TabModel

    var body: some View {
        TabView(selection: $tabModel.tab) {
            AccountTab().tabItem { Text("账户") }.tag(SettingsTab.account)
            TOTPTab().tabItem { Text("验证码") }.tag(SettingsTab.totp)
            GeneralTab().tabItem { Text("通用") }.tag(SettingsTab.general)
            LogTab().tabItem { Text("日志") }.tag(SettingsTab.log)
        }
        .padding()
        .frame(minWidth: 520, minHeight: 420)
    }
}

// MARK: - 账户

struct AccountTab: View {
    @EnvironmentObject var config: AppConfig
    @State private var server = ""
    @State private var group = ""
    @State private var username = ""
    @State private var password = ""
    @State private var saved = false
    @State private var error = ""

    var body: some View {
        Form {
            Section {
                TextField("VPN 服务器 *", text: $server, prompt: Text("例如 vpn.example.com"))
                TextField("组 (Group)", text: $group, prompt: Text("可选，登录时没有组选择就留空"))
                TextField("用户名 *", text: $username, prompt: Text("Cisco 登录框中的 Username，必填"))
                SecureField("密码（第一步） *", text: $password,
                            prompt: Text(config.hasPassword ? "已保存，留空则不修改" : "输入 VPN 密码，必填"))
            } footer: {
                Text("* 为必填项。在 Cisco Secure Client 中修改密码后，在这里输入新密码并保存即可。密码只保存在 macOS 钥匙串中。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                if !error.isEmpty { Text(error).foregroundStyle(.red) }
                if saved { Text("已保存 ✓").foregroundStyle(.green) }
                Button("保存") { save() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .formStyle(.grouped)
        .onAppear {
            server = config.server
            group = config.group
            username = config.username.isEmpty ? (AppConfig.detectCiscoUsername() ?? "") : config.username
        }
    }

    private func save() {
        var missing: [String] = []
        if server.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { missing.append("服务器") }
        if username.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { missing.append("用户名") }
        if password.isEmpty && !config.hasPassword { missing.append("密码") }
        guard missing.isEmpty else {
            error = "请填写：" + missing.joined(separator: "、")
            return
        }
        error = ""
        config.server = server.trimmingCharacters(in: .whitespacesAndNewlines)
        config.group = group.trimmingCharacters(in: .whitespacesAndNewlines)
        config.username = username.trimmingCharacters(in: .whitespacesAndNewlines)
        if !password.isEmpty {
            config.setPassword(password)
            password = ""
        }
        saved = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { saved = false }
    }
}

// MARK: - 验证码

struct TOTPTab: View {
    @EnvironmentObject var config: AppConfig
    @State private var manualInput = ""
    @State private var candidates: [TOTPConfig] = []
    @State private var message = ""
    @State private var isError = false
    @State private var dropTargeted = false
    @State private var now = Date()
    private let ticker = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            currentBox

            GroupBox("更换 / 绑定验证码") {
                VStack(alignment: .leading, spacing: 10) {
                    dropZone
                    HStack {
                        Button("选择二维码图片…") { pickImage() }
                        Button("从剪贴板粘贴图片") { handle(QRDecoder.decodeFromPasteboard(), source: "剪贴板") }
                    }
                    HStack {
                        TextField("或粘贴二维码识别结果（otpauth://…）或 Base32 密钥", text: $manualInput)
                            .textFieldStyle(.roundedBorder)
                        Button("识别") { handle([manualInput], source: "文本") }
                            .disabled(manualInput.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                    if !message.isEmpty {
                        Text(message).font(.callout).foregroundStyle(isError ? .red : .green)
                    }
                    if candidates.count > 1 {
                        Text("二维码中包含多个账户，请选择 VPN 对应的那个：").font(.callout)
                        ForEach(Array(candidates.enumerated()), id: \.offset) { _, c in
                            HStack {
                                Text(c.displayName)
                                Spacer()
                                Text(TOTP.code(for: c, at: now) ?? "—").monospacedDigit()
                                Button("使用") { apply(c) }
                            }
                        }
                    }
                }
                .padding(6)
            }
            Text("提示：二维码可以是绑定时的原始二维码，也可以是 Google Authenticator「转移账户 → 导出账户」生成的二维码。保存后请对比下方验证码与手机上是否一致。")
                .font(.caption).foregroundStyle(.secondary)
            Spacer()
        }
        .onReceive(ticker) { now = $0 }
    }

    private var currentBox: some View {
        GroupBox("当前验证码") {
            HStack {
                if let t = config.totp, let code = TOTP.code(for: t, at: now) {
                    VStack(alignment: .leading) {
                        Text(t.displayName).font(.caption).foregroundStyle(.secondary)
                        Text(code.prefix(3) + " " + code.dropFirst(3))
                            .font(.system(size: 28, weight: .semibold, design: .monospaced))
                    }
                    Spacer()
                    Text("\(TOTP.secondsRemaining(period: t.period, at: now))s")
                        .monospacedDigit().foregroundStyle(.secondary)
                    Button("清除") { config.setTOTP(nil) }
                } else {
                    Text("尚未配置").foregroundStyle(.secondary)
                    Spacer()
                }
            }
            .padding(6)
        }
    }

    private var dropZone: some View {
        RoundedRectangle(cornerRadius: 8)
            .strokeBorder(style: StrokeStyle(lineWidth: 1.5, dash: [6]))
            .foregroundStyle(dropTargeted ? Color.accentColor : Color.secondary.opacity(0.5))
            .frame(height: 70)
            .overlay(Text("把二维码图片拖到这里").foregroundStyle(.secondary))
            .onDrop(of: [.fileURL, .image], isTargeted: $dropTargeted) { providers in
                handleDrop(providers)
                return true
            }
    }

    private func pickImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url {
            handle(QRDecoder.decode(url: url), source: "图片")
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) {
        guard let p = providers.first else { return }
        if p.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
            _ = p.loadObject(ofClass: URL.self) { url, _ in
                let texts = url.map { QRDecoder.decode(url: $0) } ?? []
                DispatchQueue.main.async { handle(texts, source: "图片") }
            }
        } else {
            _ = p.loadObject(ofClass: NSImage.self) { img, _ in
                let texts = (img as? NSImage).map { QRDecoder.decode(image: $0) } ?? []
                DispatchQueue.main.async { handle(texts, source: "图片") }
            }
        }
    }

    private func handle(_ texts: [String], source: String) {
        candidates = []
        guard !texts.isEmpty else {
            fail("没有在\(source)中识别到二维码")
            return
        }
        let configs = texts.flatMap { TOTP.parseAny($0) }
        switch configs.count {
        case 0: fail("识别到内容，但不是有效的验证码二维码/密钥")
        case 1: apply(configs[0])
        default:
            candidates = configs
            isError = false
            message = "识别到 \(configs.count) 个账户"
        }
    }

    private func apply(_ c: TOTPConfig) {
        config.setTOTP(c)
        candidates = []
        manualInput = ""
        isError = false
        message = "已保存：\(c.displayName)。请核对验证码与手机是否一致。"
    }

    private func fail(_ s: String) {
        isError = true
        message = s
    }
}

// MARK: - 通用

struct GeneralTab: View {
    @EnvironmentObject var config: AppConfig
    @EnvironmentObject var vpn: VPNController

    var body: some View {
        Form {
            Toggle("断线后自动重连（包括锁屏/睡眠唤醒后）", isOn: $config.autoReconnect)
            Toggle("连接前自动退出 Cisco Secure Client 界面", isOn: $config.quitCiscoGUI)
            Text("Cisco 界面开着时，命令行无法发起连接。退出界面不会影响 VPN 本身。")
                .font(.caption).foregroundStyle(.secondary)
            Toggle("自动接受登录 banner", isOn: $config.acceptBanner)
            Stepper("状态检查间隔：\(config.checkInterval) 秒", value: $config.checkInterval, in: 5...300, step: 5)
                .onChange(of: config.checkInterval) { _ in vpn.rescheduleTimer() }
            Toggle("登录 macOS 时自动启动", isOn: Binding(
                get: { config.launchAtLogin },
                set: { config.launchAtLogin = $0 }))
            LabeledContent("Cisco 命令行", value: vpn.cliPath ?? "未找到")
        }
        .formStyle(.grouped)
    }
}

// MARK: - 日志

struct LogTab: View {
    @EnvironmentObject var vpn: VPNController

    var body: some View {
        VStack(alignment: .leading) {
            HStack {
                Text("状态：\(vpn.state.title)")
                if vpn.autoPaused, !vpn.pauseReason.isEmpty {
                    Text("· \(vpn.pauseReason)").foregroundStyle(.orange).lineLimit(2)
                }
                Spacer()
                Button("打开日志文件") { NSWorkspace.shared.open(VPNController.logFileURL) }
                Button("清空") { vpn.clearLog() }
                Button("复制") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(vpn.logLines.joined(separator: "\n"), forType: .string)
                }
            }
            ScrollViewReader { proxy in
                ScrollView {
                    Text(vpn.logLines.joined(separator: "\n"))
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(6)
                    Color.clear.frame(height: 1).id("bottom")
                }
                .background(Color(nsColor: .textBackgroundColor))
                .onChange(of: vpn.logLines.count) { _ in proxy.scrollTo("bottom") }
            }
        }
    }
}
