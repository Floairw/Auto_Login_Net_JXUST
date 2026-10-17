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
# 探测对象不再写死（2026-10 起）：
#   以前这里把「2.0.7 的产品码」和「D:\autologin」硬编码成参数默认值，换一台机器
#   （没装那个版本、或装在别的目录）脚本就无效甚至误判。现在改为自动发现：
#   复用 Setup\Get-InstalledProducts.ps1 扫描 ARP 并读缓存 MSI，找到本机已装的 2.x
#   产品及其真实安装目录，作为「旧版原位」的期望值；1.x 同理，用于跨代检查。
#   找不到已安装的 2.x（且没有显式传 -ExpectedDir）时，脚本会明确报错并以非 0 退出，
#   而不是拿着错误的默认目录跑一遍得出假结论。
#   包内默认安装目录仍是 C:\Program Files\AutoLoginNet —— 自定义动作若失效就会落在这里，
#   所以脚本会确认该目录**没有**被创建。

[CmdletBinding()]
param(
    [string]$MsiPath,
    # 以下三项留空 = 自动检测本机已装的产品与目录；显式传值可覆盖（复现特定场景）。
    [string]$ExpectedDir,
    [string]$DefaultDir  = 'C:\Program Files\AutoLoginNet',
    [string]$LegacyDir,
    [string]$ProductCode2x,
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

# ---------------------------------------------------------------- 自动定位本机产品
# 断言必须建立在「本机实际装了什么」之上，而不是写死的机器状态。
. (Join-Path $PSScriptRoot 'Get-InstalledProducts.ps1') -Library

function Get-VersionKey($product) {
    $version = $null
    [void][version]::TryParse([string]$product.Version, [ref]$version)
    if ($null -eq $version) { return [version]'0.0.0' }
    return $version
}

$installed = @(Get-InstalledAutoLoginNetProducts)
$product2x = $installed | Where-Object { $_.AppKind -eq '2x' } | Sort-Object { Get-VersionKey $_ } -Descending | Select-Object -First 1
$product1x = $installed | Where-Object { $_.AppKind -eq '1x' } | Sort-Object { Get-VersionKey $_ } -Descending | Select-Object -First 1

Write-Host ''
Write-Host '=== 自动发现的本机产品 ===' -ForegroundColor Cyan
if ($installed.Count -eq 0) { Write-Host '    （一个都没有）' }
foreach ($product in $installed) {
    $kindText = if ($product.AppKind) { $product.AppKind } else { '?' }
    Write-Host ("    {0,-3} {1,-10} {2}" -f $kindText, $product.Version, $product.ProductCode)
    Write-Host ("        目录: {0}" -f $(if ($product.InstallDirectory) { $product.InstallDirectory } else { '(未知)' })) -ForegroundColor DarkGray
}
if (-not $product1x) { Write-Host '    1.x：未安装（跨代检查将跳过）' -ForegroundColor DarkGray }
if (-not $product2x) { Write-Host '    2.x：未安装' -ForegroundColor DarkGray }

# 显式传入的参数优先，缺省才用自动发现的结果。
if ([string]::IsNullOrEmpty($ProductCode2x) -and $product2x) { $ProductCode2x = $product2x.ProductCode }
if ([string]::IsNullOrEmpty($ExpectedDir) -and $product2x -and $product2x.InstallDirectory) { $ExpectedDir = $product2x.InstallDirectory }
if ([string]::IsNullOrEmpty($LegacyDir) -and $product1x -and $product1x.InstallDirectory) { $LegacyDir = $product1x.InstallDirectory }

if ([string]::IsNullOrEmpty($ExpectedDir)) {
    Write-Host ''
    Write-Host '找不到本机已安装的 2.x 产品，因而无法确定「旧版原位」的期望目录。' -ForegroundColor Red
    Write-Host '本脚本要验证的是「装新版时原地覆盖旧版」，所以必须先装一个 2.x 版本；' -ForegroundColor Red
    Write-Host '如需验证特定场景，请显式传 -ExpectedDir（以及 -ProductCode2x / -LegacyDir）。' -ForegroundColor Red
    exit 3
}
Write-Host "==> 期望的旧版原位：$ExpectedDir" -ForegroundColor Cyan
if (-not [string]::IsNullOrEmpty($LegacyDir)) { Write-Host "==> 1.x 目录（跨代检查）：$LegacyDir" -ForegroundColor Cyan }

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

# ProductState 是 Installer 对象的「参数化属性」，COM 后期绑定调用不进去（实测必然抛异常），
# 所以直接走 msi.dll 的 MsiQueryProductStateW（Get-InstalledProducts.ps1 里已封装）。
if ([string]::IsNullOrEmpty($ProductCode2x)) {
    Write-Host '    （没有产品码，跳过产品状态查询）' -ForegroundColor DarkGray
} else {
    $productState = Get-MsiProductState $ProductCode2x
    Write-Host "    产品 $ProductCode2x 状态：$productState（5=已安装，-1=未安装，-2=未知）"
}

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
if ([string]::IsNullOrEmpty($LegacyDir)) {
    Write-Host '    （本机未安装 1.x，跳过跨代检查）' -ForegroundColor DarkGray
} elseif (Test-Path $LegacyDir) {
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
Write-Host '（注意：卸载只会删掉它自己装的文件；安装目录里原有的便携版/用户文件不受影响，'
Write-Host '  但若你想恢复成本次安装之前的状态，请重新解压 dist\AutoLoginNet-*-portable-*.zip。）' -ForegroundColor DarkGray

exit ($failed -eq 0 ? 0 : 1)
