using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VPNAutoConnect.Core;

namespace VPNAutoConnect;

/// 非敏感配置存 %APPDATA%\VPNAutoConnect\config.json；密码和 TOTP 密钥存 Windows 凭据管理器
sealed class AppConfig
{
    public static AppConfig Shared { get; } = new();

    /// 任意配置变化时触发（UI 据此刷新）
    public event Action? Changed;

    sealed class Settings
    {
        public string Server { get; set; } = "";
        public string Group { get; set; } = "";
        public string Username { get; set; } = "";
        public bool AutoReconnect { get; set; } = true;
        public bool QuitCiscoGUI { get; set; } = true;
        public bool AcceptBanner { get; set; } = true;
        public int CheckInterval { get; set; } = 15;
    }

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNAutoConnect");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly Settings s;

    public string Server { get => s.Server; set => Update(() => s.Server = value); }
    public string Group { get => s.Group; set => Update(() => s.Group = value); }
    public string Username { get => s.Username; set => Update(() => s.Username = value); }
    public bool AutoReconnect { get => s.AutoReconnect; set => Update(() => s.AutoReconnect = value); }
    public bool QuitCiscoGUI { get => s.QuitCiscoGUI; set => Update(() => s.QuitCiscoGUI = value); }
    public bool AcceptBanner { get => s.AcceptBanner; set => Update(() => s.AcceptBanner = value); }
    public int CheckInterval { get => s.CheckInterval; set => Update(() => s.CheckInterval = Math.Clamp(value, 5, 300)); }

    public bool HasPassword { get; private set; }
    public TOTPConfig? Totp { get; private set; }
    /// 凭据管理器是否已成功读取过
    public bool SecretsLoaded { get; private set; }
    public string? CredentialError { get; private set; }
    string? cachedPassword;

    AppConfig()
    {
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
                : new Settings();
        }
        catch
        {
            s = new Settings();
        }
        s.CheckInterval = Math.Clamp(s.CheckInterval, 5, 300);
    }

    void Update(Action apply)
    {
        apply();
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOptions));
        }
        catch (Exception e)
        {
            VPNController.Shared.Log($"保存配置失败：{e.Message}");
        }
        Changed?.Invoke();
    }

    /// 读取凭据管理器，成功后缓存在内存；失败则下次再试
    public void LoadSecretsIfNeeded()
    {
        if (SecretsLoaded) return;
        var pw = CredentialStore.Read("password");
        var json = CredentialStore.Read("totp");
        var failed = new[] { pw.error, json.error }.FirstOrDefault(e => e != 0 && e != CredentialStore.ERROR_NOT_FOUND);
        if (failed != 0)
        {
            CredentialError = $"无法读取 Windows 凭据管理器（错误码 {failed}）";
            Changed?.Invoke();
            return;
        }
        CredentialError = null;
        cachedPassword = pw.value;
        HasPassword = !string.IsNullOrEmpty(pw.value);
        if (json.value != null)
        {
            try { Totp = JsonSerializer.Deserialize<TOTPConfig>(json.value); } catch { Totp = null; }
        }
        SecretsLoaded = true;
        Changed?.Invoke();
    }

    public string? Password => cachedPassword;

    public void SetPassword(string value)
    {
        CredentialStore.Set(value, "password");
        cachedPassword = value;
        HasPassword = value.Length > 0;
        Changed?.Invoke();
    }

    public void SetTotp(TOTPConfig? config)
    {
        CredentialStore.Set(config == null ? null : JsonSerializer.Serialize(config), "totp");
        Totp = config;
        Changed?.Invoke();
    }

    // MARK: - 从 Cisco 读取上次登录的信息

    /// Cisco 会把上次登录的用户名/服务器记在 %LOCALAPPDATA%\Cisco\...\preferences.xml 中
    static string? ReadCiscoPreference(string tag)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] files =
        [
            Path.Combine(local, @"Cisco\Cisco Secure Client\VPN\preferences.xml"),
            Path.Combine(local, @"Cisco\Cisco AnyConnect Secure Mobility Client\preferences.xml"),
        ];
        var regex = new Regex($@"<{tag}>\s*([^<]+?)\s*</{tag}>");
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                var m = regex.Match(File.ReadAllText(f));
                if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.Trim();
            }
            catch { /* 读不到就算了 */ }
        }
        return null;
    }

    public static string? DetectCiscoUsername() => ReadCiscoPreference("DefaultUser");
    public static string? DetectCiscoServer() => ReadCiscoPreference("DefaultHostName");

    /// 用户名为空时自动补上，返回是否补了
    public bool FillUsernameFromCiscoIfEmpty()
    {
        if (Username.Trim().Length > 0 || DetectCiscoUsername() is not { } name) return false;
        Username = name;
        return true;
    }

    /// 缺少的配置项（用于提示为什么不能自动连接）
    public List<string> MissingItems
    {
        get
        {
            var m = new List<string>();
            if (Server.Trim().Length == 0) m.Add("服务器");
            if (Username.Trim().Length == 0) m.Add("用户名");
            if (!HasPassword) m.Add("密码");
            if (Totp == null) m.Add("验证码密钥");
            return m;
        }
    }

    public bool IsComplete => MissingItems.Count == 0;

    // MARK: - 开机自启

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "VPNAutoConnect";

    public bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue(RunValue, false);
            }
            catch (Exception e)
            {
                VPNController.Shared.Log($"设置开机自启失败：{e.Message}");
            }
            Changed?.Invoke();
        }
    }

    /// 自启项里记录的路径与当前程序不一致（程序被挪动过）时，更新为当前路径
    public void RefreshLaunchAtLoginPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is string v && v != $"\"{Environment.ProcessPath}\"")
            LaunchAtLogin = true;
    }
}
