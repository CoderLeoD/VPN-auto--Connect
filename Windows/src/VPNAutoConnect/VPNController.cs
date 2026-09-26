using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using VPNAutoConnect.Core;

namespace VPNAutoConnect;

/// 通过 Cisco 自带的命令行工具 vpncli.exe 查询状态、建立和断开连接。
/// 所有方法都在 UI 线程上调用（async 后续也回到 UI 线程），与 Mac 版的 @MainActor 对应。
sealed class VPNController
{
    public static VPNController Shared { get; } = new();

    /// 状态、日志等变化时触发（UI 据此刷新）
    public event Action? Changed;
    /// 需要弹系统通知时触发 (title, body)
    public event Action<string, string>? Notify;

    public VPNState State { get; private set; } = VPNState.Unknown;
    public bool Busy { get; private set; }
    public string LastMessage { get; private set; } = "";
    public DateTime? LastConnectedAt { get; private set; }
    public List<string> LogLines { get; } = [];
    /// 用户在本程序里手动断开，或连续认证失败后，暂停自动重连
    public bool AutoPaused { get; private set; }
    public string PauseReason { get; private set; } = "";

    readonly AppConfig config = AppConfig.Shared;
    SynchronizationContext? ui;
    System.Windows.Forms.Timer? timer;
    int consecutiveFailures;
    int authFailures;
    DateTime nextAutoAttempt = DateTime.MinValue;
    long? lastUsedTOTPCounter;
    string? lastBlocker;

    static readonly string[] CliCandidates = BuildCliCandidates();

