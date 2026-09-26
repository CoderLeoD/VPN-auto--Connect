# Cisco VPN AutoConnect

自动完成 Cisco Secure Client 的「密码 + 六位验证码」两步登录，锁屏/睡眠唤醒后掉线会自动重连。

```
CiscoVPNAuto/
├── Mac/        macOS 菜单栏程序（Swift / SwiftUI），可打包为 dmg
└── Windows/    Windows 托盘程序（C# / .NET 8 / WinForms），打包为单个 exe，见其中 README
```

## clone 后先运行

```bash
./scripts/setup-git.sh
```

只为本仓库配置提交身份（GitHub 隐藏邮箱）、SSH 密钥，并启用 `.githooks`：提交和推送前会检查作者/提交者邮箱，
不是 `xxx@users.noreply.github.com` 的一律拒绝，避免把真实邮箱带到 GitHub。不会修改全局 git 或 `~/.ssh` 配置。
其他人使用时通过环境变量换成自己的：`GIT_NAME=... GIT_EMAIL=...@users.noreply.github.com SSH_KEY=~/.ssh/xxx ./scripts/setup-git.sh`。

## Mac 版

### 打包

```bash
cd Mac
./scripts/build_dmg.sh                 # 输出 dist/VPNAutoConnect-1.0.0.dmg
UNIVERSAL=1 ./scripts/build_dmg.sh     # Intel + Apple Silicon 通用版
VERSION=1.1.0 ./scripts/build_dmg.sh   # 指定版本号
swift test                             # 单元测试
```

需要 macOS 13+ 与 Xcode。

### 安装与配置

1. 打开 dmg，把 **VPN AutoConnect** 拖到「应用程序」，然后启动。首次启动会自动打开设置窗口。
2. **账户**：填写 VPN 服务器、用户名、密码（第一步）。
3. **验证码**：任选一种方式导入 Google Authenticator 的密钥
   - 拖入 / 选择 / 从剪贴板粘贴 二维码图片（程序内置识别）
   - 粘贴其他工具识别出的文本：`otpauth://totp/...`、`otpauth-migration://...` 或 Base32 密钥
   - 如果原始二维码找不到了：在手机 Google Authenticator 中「转移账户 → 导出账户」，截图后导入即可
   - 导入后对比界面上的验证码与手机上是否一致
4. **通用**：建议勾选「登录 macOS 时自动启动」。

菜单栏图标：🔒 已连接 / 🔄 连接中 / 斜杠盾牌 未连接。菜单里可手动连接、断开、暂停自动重连、复制当前验证码、查看日志。

### 更换密码 / 验证码

在 Cisco Secure Client 那边改完后：

- 改密码：设置 → 账户 → 输入新密码 → 保存（留空表示不修改）
- 换验证码：设置 → 验证码 → 导入新二维码（会覆盖旧的）

### 工作原理与注意事项

- 调用 Cisco 自带命令行 `/opt/cisco/secureclient/bin/vpn`，每 15 秒（可调）检查一次状态，唤醒/解锁后会立即检查。
- Cisco 图形界面开着时命令行无法连接，所以连接前会自动退出 Cisco 界面（VPN 服务本身不受影响，可在「通用」里关闭）。
- 密码和验证码密钥只保存在 macOS 钥匙串中；日志里会把密码和验证码打码。
- **防锁号**：连续 2 次认证失败会暂停自动重连并发通知，更新密码/验证码后点「立即连接」恢复。
  非认证类失败（网络没就绪等）按 15s → 30s → … → 5 分钟退避重试。
- 在本程序里手动「断开」后会暂停自动重连，点「立即连接」恢复。
- 程序是本地 ad-hoc 签名；若将来重新打包覆盖安装，首次读取钥匙串时 macOS 可能会弹窗询问，选「始终允许」即可。
