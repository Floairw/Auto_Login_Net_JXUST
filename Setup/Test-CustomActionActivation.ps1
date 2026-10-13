# 自定义动作的激活验证：证明编译出来的原生 DLL 真的会被 msiexec 调起来，
# 并且真的把 INSTALLFOLDER 改写成旧版安装目录。
#
# 为什么需要这个脚本，而不是单元测试：
#   「探测定向逻辑正确」和「这个 DLL 能被 msiexec 加载并生效」是两件不同的事。
#   后者只有把 MSI 真的交给 msiexec 跑一遍才能证明。
#
# 安全性：
#   为了触发 WiX 的 WIX_UPGRADE_DETECTED，探针 MSI 必须共用真实产品的 UpgradeCode，
#   因此它确实会进入升级流程、并真的把新文件写进旧版目录（C:\Program Files\AutoLoginNet App）。
#   写入的只有一个 aln-ca-probe.txt 标记文件，脚本结束后会立刻卸载探针产品并删除该文件。
#   不要在本机装有正式版且不希望被动过的情况下运行。

[CmdletBinding()]
param(
    [string]$UpgradeCode,
    [switch]$KeepTemp,
    # 只编译探针 MSI 并打印 CustomAction 表，不实际部署。
    # 用途：在没有管理员权限、或不想动本机已装产品的场景下，仍能验证
    # 「动作有没有被正确写进 MSI、入口名与导出名是否一致」。
    [switch]$AuthorOnly
)

$ErrorActionPreference = 'Stop'
$passed = 0
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Write-Host "  [通过] $name" }
    else { $script:failed++; Write-Host "  [失败] $name  $detail" }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = 'wix' }

# UpgradeCode 必须与 Build-Installer.ps1 用同一个种子算出来，否则探针进入不了升级流程。
if (-not $UpgradeCode) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $UpgradeCode = [Guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes('AutoLoginNet/UpgradeCode'))).ToString().ToUpperInvariant()
    }
    finally { $md5.Dispose() }
}
Write-Host "==> 使用 UpgradeCode：$UpgradeCode" -ForegroundColor Cyan

$nativeDll = Join-Path $PSScriptRoot 'CustomActions.Native\bin\AutoLoginNet.SetupActions.Native.dll'
if (-not (Test-Path $nativeDll)) {
    throw "找不到原生自定义动作：$nativeDll（先运行 Setup\Build-NativeCustomActions.ps1）"
}

$tempDir = Join-Path $repoRoot 'tmp\ca-activation'
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$probeFile = Join-Path $tempDir 'aln-ca-probe.txt'
Set-Content -Path $probeFile -Value 'probe' -Encoding ASCII

$legacyDir = 'C:\Program Files\AutoLoginNet App'
$installedMarker = Join-Path $legacyDir 'aln-ca-probe.txt'
$junkDir = Join-Path $tempDir 'junk-target'

$productWxs = Join-Path $tempDir 'Probe.wxs'
$msiPath = Join-Path $tempDir 'Probe.msi'
$logPath = Join-Path $tempDir 'install.log'

# Version 必须小于已安装的 1.0.0 之外还要触发 WIX_UPGRADE_DETECTED：
# 探针版本取 9.9.9，这样已安装的 1.0.0 与 2.0.7 都会被识别为「旧版」。
$wxs = @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="ALN CA 探针" Manufacturer="AutoLoginNet" Version="9.9.9"
           UpgradeCode="$UpgradeCode" Language="2052" Codepage="65001"
           Scope="perMachine" InstallerVersion="500" Compressed="yes">
    <!-- Schedule="afterInstallFinalize" 让升级移除排到最后，而 FindRelatedProducts
         依然会填充 WIX_UPGRADE_DETECTED —— 探测需要的东西拿到了，本机已装的两个正式版
         也不会被这个探针提前拆掉（测试流程会在移除发生前就卸载探针）。 -->
    <MajorUpgrade DowngradeErrorMessage="探针不应被降级。" Schedule="afterInstallFinalize" />
    <MediaTemplate EmbedCab="yes" />

    <Binary Id="UpgradeLocationCa" SourceFile="$nativeDll" />
    <CustomAction Id="DetectPreviousInstallLocation" BinaryRef="UpgradeLocationCa"
                  DllEntry="DetectPreviousInstallLocation" Execute="immediate" Return="ignore" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="junk-target" />
    </StandardDirectory>

    <Feature Id="Main" Level="1">
      <Component Id="ProbeComponent" Directory="INSTALLFOLDER" Guid="*">
        <File Id="ProbeFile" Source="$probeFile" Name="aln-ca-probe.txt" KeyPath="yes" />
      </Component>
    </Feature>

    <InstallExecuteSequence>
      <Custom Action="DetectPreviousInstallLocation" Before="CostFinalize" Condition="1" />
    </InstallExecuteSequence>
    <InstallUISequence>
      <Custom Action="DetectPreviousInstallLocation" Before="CostFinalize" Condition="1" />
    </InstallUISequence>
  </Package>
