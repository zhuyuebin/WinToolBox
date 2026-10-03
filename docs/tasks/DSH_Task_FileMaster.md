# DSH 任务书：FileMaster 文件管理工具全面开发

## 1. 任务背景与目标
当前仓库为 WinToolBox（https://github.com/zhuyuebin/WinToolBox），是一个 Windows 小工具集合（monorepo），使用 C# (.NET 8) + WinForms + xUnit。

仓库现有结构：
- src/WinToolBox.Core/          # 核心共享库（已有 Logger、Notifier、FileCopier、FileScanner 等）
- src/Tools/FolderCreator/      # 现有独立 WinForms 程序（文件夹结构创建与检查）
- src/Tools/UsbBackup/          # 现有独立托盘程序（U盘备份）
- tests/WinToolBox.Core.Tests/  # 单元测试

本任务目标：
1. 将 `src/Tools/FolderCreator/` 重命名为 `src/Tools/FileMaster/`，工具名称改为 **FileMaster**。
2. 保留 FolderCreator 原有的“文件夹结构创建与检查”功能。
3. 在 FileMaster 中新增 8 个文件/文件夹管理功能（详见分阶段计划，已移除右键菜单集成）。
4. 所有核心逻辑必须放在 `WinToolBox.Core` 或 `FileMaster/Services` 中，与 UI 解耦，并编写 xUnit 单元测试。
5. 继续使用 WinForms 独立 exe，不引入主程序架构（无 WinToolBox.App）。

**注意：你是在一个全新的对话中执行此任务，没有历史上下文。请严格按本文档执行，不要假设存在 WinToolBox.App、Tabs、FolderCreatorTab、DirectoryPicker、ProgressPanel、LogPanel 等历史设计中提到的类。**

## 2. 命名与路径变更要求
- 项目目录：`src/Tools/FolderCreator/` → `src/Tools/FileMaster/`
- 项目文件：`FolderCreator.csproj` → `FileMaster.csproj`
- 主窗口：保留 `MainForm.cs`，窗口标题改为 "FileMaster"。
- 解决方案 `WinToolBox.sln` 中同步更新项目引用。
- 更新根目录 `README.md`，将 FolderCreator 改名为 FileMaster 并补充新功能说明。
- 原 FolderCreator 的所有功能必须保留，不得删除。

## 3. 架构与复用约束
- 复用 `WinToolBox.Core`：Logger、Notifier、FileCopier、FileScanner、ConfigManager。
- 核心业务逻辑放在 `FileMaster/Services/` 下，UI 只调用 Service。
- 所有耗时操作必须使用 `Task.Run` + `IProgress<T>` + `CancellationToken`，绝不阻塞 UI。
- 所有删除操作默认走回收站（`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/DeleteDirectory`），并二次确认。
- **严禁修改系统注册表**。本任务不包含右键菜单集成，不要写任何注册表操作代码。
- 不引入未经许可的商业 NuGet 包，尽量使用 .NET 原生 API。
- 遇到编译错误必须自行修复，直到 `dotnet build` 成功。

## 4. 分阶段执行计划（必须严格遵守）

### 🚩 阶段一：项目重命名与基础扩展
**目标**：完成重命名，并实现最简单、最安全的 3 个功能。
**功能清单**：
1. 文件夹批量重命名（并入现有的批量重命名逻辑，支持“处理对象：文件 / 文件夹 / 两者”）。
2. 空文件夹清理（扫描 → 预览 → 选择性删除 → 默认回收站）。
3. 时间戳批量修改（批量把修改时间 = 创建时间，或统一改成指定日期）。
**执行步骤**：
1. 重命名目录、项目文件、更新 sln。
2. 在 `FileMaster/Services/` 中实现：
   - `FileRenamerService.cs`：支持文件+文件夹，正则替换、前后缀、序号、日期、删除字符。
   - `EmptyFolderCleanerService.cs`：扫描空目录（没有任何文件），返回列表。
   - `TimestampService.cs`：批量修改文件/文件夹时间戳。
3. UI 增加对应按钮、预览列表和确认对话框。
4. 编写单元测试（重命名规则、空目录判断、时间戳修改）。
5. 本地运行 `dotnet build` 和 `dotnet test`，通过后汇报，再进入阶段二。

