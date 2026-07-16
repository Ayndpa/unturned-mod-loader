# WinFsp（Mod Loader 集成）

[WinFsp](https://github.com/winfsp/winfsp) 是 Windows 上的用户态文件系统框架，部分高级挂载/虚拟文件系统场景会依赖它。

## 应用内使用

在 **设置 → 游戏目录** 中可：

- 查看 WinFsp 是否已安装及版本
- **一键安装**：通过 UAC 提升运行 `Install-WinFsp.ps1`

## 手动脚本

| 脚本 | 说明 |
|------|------|
| `Check-WinFsp.ps1` | 检测安装状态，成功输出 `INSTALLED` 或 `INSTALLED:版本` |
| `Install-WinFsp.ps1` | 需管理员；优先使用本目录 `winfsp-*.msi`，否则从 GitHub 下载后静默安装 |

### 离线安装

将官方发布的 `winfsp-x.x.x.msi` 复制到本目录后，在管理员 PowerShell 中执行：

```powershell
.\Install-WinFsp.ps1
```

### 仅检查

```powershell
.\Check-WinFsp.ps1
echo $LASTEXITCODE
```