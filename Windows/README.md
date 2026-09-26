# Windows 版

逻辑、界面与 Mac 版一致：通知区域（托盘）图标 + 右键菜单 + 四页设置窗口（账户 / 验证码 / 通用 / 日志）。
C# / .NET 8 / WinForms，打包为单个 exe（自带运行时，目标电脑无需安装 .NET）。

```
Windows/
├── src/VPNAutoConnect.Core/   跨平台核心：TOTP、otpauth / otpauth-migration 解析、二维码识别(ZXing)、vpncli 输出解析
├── src/VPNAutoConnect/        WinForms 托盘程序
├── tests/                     单元测试（与 Mac 版同一套测试向量，可在 macOS 上跑）
└── scripts/                   打包脚本、图标生成脚本
```

## 打包

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，在 Windows、macOS、Linux 上都能打包。

```powershell
# Windows
.\scripts\build.ps1                     # 输出 dist\VPNAutoConnect-1.0.0-win-x64.zip
.\scripts\build.ps1 -Arch arm64         # ARM 版 Windows
.\scripts\build.ps1 -Version 1.1.0      # 指定版本号
```

```bash
# macOS / Linux 交叉编译
./scripts/build.sh
ARCH=arm64 VERSION=1.1.0 ./scripts/build.sh
dotnet test tests/VPNAutoConnect.Tests  # 单元测试
```

程序图标 `src/VPNAutoConnect/Resources/app.ico` 由 `python3 scripts/make_icon.py src/VPNAutoConnect/Resources/app.ico` 生成。

## 安装与配置

1. 解压 zip，把 `VPNAutoConnect.exe` 放到一个固定位置（例如 `%LOCALAPPDATA%\Programs\VPNAutoConnect\`），双击运行。
   首次运行 Windows SmartScreen 可能提示“已保护你的电脑”，点「更多信息 → 仍要运行」。首次启动会自动打开设置窗口。
2. **账户**：填写 VPN 服务器、用户名、密码（第一步）。服务器和用户名会尝试从 Cisco 上次的登录记录中预填。
3. **验证码**：任选一种方式导入 Google Authenticator 的密钥
   - 拖入 / 选择 / 从剪贴板粘贴 二维码图片（程序内置识别；截图后 `Win+Shift+S` 复制即可直接粘贴）
   - 粘贴其他工具识别出的文本：`otpauth://totp/...`、`otpauth-migration://...` 或 Base32 密钥
   - 如果原始二维码找不到了：在手机 Google Authenticator 中「转移账户 → 导出账户」，截图后导入即可
   - 导入后对比界面上的验证码与手机上是否一致
4. **通用**：建议勾选「登录 Windows 时自动启动」。

托盘图标：绿色带锁盾牌 已连接 / 蓝色旋转箭头 连接中 / 灰色斜杠盾牌 未连接 / 橙色感叹号 异常。
右键菜单可手动连接、断开、暂停自动重连、复制当前验证码、查看日志；左键单击打开设置。
Windows 11 默认会把新图标收进「^」溢出区，可在「设置 → 个性化 → 任务栏 → 其他系统托盘图标」里让它常驻。

### 更换密码 / 验证码

- 改密码：设置 → 账户 → 输入新密码 → 保存（留空表示不修改）
- 换验证码：设置 → 验证码 → 导入新二维码（会覆盖旧的）

## 工作原理与注意事项

- 调用 Cisco 自带命令行 `vpncli.exe`（`C:\Program Files (x86)\Cisco\Cisco Secure Client\`，也兼容旧版 AnyConnect 路径），
  每 15 秒（可调）执行 `vpncli state` 检查一次，睡眠唤醒 / 解锁后会立即检查。
- 连接时执行 `vpncli -s`，从标准输入依次写入 `connect <服务器>` → [组] → 用户名 → 密码 → 验证码 → `y` → `exit`。
- Cisco 图形界面（`csc_ui.exe`）开着时命令行无法连接，所以连接前会自动退出它（VPN 服务本身不受影响，可在「通用」里关闭）。
- 密码和验证码密钥只保存在 Windows 凭据管理器中（控制面板 → 凭据管理器 → Windows 凭据，名称以 `VPNAutoConnect:` 开头）；
  其余设置在 `%APPDATA%\VPNAutoConnect\config.json`；日志在 `%LOCALAPPDATA%\VPNAutoConnect\VPNAutoConnect.log`，密码和验证码会打码。
- **防锁号**：连续 2 次认证失败会暂停自动重连并弹通知，更新密码/验证码后点「立即连接」恢复。
  非认证类失败（网络没就绪等）按 15s → 30s → … → 5 分钟退避重试。
- 在本程序里手动「断开」后会暂停自动重连，点「立即连接」恢复。
- 若提示「Cisco VPN 服务未运行」，菜单里可一键打开「服务」，启动 **Cisco Secure Client - AnyConnect VPN Agent**（`csc_vpnagent`）。
- 开机自启写在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；挪动 exe 位置后运行一次即会自动更新路径。
