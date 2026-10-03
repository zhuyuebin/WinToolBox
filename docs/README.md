# WinToolBox 文档中心

本目录是 WinToolBox 仓库的文档中心：**非模块级**文档（审计报告、修复报告、手动测试清单、历史任务书）集中在这里；
模块级文档仍保留在各自模块目录下，仓库总览见根目录 [README.md](../README.md) / [README_EN.md](../README_EN.md)。

## 目录结构

| 目录 | 内容 |
| --- | --- |
| [`audit/`](audit/) | 代码审计报告 |
| [`reports/`](reports/) | 修复报告、技术报告 |
| [`testing/`](testing/) | 手动测试清单（真机验收步骤，中英对照） |
| [`tasks/`](tasks/) | DSH 任务书（历史归档） |
| [`archive/`](archive/) | 已被取代的早期任务书 |

## 常用文档

### 审计与修复

| 文档 | 内容 |
| --- | --- |
| [代码审计报告](audit/AUDIT_REPORT.md) | 全仓库只读审计：正确性 / 并发 / 安全 / 性能等问题的清单、证据与修复建议 |
| [P0/P1 修复报告](reports/FIX_REPORT.md) | 针对审计结论的 3 个 P0 + 20 个 P1 修复记录（分阶段、含验证方式） |
| [文档整理报告](reports/DOCS_REORG_REPORT.md) | 本次 Markdown 文档整理的扫描结果、`git mv` 清单、链接更新与验证结果 |

### 手动测试清单（真机验收）

| 清单 | 适用 |
| --- | --- |
| [FileMaster 本地手动测试清单](testing/FileMaster-本地手动测试清单.md) · [EN](testing/FileMaster-本地手动测试清单_EN.md) | 8 项文件管理功能：批量重命名 / 空目录清理 / 时间戳 / 自动分类 / 差异比对 / 重复文件 / 同步镜像 / 占用解锁 |
| [FileMaster 文件夹结构 本地手动测试清单](testing/FileMaster-文件夹结构-本地手动测试清单.md) · [EN](testing/FileMaster-文件夹结构-本地手动测试清单_EN.md) | 文件夹结构六项增强功能（原 FolderCreator 功能，v0.4.0 起并入 FileMaster） |
| [UsbBackup 本地手动测试清单](testing/UsbBackup-本地手动测试清单.md) · [EN](testing/UsbBackup-本地手动测试清单_EN.md) | U 盘插拔 / 备份 / 版本历史的实体机验收（同时包含文件夹结构通用验收轮次） |

### 模块文档（保留在原位，不在本目录）

| 模块 | 中文 | 英文 |
| --- | --- | --- |
| 仓库总览 | [README.md](../README.md) | [README_EN.md](../README_EN.md) |
| UsbBackup | [README.md](../src/Tools/UsbBackup/README.md) | [README_EN.md](../src/Tools/UsbBackup/README_EN.md) |
| FileMaster | [README.md](../src/Tools/FileMaster/README.md) | [README_EN.md](../src/Tools/FileMaster/README_EN.md) |
| WinToolBox.Core | [README.md](../src/WinToolBox.Core/README.md) | [README_EN.md](../src/WinToolBox.Core/README_EN.md) |

## 命名约定

- 中文文档为 `X.md`，英文为同目录下的 `X_EN.md`（全仓库统一，模块 README 亦然）。
- 文件名中的词间分隔使用 `-`，不使用空格；不出现 `.md.md` 双扩展名。
- 报告类文档的日期前缀 `YYYY-MM-DD-` 仅在同一类报告存在多份时使用（当前审计报告与修复报告各一份，故保留原名）。

## 归档说明

- [`tasks/`](tasks/) 是 DSH 任务书的历史归档：这些文档描述的是**当时**的任务要求，其中提到的路径、类名、目录结构可能已经过期。
- [`archive/`](archive/) 存放被后续实现取代的早期任务书（例如按独立 FolderCreator 项目设计的版本）。
- [`audit/`](audit/) 与 [`reports/`](reports/) 中的报告保留**当时**的路径与事实表述（例如 `MD-files/...`、`src/Tools/FolderCreator/`），
  用于还原问题发生时的状态；它们不是当前仓库结构的指引，当前结构以根目录 [README.md](../README.md) 为准。
