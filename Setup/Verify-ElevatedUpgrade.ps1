# 升级安装的端到端验证（**需要在提升权限的终端里运行**）。
#
# 为什么必须提权：MSI 的 InstallExecuteSequence 只有以管理员身份启动的 msiexec
# 才会真正执行，自定义动作也才跑得起来；普通权限下只会走 UI 序列，验证不到实际安装。
#
# 它验证的是「第 24 号需求」：装新版时自动找到旧版装在哪里，并原地覆盖，而不是另起目录。
#
# 用法：
#   # 在「以管理员身份运行」的 PowerShell 里
#   Set-Location F:\C#code\study\autonettest\Auto_Login_Net_JXUST
#   pwsh -NoProfile -File Setup\Verify-ElevatedUpgrade.ps1
#
# 本机现状（断言依赖它）：
#   2.0.7 的产品 {36D0C2B2-...} 登记为已安装，实际文件在 D:\autologin
#   包内默认安装目录是 C:\Program Files\AutoLoginNet（自定义动作若失效就会落在这里）
#   为此脚本会在开始前**确认 D:\autologin 里确实是我们的程序**，避免误判。

[CmdletBinding()]
param(
    [string]$MsiPath,
    [string]$ExpectedDir = 'D:\autologin',
    [string]$DefaultDir  = 'C:\Program Files\AutoLoginNet',
    [string]$LegacyDir   = 'C:\Program Files\AutoLoginNet App',
    [string]$ProductCode2x = '{36D0C2B2-807C-40DF-A422-C228394C6F98}',
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$passed = 0
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Write-Host "  [通过] $name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "  [失败] $name  $detail" -ForegroundColor Red }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host '本脚本必须以管理员身份运行（否则 MSI 的安装序列根本不执行）。' -ForegroundColor Red
    Write-Host '请在「以管理员身份运行」的 PowerShell 里重新执行。' -ForegroundColor Red
    exit 2
}

if (-not $MsiPath) {
    $candidate = Get-ChildItem (Join-Path $PSScriptRoot '..\dist\AutoLoginNet-*-x64.msi') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw '在 dist 下找不到 MSI，请用 -MsiPath 指定。' }
    $MsiPath = $candidate.FullName
}
$MsiPath = (Resolve-Path $MsiPath).Path
if (-not $LogPath) { $LogPath = Join-Path $env:TEMP 'aln-upgrade-verify.log' }

Write-Host "==> 待安装的包：$MsiPath" -ForegroundColor Cyan
Write-Host "==> 安装日志：  $LogPath" -ForegroundColor Cyan

# ---------------------------------------------------------------- 安装前检查
Write-Host ''
Write-Host '=== 安装前 ===' -ForegroundColor Cyan

$hasOldExe = Test-Path (Join-Path $ExpectedDir 'AutoLoginNet.exe')
Check "旧版程序确实在 $ExpectedDir（否则本次验证无意义）" $hasOldExe "缺少 $ExpectedDir\AutoLoginNet.exe"

$exeVersion = $null
if ($hasOldExe) {
    $exeVersion = (Get-Item (Join-Path $ExpectedDir 'AutoLoginNet.exe')).VersionInfo.FileVersion
    Write-Host "    旧版文件版本：$exeVersion"
}

$productState = -1
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $productState = $installer.ProductState($ProductCode2x)
} catch {
    Write-Host "    （无法查询产品状态：$($_.Exception.Message)）" -ForegroundColor DarkGray
}
Write-Host "    产品 $ProductCode2x 状态：$productState（5=已安装）"

