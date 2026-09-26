using System.Runtime.InteropServices;
using System.Text;

namespace VPNAutoConnect;

/// 密码与 TOTP 密钥都只保存在 Windows 凭据管理器中（由系统用 DPAPI 加密），不落盘明文
static class CredentialStore
{
    const string Prefix = "VPNAutoConnect:";
    const int CRED_TYPE_GENERIC = 1;
    const int CRED_PERSIST_LOCAL_MACHINE = 2;
    public const int ERROR_NOT_FOUND = 1168;

    public static void Set(string? value, string account)
    {
        var target = Prefix + account;
        if (string.IsNullOrEmpty(value))
        {
            CredDelete(target, CRED_TYPE_GENERIC, 0);
            return;
        }
        var blob = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = ptr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = account,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"写入凭据管理器失败（错误码 {Marshal.GetLastWin32Error()}）");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// 返回值和错误码；ERROR_NOT_FOUND 表示确实没存过，其他非 0 表示读取失败
    public static (string? value, int error) Read(string account)
    {
        if (!CredRead(Prefix + account, CRED_TYPE_GENERIC, 0, out var p))
            return (null, Marshal.GetLastWin32Error());
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return (null, 0);
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return (Encoding.UTF8.GetString(bytes), 0);
        }
        finally
        {
            CredFree(p);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
