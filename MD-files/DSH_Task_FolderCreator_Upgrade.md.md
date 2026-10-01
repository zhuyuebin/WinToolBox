# DSH 任务书：FolderCreator 全面功能增强（分三阶段执行）

## 1. 任务背景与目标
WinToolBox 主程序 (WinToolBox.App) 已经包含了 FolderCreatorTab（文件夹结构创建与检查）。现在需要为其增加以下 6 大功能：
1. 规则模板管理（保存、加载、删除、内置模板）
2. 反向生成规则（从现有目录提取短横线规则）
3. 占位文件创建（为空目录生成 .gitkeep 等）
4. 多根目录批量创建（一次给多个目录建相同结构）
5. 检查报告导出（Markdown 格式）
6. 目录树文本导出（生成标准 tree 格式）

**要求：必须严格按照本文档的“阶段一、二、三”分步执行。每完成一个阶段，必须本地执行 `dotnet build` 和 `dotnet test`，确保通过后再进入下一阶段。严禁一次性把代码全部写完。**

## 2. 架构与位置约束
- 目标项目：`src/Tools/WinToolBox.App`
- 目标文件：`Tabs/FolderCreatorTab.cs`
- 核心业务逻辑不要直接写在 UI 里。请在 `WinToolBox.Core` 中新增以下类：
  - `Services/TemplateManager.cs`：模板读写。
  - `Services/RuleGenerator.cs`：反向生成规则、目录树导出。
  - `Services/FolderCreatorService.cs`（可选）：封装创建、检查、报告导出的核心逻辑。
- 所有新逻辑必须包含 xUnit 单元测试，放在 `tests/WinToolBox.Core.Tests`。

## 3. UI 布局要求（需重构 FolderCreatorTab）
由于功能增多，原有的简单布局必须重构。建议采用如下结构（使用 WinForms 控件实现）：
- **顶部工具栏**：模板下拉框 + [保存为模板] + [删除模板] 按钮。
- **目标设置区**：
  - 目标根目录：[DirectoryPicker] + [从现有目录生成规则] 按钮。
  - 多根目录模式：[ ] 启用（启用后显示多行 TextBox 输入路径）。
  - 选项：[√] 创建占位文件 (默认 .gitkeep) [√] 严格模式。
- **规则输入区**：多行 TextBox（占据主要空间）。
- **操作按钮区**：[创建文件夹] [检查一致性] [导出检查报告] [导出目录树]。
- **进度与结果区**：ProgressPanel + 结果 TreeView（检查时显示）。
- **日志区**：LogPanel 显示操作日志。

---

## 4. 分阶段执行计划（请严格遵守）

### 🚩 阶段一：文本处理与数据管理（风险最低，先做）
**目标**：实现模板管理、反向生成规则、目录树导出。
**执行步骤**：
1. 在 `WinToolBox.Core` 中实现 `TemplateManager.cs`：
   - 读写 `%AppData%\WinToolBox\FolderCreator\templates.json`。
   - 数据结构：`Dictionary<string, string>` (模板名 -> 规则文本)。
   - 内置默认模板（如“Web 项目”、“Python 项目”），首次运行自动写入。
   - 提供保存、读取、删除、获取所有模板名的方法。
2. 在 `WinToolBox.Core` 中实现 `RuleGenerator.cs`：
   - `GenerateRuleFromDirectory(string rootPath, string[] excludes)`：递归扫描目录，生成短横线文本（如 `-一级目录`、`--二级目录`）。必须默认排除 `.git`、`node_modules`、`bin`、`obj`、`.vs`。生成结果必须按名称排序，保证稳定性。
   - `GenerateTreeText(string rootPath, string[] excludes)`：输出标准的 `tree` 格式文本（如 `├─`、`└─`）。
3. 修改 `FolderCreatorTab` 的 UI：
   - 顶部加模板下拉框和按钮，绑定 `TemplateManager`。
   - 加“从现有目录生成规则”按钮，绑定 `RuleGenerator`。
   - 加“导出目录树”按钮。
