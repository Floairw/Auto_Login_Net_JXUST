<#
.SYNOPSIS
    为目标机安装本产品运行所需的两个前置运行时（缺哪个装哪个）。

.DESCRIPTION
    本产品自 2.0.7 起为「框架依赖」发布：
      * .NET 运行时      —— Microsoft.NETCore.App 10.x
      * Windows App Runtime —— 1.8 系列（与 Microsoft.WindowsAppSDK 1.8 对应）
    目标机缺任意一个时，AutoLoginNet.exe 会在启动阶段直接退出
    （0xC000027B / “找不到指定的模块”），且不会有任何界面提示。

    本脚本先检测、后安装：两个都已装好则不做任何改动，直接退出 0。

    安装器来源（按顺序找）：
      1) 本脚本同级目录下的 Runtime\ 子目录；
      2) 同级的 Setup\Runtime\ 子目录（从仓库根目录运行时用得到）；
      3) -InstallerDir 显式指定的目录。
    运行时安装器是二进制大文件、不入库，获取方式见 Setup\Runtime\README.md。

.PARAMETER InstallerDir
    安装器所在目录。省略时按上面的顺序自动查找。

.PARAMETER WhatIf
    只检测与打印计划，不实际安装。

.EXAMPLE
    pwsh -NoProfile -File Setup\Install-Runtime.ps1

.EXAMPLE
    pwsh -NoProfile -File Setup\Install-Runtime.ps1 -WhatIf
#>
[CmdletBinding()]
param(
    [string]$InstallerDir,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 运行时版本要求：改这两个常量即可适配后续升级
$DotNetMajor          = 10
$WapPackageName       = 'Microsoft.WindowsAppRuntime.1.8'
$DotNetInstallerNames = @('windowsdesktop-runtime-10.0.12-win-x64.exe', 'windowsdesktop-runtime-10.0-win-x64.exe', 'windowsdesktop-runtime-win-x64.exe')
$WapInstallerNames    = @('WindowsAppRuntimeInstall-1.8-x64.exe', 'windowsappruntimeinstall-1.8-x64.exe', 'windowsappruntimeinstall-x64.exe')

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "  [已装] $Text" -ForegroundColor Green }
function Write-Miss([string]$Text) { Write-Host "  [缺失] $Text" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 检测
function Test-DotNetRuntime {
    $root = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    $versions = @(Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { $_.Name -match "^$DotNetMajor\." } |
        ForEach-Object { $_.Name } | Sort-Object)
    if ($versions.Count -eq 0) { return $null }
    return $versions[-1]
}

function Test-WapRuntime {
    $pkg = @(Get-AppxPackage -Name $WapPackageName -ErrorAction SilentlyContinue)
    if ($pkg.Count -eq 0) { return $null }
    return ($pkg | Sort-Object Version -Descending | Select-Object -First 1).Version.ToString()
}

Write-Step '检测运行时'
$dotnetVer = Test-DotNetRuntime
$wapVer = Test-WapRuntime
if ($dotnetVer) { Write-Ok ".NET 运行时 $dotnetVer（Microsoft.NETCore.App）" } else { Write-Miss ".NET $DotNetMajor 运行时（Microsoft.NETCore.App）" }
if ($wapVer)    { Write-Ok "Windows App Runtime $wapVer（$WapPackageName）" }   else { Write-Miss "Windows App Runtime 1.8（$WapPackageName）" }

$needDotNet = -not $dotnetVer
$needWap = -not $wapVer
if (-not $needDotNet -and -not $needWap) {
    Write-Host ''
    Write-Host '两个运行时都已安装，无需任何操作。' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- 定位安装器
if (-not $InstallerDir) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'Runtime'),
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'Setup\Runtime'),
        $PSScriptRoot
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { $InstallerDir = $c; break }
    }
}
if (-not $InstallerDir -or -not (Test-Path -LiteralPath $InstallerDir)) {
    throw "找不到运行时安装器目录。请先把安装器放到 Setup\Runtime\（获取方式见 Setup\Runtime\README.md），或用 -InstallerDir 指定。"
}
Write-Host "  安装器目录：$InstallerDir" -ForegroundColor DarkGray

function Find-Installer([string[]]$Names) {
    foreach ($n in $Names) {
        $p = Join-Path $InstallerDir $n
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

$dotnetInstaller = $null
$wapInstaller = $null
if ($needDotNet) {
    $dotnetInstaller = Find-Installer $DotNetInstallerNames
    if (-not $dotnetInstaller) {
        throw "缺少 .NET $DotNetMajor 运行时安装器（期望文件名之一：$($DotNetInstallerNames -join '、')）。下载地址见 Setup\Runtime\README.md。"
    }
}
if ($needWap) {
    $wapInstaller = Find-Installer $WapInstallerNames
    if (-not $wapInstaller) {
        throw "缺少 Windows App Runtime 1.8 安装器（期望文件名之一：$($WapInstallerNames -join '、')）。下载地址见 Setup\Runtime\README.md。"
    }
}

if ($WhatIf) {
    Write-Step 'WhatIf：将要执行的安装'
    if ($dotnetInstaller) { Write-Host "  $dotnetInstaller /install /quiet /norestart" }
    if ($wapInstaller)    { Write-Host "  $wapInstaller --quiet --force" }
    exit 0
}

# ---------------------------------------------------------------- 提权
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host ''
    Write-Host '安装 MSIX 运行时包需要管理员权限，正在请求提权…' -ForegroundColor Yellow
    # -NoExit 让提权窗口保留，用户能看见结果；当前窗口无法读取另一个提权窗口的退出码
    $argList = @('-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($InstallerDir) { $argList += @('-InstallerDir', "`"$InstallerDir`"") }
    Start-Process -FilePath 'pwsh.exe' -ArgumentList $argList -Verb RunAs | Out-Null
    Write-Host '已在新窗口中以管理员身份继续。' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- 安装
function Invoke-Installer([string]$Exe, [string[]]$Arguments, [string]$What) {
    Write-Host "  正在安装 $What …" -ForegroundColor DarkGray
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "$What 安装失败，退出码 $($p.ExitCode)（安装器：$Exe）"
    }
}

if ($dotnetInstaller) {
    Write-Step ".NET $DotNetMajor 运行时"
    # /install /quiet /norestart 是 .NET 官方推荐的静默参数
    Invoke-Installer $dotnetInstaller @('/install', '/quiet', '/norestart') ".NET $DotNetMajor 运行时"
}
if ($wapInstaller) {
    Write-Step 'Windows App Runtime 1.8'
    # --quiet 无交互；--force 允许安装器在必要时结束占用运行时包的老进程
    Invoke-Installer $wapInstaller @('--quiet', '--force') 'Windows App Runtime 1.8'
}

# ---------------------------------------------------------------- 复检
Write-Step '复检'
$dotnetVer = Test-DotNetRuntime
$wapVer = Test-WapRuntime
if ($dotnetVer) { Write-Ok ".NET 运行时 $dotnetVer" } else { Write-Host "  [仍未装] .NET $DotNetMajor 运行时" -ForegroundColor Red }
if ($wapVer)    { Write-Ok "Windows App Runtime $wapVer" }   else { Write-Host '  [仍未装] Windows App Runtime 1.8' -ForegroundColor Red }

if ($dotnetVer -and $wapVer) {
    Write-Host ''
    Write-Host '运行时已就绪，现在可以安装/启动「校园网认证客户端」。' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host '仍有运行时未安装成功，请检查上面的安装器输出。' -ForegroundColor Red
exit 1