</Wix>
"@
Set-Content -Path $productWxs -Value $wxs -Encoding UTF8

Write-Host "==> 编译探针 MSI"
& $wix build $productWxs -arch x64 -out $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$before = Test-Path $installedMarker

# 静态校验：动作是否真的写进了 MSI，入口名与 DLL 导出名是否对得上。
# 这两件事在 msiexec 运行期只会表现为「静默不生效」，所以值得单独证明一次。
function Get-MsiRows([string]$sql) {
    $wi = New-Object -ComObject WindowsInstaller.Installer
    $db = $wi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $wi, @($msiPath, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @($null))
    $rows = @()
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        $rows += , $rec
    }
    return $rows
}

Write-Host ''
Write-Host '==> 静态校验：MSI 里的自定义动作登记' -ForegroundColor Cyan
$caRows = Get-MsiRows 'SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`'
foreach ($r in $caRows) {
    Write-Host ("    CustomAction: Action={0} Type={1} Source={2} Target={3}" -f $r.StringData(1), $r.IntegerData(2), $r.StringData(3), $r.StringData(4))
}
$seqRows = Get-MsiRows 'SELECT `Action`,`Sequence` FROM `InstallExecuteSequence` WHERE `Action`=''DetectPreviousInstallLocation'''
foreach ($r in $seqRows) {
    Write-Host ("    InstallExecuteSequence: {0} seq={1}" -f $r.StringData(1), $r.IntegerData(2))
}

$staticAction = ($caRows | Where-Object { $_.StringData(1) -eq 'DetectPreviousInstallLocation' })
$staticTarget = if ($staticAction) { $staticAction[0].StringData(4) } else { '' }
Check 'CustomAction 表里有 DetectPreviousInstallLocation' ($null -ne $staticAction)
Check 'DllEntry 指向已导出的入口名' ($staticTarget -eq 'DetectPreviousInstallLocation') "实际 '$staticTarget'"
Check '已排入 InstallExecuteSequence' ($seqRows.Count -gt 0)

if ($AuthorOnly) {
    Write-Host ''
    Write-Host "探针 MSI 与静态校验完成（-AuthorOnly，未部署）。产物：$msiPath" -ForegroundColor Green
    exit ($failed -eq 0 ? 0 : 1)
}

Write-Host "==> 安装前：旧版目录里有探针文件？$before"
if ($before) { throw "旧版目录里已存在 $installedMarker，测试前提不成立（请先清理）" }

Write-Host "==> 部署探针（msiexec /i /qn，目标目录故意设成垃圾路径）"
Write-Host "    期望：自定义动作把 INSTALLFOLDER 改写成 $legacyDir"
$proc = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msiPath`"", '/qn', '/norestart', '/l*v', "`"$logPath`"") -Wait -PassThru
Write-Host "    msiexec 退出码 = $($proc.ExitCode)"

$after = Test-Path $installedMarker
Write-Host "==> 安装后：旧版目录里有探针文件？$after"

# 卸载探针，避免本机残留一个 9.9.9 的产品
Write-Host "==> 卸载探针"
$uninstall = Start-Process msiexec.exe -ArgumentList @('/x', "`"$msiPath`"", '/qn', '/norestart') -Wait -PassThru
Write-Host "    卸载退出码 = $($uninstall.ExitCode)"
if (Test-Path $installedMarker) { Remove-Item $installedMarker -Force -ErrorAction SilentlyContinue }

$resolved = 'C:\Program Files\AutoLoginNet\aln-ca-probe.txt'
$wrongTarget = Test-Path $resolved

Write-Host ''
Write-Host '=== 结论 ===' -ForegroundColor Cyan

Check '自定义动作被 msiexec 调用（日志里出现该动作）' ((Select-String -Path $logPath -Pattern 'DetectPreviousInstallLocation' -SimpleMatch -Quiet) -eq $true)
Check 'INSTALLFOLDER 被改写成旧版目录' ((Select-String -Path $logPath -Pattern ([regex]::Escape($legacyDir)) -SimpleMatch -Quiet) -eq $true)
Check '文件落进旧版目录（而不是默认的 AutoLoginNet 目录）' $after
Check '没有落到默认目录' (-not $wrongTarget)

Write-Host ''
if (-not $KeepTemp) { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "通过 $passed 项，失败 $failed 项"
exit ($failed -eq 0 ? 0 : 1)
