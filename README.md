# WinToolBox

> Windows 小工具集合 · Monorepo 单仓库多项目

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D4.svg)
[![Release](https://github.com/zhiyuebin/WinToolBox/actions/workflows/release.yml/badge.svg)](https://github.com/zhiyuebin/WinToolBox/actions/workflows/release.yml)

WinToolBox 是一个面向 Windows 10/11 的**小工具集合**，采用 Monorepo（单仓库多项目）方式组织：
每个工具是 `src/Tools/` 下的一个独立可执行程序，公共能力沉淀在 `src/WinToolBox.Core` 共享库中，单元测试放在 `tests/` 下。

当前包含 **1 个可用工具 + 1 个共享库**：

## 包含的工具

| 项目 | 类型 | 版本 | 说明 |
| --- | --- | --- | --- |
| **UsbBackup** | WinForms 托盘程序（`src/Tools/UsbBackup`） | v0.1.0 | U 盘自动备份托盘工具：插入 U 盘后自动按“卷标 + 卷序列号 + 日期”增量备份到指定目录，常驻系统托盘，支持设置、立即备份、打开日志、退出 |
| **WinToolBox.Core** | 类库（`src/WinToolBox.Core`） | v0.1.0 | 共享核心库：配置读写（`ConfigManager`）、日志（`Logger`）、托盘通知（`Notifier`）、增量复制引擎（`FileCopier`）、U 盘识别（`UsbDetector`）、备份业务规则（`BackupRules`，含 `ExcludeRules`） |

> 运行环境：Windows 10 / 11（x64）。UsbBackup 为**免安装单文件**程序，发布包已自包含 .NET 运行时，**无需预装 .NET**。

---

## 下载与安装 UsbBackup

1. 打开最新发布页：**<https://github.com/zhiyuebin/WinToolBox/releases/latest>**
2. 下载 **`UsbBackup-win-x64.zip`**
3. 解压到任意目录（例如 `D:\Tools\UsbBackup\`），双击 **`UsbBackup.exe`** 即可运行

特点：

- **免安装**：解压即用，不写注册表、不注册系统服务（只在自己的用户目录下写配置文件与日志）。
- **单文件**：整个程序就是一个 `UsbBackup.exe`。
- **自包含**：内置 .NET 8 运行时，目标机器**不需要**预装 .NET。

> 首次运行可能被 Windows SmartScreen 提示“Windows 已保护你的电脑”，因为程序未做代码签名：
> 点击“更多信息” → “仍要运行”即可。单文件自包含程序首次启动时会自解压到临时目录，属正常现象。

---

## 使用说明

1. **首次运行**：双击 `UsbBackup.exe`，程序启动后**不显示主窗口**，只在**系统托盘**（任务栏右下角，可能收在“隐藏的图标”里）出现 `UsbBackup` 图标。
2. **配置**：在托盘图标上**右键 → 「设置」**，选择**备份目标目录**（建议选本地硬盘上的一个空目录，不要选 U 盘自身），并打开/关闭**自动备份**开关，保存。
3. **自动备份**：插入 U 盘后，程序自动识别设备并开始备份；备份完成会弹出**托盘气泡通知**，标题为「U盘备份完成」，内容形如“‘MYUSB’备份完成：新增/更新 N 个文件，跳过 M 个未修改文件，共 1.2 GB。保存位置：D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30”。
4. **查看结果**：备份文件位于 `{你设置的目标目录}\{卷标}_{卷序列号}\{yyyy-MM-dd}\`；如需排查问题，用托盘右键 **「打开日志」** 打开日志目录，再双击当天的 `wintoolbox-yyyyMMdd.log`（直接点击刚刚弹出的托盘气泡效果相同）。
5. **退出**：托盘右键 **「退出」**，程序结束并移除托盘图标；不需要备份时可以直接退出，不会驻留后台。

### 托盘右键菜单

| 菜单项 | 作用 |
| --- | --- |
| **设置** | 打开设置窗口：选择备份目标目录、开启/关闭自动备份（可在此修改排除的文件后缀） |
| **立即备份** | 忽略“自动备份”开关，立刻对所有**已插入**的 U 盘执行一次增量备份（适合插入 U 盘时程序没运行的情况）；**双击托盘图标**效果相同 |
| **打开日志** | 用资源管理器打开日志目录 `%LocalAppData%\WinToolBox\logs\`（点击备份完成的托盘气泡效果相同），再双击当天的 `wintoolbox-yyyyMMdd.log` 查看详情 |
| **退出** | 停止监听并退出程序（不会驻留后台进程） |

> 程序为**单实例**运行：重复双击 `UsbBackup.exe` 不会启动第二个托盘图标，而是提示“已经在运行，请查看系统托盘图标”。

### 命令行参数

```powershell
# 本机冒烟自检：在临时目录里跑一遍“首次复制 → 增量跳过 → 增量更新 → 排除规则 → 配置读写 → U 盘枚举”，
# 不依赖真实 U 盘；退出码 0 表示全部通过（不加路径时报告写到 %TEMP%\UsbBackup-selftest.txt）
UsbBackup.exe --selftest
UsbBackup.exe --selftest D:\selftest-report.txt   # 同时把报告写到指定文件

# 查看版本
UsbBackup.exe --version
```

---

## 备份规则说明

### 目标路径规则

```text
{用户配置的目标目录}\{卷标}_{卷序列号}\{yyyy-MM-dd}\
```

例如目标目录为 `D:\UsbBackup`、U 盘卷标 `MYUSB`、卷序列号 `1A2B3C4D`，则 2026-09-30 这次备份写入：

```text
D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30\
```

- 用**卷序列号**而不是盘符来区分设备（盘符会随插口变化），同一 U 盘换 USB 口、盘符从 `E:` 变成 `F:`，仍然备份到同一个 `MYUSB_1A2B3C4D` 目录。
- 每天一个日期目录，历史备份不会被新的备份覆盖掉。
- 卷标为空时用 `NOLABEL` 占位，序列号读取失败时用 `00000000` 占位。

### 增量策略

- **只复制，不删除**：程序**永远不会删除**目标目录中的任何文件，也不会清理 U 盘上的任何内容。
- **不覆盖未修改文件**：按“**修改时间 + 大小**”判断，源文件与目标文件大小相同且目标不比源旧时，跳过该文件。
- 只有**新增文件**和**已修改文件**会被复制；复制后会把目标文件的修改时间对齐源文件，保证下次判断准确。
- U 盘上被删除的文件，在备份目录里**依然保留**（历史快照语义，不做同步删除）。

### 默认排除项

以下内容默认不参与备份（避免把系统垃圾和自动播放脚本带进备份）：

| 类别 | 默认排除 |
| --- | --- |
| 文件名 | `autorun.inf`、`desktop.ini`、`Thumbs.db` |
| 目录名 | `System Volume Information`、`$RECYCLE.BIN` |
| 文件后缀 | `*.tmp`、`*.part`、`*.crdownload` |

> 实现中另外还排除了 `found.000`、`.Trash`、`.Trashes` 等回收站/修复目录。
> 你可以在配置文件的 `excludedExtensions` 中追加需要排除的后缀（不区分大小写）。

### 安全策略

- **只复制**：程序对 U 盘只做读取 + 复制，**绝对不执行** U 盘内的任何 `exe`、`bat`、`cmd`、快捷方式或脚本。
- 不做自动播放、不调用 `ShellExecute`、不解析 `autorun.inf`（该文件本身就被排除）。
- 所有复制都是流式读写（1 MiB 缓冲区），大文件不会一次性占用大量内存。
- 目标目录若位于源 U 盘内部会被拒绝，避免自我递归复制。

---

## 目录结构

```text
WinToolBox/
├─ .github/
│  └─ workflows/
│     └─ release.yml                    # GitHub Actions：推送 v* 标签时测试、构建、打包并发布 Release
├─ src/
│  ├─ WinToolBox.Core/                  # 核心共享库（net8.0-windows）
│  │  ├─ AppPaths.cs                    # 统一路径定义（配置目录 / 日志目录）
│  │  ├─ BackupConfig.cs                # 配置模型 + 默认值 + 规范化
│  │  ├─ BackupRules.cs                 # 备份业务规则：唯一标识、目标路径、安全校验
│  │  ├─ ConfigManager.cs               # config.json 读写（损坏时回退默认配置）
│  │  ├─ CopyModels.cs                  # 复制进度（CopyProgress）与结果（CopyResult）
│  │  ├─ ExcludeRules.cs                # 排除规则（目录名 / 文件名 / 后缀）
│  │  ├─ FileCopier.cs                  # 流式 + 增量复制引擎
│  │  ├─ Logger.cs                      # 按天滚动日志（保留 30 天）
│  │  ├─ Notifier.cs                    # 托盘气泡通知封装
│  │  ├─ UsbDetector.cs                 # U 盘识别（卷标 / 序列号 / 容量，Win32 API）
│  │  ├─ UsbDeviceInfo.cs               # U 盘设备信息模型
│  │  └─ WinToolBox.Core.csproj
│  └─ Tools/
│     └─ UsbBackup/                     # U 盘备份托盘程序（WinForms，输出 UsbBackup.exe）
│        ├─ Program.cs                  # 程序入口：托盘常驻、单实例、--selftest / --version
│        ├─ TrayApplicationContext.cs   # 托盘图标与右键菜单（设置 / 立即备份 / 打开日志 / 退出）
│        ├─ BackupService.cs            # 备份调度：读配置、拼目标路径、调用复制引擎、发通知
│        ├─ UsbWatcher.cs               # 监听设备插入/拔出（WM_DEVICECHANGE）
│        ├─ SettingsForm.cs             # 设置窗口：目标目录、自动备份开关、排除后缀
│        ├─ app.manifest                # 应用清单（DPI / 权限声明）
│        └─ UsbBackup.csproj
├─ tests/
│  └─ WinToolBox.Core.Tests/            # 单元测试（xUnit，net8.0-windows）
│     ├─ FileCopierTests.cs             # 增量复制、排除规则
│     ├─ ConfigManagerTests.cs          # 配置读写与规范化
│     ├─ BackupRulesTests.cs            # 目标路径、设备唯一标识、同卷校验
│     ├─ LoggerTests.cs                 # 日志写入、按天切割与清理
│     ├─ TempWorkspace.cs               # 测试用临时目录辅助类
│     └─ WinToolBox.Core.Tests.csproj
├─ .gitignore
├─ Directory.Build.props                # 统一版本（0.1.0）与作者/仓库/协议元数据
├─ LICENSE                              # MIT
├─ README.md                            # 本文件
├─ 本地手动测试清单.md                    # 实体 U 盘手动验收清单（见下文）
├─ DSH_Task.md                          # 项目任务书
└─ WinToolBox.sln
```

> 上表只列出关键文件；`bin/`、`obj/`、`publish/`、`.dotnet/`（本地 SDK 缓存）等均已在 `.gitignore` 中忽略。

---

## 从源码构建

需要 **.NET 8 SDK**（Windows 10/11）。

```powershell
# 1) 还原 + 编译整个解决方案（Release）
dotnet build WinToolBox.sln -c Release

# 2) 运行单元测试（xUnit）
dotnet test WinToolBox.sln

# 3) 本地运行托盘程序（调试用）
dotnet run --project src/Tools/UsbBackup/UsbBackup.csproj

# 4) 发布单文件 exe（与 CI 发布一致）
dotnet publish src/Tools/UsbBackup/UsbBackup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
# 产物：publish\UsbBackup.exe
```

---

## 配置文件与日志

| 内容 | 路径 |
| --- | --- |
| 配置文件 | `%AppData%\WinToolBox\UsbBackup\config.json`（通常为 `C:\Users\<用户名>\AppData\Roaming\WinToolBox\UsbBackup\config.json`） |
| 日志目录 | `%LocalAppData%\WinToolBox\logs\`（通常为 `C:\Users\<用户名>\AppData\Local\WinToolBox\logs\`） |
| 日志文件 | `wintoolbox-yyyyMMdd.log`，按天切割，自动清理 30 天前的日志 |

配置示例（`config.json`）：

```json
{
  "backupTargetDirectory": "D:\\UsbBackup",
  "autoBackupEnabled": true,
  "excludedExtensions": [
    ".tmp",
    ".part",
    ".crdownload"
  ]
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `backupTargetDirectory` | string | 备份目标目录（本地硬盘上的目录；留空表示尚未配置，此时不会自动备份） |
| `autoBackupEnabled` | bool | 是否在检测到 U 盘插入时自动备份 |
| `excludedExtensions` | string[] | 追加排除的文件后缀（不区分大小写，可写 `.tmp` 或 `tmp`，保存时会统一为小写并补 `.`） |

> 配置文件损坏或为空时，程序会回退到默认配置并记录一条 WARN 日志，不会覆盖你的原文件；
> 在「设置」窗口中保存一次即可重新生成规范格式的配置。

日志行格式与关键词（`时间 [级别] 内容`，级别为 `INFO `/`WARN `/`ERROR`）：

```text
2026-09-30 21:08:38.001 [INFO ] U 盘已插入：MYUSB (E:) 7.2 GB，可用 3.1 GB 序列号=1A2B3C4D
2026-09-30 21:08:38.120 [INFO ] 开始备份：MYUSB (E:) 7.2 GB，可用 3.1 GB 序列号=1A2B3C4D -> D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30
2026-09-30 21:08:41.002 [INFO ] 命中排除规则，跳过目录：E:\System Volume Information
2026-09-30 21:08:41.123 [INFO ] 复制完成：E:\ -> D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30；新增/更新 12，跳过 0，失败 0，目录 3，字节 1258291
2026-09-30 21:08:41.240 [INFO ] 备份完成：MYUSB -> D:\UsbBackup\MYUSB_1A2B3C4D\2026-09-30；新增/更新 12 个文件，跳过 0 个未修改文件，共 1.2 MB。
2026-09-30 21:08:41.301 [INFO ] 已发送通知：U盘备份完成 - “MYUSB”备份完成：新增/更新 12 个文件，跳过 0 个未修改文件，共 1.2 MB。
```

排查问题时搜索关键字：`复制完成`、`备份完成`、`新增/更新`、`跳过`、`命中排除规则`、`自动备份已关闭`、`复制失败`、`备份部分失败`、`ERROR`。

---

## 版本与发布

- 版本号统一在 [`Directory.Build.props`](Directory.Build.props) 中维护（当前 `0.1.0`）。
- 发布流程由 [`.github/workflows/release.yml`](.github/workflows/release.yml) 自动完成：**推送 `v*` 标签**（例如 `v0.1.0`）即触发 GitHub Actions —— 在 `windows-latest` 上还原依赖、编译 Release、运行 `dotnet test` 单元测试、发布单文件 `UsbBackup.exe`、打包为 `UsbBackup-win-x64.zip`，最后创建 GitHub Release 并上传该 zip。

```powershell
git tag v0.1.0
git push origin v0.1.0
```

> 由于云端 CI 环境没有物理 U 盘，`UsbDetector` / `WM_DEVICECHANGE` 的运行期行为不在 CI 中验证（但必须编译通过）；
> U 盘插拔、自动备份、增量复制的真实功能请按 **[本地手动测试清单](本地手动测试清单.md)** 在本机手动验收。

---

## 开源协议

本项目采用 **MIT License**，详见 [LICENSE](LICENSE)。

作者：**朱玥彬** · 仓库：<https://github.com/zhiyuebin/WinToolBox>
