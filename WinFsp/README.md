# WinFsp（Mod Loader 集成）

[WinFsp](https://github.com/winfsp/winfsp) 是 Windows 上的用户态文件系统框架。Mod Loader 的虚拟盘依赖它。

## 应用内安装（推荐）

在 **设置 → 游戏目录** 或 **首次引导** 中：

1. 测速并选择下载源
2. 点 **一键安装 / 升级**
3. 通过 UAC 管理员确认

流程全部在 C# 内完成（不再弹 PowerShell 窗口）：

- 多线程分段下载与 `winfsp.net` 匹配的 MSI（当前 `2.2.26194`，含 GitHub Pre-release）
- 若已安装其他版本，先静默卸载
- 再静默安装匹配版本
- 安装成功后自动挂载虚拟盘

缓存目录：`%LocalAppData%\UnturnedModLoader\cache\`

## 遗留脚本

`Check-WinFsp.ps1` / `Install-WinFsp.ps1` 仍可作为离线备用，但 UI 默认路径已改为进程内安装。
