# WinToolBox

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
| **FolderCreator** | WinForms 桌面 | 批量创建文件夹结构，并检查现有目录是否符合规则（严格 / 宽松两种模式） | **[使用说明](src/Tools/FolderCreator/README.md)** |
| **WinToolBox.Core** | 类库 | 共享基础能力：配置读写、日志、托盘通知、增量复制引擎、U 盘识别、备份业务规则 | **[类库说明](src/WinToolBox.Core/README.md)** |

> 运行环境：Windows 10 / 11（x64）。发布包为**自包含单文件**，目标机器**无需预装 .NET**。

---

## 快速开始

### 方式一：直接使用发布包

1. 打开最新发布页：<https://github.com/zhuyuebin/WinToolBox/releases/latest>
2. 下载需要的 zip（每个都是免安装单文件，解压即用）：
   - **`UsbBackup-win-x64.zip`** → 解压后双击 `UsbBackup.exe`
   - **`FolderCreator-win-x64.zip`** → 解压后双击 `FolderCreator.exe`
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
dotnet run --project src/Tools/FolderCreator/FolderCreator.csproj

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
│  ├─ WinToolBox.Core/             # 共享类库（配置 / 日志 / 通知 / 复制 / U 盘识别 / 备份规则）
│  └─ Tools/
│     ├─ UsbBackup/                # U 盘备份工具（WinForms，主界面 + 托盘常驻）
│     └─ FolderCreator/            # 文件夹批量创建 + 规则一致性检查（WinForms）
├─ tests/
│  ├─ WinToolBox.Core.Tests/       # Core 单元测试（xUnit）
│  └─ FolderCreator.Tests/         # FolderCreator 单元测试（xUnit）
├─ Directory.Build.props           # 统一版本号与包元数据
├─ WinToolBox.sln
└─ README.md
```

每个工具目录下都有自己的 `README.md`，包含完整的使用说明与常见问题。

---

## 文档索引

| 文档 | 内容 |
| --- | --- |
| [UsbBackup 使用说明](src/Tools/UsbBackup/README.md) | 主界面与托盘用法、备份规则、配置与日志路径、命令行参数、常见问题 |
| [FolderCreator 使用说明](src/Tools/FolderCreator/README.md) | 规则语法、父级自动补齐、严格/宽松检查模式、命令行参数 |
| [WinToolBox.Core 类库说明](src/WinToolBox.Core/README.md) | 共享库的类型清单、典型用法与设计约定 |
| [本地手动测试清单](MD-files/本地手动测试清单.md) | U 盘插拔 / 备份 / 增量的真机验收步骤（由使用者手动执行） |

---

## 技术栈与约定

- **语言 / 框架**：C# · .NET 8（`net8.0-windows`）· WinForms
- **测试**：xUnit（`dotnet test`）
- **依赖**：不引入任何第三方商业 NuGet 包，全部使用 .NET 原生 API
- **版本号**：统一在 [`Directory.Build.props`](Directory.Build.props) 中维护（当前 `0.2.1`）
- **持续集成**：[`.github/workflows/release.yml`](.github/workflows/release.yml) —— 推送 `v*` 标签后，
  在 `windows-latest` 上还原依赖、编译 Release、运行 `dotnet test`、分别发布 UsbBackup 与 FolderCreator 的
  单文件 exe、打包为 `UsbBackup-win-x64.zip` 与 `FolderCreator-win-x64.zip`，并创建 GitHub Release：

  ```powershell
  git tag v0.2.1
  git push origin v0.2.1
  ```

> 云端 CI 没有物理 U 盘，`UsbDetector` / `WM_DEVICECHANGE` 的运行期行为不在 CI 中验证（但必须编译通过）；
> 真实插拔与增量备份请按[本地手动测试清单](MD-files/本地手动测试清单.md)在本机验收。

---

## 开源协议

本项目采用 **MIT License**，详见 [LICENSE](LICENSE)。

作者：**朱玥彬** · 仓库：<https://github.com/zhuyuebin/WinToolBox>