    static string[] BuildCliCandidates()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }.Where(r => r.Length > 0).Distinct();
        string[] products = ["Cisco Secure Client", "Cisco AnyConnect Secure Mobility Client"];
        return products.SelectMany(p => roots.Select(r => Path.Combine(r, "Cisco", p, "vpncli.exe"))).ToArray();
    }

    public string? CliPath => CliCandidates.FirstOrDefault(File.Exists);

    /// Cisco 图形界面的进程名（Secure Client / 旧版 AnyConnect）
    static readonly string[] CiscoGuiProcesses = ["csc_ui", "vpnui"];

    // MARK: - 生命周期

    public void Start()
    {
        ui = SynchronizationContext.Current;
        if (config.FillUsernameFromCiscoIfEmpty())
            Log("用户名为空，已从 Cisco Secure Client 的记录中自动填入");

        // SystemEvents 可能在其他线程触发，统一切回 UI 线程
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) ui?.Post(_ => HandleWake(), null);
        };
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
                or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                ui?.Post(_ => HandleWake(), null);
        };
        RescheduleTimer();
        _ = Tick();
    }

    public void RescheduleTimer()
    {
        timer?.Dispose();
        timer = new System.Windows.Forms.Timer { Interval = Math.Max(5, config.CheckInterval) * 1000 };
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
    }

    async void HandleWake()
    {
        Log("检测到唤醒/解锁，稍后检查 VPN 状态");
        consecutiveFailures = 0;
        nextAutoAttempt = DateTime.MinValue;
        // 刚唤醒时网络往往还没就绪，分两次检查
        await Task.Delay(4000);
        await Tick();
        await Task.Delay(8000);
        await Tick();
    }

    // MARK: - 定时检查 + 自动重连

    public async Task Tick()
    {
        if (Busy) return;
        config.LoadSecretsIfNeeded();
        await RefreshState();
        if (State != VPNState.Disconnected) { lastBlocker = null; return; }
        // 未连接却不能自动连时，把原因写进日志（原因变化时才记一次）
        var blocker = AutoConnectBlocker();
        if (blocker != lastBlocker && blocker != null) Log($"未连接，但不自动重连：{blocker}");
        lastBlocker = blocker;
        if (blocker != null || DateTime.Now < nextAutoAttempt) return;
        Log("检测到 VPN 未连接，自动重连");
        await Connect(manual: false);
    }

    public async Task RefreshState()
    {
        if (CliPath is not { } cli) { SetState(VPNState.NotInstalled); return; }
        var output = (await Shell.Run(cli, "state", null, TimeSpan.FromSeconds(20))).Output;
        var newState = VpnOutput.ParseState(output);
        if (newState == VPNState.Connected && State != VPNState.Connected) LastConnectedAt = DateTime.Now;
        if (newState != State)
        {
            Log($"状态：{State.Title()} → {newState.Title()}");
            if (newState == VPNState.ServiceUnavailable)
            {
                LastMessage = "Cisco VPN 服务没有运行，请在「服务」中启动 Cisco Secure Client Agent";
                Log(LastMessage + "\n" + VpnOutput.Tail(output, 6));
                Notify?.Invoke("Cisco VPN 服务未运行", LastMessage);
            }
            else if (State == VPNState.ServiceUnavailable)
            {
                LastMessage = "";
            }
        }
        SetState(newState);
    }

    string? AutoConnectBlocker()
    {
        if (!config.AutoReconnect) return "自动重连已在设置中关闭";
        if (AutoPaused) return PauseReason.Length == 0 ? "自动重连已暂停" : PauseReason;
        if (config.CredentialError is { } e) return e;
        if (!config.SecretsLoaded) return "尚未读取到凭据管理器";
        if (!config.IsComplete) return "配置不完整，缺少" + string.Join("、", config.MissingItems);
        return null;
    }

    // MARK: - 连接 / 断开

    public async Task Connect(bool manual)
    {
        if (Busy) return;
        if (CliPath is not { } cli) { SetState(VPNState.NotInstalled); return; }
        if (State == VPNState.ServiceUnavailable)
        {
            LastMessage = "Cisco VPN 服务未运行，无法连接。请先在「服务」中启动它。";
            Log(LastMessage);
            return;
        }
        config.LoadSecretsIfNeeded();
        if (!config.IsComplete || config.Password is not { } password || config.Totp is not { } totp)
        {
            LastMessage = config.CredentialError ?? ("请先在设置中补全：" + string.Join("、", config.MissingItems));
            Log(LastMessage);
            return;
        }
        if (manual)
        {
            AutoPaused = false;
            PauseReason = "";
            authFailures = 0;
        }

        Busy = true;
        SetState(VPNState.Connecting);
        try
        {
            if (config.QuitCiscoGUI) await QuitCiscoGUI();

            // 验证码快过期时等下一个周期，避免提交时刚好失效；同一个码不重复使用
            var now = DateTimeOffset.UtcNow;
            var counter = TOTP.Counter(totp.Period, now);
            if (TOTP.SecondsRemaining(totp.Period, now) < 8 || counter == lastUsedTOTPCounter)
            {
                var wait = TOTP.SecondsRemaining(totp.Period, now) + 1;
                LastMessage = $"等待新的验证码（{wait} 秒）…";
                Log(LastMessage);
                await Task.Delay(wait * 1000);
                now = DateTimeOffset.UtcNow;
                counter = TOTP.Counter(totp.Period, now);
            }
            if (TOTP.Code(totp, now) is not { } code)
            {
                LastMessage = "验证码密钥无效，请重新配置";
                return;
            }
            lastUsedTOTPCounter = counter;

            // 与 Cisco GUI 的登录顺序一致：[组] → 用户名 → 密码 → Second Password(验证码) → 接受 banner
            var lines = new List<string> { $"connect {config.Server.Trim()}" };
            if (config.Group.Trim().Length > 0) lines.Add(config.Group.Trim());
            lines.Add(config.Username.Trim());
            lines.Add(password);
            lines.Add(code);
            if (config.AcceptBanner) lines.Add("y");
            lines.Add("exit");
            var input = string.Join("\r\n", lines) + "\r\n";

            LastMessage = $"正在连接 {config.Server}…";
            Log(LastMessage);
            var result = await Shell.Run(cli, "-s", input, TimeSpan.FromSeconds(90));
            var output = VpnOutput.Redact(result.Output, [password, code]);
            Log("vpncli 输出：\n" + VpnOutput.Tail(output, 25));

            await RefreshState();
            if (State == VPNState.Connected)
            {
                consecutiveFailures = 0;
                authFailures = 0;
                LastConnectedAt = DateTime.Now;
                LastMessage = "连接成功";
                Log(LastMessage);
                return;
            }

            consecutiveFailures++;
            if (VpnOutput.IsAuthFailure(output))
            {
                authFailures++;
                LastMessage = $"认证失败（第 {authFailures} 次）";
                // 连续两次认证失败就停，防止账号被锁
                if (authFailures >= 2)
                {
                    AutoPaused = true;
                    PauseReason = "连续认证失败，已暂停自动重连。请检查密码/验证码是否已在 Cisco 中更换。";
                    Notify?.Invoke("VPN 自动连接已暂停", PauseReason);
                }
            }
            else if (result.TimedOut)
            {
                LastMessage = "连接超时";
            }
            else
            {
                LastMessage = "连接失败：" + (VpnOutput.LastMeaningfulLine(output) ?? "未知原因");
            }
            Log(LastMessage);

            var backoff = Math.Min(300.0, 15.0 * Math.Pow(2.0, consecutiveFailures - 1));
            nextAutoAttempt = DateTime.Now.AddSeconds(backoff);
            if (!manual && consecutiveFailures == 3)
                Notify?.Invoke("VPN 自动重连失败", LastMessage);
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    public async Task Disconnect()
    {
        if (Busy || CliPath is not { } cli) return;
        Busy = true;
        SetState(VPNState.Disconnecting);
        AutoPaused = true;
        PauseReason = "你手动断开了连接，自动重连已暂停（点“立即连接”即可恢复）";
        var output = (await Shell.Run(cli, "disconnect", null, TimeSpan.FromSeconds(30))).Output;
        Log("断开：\n" + VpnOutput.Tail(output, 5));
        Busy = false;
        await RefreshState();
        LastMessage = "已断开";
        Changed?.Invoke();
    }

    public void ToggleAutoPause()
    {
        AutoPaused = !AutoPaused;
        PauseReason = AutoPaused ? "已手动暂停自动重连" : "";
        Changed?.Invoke();
        if (!AutoPaused)
        {
            authFailures = 0;
            consecutiveFailures = 0;
            nextAutoAttempt = DateTime.MinValue;
            _ = Tick();
        }
    }

    /// Cisco 的图形界面运行时会占用连接能力，命令行无法发起连接，因此先退出它（VPN 服务本身不受影响）
    async Task QuitCiscoGUI()
    {
        var procs = CiscoGuiProcesses.SelectMany(Process.GetProcessesByName).ToList();
        if (procs.Count == 0) return;
        Log("退出 Cisco Secure Client 界面以便命令行连接");
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { }
        }
        for (int i = 0; i < 10 && procs.Any(p => !HasExited(p)); i++)
            await Task.Delay(300);
        foreach (var p in procs.Where(p => !HasExited(p)))
        {
            try { p.Kill(); } catch (Exception e) { Log($"无法结束 {p.ProcessName}：{e.Message}"); }
        }
        await Task.Delay(500);
        procs.ForEach(p => p.Dispose());
    }

    static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    // MARK: - 工具

    void SetState(VPNState s)
    {
        State = s;
        Changed?.Invoke();
    }

    public void Log(string s)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {s}";
        LogLines.Add(line);
        AppendToLogFile(line);
        if (LogLines.Count > 400) LogLines.RemoveRange(0, LogLines.Count - 400);
        Changed?.Invoke();
    }

    public void ClearLog()
    {
        LogLines.Clear();
        Changed?.Invoke();
    }

    public static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VPNAutoConnect", "VPNAutoConnect.log");

    static void AppendToLogFile(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            // 超过 1MB 就轮转一次
            var info = new FileInfo(LogFilePath);
            if (info.Exists && info.Length > 1_000_000)
                File.Move(LogFilePath, LogFilePath + ".old", overwrite: true);
            File.AppendAllText(LogFilePath, line.Replace("\n", Environment.NewLine) + Environment.NewLine);
        }
        catch { /* 日志写不进去不影响主流程 */ }
    }

    /// 打开「服务」管理器，方便启动 Cisco Secure Client Agent
    public void OpenServices()
    {
        try { Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); } catch { }
    }
}

static class Shell
{
    public record Result(string Output, int ExitCode, bool TimedOut);

    public static async Task<Result> Run(string path, string args, string? input, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(path, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 不写 BOM，否则第一行命令前会多出不可见字符
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception e)
        {
            return new Result($"无法启动 {path}: {e.Message}", -1, false);
        }

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            if (input != null) await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close();
        }
        catch { /* 进程提前退出时写入会失败，忽略 */ }

        var timedOut = false;
        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        // 被结束的进程也要把已输出的内容收回来
        var done = Task.WhenAll(stdout, stderr);
        await Task.WhenAny(done, Task.Delay(3000));
        var text = (stdout.IsCompletedSuccessfully ? stdout.Result : "")
            + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
        return new Result(text, p.HasExited ? p.ExitCode : -1, timedOut);
    }
}
