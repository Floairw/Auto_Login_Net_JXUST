# 验证原生自定义动作的目录探测逻辑（不需要管理员权限、不碰本机已装产品）。
#
# 为什么要有这个脚本：
#   正式路径必须在 msiexec 会话里跑，需要管理员权限；而「解析逻辑对不对」这件事
#   可以在普通权限下验证。这个脚本加载**编译出来的诊断版 DLL**，调用它里面的
#   自检入口，因此验证的就是即将交付的那份 C++ 代码。
#
#   上一轮 bug 正是因为验证夹具另写了一份逻辑（夹具用等值 SQL，交付代码用
#   被 msiexec 拒绝的 LIKE），夹具全绿而功能从未生效。这里不再复刻逻辑。
#
# 探测对象从哪里来（2026-10 起）：
#   以前这里写死了本机的两个产品码与两个目录（1.0.0 在 C:\Program Files\AutoLoginNet App、
#   2.0.7 在 D:\autologin）。换一台机器、或装的是别的版本，整段断言就失去意义甚至全红。
#   现在改为**自动发现**：复用 Setup\Get-InstalledProducts.ps1，扫描本机 ARP 里所有
#   「校园网认证客户端」产品，再读各自的**缓存 MSI**（File 表 + MsiGetComponentPath）
#   判断它属于 1.x 还是 2.x、真实装在哪个目录。逻辑与生产 C++ 自定义动作同源。
#
#   某代次没装时不再静默通过：明确打印「跳过（本机未安装 X）」并计入 skipped；
#   断言只对实际找到的产品生效。仍然可以用参数显式覆盖，便于复现特定场景。
#
# 用法：
#   pwsh -NoProfile -File Setup\Test-NativeDetection.ps1
#   （先运行 Setup\Build-NativeCustomActions.ps1 生成诊断版 DLL）

[CmdletBinding()]
param(
    # 留空 = 自动检测本机已装的对应代次产品；显式传值可复现特定场景。
    [string]$Legacy1x,
    [string]$Legacy2x,
    [string]$Dir1x,
    [string]$Dir2x
)

$ErrorActionPreference = 'Stop'
$passed = 0
$failed = 0
$skipped = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Write-Host "  [通过] $name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "  [失败] $name  $detail" -ForegroundColor Red }
}

function Skip([string]$name, [string]$reason) {
    $script:skipped++
    Write-Host "  [跳过] $name" -ForegroundColor Yellow
    Write-Host "         $reason" -ForegroundColor DarkGray
}

$diagDll = Join-Path $PSScriptRoot 'CustomActions.Native\bin\AutoLoginNet.SetupActions.Native.selftest.dll'
if (-not (Test-Path $diagDll)) {
    throw "找不到诊断版 DLL：$diagDll —— 请先运行 Setup\Build-NativeCustomActions.ps1"
}

# ---------------------------------------------------------------- 自动发现本机产品
# Get-InstalledProducts.ps1 只定义函数、不打印报告（-Library）。
. (Join-Path $PSScriptRoot 'Get-InstalledProducts.ps1') -Library

function Get-VersionKey($product) {
    $version = $null
    [void][version]::TryParse([string]$product.Version, [ref]$version)
    if ($null -eq $version) { return [version]'0.0.0' }
    return $version
}

$installed = @(Get-InstalledAutoLoginNetProducts)
$product1x = $installed | Where-Object { $_.AppKind -eq '1x' } | Sort-Object { Get-VersionKey $_ } -Descending | Select-Object -First 1
$product2x = $installed | Where-Object { $_.AppKind -eq '2x' } | Sort-Object { Get-VersionKey $_ } -Descending | Select-Object -First 1

Write-Host '==> 本机已装的「校园网认证客户端」产品（自动发现）' -ForegroundColor Cyan
if ($installed.Count -eq 0) {
    Write-Host '    （一个都没有）'
}
foreach ($product in $installed) {
    $kindText = if ($product.AppKind) { $product.AppKind } else { '?' }
    Write-Host ("    {0,-3} {1,-10} {2}" -f $kindText, $product.Version, $product.ProductCode)
    Write-Host ("        目录: {0}" -f $(if ($product.InstallDirectory) { $product.InstallDirectory } else { '(未知)' })) -ForegroundColor DarkGray
}
if (-not $product1x) { Write-Host '    1.x: 本机未安装' -ForegroundColor DarkGray }
if (-not $product2x) { Write-Host '    2.x: 本机未安装' -ForegroundColor DarkGray }

