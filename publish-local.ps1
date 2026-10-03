<#
.SYNOPSIS
    一键把 WinToolBox 工具发布到「工作区之外」的可运行目录，并解除文件的安全标记。

.DESCRIPTION
    为什么需要这个脚本：
    1) 本会话的工作区（D:\github\WinToolBox）会被沙箱强制以「低完整性」运行，
       低完整性进程无法注册系统托盘图标 —— 直接在工作区里双击 UsbBackup.exe 会
       出现「进程在跑但托盘里没有图标」的现象。放到工作区之外运行就没有这个问题。
    2) 从网盘 / 聊天工具 / 浏览器复制出来的 exe 会带「下载来源」标记（Zone.Identifier），
       Windows SmartScreen 每次双击都会弹蓝色安全提示。脚本会对目标目录执行
       Unblock-File 一次性解除该标记。

    安全护栏（本脚本会递归删除目标目录，因此必须先过以下检查才会动手）：
    1) 目标不能位于工作区内；
    2) 目标不能是驱动器根（例如 D:\、C:\）；
    3) 目标不能是 Windows / Program Files / 用户主目录等系统或敏感目录
       （唯一豁免：本工具自己的 %LocalAppData%\WinToolBox 子树，默认目标在里面）；
    4) 目标必须「尚不存在」或「带本脚本写入的标记文件 .wintoolbox-publish」，
       否则拒绝清空，避免误删用户自己的数据；
    5) 真正删除前会打印将被清空的路径并要求确认（-Confirm:$false 跳过确认，
       -WhatIf 只预览不执行）。

.PARAMETER Target
    发布目标目录（必须在工作区之外）。默认 %LocalAppData%\WinToolBox\tools

.PARAMETER NoLaunch
    加上此开关则发布完成后不自动启动 UsbBackup。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\publish-local.ps1
    powershell -ExecutionPolicy Bypass -File .\publish-local.ps1 -Target 'D:\Tools\WinToolBox' -NoLaunch
    powershell -ExecutionPolicy Bypass -File .\publish-local.ps1 -WhatIf          # 只预览将被清空的路径
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$Target = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WinToolBox\tools'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$targetFull = [System.IO.Path]::GetFullPath($Target)

# 本脚本在目标目录里留下的标记文件：只有它存在（或目录本来就不存在）才允许清空。
$markerName = '.wintoolbox-publish'

