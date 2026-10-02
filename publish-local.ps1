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

.PARAMETER Target
    发布目标目录（必须在工作区之外）。默认 D:\github\WinToolBox-UsbBackup

.PARAMETER NoLaunch
    加上此开关则发布完成后不自动启动 UsbBackup。

.EXAMPLE
    pwsh -File .\publish-local.ps1
    pwsh -File .\publish-local.ps1 -Target 'D:\Tools\WinToolBox' -NoLaunch
#>
[CmdletBinding()]
param(
    [string]$Target = 'D:\github\WinToolBox-UsbBackup',
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$targetFull = [System.IO.Path]::GetFullPath($Target)

if ($targetFull.StartsWith([System.IO.Path]::GetFullPath($root), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "目标目录不能位于工作区内（否则程序会被强制以低完整性运行、托盘图标无法显示）：$targetFull"
}

Write-Host "==> 1/4 编译并发布到 $targetFull"
Remove-Item $targetFull -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $targetFull 'FileMaster') | Out-Null

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
