# Codex Peek

A compact Windows widget for monitoring Codex weekly quota, local token usage, estimated throughput, and activity status. Supports floating and Codex-attached modes.

**Codex 周额度和本机用量的小型 Windows 悬浮窗。** 默认约 286 × 56 逻辑像素，支持独立悬浮或吸附在 Codex 桌面窗口旁。点击悬浮条可打开详情。

[下载 Windows 程序](./CodexPeek.exe) · [使用说明](#功能) · [安全与隐私](#安全与隐私) · [构建源码](#构建源码)

## 功能

- 显示 ChatGPT 账号的 Codex 周剩余额度和重置时间，默认每分钟读取一次。
- 查看今日或近 7 个本地自然日的输入、输出、缓存、推理 Token 和会话数。
- 显示最近完成回合的估算速度和本机任务状态。
- 可拖动的小窗口，支持展开、透明度、大小、刷新频率、置顶、托盘隐藏和吸附位置设置。
- Windows 原生 WinForms 程序；不依赖 Electron 或额外的第三方运行时。

## 指标口径

- 周额度使用账号接口返回的周窗口（10080 分钟）；额度不可读取时显示 `--`，不会用 Token 消耗推算周额度。
- 缓存 Token 包含在输入中；推理 Token 包含在输出中，两者不会再次计入总量。
- 速度按最近一个已完成回合的输出 Token ÷ 整轮耗时估算。计算时间包含工具调用、网络和等待，不代表模型的实时解码速度。
- 状态根据本机日志里的结构化事件推断。绿灯表示近期仍有工作事件；红灯表示近期有错误或待确认事件；日志不全或安静超过 3 分钟时显示未知。部分 Codex 版本未记录所有待确认事件，因此状态可能不完整。
- 本地 Token 用量只包括本机可读取的会话日志，不代表整个账号、其他设备或所有云端用量。

## 安全与隐私

- 只读当前 Codex 登录文件和本机 `sessions`、`archived_sessions` 日志。额度请求发送到固定的 HTTPS 额度接口；程序不会刷新、改写或导出认证文件。
- 不上传会话内容，没有云同步、遥测、自动更新或自动启动功能。
- 首次读取历史日志时，统计可能在后台逐步补齐；无法可靠归属的数据会标为不完整。
- 周额度使用 Codex 当前的内部接口，接口字段变化时可能需要更新。
- 安装包附带的 EXE 尚未签名。可在 PowerShell 中核对文件：`Get-FileHash .\CodexPeek.exe -Algorithm SHA256`，并与 [SHA256SUMS.txt](./SHA256SUMS.txt) 比对。

## 运行

下载 `CodexPeek.exe` 和同目录的 `CodexPeek.exe.config`，放在同一文件夹后运行。需要 Windows 10/11 和 .NET Framework 4.8。设置写入程序同目录的 `CodexPeek.settings.json`；请解压后运行，不要直接从 ZIP 内启动。

首次运行会显示额度接口和本机日志的读取结果。找不到桌面版 Codex 窗口时，吸附模式会回到独立悬浮；也可在托盘菜单中切换显示模式。再次启动程序会打开已运行实例的详情。

## 构建源码

Windows 上使用系统自带的 C# 编译器，无需下载 npm 包或第三方依赖：

```powershell
powershell -File .\build.ps1 -Test
```

脚本将输出 `bin\CodexPeek.exe`，并运行额度、日志与设置检查。当前版本通过 70 项自动检查。

暂未指定开源许可证。仓库公开可浏览和下载；在添加许可证之前，请勿假定他人获得复制或再发布本项目的授权。
