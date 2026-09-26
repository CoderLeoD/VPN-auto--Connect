import SwiftUI
import AppKit

@main
struct VPNAutoConnectApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var vpn = VPNController.shared
    @StateObject private var config = AppConfig.shared

    var body: some Scene {
        MenuBarExtra {
            MenuContent()
                .environmentObject(vpn)
                .environmentObject(config)
        } label: {
            Image(systemName: vpn.state.symbol)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)   // 只在菜单栏显示，不占 Dock
        Task { @MainActor in
            await AppConfig.shared.loadSecretsIfNeeded()
            VPNController.shared.start()
            if AppConfig.shared.secretsLoaded && !AppConfig.shared.isComplete {
                SettingsWindow.show()
            }
        }
    }
}

struct MenuContent: View {
    @EnvironmentObject var vpn: VPNController
    @EnvironmentObject var config: AppConfig

    var body: some View {
        Text("VPN：\(vpn.state.title)")
        if !config.server.isEmpty {
            Text(config.server)
        }
        if let t = vpn.lastConnectedAt, vpn.state == .connected {
            Text("连接于 \(t.formatted(date: .omitted, time: .shortened))")
        }
        if !vpn.lastMessage.isEmpty {
            Text(vpn.lastMessage)
        }
        if vpn.state == .serviceUnavailable {
            Button("⚠️ 打开系统设置，允许 Cisco VPN 服务…") { vpn.openLoginItemsSettings() }
        }
        if vpn.autoPaused {
            Text("⏸ 自动重连已暂停")
        } else if !config.autoReconnect {
            Text("自动重连：关闭")
        }
        if let e = config.keychainError {
            Text("⚠️ \(e)")
        } else if !config.isComplete {
            Text("⚠️ 尚未完成配置：缺少" + config.missingItems.joined(separator: "、"))
        }

        Divider()

        Button("立即连接") { Task { await vpn.connect(manual: true) } }
            .disabled(vpn.busy || vpn.state == .connected || !config.isComplete)
        Button("断开") { Task { await vpn.disconnect() } }
            .disabled(vpn.busy || vpn.state == .disconnected)
        Button(vpn.autoPaused ? "恢复自动重连" : "暂停自动重连") { vpn.toggleAutoPause() }
            .disabled(!config.autoReconnect)
        Button("刷新状态") { Task { await vpn.refreshState() } }

        Divider()

        Button("复制当前验证码") {
            if let t = config.totp, let code = TOTP.code(for: t) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(code, forType: .string)
            }
        }
        .disabled(config.totp == nil)
        Button("设置…") { SettingsWindow.show() }
            .keyboardShortcut(",")
        Button("日志…") { SettingsWindow.show(tab: .log) }

        Divider()

        Button("退出") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
