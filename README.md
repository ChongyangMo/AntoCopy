# AutoCopy

Windows 文件夹自动备份工具。将多个源文件夹分别压缩为 `.7z` 文件，按定时计划或倒计时间隔保存到目标目录。

使用 C#、WPF 和 .NET 10 开发，支持托盘后台运行。

## 功能

- **多文件夹备份**：添加或拖入源文件夹，每个文件夹生成独立压缩包。
- **定时模式**：按每日指定时间自动执行，也可手动立即执行。
- **倒计时模式**：按设定间隔循环备份，可选择跳过每日 00:00–07:59。
- **独立备份频率**：每个文件夹可跟随全局、自定义频率或停用某种模式；自定义定时支持每隔若干天、一天多个时间点。
- **副本保留**：两种模式分别设置保留份数，按源文件夹清理旧副本，`0` 表示不限。
- **进度与日志**：显示压缩、复制进度，以及 CPU、内存、磁盘容量和程序资源占用。
- **后台运行**：托盘操作、开机自启、单实例运行，备份期间阻止系统自动睡眠。
- **显示设置**：浅色、深色或跟随系统主题；支持手动息屏及 WiFi 断开时自动息屏。

## 运行要求

- Windows。
- .NET 10 Desktop Runtime；使用自包含发布版本时无需单独安装运行时。
- 7-Zip 命令行组件。程序会在自身目录、当前工作目录和标准的 7-Zip 安装目录中查找 `7z.exe`、`7za.exe` 或 `7zr.exe`。使用 `7z.exe` 时应保留其配套依赖文件。
- 源目录的读取权限，以及备份目标和临时压缩包目录的写入权限。

## 使用

1. 启动 `AntoCopy.exe`。
2. 添加需要备份的源文件夹，或直接将文件夹拖入列表。
3. 选择备份目标目录。
4. 设置每日执行时间、倒计时间隔、模式子目录和保留份数，点击“保存设置”。
5. 点击定时模式的“立即执行”验证备份；需要循环备份时，点击“启动倒计时”。

双击源文件夹，或选中后点击“备份频率”，可单独设置该文件夹的计划。

定时模式的每日时间使用 `HH:mm` 格式，留空可停用全局每日定时。倒计时需要手动启动，程序退出或重新启动后不会自动恢复。

关闭或最小化窗口会隐藏到托盘，计划继续运行。需要完全退出时，使用托盘菜单或“文件”菜单中的退出项。程序完全退出后不会执行备份计划。

勾选“开机自启”后，程序在当前用户登录 Windows 时后台启动。

## 备份文件

默认目录结构如下，模式子目录名称可以在界面中修改：

```text
备份目标/
├─ 定时模式/
│  └─ yyyyMMdd_HHmmss/
│     └─ 源文件夹名.7z
└─ 倒计时模式/
   └─ yyyyMMdd_HHmmss/
      └─ 源文件夹名.7z
```

每次执行都会重新压缩源文件夹，生成新的完整压缩包。程序先生成临时压缩包，再复制到本次备份目录。

保留份数按每个源文件夹、每种模式分别计算。超过上限的旧压缩包会被删除，清空后的副本目录也会被移除。多个源文件夹不能使用相同的末级目录名。

恢复文件时，使用 7-Zip 解压需要的备份压缩包。

## 设置与日志

- 设置保存在当前用户注册表：`HKEY_CURRENT_USER\SOFTWARE\AutoCopy`。
- 开机自启项保存在：`HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`，值名为 `AutoCopy`。
- 日志保存在当前用户的 `AppData\Local\AutoCopy\logs` 目录，文件名为 `autocopy-yyyyMMdd.log`，启动时清理超过 14 天的旧日志。
- 可从“文件 → 打开日志目录”或托盘菜单直接打开日志目录。

## 开发与发布

在 Windows 上安装 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet build .\AntoCopy.csproj -c Release
dotnet run --project .\AntoCopy.csproj
```

发布 Windows x64 自包含版本：

```powershell
dotnet publish .\AntoCopy.csproj -c Release -r win-x64 --self-contained true -o .\bin\publish\win-x64
```

将整个发布目录一起分发，并准备好 7-Zip 命令行组件。发布目录中的入口程序为 `AntoCopy.exe`。

## 源码结构

| 文件 | 用途 |
| --- | --- |
| `Program.cs` | 程序入口、设置存储、日志、备份流程和调度 |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | 主界面及交互 |
| `BackupRules.cs` / `SourceRuleWindow.xaml` / `SourceRuleWindow.xaml.cs` | 文件夹独立备份频率 |
| `BackupProgress.cs` | 备份进度状态 |
| `ResourceMonitor.cs` | 系统与进程资源采样 |
| `KeepAlive.cs` | 单实例、防睡眠、子进程管理和临时包清理 |
| `WifiScreenOff.cs` | WiFi 断开检测与息屏 |
| `Theme.cs` / `Themes/` | 主题与通用控件样式 |
