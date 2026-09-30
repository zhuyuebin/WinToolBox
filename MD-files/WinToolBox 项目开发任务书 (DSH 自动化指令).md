# WinToolBox 项目开发任务书 (DSH 自动化指令)

## 1. 项目基本信息
- 仓库名称：WinToolBox
- 仓库地址：https://github.com/zhuyuebin/WinToolBox
- 作者：朱玥彬
- 开源协议：MIT
- 项目定位：Windows 小工具集合（Monorepo 单仓库多项目模式）

## 2. 技术栈与架构要求
- 开发语言：C# (.NET 8)
- UI 框架：WPF 或 WinForms（优先选择能稳定实现托盘图标的方案，推荐 WinForms 的 NotifyIcon）
- 测试框架：xUnit
- CI/CD：GitHub Actions

### 目录结构要求（必须严格遵守）
```text
WinToolBox/
├─ .github/
│  └─ workflows/
│     └─ release.yml          # 自动化发布脚本
├─ src/
│  ├─ WinToolBox.Core/        # 核心共享库 (类库)
│  └─ Tools/
│     └─ UsbBackup/           # U盘备份工具 (托盘程序)
├─ tests/
│  └─ WinToolBox.Core.Tests/  # 单元测试 (xUnit)
├─ WinToolBox.sln
├─ Directory.Build.props      # 统一版本和元数据
├─ README.md
└─ LICENSE
```

## 3. 详细执行步骤（请按顺序执行）

### 步骤 1：初始化项目与基础配置
1. 在本地创建上述目录结构。
2. 运行 `dotnet new sln -n WinToolBox` 创建解决方案。
3. 创建 `Directory.Build.props`，内容如下：
```xml
<Project>
  <PropertyGroup>
    <Authors>朱玥彬</Authors>
    <RepositoryUrl>https://github.com/zhuyuebin/WinToolBox</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```
4. 创建 .NET 项目：
   - `dotnet new classlib -n WinToolBox.Core -o src/WinToolBox.Core`
   - `dotnet new wpf -n UsbBackup -o src/Tools/UsbBackup`（若 WPF 配置复杂或无法稳定使用，自动改用 `dotnet new winforms`）
   - `dotnet new xunit -n WinToolBox.Core.Tests -o tests/WinToolBox.Core.Tests`
5. 将它们全部添加到 `WinToolBox.sln` 中，并配置 `UsbBackup` 和 `WinToolBox.Core.Tests` 引用 `WinToolBox.Core`。

### 步骤 2：开发 WinToolBox.Core 共享库
请在 `src/WinToolBox.Core` 中实现以下类：
1. **ConfigManager**：管理 `%AppData%\WinToolBox\UsbBackup\config.json` 的读写。配置包含：备份目标目录、是否自动备份、排除文件后缀。
2. **Logger**：日志记录器，写入 `%LocalAppData%\WinToolBox\logs\`，支持滚动日志（按天切割），提供 Info/Warn/Error 级别。
3. **Notifier**：封装 Windows 托盘气泡通知或 Toast 通知，提供 `ShowNotification(title, message)` 方法。
4. **FileCopier**：提供 `CopyDirectory(source, target, progressCallback, excludeRules)` 方法。要求：
   - 流式复制，支持大文件。
   - 比较源文件和目标文件的修改时间与大小，实现**增量复制**（跳过未修改文件）。
   - 默认不删除、不覆盖目标文件夹中已存在的文件（如果覆盖，则需比较时间后决定）。
5. **UsbDetector**：获取当前插入的 USB 存储设备信息。需获取：卷标、总容量、剩余空间、卷序列号（唯一标识，不使用盘符，因为盘符会变）。

### 步骤 3：开发 UsbBackup 工具
1. **托盘程序入口**：实现系统托盘图标常驻。右键菜单包含：“设置”、“立即备份”、“打开日志”、“退出”。
2. **监听 U 盘插入**：使用 Windows API `WM_DEVICECHANGE` 或 WMI 监听 USB 存储设备的插入和拔出。
3. **识别 U 盘唯一性**：通过“卷序列号 + 卷标 + 容量”组合生成唯一标识。
4. **备份规则（核心业务逻辑）**：
   - 目标路径：`{用户配置的目标目录}/{卷标}_{序列号}/{当前日期:yyyy-MM-dd}/`
   - 增量策略：默认**不删除、不覆盖**，只复制新增或修改过的文件。
   - 排除规则：默认排除 `autorun.inf`、`System Volume Information`、`$RECYCLE.BIN`。
   - 安全策略：只复制，**绝对不执行** U 盘内的任何 exe、bat 或快捷方式。
5. **配置界面**：提供一个简单的设置窗口，允许用户选择备份目标文件夹、开启/关闭自动备份。
6. **日志与通知**：备份完成后调用 `Notifier` 通知用户，记录备份成功或失败信息到 `Logger`。

### 步骤 4：编写单元测试
在 `tests/WinToolBox.Core.Tests` 中，为 `FileCopier`（增量复制逻辑）和 `ConfigManager`（配置读写）编写 xUnit 测试，确保核心逻辑通过 `dotnet test`。

### 步骤 5：配置 GitHub Actions 自动发布
在 `.github/workflows/release.yml` 中编写：
- 触发条件：推送 `v*` 标签时触发。
- 环境：`windows-latest`。
- 步骤：
  1. Checkout 代码。
  2. 设置 .NET 8。
  3. 运行 `dotnet test`。
  4. 发布单文件 exe：`dotnet publish src/Tools/UsbBackup/UsbBackup.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true`。
  5. 将生成的 exe 打包为 `UsbBackup-win-x64.zip`。
  6. 使用 `softprops/action-gh-release` 创建 GitHub Release 并上传 zip 包。

### 步骤 6：更新 README 并提交
1. 更新 `README.md`，说明 WinToolBox 是什么，包含 UsbBackup 工具的下载链接和使用说明。
2. 执行 Git 操作：
   - `git add .`
   - `git commit -m "feat: 初始化 WinToolBox monorepo 并完成 UsbBackup v0.1.0 MVP"`
   - `git push origin main`
3. 最后打标签并推送以触发发布：
   - `git tag v0.1.0`
   - `git push origin v0.1.0`

## 4. 测试策略（极其重要，必须严格遵守）
1. **本地单元测试**：本项目的所有 `dotnet test` 单元测试，请在本地终端执行，不要依赖云端环境。测试通过后再提交代码。
2. **云端编译测试**：由于 GitHub Actions 云端环境没有物理 U 盘，`UsbDetector` 和 `WM_DEVICECHANGE` 相关的代码不要求云端测试通过，但必须保证编译通过（`dotnet build` 成功）。
3. **实体 U 盘测试（由用户手动完成）**：U 盘插拔、自动备份、增量复制的真实功能，由用户（我）在本地物理机上手动手动验证。你在编写代码时，必须确保 `UsbBackup.exe` 可以本地运行。
4. **交付物**：在完成 MVP 代码后，请务必生成一份《本地手动测试清单》放在项目根目录下，告诉我需要插拔几次 U 盘、检查哪些文件、看哪个日志文件。

## 5. 约束与注意事项
1. 所有代码必须符合 C# 规范，使用依赖注入或静态单例模式保持代码整洁。
2. 不要引入任何未经我许可的商业 NuGet 包，尽量使用 .NET 原生 API。
3. 托盘程序必须兼容 Windows 10/11。
4. 如果遇到编译错误，请自动修复，直到整个解决方案 `dotnet build` 成功。
5. 全部操作完成后，请汇报你修改了哪些文件，以及 GitHub Actions 的运行状态。