### 🚩 阶段二：文件移动与差异比对
**目标**：实现批量移动/自动分类、文件夹差异比对。
**功能清单**：
1. 批量移动/自动分类（按扩展名、按日期、按首字母移动到目标目录）。
2. 文件夹差异比对（左右两栏，颜色标记：绿色=相同，蓝色=仅左侧，红色=仅右侧，黄色=内容不同）。
**执行步骤**：
1. 在 `FileMaster/Services/` 中实现：
   - `AutoArchiverService.cs`：规则表 + 遍历 + 移动。
   - `FolderDiffService.cs`：遍历左右目录，比较大小和修改时间（可选哈希），返回差异列表。
2. UI 增加规则配置、左右目录选择、结果双栏展示。
3. 编写单元测试（移动规则、差异比对逻辑）。
4. 本地运行 `dotnet build` 和 `dotnet test`，通过后汇报，再进入阶段三。

### 🚩 阶段三：哈希与同步（高风险，小心处理）
**目标**：实现重复文件查找、文件夹同步/镜像。
**功能清单**：
1. 重复文件查找（先比大小，再比哈希 SHA256，分组展示，支持预览和批量删除到回收站）。
2. 文件夹同步/镜像（单向同步：源→目标，多余删除；镜像：目标完全等于源）。
**执行步骤**：
1. 在 `WinToolBox.Core` 中新增 `HashEngine.cs`：流式读取大文件，支持 MD5/SHA1/SHA256，带进度回调。
2. 在 `FileMaster/Services/` 中实现：
   - `DuplicateFinderService.cs`：先比大小，再比哈希，分组。
   - `FolderSyncService.cs`：支持“单向同步”和“镜像”两种模式，删除操作默认回收站，必须有预览和二次确认。
3. UI 增加重复组展示、勾选删除、同步预览和确认。
4. 编写单元测试（哈希计算、重复分组、同步逻辑）。
5. 本地运行 `dotnet build` 和 `dotnet test`，通过后汇报，再进入阶段四。

### 🚩 阶段四：文件占用解锁（无需注册表）
**目标**：实现文件占用查询与解锁。
**功能清单**：
1. 文件占用解锁（显示占用文件的进程，提供“结束进程”和“仅查看”选项）。
**执行步骤**：
1. 在 `FileMaster/Services/` 中实现：
   - `FileUnlockerService.cs`：使用 `RestartManager` API（`RmStartSession`、`RmRegisterResources`、`RmGetList`、`RmEndSession`）查找占用文件的进程。提供方法返回进程列表（PID、进程名），以及结束指定进程的方法。
2. UI 增加“选择文件 → 查询占用 → 显示进程列表 → 结束进程”的流程，结束进程前必须二次确认。
3. 编写单元测试（用临时文件模拟占用，验证能否正确查出进程；不强制要求测试结束进程逻辑）。
4. 本地运行 `dotnet build` 和 `dotnet test`，通过后汇报。

## 5. 交付物要求
1. `src/Tools/FileMaster/` 完整项目，已添加到 `WinToolBox.sln`。
2. `WinToolBox.Core` 中新增的 `HashEngine.cs`。
3. `FileMaster/Services/` 下的所有 Service 类。
4. `tests/WinToolBox.Core.Tests` 中新增的测试代码。
5. 更新后的根目录 `README.md`。
6. 一份《本地手动测试清单》放在项目根目录，说明如何测试这 8 个功能。
7. 每个阶段完成后汇报：修改了哪些文件、`dotnet build` 和 `dotnet test` 结果。

## 6. 约束与注意事项
1. **安全第一**：所有删除、同步、镜像操作必须预览 + 二次确认 + 默认回收站。
2. **UI 不卡死**：所有耗时操作必须后台线程 + 进度回调 + 可取消。
3. **每个功能独立**：一个功能崩溃不应影响其他功能，按钮事件必须 try-catch。
4. **不要修改 UsbBackup**：保持独立，不要动它的代码。
5. **不要自动 git push**：等用户本地验证后再手动提交。
6. **阶段依赖**：必须完成当前阶段并通过测试后，才能进入下一阶段。严禁一次性全部写完。
7. **不碰注册表**：本任务严禁任何注册表读写操作。