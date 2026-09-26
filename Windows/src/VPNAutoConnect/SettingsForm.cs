using System.Diagnostics;
using System.Drawing.Drawing2D;
using VPNAutoConnect.Core;

namespace VPNAutoConnect;

enum SettingsTab { Account, Totp, General, Log }

/// 设置窗口：账户 / 验证码 / 通用 / 日志 四个标签页，与 Mac 版一致
sealed class SettingsForm : Form
{
    static SettingsForm? instance;

    public static void ShowWindow(SettingsTab? tab = null)
    {
        instance ??= new SettingsForm();
        if (tab is { } t) instance.tabs.SelectedIndex = (int)t;
        instance.OnAppear();
        instance.Show();
        if (instance.WindowState == FormWindowState.Minimized) instance.WindowState = FormWindowState.Normal;
        instance.Activate();
    }

    readonly AppConfig config = AppConfig.Shared;
    readonly VPNController vpn = VPNController.Shared;
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 1000 };
    readonly ToolTip tooltip = new();

    static readonly Color Secondary = SystemColors.GrayText;
    static readonly Color Green = Color.FromArgb(0x1E, 0x8E, 0x3E);
    static readonly Color Red = Color.FromArgb(0xD0, 0x30, 0x30);
    static readonly Color Orange = Color.FromArgb(0xD0, 0x70, 0x00);

    SettingsForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "VPN AutoConnect 设置";
        ClientSize = new Size(560, 480);
        MinimumSize = new Size(560, 480);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        tabs.TabPages.Add(BuildAccountTab());
        tabs.TabPages.Add(BuildTotpTab());
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildLogTab());
        tabs.SelectedIndexChanged += (_, _) => OnAppear();
        Controls.Add(tabs);

        ticker.Tick += (_, _) => RefreshTotp();
        vpn.Changed += RefreshDynamic;
        config.Changed += RefreshDynamic;
    }

    // 关闭时只隐藏，下次打开保留状态
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ticker.Stop();
            return;
        }
        base.OnFormClosing(e);
    }

    void OnAppear()
    {
        switch ((SettingsTab)tabs.SelectedIndex)
        {
            case SettingsTab.Account:
                LoadAccountFields();
                AcceptButton = saveButton;
                break;
            case SettingsTab.Totp:
                AcceptButton = null;
                break;
            default:
                AcceptButton = null;
                break;
        }
        ticker.Start();
        RefreshDynamic();
        RefreshTotp();
    }

    void RefreshDynamic()
    {
        if (!Visible && !IsHandleCreated) return;
        RefreshGeneral();
        RefreshLog();
        RefreshTotp();
    }

    // MARK: - 小工具

    static Label MakeLabel(string text, Color? color = null, float? size = null, int wrap = 0)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 3),
        };
        if (color is { } c) l.ForeColor = c;
        if (size is { } s) l.Font = new Font(l.Font.FontFamily, s);
        if (wrap > 0) l.MaximumSize = new Size(wrap, 0);
        return l;
    }

    static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1) };
        b.Click += (_, _) => onClick();
        return b;
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 2),
        };
        p.Controls.AddRange(controls);
        return p;
    }

    static TableLayoutPanel Column(Padding? padding = null)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = padding ?? new Padding(12),
            AutoSize = false,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static void AddRow(TableLayoutPanel t, Control c, SizeType type = SizeType.AutoSize, float height = 0)
    {
        t.RowStyles.Add(type == SizeType.AutoSize ? new RowStyle(SizeType.AutoSize) : new RowStyle(type, height));
        t.Controls.Add(c, 0, t.RowCount++);
    }

    // MARK: - 账户

    readonly TextBox serverBox = new() { PlaceholderText = "例如 vpn.example.com", Dock = DockStyle.Fill };
    readonly TextBox groupBox = new() { PlaceholderText = "可选，登录时没有组选择就留空", Dock = DockStyle.Fill };
    readonly TextBox usernameBox = new() { PlaceholderText = "Cisco 登录框中的 Username，必填", Dock = DockStyle.Fill };
    readonly TextBox passwordBox = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    readonly Label accountStatus = MakeLabel("");
    Button saveButton = null!;

    TabPage BuildAccountTab()
    {
        var page = new TabPage("账户") { UseVisualStyleBackColor = true };
        var col = Column();

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Field(string label, TextBox box)
        {
            grid.Controls.Add(MakeLabel(label), 0, grid.RowCount);
            box.Margin = new Padding(3, 3, 3, 6);
            grid.Controls.Add(box, 1, grid.RowCount);
            grid.RowCount++;
        }
        Field("VPN 服务器 *", serverBox);
        Field("组 (Group)", groupBox);
        Field("用户名 *", usernameBox);
        Field("密码（第一步） *", passwordBox);
        AddRow(col, grid);

        AddRow(col, MakeLabel("* 为必填项。在 Cisco Secure Client 中修改密码后，在这里输入新密码并保存即可。密码只保存在 Windows 凭据管理器中。",
            Secondary, 8, 500));

        saveButton = MakeButton("保存", SaveAccount);
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0),
        };
        bottom.Controls.Add(saveButton);
        bottom.Controls.Add(accountStatus);
        AddRow(col, bottom);
        AddRow(col, new Panel(), SizeType.Percent, 100);

        page.Controls.Add(col);
        return page;
    }

    void LoadAccountFields()
    {
        serverBox.Text = config.Server.Length > 0 ? config.Server : AppConfig.DetectCiscoServer() ?? "";
        groupBox.Text = config.Group;
        usernameBox.Text = config.Username.Length > 0 ? config.Username : AppConfig.DetectCiscoUsername() ?? "";
        passwordBox.Text = "";
        passwordBox.PlaceholderText = config.HasPassword ? "已保存，留空则不修改" : "输入 VPN 密码，必填";
        accountStatus.Text = "";
    }

    async void SaveAccount()
    {
        var missing = new List<string>();
        if (serverBox.Text.Trim().Length == 0) missing.Add("服务器");
        if (usernameBox.Text.Trim().Length == 0) missing.Add("用户名");
        if (passwordBox.Text.Length == 0 && !config.HasPassword) missing.Add("密码");
        if (missing.Count > 0)
        {
            SetStatus(accountStatus, "请填写：" + string.Join("、", missing), Red);
            return;
        }
        config.Server = serverBox.Text.Trim();
        config.Group = groupBox.Text.Trim();
        config.Username = usernameBox.Text.Trim();
        if (passwordBox.Text.Length > 0)
        {
            try
            {
                config.SetPassword(passwordBox.Text);
            }
            catch (Exception e)
            {
                SetStatus(accountStatus, e.Message, Red);
                return;
            }
            passwordBox.Text = "";
            passwordBox.PlaceholderText = "已保存，留空则不修改";
        }
        SetStatus(accountStatus, "已保存 ✓", Green);
        await Task.Delay(2000);
        if (accountStatus.Text == "已保存 ✓") accountStatus.Text = "";
    }

    static void SetStatus(Label l, string text, Color color)
    {
        l.ForeColor = color;
        l.Text = text;
    }

    // MARK: - 验证码

    readonly Label totpName = MakeLabel("", Secondary, 8);
    readonly Label totpCode = new() { AutoSize = true, Font = new Font("Consolas", 22, FontStyle.Bold), Margin = new Padding(0) };
    readonly Label totpRemaining = MakeLabel("", Secondary);
    readonly Label totpEmpty = MakeLabel("尚未配置", Secondary);
    Button totpClear = null!;
    readonly TextBox manualInput = new() { PlaceholderText = "或粘贴二维码识别结果（otpauth://…）或 Base32 密钥", Dock = DockStyle.Fill };
    readonly Label totpMessage = MakeLabel("", null, null, 500);
    readonly Label candidatesTitle = MakeLabel("二维码中包含多个账户，请选择 VPN 对应的那个：");
    readonly TableLayoutPanel candidatesPanel = new() { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3 };
    List<TOTPConfig> candidates = [];
    readonly List<Label> candidateCodes = [];
    bool dropTargeted;

    TabPage BuildTotpTab()
    {
        var page = new TabPage("验证码") { UseVisualStyleBackColor = true };
        var col = Column();

        // 当前验证码
        var current = new GroupBox { Text = "当前验证码", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var currentRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        currentRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        currentRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        currentRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        currentRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var codeStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        codeStack.Controls.Add(totpName);
        codeStack.Controls.Add(totpCode);
        codeStack.Controls.Add(totpEmpty);
        totpClear = MakeButton("清除", () =>
        {
            if (MessageBox.Show(this, "确定清除已保存的验证码密钥？", "VPN AutoConnect",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                config.SetTotp(null);
        });
        totpRemaining.Anchor = AnchorStyles.Right;
        totpClear.Anchor = AnchorStyles.Right;
        currentRow.Controls.Add(codeStack, 0, 0);
        currentRow.Controls.Add(totpRemaining, 2, 0);
        currentRow.Controls.Add(totpClear, 3, 0);
        current.Controls.Add(currentRow);
        AddRow(col, current);

        // 更换 / 绑定
        var bind = new GroupBox { Text = "更换 / 绑定验证码", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), Margin = new Padding(3, 10, 3, 3) };
        var bindCol = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        bindCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var drop = new Panel { Dock = DockStyle.Top, Height = 70, AllowDrop = true, Cursor = Cursors.Hand, Margin = new Padding(3, 3, 3, 6) };
        drop.Paint += (_, e) => PaintDropZone(drop, e.Graphics);
        drop.Resize += (_, _) => drop.Invalidate();
        drop.Click += (_, _) => PickImage();
        drop.DragEnter += (_, e) =>
        {
            var ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true || e.Data?.GetDataPresent(DataFormats.Bitmap) == true;
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            dropTargeted = ok;
            drop.Invalidate();
        };
        drop.DragLeave += (_, _) => { dropTargeted = false; drop.Invalidate(); };
        drop.DragDrop += (_, e) =>
        {
            dropTargeted = false;
            drop.Invalidate();
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                HandleDecoded(QRImage.Decode(files[0]), "图片");
            else if (e.Data?.GetData(DataFormats.Bitmap) is Image img)
                HandleDecoded(QRImage.Decode(img), "图片");
        };
        tooltip.SetToolTip(drop, "也可以点击这里选择图片");
        bindCol.Controls.Add(drop);

        bindCol.Controls.Add(Row(
            MakeButton("选择二维码图片…", PickImage),
            MakeButton("从剪贴板粘贴图片", () => HandleDecoded(QRImage.DecodeFromClipboard(), "剪贴板"))));

        var manualRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) };
        manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var recognize = MakeButton("识别", () => HandleDecoded([manualInput.Text], "文本"));
        recognize.Enabled = false;
        manualInput.TextChanged += (_, _) => recognize.Enabled = manualInput.Text.Trim().Length > 0;
        manualInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && recognize.Enabled) { e.SuppressKeyPress = true; recognize.PerformClick(); }
        };
        manualInput.Margin = new Padding(3, 4, 3, 3);
        manualRow.Controls.Add(manualInput, 0, 0);
        manualRow.Controls.Add(recognize, 1, 0);
        bindCol.Controls.Add(manualRow);

        bindCol.Controls.Add(totpMessage);
        candidatesTitle.Visible = false;
        bindCol.Controls.Add(candidatesTitle);
        candidatesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        candidatesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        candidatesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bindCol.Controls.Add(candidatesPanel);

        bind.Controls.Add(bindCol);
        AddRow(col, bind);

        AddRow(col, MakeLabel("提示：二维码可以是绑定时的原始二维码，也可以是 Google Authenticator「转移账户 → 导出账户」生成的二维码。保存后请对比上方验证码与手机上是否一致。",
            Secondary, 8, 500));
        AddRow(col, new Panel(), SizeType.Percent, 100);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(col);
        page.Controls.Add(scroll);
        return page;
    }

    void PaintDropZone(Panel p, Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, p.Width - 3, p.Height - 3);
        using var path = new GraphicsPath();
        float d = 16;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var pen = new Pen(dropTargeted ? SystemColors.Highlight : Color.FromArgb(160, SystemColors.GrayText), 1.5f)
        {
            DashStyle = DashStyle.Dash,
        };
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, "把二维码图片拖到这里", Font, p.ClientRectangle, Secondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void PickImage()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) HandleDecoded(QRImage.Decode(dlg.FileName), "图片");
    }

    void HandleDecoded(List<string> texts, string source)
    {
        SetCandidates([]);
        texts = texts.Where(t => t.Trim().Length > 0).ToList();
        if (texts.Count == 0)
        {
            SetStatus(totpMessage, $"没有在{source}中识别到二维码", Red);
            return;
        }
        var configs = texts.SelectMany(TOTP.ParseAny).ToList();
        switch (configs.Count)
        {
            case 0:
                SetStatus(totpMessage, "识别到内容，但不是有效的验证码二维码/密钥", Red);
                break;
            case 1:
                Apply(configs[0]);
                break;
            default:
                SetCandidates(configs);
                SetStatus(totpMessage, $"识别到 {configs.Count} 个账户", Green);
                break;
        }
    }

    void Apply(TOTPConfig c)
    {
        try
        {
            config.SetTotp(c);
        }
        catch (Exception e)
        {
            SetStatus(totpMessage, e.Message, Red);
            return;
        }
        SetCandidates([]);
        manualInput.Text = "";
        SetStatus(totpMessage, $"已保存：{c.DisplayName}。请核对验证码与手机是否一致。", Green);
    }

    void SetCandidates(List<TOTPConfig> list)
    {
        candidates = list;
        candidateCodes.Clear();
        candidatesPanel.SuspendLayout();
        candidatesPanel.Controls.Clear();
        candidatesPanel.RowStyles.Clear();
        candidatesPanel.RowCount = 0;
        if (list.Count > 1)
        {
            foreach (var c in list)
            {
                var code = MakeLabel("—");
                code.Font = new Font("Consolas", 10);
                candidateCodes.Add(code);
                var use = MakeButton("使用", () => Apply(c));
                candidatesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                candidatesPanel.Controls.Add(MakeLabel(c.DisplayName), 0, candidatesPanel.RowCount);
                candidatesPanel.Controls.Add(code, 1, candidatesPanel.RowCount);
                candidatesPanel.Controls.Add(use, 2, candidatesPanel.RowCount);
                candidatesPanel.RowCount++;
            }
        }
        candidatesTitle.Visible = list.Count > 1;
        candidatesPanel.ResumeLayout();
        RefreshTotp();
    }

    void RefreshTotp()
    {
        if (config.Totp is { } t && TOTP.Code(t) is { } code)
        {
            totpName.Text = t.DisplayName;
            totpCode.Text = code[..3] + " " + code[3..];
            totpRemaining.Text = $"{TOTP.SecondsRemaining(t.Period)}s";
            totpName.Visible = totpCode.Visible = totpRemaining.Visible = totpClear.Visible = true;
            totpEmpty.Visible = false;
        }
        else
        {
            totpName.Visible = totpCode.Visible = totpRemaining.Visible = totpClear.Visible = false;
            totpEmpty.Visible = true;
        }
        for (int i = 0; i < candidateCodes.Count && i < candidates.Count; i++)
            candidateCodes[i].Text = TOTP.Code(candidates[i]) ?? "—";
    }

    // MARK: - 通用

    readonly CheckBox autoReconnectBox = new() { Text = "断线后自动重连（包括锁屏/睡眠唤醒后）", AutoSize = true };
    readonly CheckBox quitGuiBox = new() { Text = "连接前自动退出 Cisco Secure Client 界面", AutoSize = true };
    readonly CheckBox acceptBannerBox = new() { Text = "自动接受登录 banner", AutoSize = true };
    readonly NumericUpDown intervalBox = new() { Minimum = 5, Maximum = 300, Increment = 5, Width = 70 };
    readonly CheckBox launchBox = new() { Text = "登录 Windows 时自动启动", AutoSize = true };
    readonly Label cliLabel = MakeLabel("", Secondary, null, 500);
    bool refreshingGeneral;

    TabPage BuildGeneralTab()
    {
        var page = new TabPage("通用") { UseVisualStyleBackColor = true };
        var col = Column();

        autoReconnectBox.CheckedChanged += (_, _) => { if (!refreshingGeneral) config.AutoReconnect = autoReconnectBox.Checked; };
        quitGuiBox.CheckedChanged += (_, _) => { if (!refreshingGeneral) config.QuitCiscoGUI = quitGuiBox.Checked; };
        acceptBannerBox.CheckedChanged += (_, _) => { if (!refreshingGeneral) config.AcceptBanner = acceptBannerBox.Checked; };
        intervalBox.ValueChanged += (_, _) =>
        {
            if (refreshingGeneral) return;
            config.CheckInterval = (int)intervalBox.Value;
            vpn.RescheduleTimer();
        };
        launchBox.CheckedChanged += (_, _) => { if (!refreshingGeneral) config.LaunchAtLogin = launchBox.Checked; };

        foreach (var c in new Control[] { autoReconnectBox, quitGuiBox, acceptBannerBox, launchBox })
            c.Margin = new Padding(3, 6, 3, 3);

        AddRow(col, autoReconnectBox);
        AddRow(col, quitGuiBox);
        var hint = MakeLabel("Cisco 界面开着时，命令行无法发起连接。退出界面不会影响 VPN 本身。", Secondary, 8, 500);
        hint.Margin = new Padding(22, 0, 3, 3);
        AddRow(col, hint);
        AddRow(col, acceptBannerBox);
        intervalBox.Margin = new Padding(3, 3, 3, 3);
        AddRow(col, Row(MakeLabel("状态检查间隔："), intervalBox, MakeLabel("秒")));
        AddRow(col, launchBox);
        AddRow(col, Row(MakeLabel("Cisco 命令行："), cliLabel));
        AddRow(col, new Panel(), SizeType.Percent, 100);

        page.Controls.Add(col);
        return page;
    }

    void RefreshGeneral()
    {
        refreshingGeneral = true;
        autoReconnectBox.Checked = config.AutoReconnect;
        quitGuiBox.Checked = config.QuitCiscoGUI;
        acceptBannerBox.Checked = config.AcceptBanner;
        intervalBox.Value = config.CheckInterval;
        launchBox.Checked = config.LaunchAtLogin;
        cliLabel.Text = vpn.CliPath ?? "未找到";
        refreshingGeneral = false;
    }

    // MARK: - 日志

    readonly Label logState = MakeLabel("");
    readonly Label logPause = MakeLabel("", Orange);
    readonly TextBox logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9),
        BackColor = SystemColors.Window,
    };
    int shownLogCount = -1;
    string? shownLogLast;

    TabPage BuildLogTab()
    {
        var page = new TabPage("日志") { UseVisualStyleBackColor = true };
        var col = Column(new Padding(8));

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logPause.AutoEllipsis = true;
        logPause.AutoSize = false;
        logPause.Dock = DockStyle.Fill;
        logPause.TextAlign = ContentAlignment.MiddleLeft;
        top.Controls.Add(logState, 0, 0);
        top.Controls.Add(logPause, 1, 0);
        top.Controls.Add(Row(
            MakeButton("打开日志文件", () =>
            {
                try { Process.Start(new ProcessStartInfo(VPNController.LogFilePath) { UseShellExecute = true }); } catch { }
            }),
            MakeButton("清空", vpn.ClearLog),
            MakeButton("复制", () =>
            {
                if (vpn.LogLines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, vpn.LogLines));
            })), 2, 0);
        AddRow(col, top);
        AddRow(col, logBox, SizeType.Percent, 100);

        page.Controls.Add(col);
        return page;
    }

    void RefreshLog()
    {
        logState.Text = $"状态：{vpn.State.Title()}";
        var pause = vpn.AutoPaused && vpn.PauseReason.Length > 0 ? $"· {vpn.PauseReason}" : "";
        logPause.Text = pause;
        tooltip.SetToolTip(logPause, pause);

        var last = vpn.LogLines.LastOrDefault();
        if (vpn.LogLines.Count == shownLogCount && last == shownLogLast) return;
        shownLogCount = vpn.LogLines.Count;
        shownLogLast = last;
        logBox.Text = string.Join(Environment.NewLine, vpn.LogLines.Select(l => l.Replace("\n", Environment.NewLine)));
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }
}
