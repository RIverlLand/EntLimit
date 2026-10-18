# EntLimit 0.3.1

首个公开预览版本，面向 Windows x64。

## 包含功能

- 应用扫描与手动添加、前台窗口软管控和 90 分钟共享许可。
- 网络场景多问、Python 本地练习与参考解法、通过记录和轻松复习。
- 可选的 API 配置、DPAPI 密钥存储、在线代码建议和题包生成。
- JSON 题包预览去重、实验性 Java 运行器与扩展协议。
- Windows 安装器、可选登录自启与自用守护、独立紧急关闭程序。
- 无许可时学习入口置顶最大化；到期仍只提醒收尾。

## 下载与校验

下载此 Release 的 `EntLimit-Setup-0.3.1-win-x64.exe` 和同名 `.sha256`。PowerShell 可运行：

```powershell
Get-FileHash -LiteralPath .\EntLimit-Setup-0.3.1-win-x64.exe -Algorithm SHA256
```

安装包自带 .NET 运行库，安装时请求管理员授权。Python 练习需已有 Python 3.8+；实验性 Java 需已有 JDK。网络知识题可离线使用。安装器未签名，当前不具备可信发布者签名。

安装目录内含 `EntLimit.EmergencyStop.exe`，开始菜单提供 Emergency Stop 快捷方式；它能独立停用应用，保留学习记录。

## 预览版边界

题库仍少；不含自动抓取 LeetCode、完整第三方插件管理或家长模式。窗口软管控允许进程短暂显示，不能阻止拥有本机权限的用户绕过。浏览器扩展需手动安装，真实全屏、多屏和干净机器安装体验仍需进一步验证。
