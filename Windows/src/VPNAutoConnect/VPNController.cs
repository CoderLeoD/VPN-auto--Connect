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
        Log($"程序启动（版本 {Application.ProductVersion.Split('+')[0]}）");
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
            // 上一次程序异常退出时留下的 vpncli 会一直占着 Cisco 的连接能力，先清掉
            await CleanUpStrayCli();

            if (await AttemptConnect(cli, password, totp) is not { } first) return;
            var (result, output) = first;
            if (State != VPNState.Connected && VpnOutput.IsConnectCapabilityBusy(output))
            {
                Log("Cisco 的连接能力被其他程序占用，清理后重试一次");
                if (config.QuitCiscoGUI) await QuitCiscoGUI();
                await CleanUpStrayCli();
                if (await AttemptConnect(cli, password, totp) is not { } again) return;
                (result, output) = again;
            }

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
            else if (VpnOutput.IsConnectCapabilityBusy(output))
            {
                LastMessage = config.QuitCiscoGUI
                    ? "连接失败：Cisco 的连接能力被其他程序占用（已尝试清理）"
                    : "连接失败：Cisco 的连接能力被其他程序占用，可在设置中开启「连接前退出 Cisco 界面」";
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

    /// 取验证码并跑一次 vpncli -s。返回 null 表示验证码不可用（已写 LastMessage）
    async Task<(Shell.Result Result, string Output)?> AttemptConnect(string cli, string password, TOTPConfig totp)
    {
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
            return null;
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
        // 连接能力被占用时 vpncli 不会自己退出，看到这句就提前结束，不白等 90 秒
        var result = await Shell.Run(cli, "-s", input, TimeSpan.FromSeconds(90), VpnOutput.IsConnectCapabilityBusy);
        var output = VpnOutput.Redact(result.Output, [password, code]);
        Log("vpncli 输出：\n" + VpnOutput.Tail(output, 25));
        await RefreshState();
        return (result, output);
    }

    /// 结束当前会话里、不是本程序正在等待的 vpncli 进程
    async Task CleanUpStrayCli()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var mine = Shell.RunningPids();
        var all = Process.GetProcessesByName("vpncli");
        try
        {
            var strays = all.Where(p =>
            {
                try { return p.SessionId == session && !mine.Contains(p.Id); } catch { return false; }
            }).ToList();
            if (strays.Count == 0) return;
            Log("清理残留的 Cisco 命令行进程：" + string.Join(", ", strays.Select(p => p.Id)));
            foreach (var p in strays)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception e) { Log($"无法结束 vpncli（{p.Id}）：{e.Message}"); }
            }
            for (int i = 0; i < 10 && strays.Any(p => !HasExited(p)); i++)
                await Task.Delay(300);
            await Task.Delay(500);
        }
        finally
        {
            foreach (var p in all) p.Dispose();
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

    /// 本程序启动、尚未结束的子进程。退出时据此收尾，清理残留时据此排除自己的进程
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> Running = new();

    public static HashSet<int> RunningPids() => [.. Running.Keys];

    /// 程序退出前调用：结束所有还在跑的子进程，避免它们变成孤儿继续占着 Cisco 的连接能力
    public static void KillAll()
    {
        foreach (var p in Running.Values)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <param name="stopWhen">每收到一行输出就用已收集的全部输出调用一次，返回 true 时提前结束进程</param>
    public static async Task<Result> Run(string path, string args, string? input, TimeSpan timeout,
        Func<string, bool>? stopWhen = null)
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
        var buffer = new StringBuilder();
        var gate = new object();
        using var stopCts = new CancellationTokenSource();
        void OnData(object? _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            bool stop;
            lock (gate)
            {
                buffer.Append(e.Data).Append('\n');
                stop = stopWhen?.Invoke(buffer.ToString()) == true;
            }
            if (stop)
            {
                try { stopCts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
        p.OutputDataReceived += OnData;
        p.ErrorDataReceived += OnData;
        try
        {
            p.Start();
        }
        catch (Exception e)
        {
            return new Result($"无法启动 {path}: {e.Message}", -1, false);
        }
        var pid = p.Id;
        Running[pid] = p;
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                if (input != null) await p.StandardInput.WriteAsync(input);
                p.StandardInput.Close();
            }
            catch { /* 进程提前退出时写入会失败，忽略 */ }

            var timedOut = false;
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stopCts.Token);
            try
            {
                // 进程退出且输出读完才返回
                await p.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !stopCts.IsCancellationRequested;
                try { p.Kill(entireProcessTree: true); } catch { }
                // 被结束的进程也要把已输出的内容收回来
                await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(3000));
            }
            string text;
            lock (gate) text = buffer.ToString();
            return new Result(text, p.HasExited ? p.ExitCode : -1, timedOut);
        }
        finally
        {
            Running.TryRemove(pid, out _);
        }
    }
}