# 显式传入的参数优先，缺省才用自动发现的结果。
if ([string]::IsNullOrEmpty($Legacy1x) -and $product1x) { $Legacy1x = $product1x.ProductCode }
if ([string]::IsNullOrEmpty($Dir1x) -and $product1x) { $Dir1x = $product1x.InstallDirectory }
if ([string]::IsNullOrEmpty($Legacy2x) -and $product2x) { $Legacy2x = $product2x.ProductCode }
if ([string]::IsNullOrEmpty($Dir2x) -and $product2x) { $Dir2x = $product2x.InstallDirectory }

$logPath = Join-Path $env:TEMP ('aln-selftest-' + [Guid]::NewGuid().ToString('N') + '.log')
$env:ALN_SELFTEST_LOG = $logPath

# rundll32 会在自己返回前就把 DLL 跑完，但为稳妥仍然轮询等日志出现，
# 避免「文件刚被创建、内容还没落盘」这种竞态导致断言无谓失败。
function Invoke-SelfTest([string]$codes) {
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    if ([string]::IsNullOrEmpty($codes)) { Remove-Item Env:\ALN_SELFTEST_CODES -ErrorAction SilentlyContinue }
    else { $env:ALN_SELFTEST_CODES = $codes }

    $proc = Start-Process rundll32.exe -ArgumentList @($diagDll, 'AlnSelfTestDetect') -PassThru -Wait -ErrorAction Stop

    for ($i = 0; $i -lt 50; $i++) {
        if (Test-Path $logPath) {
            $lines = @(Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue)
            if ($lines.Count -ge 2) { return $lines }
        }
        Start-Sleep -Milliseconds 100
    }

    throw "自检没有写出完整日志：$logPath（rundll32 退出码 $($proc.ExitCode)）"
}

# 取 DetectCore 的最终决策行（chosen），而不是两段的中间结果 ——
# 真正生效的是「有没有关联产品」这道闸门。
function Get-ChosenPath($lines) {
    $hit = $lines | Where-Object { $_ -like 'chosen |*' } | Select-Object -First 1
    if (-not $hit) { return $null }
    if ($hit -match 'path=\[(.*)\]\s*$') { return $Matches[1] }
    return $null
}

function Get-ChosenVia($lines) {
    $hit = $lines | Where-Object { $_ -like 'chosen |*' } | Select-Object -First 1
    if ($hit -and $hit -match 'via=(\w+)') { return $Matches[1] }
    return $null
}

function Show($lines) { $lines | ForEach-Object { Write-Host "    $_" } }

$path1 = $null; $via1 = $null; $ranScenario1 = $false
$path2 = $null; $via2 = $null; $ranScenario2 = $false
$path3 = $null

