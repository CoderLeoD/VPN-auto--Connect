using VPNAutoConnect.Core;

namespace VPNAutoConnect;

/// 托盘程序主体：通知区域图标 + 右键菜单（对应 Mac 版的菜单栏 MenuBarExtra）
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new();
    readonly VPNController vpn = VPNController.Shared;
    readonly AppConfig config = AppConfig.Shared;

    public TrayApp()
    {
        tray = new NotifyIcon
        {
            Icon = TrayIcons.For(vpn.State),
            Text = "VPN AutoConnect",
            ContextMenuStrip = menu,
            Visible = true,
        };
        menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) SettingsForm.ShowWindow(); };

        vpn.Changed += UpdateTray;
        vpn.Notify += (title, body) => tray.ShowBalloonTip(5000, title, body, ToolTipIcon.Warning);

        config.RefreshLaunchAtLoginPath();
        config.LoadSecretsIfNeeded();
        vpn.Start();
        if (config.SecretsLoaded && !config.IsComplete) SettingsForm.ShowWindow();
    }

    void UpdateTray()
    {
        tray.Icon = TrayIcons.For(vpn.State);
        var text = $"VPN AutoConnect：{vpn.State.Title()}";
        if (vpn.AutoPaused) text += "（自动重连已暂停）";
        tray.Text = text.Length > 127 ? text[..127] : text;   // 系统限制 127 字符
    }

    void BuildMenu()
    {
        menu.Items.Clear();
        void Info(string s) => menu.Items.Add(new ToolStripMenuItem(s) { Enabled = false });
        ToolStripMenuItem Action(string s, Action onClick, bool enabled = true)
        {
            var item = new ToolStripMenuItem(s, null, (_, _) => onClick()) { Enabled = enabled };
            menu.Items.Add(item);
            return item;
        }

        Info($"VPN：{vpn.State.Title()}");
        if (config.Server.Length > 0) Info(config.Server);
        if (vpn.State == VPNState.Connected && vpn.LastConnectedAt is { } t) Info($"连接于 {t:HH:mm}");
        if (vpn.LastMessage.Length > 0) Info(vpn.LastMessage);
        if (vpn.State == VPNState.ServiceUnavailable)
            Action("⚠️ 打开「服务」，启动 Cisco Secure Client Agent…", vpn.OpenServices);
        if (vpn.AutoPaused) Info("⏸ 自动重连已暂停");
        else if (!config.AutoReconnect) Info("自动重连：关闭");
        if (config.CredentialError is { } err) Info($"⚠️ {err}");
        else if (!config.IsComplete) Info("⚠️ 尚未完成配置：缺少" + string.Join("、", config.MissingItems));

        menu.Items.Add(new ToolStripSeparator());

        Action("立即连接", () => _ = vpn.Connect(manual: true),
            !vpn.Busy && vpn.State != VPNState.Connected && config.IsComplete);
        Action("断开", () => _ = vpn.Disconnect(), !vpn.Busy && vpn.State != VPNState.Disconnected);
        Action(vpn.AutoPaused ? "恢复自动重连" : "暂停自动重连", vpn.ToggleAutoPause, config.AutoReconnect);
        Action("刷新状态", () => _ = vpn.RefreshState());

        menu.Items.Add(new ToolStripSeparator());

        Action("复制当前验证码", () =>
        {
            if (config.Totp is { } totp && TOTP.Code(totp) is { } code) Clipboard.SetText(code);
        }, config.Totp != null);
        Action("设置…", () => SettingsForm.ShowWindow()).Font = new Font(menu.Font, FontStyle.Bold);
        Action("日志…", () => SettingsForm.ShowWindow(SettingsTab.Log));

        menu.Items.Add(new ToolStripSeparator());

        Action("退出", ExitThread);
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}