$running = @(Get-Process -Name 'AutoLoginNet*' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ''
    Write-Host "  正在结束占用安装目录的进程：$($running.ProcessName -join ', ')" -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# ---------------------------------------------------------------- 执行安装
Write-Host ''
Write-Host '=== 执行安装（静默，全程记录日志）===' -ForegroundColor Cyan
if (Test-Path $LogPath) { Remove-Item $LogPath -Force }

$arguments = @('/i', "`"$MsiPath`"", '/qn', '/norestart', '/l*v', "`"$LogPath`"")
Write-Host "    msiexec $($arguments -join ' ')" -ForegroundColor DarkGray
$proc = Start-Process msiexec.exe -ArgumentList $arguments -PassThru -Wait
Write-Host "    msiexec 退出码：$($proc.ExitCode)"

# ---------------------------------------------------------------- 安装后检查
Write-Host ''
Write-Host '=== 安装后 ===' -ForegroundColor Cyan

$log = @()
if (Test-Path $LogPath) { $log = @(Get-Content $LogPath -Encoding UTF8 -ErrorAction SilentlyContinue) }
Check '安装日志已生成' ($log.Count -gt 0) "没有 $LogPath"

# 1) 自定义动作确实被排程并执行
$scheduled = $log | Where-Object { $_ -match 'DetectPreviousInstallLocation' }
Check '自定义动作 DetectPreviousInstallLocation 出现在安装序列里' ($scheduled.Count -gt 0)

# 2) 它把安装目录改成了旧版所在目录。
#    MSI 日志里属性赋值形如：Property(S): INSTALLFOLDER = D:\autologin\
$installFolderLine = $log | Where-Object { $_ -match 'INSTALLFOLDER\s*=\s*(.+?)\s*$' } |
    Select-Object -Last 1
$resolvedFolder = $null
if ($installFolderLine -and $installFolderLine -match 'INSTALLFOLDER\s*=\s*(.+?)\s*$') {
    $resolvedFolder = $Matches[1].TrimEnd('\')
}
Write-Host "    日志中的 INSTALLFOLDER：$resolvedFolder"
Check "安装目录被改写为旧版所在目录（$ExpectedDir）" `
    ($resolvedFolder -ieq $ExpectedDir) "日志里是 '$resolvedFolder'"

# 3) 文件真的落在了旧目录，而不是默认目录
$newExe = Join-Path $ExpectedDir 'AutoLoginNet.exe'
Check "新文件装进了旧目录（$ExpectedDir）" (Test-Path $newExe) "缺少 $newExe"

$newVersion = $null
if (Test-Path $newExe) {
    $newVersion = (Get-Item $newExe).VersionInfo.FileVersion
    Write-Host "    安装后文件版本：$newVersion"
}

$strayDir = Test-Path $DefaultDir
Check "没有另起默认目录（$DefaultDir 不存在）" (-not $strayDir) '该目录被创建了 —— 说明目录探测没生效'

# 4) 新目录里仍然是完整可用的安装（被覆盖而不是被搬空）
foreach ($required in 'AutoLoginNet.exe') {
    Check "旧目录里仍有 $required" (Test-Path (Join-Path $ExpectedDir $required))
}
$fileCount = (Get-ChildItem $ExpectedDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host "    $ExpectedDir 下文件数：$fileCount"
Check '旧目录内容完整（文件数 >= 400）' ($fileCount -ge 400) "只有 $fileCount 个文件"

# 5) 从本次开始记录安装目录，供未来版本使用
$arpLocation = $null
foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
    foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
        $props = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
        if ($props.DisplayName -like '*校园网认证客户端*') {
            Write-Host "    ARP: $($key.PSChildName) version=$($props.DisplayVersion) location='$($props.InstallLocation)'"
            if ($props.InstallLocation) { $arpLocation = $props.InstallLocation.TrimEnd('\') }
        }
    }
}
Check "ARP 里记录了安装目录（$ExpectedDir）" ($arpLocation -ieq $ExpectedDir) "实际 '$arpLocation'"

# 6) 开机自启指向的程序仍然存在（不能把自启弄坏）
$task = Get-ScheduledTask -TaskName 'AutologinForDrcom' -ErrorAction SilentlyContinue
if ($task) {
    $exeArg = ($task.Actions | Select-Object -First 1).Execute
    Write-Host "    计划任务 AutologinForDrcom -> $exeArg"
    $taskTarget = $exeArg.Trim('"')
    Check '开机自启指向的程序仍然存在' (Test-Path $taskTarget) "缺少 $taskTarget"
} else {
    Write-Host '    （未找到计划任务 AutologinForDrcom，跳过）' -ForegroundColor DarkGray
}

# 7) 1.x 那代的目录不应被本次安装碰到
if (Test-Path $LegacyDir) {
    $legacyExe = Join-Path $LegacyDir 'AutoLoginNet.App.exe'
    Check '1.x 的目录没有被本次 2.x 安装触碰' `
        ((Test-Path $legacyExe) -and -not (Test-Path (Join-Path $LegacyDir 'AutoLoginNet.exe'))) `
        "$LegacyDir 的内容不符合预期"
} else {
    Write-Host "    （$LegacyDir 不存在，跳过跨代检查）" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "通过 $passed 项，失败 $failed 项" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host ''
Write-Host '如需卸载本次安装：' -ForegroundColor DarkGray
Write-Host "  msiexec /x `"$MsiPath`" /qn" -ForegroundColor DarkGray
Write-Host '（注意：卸载会删掉它自己装的文件，D:\autologin 里原有的便携版文件不受影响，'
Write-Host '  但若你想恢复成本次安装之前的状态，请从 dist\AutoLoginNet-*-portable-x64.zip 重新解压。）' -ForegroundColor DarkGray

exit ($failed -eq 0 ? 0 : 1)
