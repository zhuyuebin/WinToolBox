# DSH 任务书：WinToolBox v0.4.8 正式发布

## 0. 前置说明
- 你是在全新对话中执行，没有历史上下文。
- 仓库：WinToolBox（https://github.com/zhuyuebin/WinToolBox）
- 当前分支：main
- 当前工作区应为 clean（上一次提交与推送已完成）
- 现有版本号：`Directory.Build.props` 中的 `<Version>0.4.0</Version>`
- CI 触发条件：`.github/workflows/release.yml` 只在推送 `v*` tag 时触发
- P1-16 修复后的 CI 有一步校验：**tag 版本必须与 `<Version>` 一致，否则拒绝发布**

## 1. 版本策略说明
- 本次发布为补丁级更新（P0/P1 修复 + 文档整理），不涉及新功能。
- 版本号定为 **0.4.8**，保持 `0.4.x` 补丁线，`0.5.0` 留给未来的大版本更新。

## 2. 任务目标
完成 v0.4.8 的正式发布：
1. 修改版本号。
2. 提交版本号改动并推送。
3. 打 tag 并推送，触发 GitHub Actions。
4. 验证 Actions 运行状态与 Release 产物。

## 3. 执行步骤

### 步骤 1：确认工作区 clean
```powershell
git status
git log --oneline -5
```
- 如果 `git status` 不为 clean，停下来问用户。
- 确认最近提交是 `docs: 添加提交与推送报告`（或类似信息）。

### 步骤 2：修改版本号
编辑 `Directory.Build.props`，把 `<Version>0.4.0</Version>` 改为 `<Version>0.4.8</Version>`。

**修改后立即验证**：
```powershell
[xml]$props = Get-Content Directory.Build.props
$props.Project.PropertyGroup.Version
# 应输出 0.4.8
```

### 步骤 3：提交并推送版本号改动
```powershell
git add Directory.Build.props
git commit -m "chore: bump version to 0.4.8

- 本次为 P0/P1 修复 + 文档结构整理
- 准备发布 v0.4.8"
git push origin main
git push gitee main
```

**推送前停下来问用户确认。**

### 步骤 4：打 tag 并推送（触发 CI）
```powershell
git tag v0.4.8
git push origin v0.4.8
git push gitee v0.4.8
```

**注意**：
- `git push origin v0.4.8` 会触发 GitHub Actions 的 release workflow。
- `git push gitee v0.4.8` 只是同步 tag 到 Gitee，Gitee 不会跑 GitHub Actions。

**推送前停下来问用户确认。**

### 步骤 5：验证推送结果
```powershell
git status
git log --oneline -5
git rev-list --count origin/main..main
git rev-list --count gitee/main..main
git tag -l
```
- `git status` 应为 clean。
- 两条 `rev-list` 都应为 0。
- `git tag -l` 应包含 `v0.4.8`。

### 步骤 6：报告 GitHub Actions 状态
**你无法直接访问 GitHub Actions 页面**，所以：
1. 告诉用户：**“tag 已推送，请前往 https://github.com/zhuyuebin/WinToolBox/actions 查看 release workflow 是否在运行。”**
2. 告诉用户等待 Actions 跑完（通常 2-5 分钟）。
3. 让用户确认：
   - Actions 是否成功
   - Releases 页面是否出现 `v0.4.8` 的 Release
   - 是否上传了 `FileMaster-win-x64.zip` 和 `UsbBackup-win-x64.zip`
4. 如果用户报告 Actions 失败，让用户把失败日志（尤其是失败步骤的输出）复制给你，你分析原因。

## 4. 约束与禁止
1. **不改任何源码、文档内容**，除了 `Directory.Build.props` 的版本号。
2. **不使用 `--force`**。
3. **不修改远程配置**。
4. **每次 commit/push 前停下来问用户确认**。
5. **不擅自打非 `v0.4.8` 的 tag**。
6. **如果版本号已经是 0.4.8**，跳过步骤 2-3，直接打 tag 并推送。

## 5. 输出要求
完成后输出一份 `RELEASE_V048_REPORT.md`（放 `docs/reports/`），包含：
1. 版本号修改前后对比。
2. 提交 hash 与推送结果。
3. tag 推送结果。
4. 用户报告的 GitHub Actions 状态。
5. 遗留问题（如有）。

## 6. 执行原则
**分步执行，每步完成后等用户确认，不要一口气全做完。**