4. 编写阶段一对应的单元测试（模板的增删改查、反向生成规则的准确性）。
5. 本地执行 `dotnet build` 和 `dotnet test`，**通过后向我汇报，再进入阶段二。**

### 🚩 阶段二：创建能力增强（涉及文件写入，小心处理）
**目标**：实现占位文件创建、多根目录批量创建。
**执行步骤**：
1. 修改 `FolderCreatorService` 的创建逻辑：
   - 增加参数 `bool createPlaceholder, string placeholderName = ".gitkeep"`。
   - 创建完文件夹后，如果该目录为空（没有任何文件），则创建占位文件。
2. 修改多根目录逻辑：
   - 如果启用多根目录，UI 读取多行文本框的路径列表。
   - 使用 `HashSet<string>` 去重（忽略大小写），并校验路径是否合法。
   - 遍历路径列表，依次执行创建。进度条显示“当前处理第 X / 共 Y 个”。
   - 单个路径失败不中断整体，记录到日志，最后汇总报告。
3. 更新 UI 事件绑定和进度条显示逻辑。
4. 编写单元测试（占位文件生成、多根目录去重与批量执行逻辑）。
5. 本地执行 `dotnet build` 和 `dotnet test`，**通过后向我汇报，再进入阶段三。**

### 🚩 阶段三：检查报告导出与最终优化（收尾）
**目标**：实现检查报告导出，并优化整体 UI 体验。
**执行步骤**：
1. 在 `FolderCreatorService` 中实现 `ExportCheckReport(string filePath, CheckResult result)`：
   - 导出格式：Markdown (.md)。
   - 报告内容：时间、目标目录、检查模式、统计（匹配数/缺失数/多余数）、详细列表。
   - 如果缺失/多余数量超过 100，使用 `<details>` 标签折叠，保证 Markdown 可读性。
2. 在 `FolderCreatorTab` 底部增加“导出检查报告”按钮，绑定保存文件对话框。
3. 检查所有耗时操作是否都在 `Task.Run` 中执行，UI 是否使用了 `IProgress<T>`。
4. 全局异常处理：每个按钮事件都必须 `try-catch`，异常写入 `LogPanel`，防止程序崩溃。
5. 本地执行 `dotnet build` 和 `dotnet test`，确保全部通过。
6. 生成一份《本地手动测试清单》放在项目根目录，告诉用户如何测试这 6 个功能。

---

## 5. 测试与交付物要求
- 所有核心逻辑必须有无 UI 依赖的单元测试。
- 必须复用 `WinToolBox.Core` 现有的 `Logger` 和 `Notifier`。
- 严禁在 `FolderCreatorTab` 中直接写大段业务逻辑，必须调用 Core 中的 Service。
- 交付物：
  1. 修改后的 `FolderCreatorTab.cs` 和 Designer 文件。
  2. `WinToolBox.Core` 中的 `TemplateManager.cs`、`RuleGenerator.cs`、`FolderCreatorService.cs`。
  3. `tests/WinToolBox.Core.Tests` 中新增的测试代码。
  4. 根目录下的《本地手动测试清单》。
  5. 完整的 `dotnet build` 和 `dotnet test` 通过截图或日志输出。

## 6. 约束与注意事项
1. **异步与阻塞**：反向生成规则、创建文件夹、检查目录，这些全部必须在 `Task.Run` 中执行，并支持 `CancellationToken` 取消。
2. **占位文件安全性**：如果占位文件已存在，跳过不报错。
3. **报告导出**：路径中可能包含特殊字符，写入 Markdown 时需简单转义。
4. **不要破坏主程序其他 Tab**：不要修改 `FileRenamerTab` 和 `UsbBackup` 的代码。
5. **遇到编译错误**：必须自行修复，直到 `dotnet build` 成功。
6. **不要自动 git push**：等我本地验证 UI 效果后再手动提交。