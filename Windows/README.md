# Windows 版（待开发）

思路与 Mac 版一致，可复用的部分：

- 登录流程：`vpncli.exe -s`（路径通常为 `C:\Program Files (x86)\Cisco\Cisco Secure Client\vpncli.exe`），
  标准输入依次写入 `connect <服务器>` → [组] → 用户名 → 密码 → 验证码 → `y` → `exit`
- 状态查询：`vpncli.exe state`，取最后一行 `state: xxx`
- 连接前需退出 `csc_ui.exe`（Cisco 界面），否则命令行无法连接
- TOTP 算法、otpauth:// 与 otpauth-migration:// 的解析逻辑见 `../Mac/Sources/VPNAutoConnect/TOTP.swift`，
  测试向量见 `../Mac/Tests/VPNAutoConnectTests/TOTPTests.swift`
- 凭据存储改用 Windows 凭据管理器（DPAPI）
