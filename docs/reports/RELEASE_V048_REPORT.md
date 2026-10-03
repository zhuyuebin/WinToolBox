# RELEASE_V048_REPORT — WinToolBox v0.4.8 正式发布报告

- **任务书**：[`docs/tasks/DSH_Task_ReleaseV048.md`](../tasks/DSH_Task_ReleaseV048.md)
- **执行时间**：2026-10-04（Asia/Shanghai）
- **执行范围**：版本号变更 + 3 次提交 + 推送 main + 创建并推送 tag；未改动任何源码
- **约束遵守**：未使用 `--force`、未修改远程配置、每次 commit/push 前均获用户确认、只打了 `v0.4.8`

---

## 1. 版本号修改前后对比

| 位置 | 修改前 | 修改后 |
| --- | --- | --- |
| `Directory.Build.props:6` | `<Version>0.4.0</Version>` | **`<Version>0.4.8</Version>`** |
| `README.md:129` | 版本号…（当前 `0.4.0`） | （当前 `0.4.8`） |
| `README.md:137-138` | `git tag v0.4.0` / `git push origin v0.4.0` | `git tag v0.4.8` / `git push origin v0.4.8` |
| `README_EN.md:127` | (currently `0.4.0`) | (currently `0.4.8`) |
| `README_EN.md:134-135` | `git tag v0.4.0` / `git push origin v0.4.0` | `git tag v0.4.8` / `git push origin v0.4.8` |
| `src/Tools/UsbBackup/README.md:13` | 版本 `0.4.0` | 版本 `0.4.8` |
| `src/Tools/UsbBackup/README.md:172` | 输出形如 … `UsbBackup 0.4.0` | … `UsbBackup 0.4.8` |
| `src/Tools/UsbBackup/README_EN.md:13` | Version `0.4.0` | Version `0.4.8` |
| `src/Tools/UsbBackup/README_EN.md:171` | … `UsbBackup 0.4.0` | … `UsbBackup 0.4.8` |

**验证**：

```
[xml]$props = Get-Content Directory.Build.props -Raw
$props.Project.PropertyGroup.Version   →  0.4.8
```

**构建与产物版本验证**（`dotnet build WinToolBox.sln -c Release -m:1`）：

```
Build succeeded.   0 Error(s)   26 Warning(s)（均为既有代码告警与 NU1900 联网告警）

WinToolBox.Core.dll   Assembly=0.4.8.0   FileVersion=0.4.8.0   Product=0.4.8+<commit>
UsbBackup.dll         Assembly=0.4.8.0   FileVersion=0.4.8.0   Product=0.4.8+<commit>
FileMaster.dll        Assembly=0.4.8.0   FileVersion=0.4.8.0   Product=0.4.8+<commit>
```

---

## 2. 提交 hash 与推送结果

| # | commit | message |
| --- | --- | --- |
| ① | `1c3cb54` | `docs: 添加提交推送报告与 v0.4.8 发布任务书` |
| ② | `3347dec` | `chore: bump version to 0.4.8` |
| ③ | `6921b1e` | `docs: 添加 FileMaster 批量重命名功能增强任务书` |

推送结果（main）：

| 远程 | 结果 |
| --- | --- |
| `origin`（GitHub） | ✅ `5fc7340..6921b1e  main -> main`（退出码 0） |
| `gitee`（Gitee） | ✅ `5fc7340..6921b1e  main -> main`（退出码 0） |

> 说明：提交③ 是因为执行期间工作区出现了一份新的未跟踪任务书，按用户确认一并入库，以保持推送前工作区 clean。

---

## 3. tag 推送结果

```
git tag v0.4.8            → v0.4.8 -> 6921b1e95e7c34de08578fa67038cd004ad1961a
git push origin v0.4.8    → * [new tag]  v0.4.8 -> v0.4.8
git push gitee  v0.4.8    → * [new tag]  v0.4.8 -> v0.4.8
```

远程复核（`git ls-remote --tags`）：

| 远程 | ref | commit |
| --- | --- | --- |
| origin | `refs/tags/v0.4.8` | `6921b1e95e7c34de08578fa67038cd004ad1961a` |
| gitee | `refs/tags/v0.4.8` | `6921b1e95e7c34de08578fa67038cd004ad1961a` |

推送前已确认：`<Version> = 0.4.8`（CI 的 `v$Version == tag` 校验通过）、工作区 clean、本地与两个远程均不存在同名 tag。

---

## 4. GitHub Actions 状态

**✅ 成功**（用户于 2026-10-04 在 https://github.com/zhuyuebin/WinToolBox/actions 确认）

- [x] `Release` workflow（由 tag `v0.4.8` 触发）**成功**
- [x] Releases 页面已出现 `v0.4.8` 的 Release
- [x] `FileMaster-win-x64.zip` 与 `UsbBackup-win-x64.zip` **均已上传**

> 本机 hosts 把 `github.com` / `api.github.com` 指向 `127.0.0.1`，沙箱内无法访问 Actions 页面或 API（只有 `ssh.github.com` 可通，故 SSH 推送正常），因此该结论由用户确认。

**CI 预期流程**（`.github/workflows/release.yml`）：版本校验 → restore → `dotnet build -c Release --no-restore` → **`dotnet test`** → 发布两个自包含单文件 exe → 校验产物存在 → 打两个 zip → 创建 Release 并上传。

**风险提示**：本会话沙箱无法运行 `dotnet test`（testhost 受限），本地是用进程内 xUnit 运行器验证的 **1001/1001 通过**；CI 走标准 `dotnet test`，若有个别环境相关用例在 GitHub runner 上失败，工作流会在「运行单元测试」步骤失败，Release 不会生成——把该步骤日志发我即可分析。

---

## 5. 遗留问题（本次未处理，均不阻塞发布）

1. **`app.manifest` 程序集标识过期**：`src/Tools/FileMaster/app.manifest:3` 与 `src/Tools/UsbBackup/app.manifest` 仍为 `version="0.1.0.0"`，且 FileMaster 的 `name` 仍是 `WinToolBox.Tools.FolderCreator.app`（审计报告已记录的遗留项）。它**不影响** exe 的 `FileVersion`/`ProductVersion`（来自 `<Version>`），也不影响 CI；任务书约束下未改动。
2. **FileMaster 关于对话框的兜底字符串**：`src/Tools/FileMaster/MainForm.cs:220` 保留了 `"0.4.0"` 作为 `Assembly.GetName().Version` 为 null 时的兜底（实际不可达，正常路径读程序集版本 → 显示 `0.4.8`）。属源码，未改。
3. **测试清单标题中的版本**：`docs/testing/*.md` 的标题仍写 `v0.4.0`（表示该清单编写时所针对的版本，属历史表述）。
4. **README 中的举例**：`README.md:132`「Release 标题是 v0.5.0、exe 里却是 0.4.0」是版本不一致的**举例**，未改。
5. **本会话环境限制**：沙箱禁止命名管道 → 裸 `dotnet build` 静默失败（0 Error）、`dotnet test` 不可用、`git ls-remote` 在非提权下失败；CI 与正常 shell 不受影响。
6. **新任务书待执行**：`docs/tasks/DSH_Task_RenameEnhance.md`（FileMaster 批量重命名增强）已随本次推送入库，尚未开始执行。
