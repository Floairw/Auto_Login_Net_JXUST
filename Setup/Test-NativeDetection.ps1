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
# 本机现状（断言依赖它，若换机器需同步调整）：
#   {4FEDDE3C-...} 1.0.0 装在 C:\Program Files\AutoLoginNet App（主程序 AutoLoginNet.App.exe）
#   {36D0C2B2-...} 2.0.7 装在 D:\autologin                      （主程序 AutoLoginNet.exe）
# 两份安装同时存在，正是「跨代串台」最容易发生的场合。
#
# 用法：先运行 Setup\Build-NativeCustomActions.ps1。

[CmdletBinding()]
param(
    [string]$Legacy1x = '{4FEDDE3C-C7B2-48BE-880A-7CB1A377E365}',
    [string]$Legacy2x = '{36D0C2B2-807C-40DF-A422-C228394C6F98}',
    [string]$Dir1x = 'C:\Program Files\AutoLoginNet App',
    [string]$Dir2x = 'D:\autologin'
)

$ErrorActionPreference = 'Stop'
$passed = 0
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Write-Host "  [通过] $name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "  [失败] $name  $detail" -ForegroundColor Red }
}

$diagDll = Join-Path $PSScriptRoot 'CustomActions.Native\bin\AutoLoginNet.SetupActions.Native.selftest.dll'
if (-not (Test-Path $diagDll)) {
    throw "找不到诊断版 DLL：$diagDll —— 请先运行 Setup\Build-NativeCustomActions.ps1"
}

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

try {
    Write-Host '==> 场景一：只有 1.0.0 登记在关联产品里（2.x 的目录仍在 D:\autologin）' -ForegroundColor Cyan
    Write-Host '    （这正是当年 1.0.0 -> 2.x 的升级形态；关键是不能把新版装进 1.x 的目录）' -ForegroundColor DarkGray
    $lines1 = Invoke-SelfTest $Legacy1x
    Show $lines1
    $path1 = Get-ChosenPath $lines1
    $via1 = Get-ChosenVia $lines1

    Write-Host ''
    Write-Host '==> 场景二：只有 2.0.7 登记在关联产品里（用户自定义安装到 D:\autologin）' -ForegroundColor Cyan
    $lines2 = Invoke-SelfTest $Legacy2x
    Show $lines2
    $path2 = Get-ChosenPath $lines2
    $via2 = Get-ChosenVia $lines2

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
Write-Host "  仅 1.0.0 关联 -> $path1   (via=$via1)"
Write-Host "  仅 2.0.7 关联 -> $path2   (via=$via2)"
Write-Host "  全新安装      -> $path3"
Write-Host ''

# 最关键的一条回归：2.x 的包绝不能落到 1.x 的目录里。
Check '从 1.0.0 升级时不会把新版装进 1.x 的目录' `
    ($path1 -ne $Dir1x) "实际 '$path1'"
Check '从 1.0.0 升级时解析到同代的 2.x 目录（D:\autologin）' `
    ($path1 -eq $Dir2x) "实际 '$path1'"
Check '该目录确实存在且含 2.x 主程序' `
    (Test-Path (Join-Path $path1 'AutoLoginNet.exe')) "缺少 $path1\AutoLoginNet.exe"

Check '从 2.0.7 升级时装回它自己的目录（用户自定义过的 D:\autologin）' `
    ($path2 -eq $Dir2x) "实际 '$path2'"
Check '该目录确实存在且含 2.x 主程序' `
    (Test-Path (Join-Path $path2 'AutoLoginNet.exe')) "缺少 $path2\AutoLoginNet.exe"
Check '2.0.7 的解析走的是关联产品（lineage）而不是全量枚举兜底' `
    ($via2 -eq 'lineage') "实际 via='$via2'"

# 同时存在两份安装时，两条产品线不能互相串台。
Check '两条产品线不会互相串台（1.0.0 的目录从未被选中）' `
    ($path1 -ne $Dir1x -and $path2 -ne $Dir1x) "1.0.0 关联='$path1' 2.0.7 关联='$path2'"

Check '全新安装时不误选任何现有旧目录' `
    ([string]::IsNullOrEmpty($path3) -or $path3 -eq '<empty>') "实际 '$path3'"

Write-Host ''
Write-Host "通过 $passed 项，失败 $failed 项"
exit ($failed -eq 0 ? 0 : 1)