# 判断 Candidate 是否等于 Root 或位于 Root 之内。
# 用「补分隔符后比较」而不是 [System.IO.Path]::GetRelativePath：后者在
# Windows PowerShell 5.1（.NET Framework）上根本不存在，会静默失效。
function Test-IsSameOrInside {
    param([string]$Candidate, [string]$Root)

    if ([string]::IsNullOrWhiteSpace($Candidate) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }

    try {
        $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
        $rootFull = [System.IO.Path]::GetFullPath($Root)
    }
    catch {
        return $false
    }

    $separators = @([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

    $candidateTrimmed = $candidateFull.TrimEnd($separators)
    $rootTrimmed = $rootFull.TrimEnd($separators)

    if ($candidateTrimmed.Equals($rootTrimmed, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    # 补上分隔符，避免「D:\foo」被误判为「D:\foobar」的父路径
    $rootWithSeparator = $rootTrimmed + [System.IO.Path]::DirectorySeparatorChar

    return $candidateTrimmed.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
}

# ---------------- 护栏 1：不能位于工作区内 ----------------
if (Test-IsSameOrInside -Candidate $targetFull -Root $root) {
    throw "目标目录不能位于工作区内（否则程序会被强制以低完整性运行、托盘图标无法显示）：$targetFull"
}

# ---------------- 护栏 2：不能是驱动器根 ----------------
if ($targetFull -eq [System.IO.Path]::GetPathRoot($targetFull)) {
    throw "拒绝执行：目标目录是驱动器根（$targetFull）。本脚本会递归删除目标目录，清空整盘绝对不允许，请指定一个专用子目录。"
}

# ---------------- 护栏 3：不能是系统 / 敏感目录 ----------------
# 注意：Windows PowerShell 5.1（.NET Framework）的 SpecialFolder 枚举里没有
# ProgramData / SystemDrive，这里用可用的枚举成员 + 环境变量拼装。
$sensitiveRoots = New-Object System.Collections.Generic.List[string]

# 本工具自己的数据目录：位于 LocalApplicationData 之下，但属于「程序自用」，
# 是唯一被豁免的敏感目录子树（见护栏 3 的豁免分支）。
$appOwnedRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'WinToolBox'

function Add-SensitiveRoot {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return }

    try { [void]$sensitiveRoots.Add([System.IO.Path]::GetFullPath($Path)) } catch { }
}

foreach ($special in @('Windows', 'ProgramFiles', 'ProgramFilesX86', 'UserProfile', 'LocalApplicationData', 'ApplicationData')) {
    try { Add-SensitiveRoot ([Environment]::GetFolderPath([Environment+SpecialFolder]::$special)) } catch { }
}

Add-SensitiveRoot $env:ProgramData
Add-SensitiveRoot $env:ProgramFiles
Add-SensitiveRoot ${env:ProgramFiles(x86)}

$systemDrive = [System.IO.Path]::GetPathRoot($env:SystemRoot)
if (-not [string]::IsNullOrWhiteSpace($systemDrive)) {
    foreach ($child in @('Windows', 'Program Files', 'Program Files (x86)', 'ProgramData', 'Users')) {
        Add-SensitiveRoot (Join-Path $systemDrive $child)
    }
}

foreach ($sensitive in ($sensitiveRoots | Sort-Object -Unique)) {
    if (Test-IsSameOrInside -Candidate $targetFull -Root $sensitive) {
        # 唯一豁免：本工具自己的 %LocalAppData%\WinToolBox 子树。
        # 它位于 LocalApplicationData 之下，但属于本程序自己的数据目录，
        # 默认目标 %LocalAppData%\WinToolBox\tools 必须能通过，否则文档里的无参调用永远失败。
        if (Test-IsSameOrInside -Candidate $targetFull -Root $appOwnedRoot) { continue }

        throw "拒绝执行：目标目录位于系统 / 敏感目录内（$sensitive）。本脚本会递归删除目标目录，请改用一个专用的空目录。"
    }
}

# ---------------- 护栏 4：只能清空「不存在」或「带标记」的目录 ----------------
$exists = Test-Path -LiteralPath $targetFull -PathType Container
$markerPath = Join-Path $targetFull $markerName
$markerExists = $exists -and (Test-Path -LiteralPath $markerPath -PathType Leaf)

if ($exists -and -not $markerExists) {
    throw "拒绝执行：目标目录已存在，但没有本脚本的标记文件「$markerName」：`r`n    $targetFull`r`n本脚本会清空目标目录，为避免误删你自己的数据，只允许清空「尚不存在的目录」或「由本脚本发布过、带 $markerName 标记的目录」。请换一个不存在的目录重试。"
}

# ---------------- 真正动手前确认 ----------------
# 只使用 ShouldProcess（配合 ConfirmImpact='High'）：
#   - 默认会交互询问；
#   - -Confirm:$false 可跳过；
#   - -WhatIf 只打印将执行的操作。
# 不再额外调用 ShouldContinue —— 它不受 -Confirm 影响，会导致「-Confirm:$false 仍然被问」
# 以及可能的两次询问。
if ($exists) {
    Write-Host "==> 目标目录已存在，将被【递归清空】：$targetFull"
}
else {
    Write-Host "==> 目标目录不存在，将新建：$targetFull"
}

if (-not $PSCmdlet.ShouldProcess($targetFull, '清空并重新发布')) {
    return
}

# ---------------- 执行 ----------------
Write-Host "==> 1/4 编译并发布到 $targetFull"

if ($exists) {
    # 失败必须暴露：不再用 SilentlyContinue 吞掉删除错误
    Remove-Item -LiteralPath $targetFull -Recurse -Force -ErrorAction Stop
}

New-Item -ItemType Directory -Force -Path (Join-Path $targetFull 'FileMaster') | Out-Null

# 写入标记文件：下次运行才允许再次清空这个目录
Set-Content -LiteralPath (Join-Path $targetFull $markerName) -Value @(
    'This directory is managed by WinToolBox publish-local.ps1.',
    "Published at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    'publish-local.ps1 may delete the whole directory before republishing.'
) -Encoding UTF8

dotnet publish (Join-Path $root 'src\Tools\UsbBackup\UsbBackup.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $targetFull | Out-Host
if ($LASTEXITCODE -ne 0) { throw "UsbBackup 发布失败，退出码 $LASTEXITCODE" }

dotnet publish (Join-Path $root 'src\Tools\FileMaster\FileMaster.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o (Join-Path $targetFull 'FileMaster') | Out-Host
if ($LASTEXITCODE -ne 0) { throw "FileMaster 发布失败，退出码 $LASTEXITCODE" }

Write-Host '==> 2/4 解除「下载来源」标记（消除 SmartScreen 反复提示）'
Get-ChildItem $targetFull -Recurse -File | Unblock-File

Write-Host '==> 3/4 复制到桌面快捷方式（可选，失败不影响主流程）'
try {
    $exe = Join-Path $targetFull 'UsbBackup.exe'
    $link = Join-Path ([Environment]::GetFolderPath('Desktop')) 'U盘备份.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $targetFull
    $shortcut.Description = 'WinToolBox - U盘备份'
    $shortcut.Save()
    Write-Host "    已创建桌面快捷方式：$link"
}
catch {
    Write-Host "    跳过（$($_.Exception.Message)）"
}

Write-Host '==> 4/4 完成'
Get-ChildItem $targetFull -Recurse -File -Filter *.exe |
    Select-Object FullName, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } |
    Format-Table -AutoSize

if (-not $NoLaunch) {
    Start-Process (Join-Path $targetFull 'UsbBackup.exe')
    Write-Host '已启动 UsbBackup。'
}
else {
    Write-Host "请手动双击：$(Join-Path $targetFull 'UsbBackup.exe')"
}