try {
    Write-Host ''
    Write-Host '==> 场景一：只有 1.x 登记在关联产品里（2.x 的目录仍在）' -ForegroundColor Cyan
    Write-Host '    （这正是当年 1.x -> 2.x 的升级形态；关键是不能把新版装进 1.x 的目录）' -ForegroundColor DarkGray
    if ([string]::IsNullOrEmpty($Legacy1x)) {
        Skip '场景一：从 1.x 升级' '本机未安装 1.x（找不到代次 1x 的产品），且未显式指定 -Legacy1x'
    }
    elseif ([string]::IsNullOrEmpty($Dir2x)) {
        Skip '场景一：从 1.x 升级' '本机未安装 2.x（找不到代次 2x 的产品），无法断言升级目标目录；可用 -Dir2x 显式指定'
    }
    else {
        $lines1 = Invoke-SelfTest $Legacy1x
        Show $lines1
        $path1 = Get-ChosenPath $lines1
        $via1 = Get-ChosenVia $lines1
        $ranScenario1 = $true
    }

    Write-Host ''
    Write-Host '==> 场景二：只有 2.x 登记在关联产品里（用户可能自定义安装目录）' -ForegroundColor Cyan
    if ([string]::IsNullOrEmpty($Legacy2x)) {
        Skip '场景二：从 2.x 升级' '本机未安装 2.x（找不到代次 2x 的产品），且未显式指定 -Legacy2x'
    }
    elseif ([string]::IsNullOrEmpty($Dir2x)) {
        Skip '场景二：从 2.x 升级' '找不到 2.x 的真实安装目录；可用 -Dir2x 显式指定'
    }
    else {
        $lines2 = Invoke-SelfTest $Legacy2x
        Show $lines2
        $path2 = Get-ChosenPath $lines2
        $via2 = Get-ChosenVia $lines2
        $ranScenario2 = $true
    }

    Write-Host ''
    Write-Host '==> 场景三：WIX_UPGRADE_DETECTED 为空（全新安装，不应误选任何现有目录）' -ForegroundColor Cyan
    $lines3 = Invoke-SelfTest ''
    Show $lines3
    $path3 = Get-ChosenPath $lines3
} finally {
    Remove-Item Env:\ALN_SELFTEST_LOG -ErrorAction SilentlyContinue
    Remove-Item Env:\ALN_SELFTEST_CODES -ErrorAction SilentlyContinue
    Remove-Item $logPath -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '=== 结论 ===' -ForegroundColor Cyan
Write-Host ("  仅 1.x 关联 -> {0}   (via={1})" -f $(if ($ranScenario1) { $path1 } else { '(未运行)' }), $via1)
Write-Host ("  仅 2.x 关联 -> {0}   (via={1})" -f $(if ($ranScenario2) { $path2 } else { '(未运行)' }), $via2)
Write-Host ("  全新安装    -> {0}" -f $path3)
Write-Host ''

# 最关键的一条回归：新版本绝不能落到 1.x 的目录里。
if (-not $ranScenario1) {
    Skip '从 1.x 升级的目录断言' '场景一未运行（缺少 1.x 产品或 2.x 目录信息）'
}
else {
    if (-not [string]::IsNullOrEmpty($Dir1x)) {
        Check '从 1.x 升级时不会把新版装进 1.x 的目录' ($path1 -ne $Dir1x) "实际 '$path1'（1.x 目录 '$Dir1x'）"
    }
    else {
        Skip '从 1.x 升级时不会把新版装进 1.x 的目录' '未知 1.x 的安装目录（可用 -Dir1x 显式指定）'
    }
    Check '从 1.x 升级时解析到同代的 2.x 目录' ($path1 -eq $Dir2x) "实际 '$path1'，期望 '$Dir2x'"
    Check '该目录确实存在且含 2.x 主程序' (Test-Path (Join-Path $path1 'AutoLoginNet.exe')) "缺少 $path1\AutoLoginNet.exe"
}

if (-not $ranScenario2) {
    Skip '从 2.x 升级的目录断言' '场景二未运行（缺少 2.x 产品或目录信息）'
}
else {
    Check '从 2.x 升级时装回它自己的目录' ($path2 -eq $Dir2x) "实际 '$path2'，期望 '$Dir2x'"
    Check '该目录确实存在且含 2.x 主程序' (Test-Path (Join-Path $path2 'AutoLoginNet.exe')) "缺少 $path2\AutoLoginNet.exe"
    Check '2.x 的解析走的是关联产品（lineage）而不是全量枚举兜底' ($via2 -eq 'lineage') "实际 via='$via2'"
}

# 同时存在两份安装时，两条产品线不能互相串台。
if ($ranScenario1 -and $ranScenario2 -and -not [string]::IsNullOrEmpty($Dir1x)) {
    Check '两条产品线不会互相串台（1.x 的目录从未被选中）' ($path1 -ne $Dir1x -and $path2 -ne $Dir1x) "1.x 关联='$path1' 2.x 关联='$path2'"
}
else {
    Skip '两条产品线不会互相串台' '需要 1.x 与 2.x 同时安装（或显式提供 1.x 目录）才能验证'
}

Check '全新安装时不误选任何现有旧目录' ([string]::IsNullOrEmpty($path3) -or $path3 -eq '<empty>') "实际 '$path3'"

Write-Host ''
Write-Host "通过 $passed 项，跳过 $skipped 项，失败 $failed 项"
exit ($failed -eq 0 ? 0 : 1)
