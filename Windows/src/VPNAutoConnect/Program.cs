namespace VPNAutoConnect;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 只允许运行一个实例
        using var mutex = new Mutex(true, @"Local\VPNAutoConnect.SingleInstance", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        // 未处理的异常只记日志，不让托盘程序崩掉
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => VPNController.Shared.Log($"内部错误：{e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        // 让 async/await 的后续代码回到 UI 线程（与 Mac 版的 @MainActor 对应）
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
    }
}
