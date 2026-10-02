# WinToolBox

[English](README_EN.md) | [简体中文](README.md)

> Windows 小工具集合 · Monorepo 单仓库多项目

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D4.svg)
[![Release](https://github.com/zhuyuebin/WinToolBox/actions/workflows/release.yml/badge.svg)](https://github.com/zhuyuebin/WinToolBox/actions/workflows/release.yml)

WinToolBox 是一个面向 Windows 10/11 的**小工具集合**：每个工具是 `src/Tools/` 下的一个独立程序，
公共能力沉淀在 `src/WinToolBox.Core` 共享库中，单元测试放在 `tests/` 下。

## 工具一览

| 工具 | 类型 | 说明 | 文档 |
| --- | --- | --- | --- |
| **UsbBackup** | WinForms 桌面 + 托盘常驻 | U 盘备份工具（**手动触发**）：按“卷标 + 卷序列号 + 日期”把 U 盘增量备份到指定目录；主界面可视化管理备份路径与运行日志 | **[使用说明](src/Tools/UsbBackup/README.md)** |
| **FileMaster** | WinForms 桌面 | 文件与文件夹管理工具：批量创建 / 检查文件夹结构（原 FolderCreator 全部功能），并新增 8 项文件管理功能：批量重命名、空文件夹清理、时间戳批量修改、批量移动 / 自动分类、文件夹差异比对、重复文件查找、文件夹同步 / 镜像、文件占用解锁 | **[使用说明](src/Tools/FileMaster/README.md)** |
| **WinToolBox.Core** | 类库 | 共享基础能力：配置读写、日志、托盘通知、增量复制引擎、U 盘识别、备份业务规则、哈希引擎 | **[类库说明](src/WinToolBox.Core/README.md)** |

> 运行环境：Windows 10 / 11（x64）。发布包为**自包含单文件**，目标机器**无需预装 .NET**。

### FileMaster 的文件管理功能

打开 FileMaster 后，从菜单「文件管理」进入；每个功能都是独立窗口，互不影响，全部支持后台执行、进度显示与取消：

| 功能 | 说明 | 安全策略 |
| --- | --- | --- |
| 批量重命名 | 文件 / 文件夹 / 两者；删除字符、查找替换（文本或正则）、前后缀、序号、日期、统一扩展名 | 先预览（冲突检测），不覆盖已有文件 |
| 空文件夹清理 | 递归扫描「自身及子目录中都没有文件」的目录 | 预览 + 二次确认，默认删除到回收站 |
| 时间戳批量修改 | 修改时间 = 创建时间，或统一改成指定日期（创建 / 修改 / 访问时间可选） | 先预览后执行 |
| 批量移动 / 自动分类 | 按扩展名、日期（如 `yyyy-MM`）、首字母或兜底规则归类到目标目录；可移动或复制 | 预览 + 冲突检测 + 二次确认 |
| 文件夹差异比对 | 绿色=相同、蓝色=仅左侧、红色=仅右侧、黄褐色=内容不同；支持「大小+时间 / 仅大小 / SHA256」 | 只读，不改动任何文件 |
| 重复文件查找 | 先按大小分组，再对候选文件计算 MD5 / SHA1 / SHA256，分组展示并保留最早的一份 | 勾选 + 二次确认，默认删除到回收站 |
| 文件夹同步 / 镜像 | 单向复制 / 单向同步（删除目标多余内容）/ 镜像；先生成计划再执行 | 预览 + 二次确认，删除默认走回收站 |
| 文件占用解锁 | 使用 Windows Restart Manager 查询占用文件的进程（PID / 类型 / 是否可重启） | 结束进程前二次确认，关键系统进程一律拒绝 |

> 本工具**不读写注册表**（也不提供右键菜单集成）；所有删除动作默认走回收站，并需要用户二次确认。

---

## 快速开始

### 方式一：直接使用发布包

1. 打开最新发布页：<https://github.com/zhuyuebin/WinToolBox/releases/latest>
2. 下载需要的 zip（每个都是免安装单文件，解压即用）：
   - **`UsbBackup-win-x64.zip`** → 解压后双击 `UsbBackup.exe`
   - **`FileMaster-win-x64.zip`** → 解压后双击 `FileMaster.exe`
3. 以 UsbBackup 为例：在主界面选择备份目录并保存 → 插上 U 盘 → 点「立即备份」

> 程序未做代码签名，首次运行可能被 Windows SmartScreen 拦截（“Windows 已保护你的电脑”）：
> 点「更多信息」→「仍要运行」即可。若**每次都**提示，见
> [UsbBackup 常见问题](src/Tools/UsbBackup/README.md#常见问题)（解除“下载来源”标记）。

### 方式二：从源码构建

```powershell
git clone https://github.com/zhuyuebin/WinToolBox.git
cd WinToolBox

dotnet build WinToolBox.sln -c Release      # 编译整个解决方案
dotnet test  WinToolBox.sln                 # 运行单元测试

# 本地运行（调试）
dotnet run --project src/Tools/UsbBackup/UsbBackup.csproj
dotnet run --project src/Tools/FileMaster/FileMaster.csproj

# 发布单文件自包含 exe
dotnet publish src/Tools/UsbBackup/UsbBackup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

需要 .NET 8 SDK（或更高版本 SDK）。

---

## 仓库结构

```text
WinToolBox/
├─ .github/workflows/release.yml   # 推送 v* 标签：测试 → 编译 → 打包 → 发布 Release
├─ src/
│  ├─ WinToolBox.Core/             # 共享类库（配置 / 日志 / 通知 / 复制 / U 盘识别 / 备份规则 / 哈希引擎）
│  └─ Tools/
│     ├─ UsbBackup/                # U 盘备份工具（WinForms，主界面 + 托盘常驻）
│     └─ FileMaster/               # 文件夹批量创建 + 8 项文件管理功能（WinForms）
├─ tests/
│  ├─ WinToolBox.Core.Tests/       # Core 单元测试（xUnit，含 HashEngine 测试）
│  └─ FileMaster.Tests/            # FileMaster 单元测试（xUnit）
├─ Directory.Build.props           # 统一版本号与包元数据
├─ WinToolBox.sln
├─ README.md                       # 仓库总览（中文）
└─ README_EN.md                    # 仓库总览（英文）
```

每个工具目录下都有自己的 `README.md`，包含完整的使用说明与常见问题。

所有文档均为中英双语：中文为 `X.md`，英文为同目录下的 `X_EN.md`（每份文档顶部可一键切换语言）。

---

## 文档索引

| 文档 | 内容 |
| --- | --- |
| [UsbBackup 使用说明](src/Tools/UsbBackup/README.md) | 主界面与托盘用法、备份规则、配置与日志路径、命令行参数、常见问题 |
| [FileMaster 使用说明](src/Tools/FileMaster/README.md) | 规则语法、父级自动补齐、严格/宽松检查模式、六项文件夹增强功能，以及 8 项文件管理功能（重命名 / 空目录 / 时间戳 / 自动分类 / 差异比对 / 重复文件 / 同步镜像 / 占用解锁） |
| [WinToolBox.Core 类库说明](src/WinToolBox.Core/README.md) | 共享库的类型清单、典型用法与设计约定 |
| [FileMaster 本地手动测试清单](FileMaster-本地手动测试清单.md) | 8 项文件管理功能的逐项验收步骤（建议真机手动执行） |
| [本地手动测试清单](MD-files/本地手动测试清单.md) | U 盘插拔 / 备份 / 增量的真机验收步骤（由使用者手动执行） |
| [FolderCreator 本地手动测试清单](MD-files/FolderCreator-本地手动测试清单.md) | 文件夹结构创建 / 检查等六项增强功能的逐项验收步骤（FileMaster 同样适用） |

---

## 技术栈与约定

- **语言 / 框架**：C# · .NET 8（`net8.0-windows`）· WinForms
- **测试**：xUnit（`dotnet test`）
- **依赖**：不引入任何第三方商业 NuGet 包，全部使用 .NET 原生 API
- **版本号**：统一在 [`Directory.Build.props`](Directory.Build.props) 中维护（当前 `0.4.0`）
- **持续集成**：[`.github/workflows/release.yml`](.github/workflows/release.yml) —— 推送 `v*` 标签后，
  在 `windows-latest` 上还原依赖、编译 Release、运行 `dotnet test`、分别发布 UsbBackup 与 FileMaster 的
  单文件 exe、打包为 `UsbBackup-win-x64.zip` 与 `FileMaster-win-x64.zip`，并创建 GitHub Release：

  ```powershell
  git tag v0.4.0
  git push origin v0.4.0
  ```

> 云端 CI 没有物理 U 盘，`UsbDetector` / `WM_DEVICECHANGE` 的运行期行为不在 CI 中验证（但必须编译通过）；
> 真实插拔与增量备份请按[本地手动测试清单](MD-files/本地手动测试清单.md)在本机验收。

---

## 开源协议

本项目采用 **MIT License**，详见 [LICENSE](LICENSE)。

作者：**Zhu Yuebin** · 仓库：<https://github.com/zhuyuebin/WinToolBox>

This project uses free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
