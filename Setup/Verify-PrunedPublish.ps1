<#
.SYNOPSIS
    在真实运行中验证「裁剪后的发布目录」仍然可用。

.DESCRIPTION
    删除文件是唯一一类「静态分析无法证明安全」的优化，所以必须有实机验证：
    启动应用 → 切遍四个页面 → 触发网络检测与日志读取 → 确认无崩溃、
    无未处理异常、无「找不到文件/程序集」类错误，然后关闭进程。

    数据目录隔离到临时目录（通过环境变量 AUTOLOGIN_DATA_DIR 覆盖，见 AppPaths）：
    先塞一份多分类的种子日志，既能验证「日志页真的从磁盘读文件」这条路径，
    又不会污染真实 %AppData%；顺带避开了一个环境陷阱——真实日志积累到上千条时，
    日志列表页的元素数量会让 UIA 的一次 FindAll 调用超时，验证会假失败。

.PARAMETER PublishDir
    被验证的发布目录。

.PARAMETER TimeoutSeconds
    每步 UI 操作的等待上限。

.OUTPUTS
    PSCustomObject：Passed / Checks / Failures / Diagnostics。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    [int]$TimeoutSeconds = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$checks = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$diagnostics = [System.Collections.Generic.List[string]]::new()

# 注意：Add-Check 不返回值。它会往调用方的输出流里塞 $true/$false（导致脚本的返回值
# 变成一个对象数组，调用方拿 $result.Checks 就报 "property cannot be found"），
# 所以这里只记录、不返回。
function Add-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $checks.Add("PASS  $Name" + ($(if ($Detail) { "  — $Detail" } else { '' })))
    }
    else {
        $failures.Add("FAIL  $Name" + ($(if ($Detail) { "  — $Detail" } else { '' })))
    }
}

$exe = Join-Path $PublishDir 'AutoLoginNet.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "发布目录里没有 AutoLoginNet.exe：$PublishDir"
}

# ------------------------------------------------ 隔离的数据目录 + 种子日志
# 应用通过 AUTOLOGIN_DATA_DIR 认识这个目录；验证结束后恢复原值并删掉临时目录。
$previousDataDir = $env:AUTOLOGIN_DATA_DIR
$verifyDataDir = Join-Path ([System.IO.Path]::GetTempPath()) ('autologin-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verifyDataDir -Force | Out-Null

$seedSpecs = @(
    'INFO|网络|开始探测网络环境',
    'INFO|网络|认证服务器可达（18 ms）',
    'INFO|网络|外网连通性正常',
    'INFO|登录|开始提交认证请求',
    'INFO|登录|认证成功：已可上网',
    'WARN|登录|上次登录结果未知，按未登录处理',
    'INFO|配置|读取 appsettings.json 成功',
    'INFO|配置|保存界面设置成功',
    'INFO|开机自启|登录时自动启动已开启',
    'WARN|开机自启|计划任务不存在，已跳过',
    'INFO|网络|DNS 延迟测量完成',
    'INFO|网络|当前上网网卡：以太网'
)
$seedCount = $seedSpecs.Count * 2
$seedBase = (Get-Date).AddMinutes(-$seedCount)
$seedLines = [System.Collections.Generic.List[string]]::new()
for ($round = 0; $round -lt 2; $round++) {
    for ($i = 0; $i -lt $seedSpecs.Count; $i++) {
        $parts = $seedSpecs[$i].Split('|')
        $ts = $seedBase.AddMinutes(($round * $seedSpecs.Count) + $i).ToString('yyyy-MM-dd HH:mm:ss')
        $seedLines.Add(('{0} [{1}] ({2}) {3}' -f $ts, $parts[0], $parts[1], $parts[2]))
    }
}
$seedLog = Join-Path $verifyDataDir 'autologin.log'
Set-Content -LiteralPath $seedLog -Value $seedLines -Encoding utf8

function Remove-VerifyDataDir {
    $env:AUTOLOGIN_DATA_DIR = $previousDataDir
    if ($verifyDataDir -and (Test-Path -LiteralPath $verifyDataDir)) {
        try { Remove-Item -LiteralPath $verifyDataDir -Recurse -Force } catch { }
    }
}

$env:AUTOLOGIN_DATA_DIR = $verifyDataDir
$diagnostics.Add("隔离数据目录：$verifyDataDir（种子日志 $seedCount 条）")

# 记录启动前的事件日志位置，用于事后判断有没有新的应用崩溃记录
$evtLog = 'Application'
$startTime = Get-Date

$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 10

Add-Check '进程存活（未在启动阶段崩溃）' (-not $proc.HasExited) "pid=$($proc.Id)"
if ($proc.HasExited) {
    $diagnostics.Add('进程已退出，退出码=' + $proc.ExitCode)
    Remove-VerifyDataDir
    return [PSCustomObject]@{ Passed = $false; Checks = $checks; Failures = $failures; Diagnostics = $diagnostics }
}

# ------------------------------------------------ UI 元素定位
$root = [System.Windows.Automation.AutomationElement]::RootElement
$window = $null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($window) { break }
    Start-Sleep -Milliseconds 400
}

Add-Check '主窗口出现（Windows App SDK 自举成功）' ($null -ne $window)
if ($null -eq $window) {
    try { $proc.Kill() } catch { }
    Remove-VerifyDataDir
    return [PSCustomObject]@{ Passed = $false; Checks = $checks; Failures = $failures; Diagnostics = $diagnostics }
}

function Get-Named([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Select-Nav([string]$name) {
    $el = Get-Named $name
    if (-not $el) { return $false }
    $p = $null
    foreach ($pid2 in @(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [System.Windows.Automation.InvokePattern]::Pattern)) {
        if ($el.TryGetCurrentPattern($pid2, [ref]$p)) { break }
    }
    if (-not $p) { return $false }
    if ($p -is [System.Windows.Automation.SelectionItemPattern]) { $p.Select() } else { $p.Invoke() }
    Start-Sleep -Milliseconds 1500
    return $true
}

function Get-AllText {
    $tc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($t in $texts) { $out.Add($t.Current.Name) }
    return $out
}

# ------------------------------------------------ 逐页验证
$pages = @(
    @{ Nav = '首页';       Must = @('校园网认证客户端') },
    @{ Nav = '网络工具';   Must = @() },
    @{ Nav = '运行日志';   Must = @('条') },
    @{ Nav = '设置';       Must = @() }
)

foreach ($page in $pages) {
    $selected = Select-Nav $page.Nav
    Add-Check "可切换到「$($page.Nav)」" $selected
    if (-not $selected) { continue }

    Start-Sleep -Seconds 1

    # 页面内容检查：$page.Must 里的文案必须真的出现在该页（用来抓「页面空白但没崩」）
    if (@($page.Must).Count -gt 0) {
        $texts = @(Get-AllText)
        foreach ($needle in $page.Must) {
            $found = @($texts | Where-Object { $_ -like "*$needle*" }).Count -gt 0
            Add-Check "「$($page.Nav)」页面含「$needle」" $found
        }
    }

    if ($page.Nav -eq '运行日志') {
        # 日志页面必须真的从磁盘读到内容（这是曾经出过问题的地方）
        $status = (Get-AllText) | Where-Object { $_ -match '显示 \d+ / 共 \d+ 条' } | Select-Object -First 1
        $ok = $false
        $detail = '未找到状态文本'
        if ($status -and $status -match '共 (\d+) 条') {
            $total = [int]$Matches[1]
            $ok = $total -gt 0
            $detail = "共 $total 条"
        }
        Add-Check '日志页面从磁盘读到记录' $ok $detail
        if ($status -and $status -match '共 (\d+) 条') {
            # 种子日志只存在于磁盘上：条数不低于种子数量，才说明读的是文件而不是内存缓冲
            Add-Check '日志页读到了种子日志（走磁盘路径）' ([int]$Matches[1] -ge $seedCount) ("种子 $seedCount 条")
        }

        # 分类多样性：只出现登录/凭据说明只读了内存缓冲
        $listCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $items = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)
        $cats = @{}
        foreach ($i in $items) {
            if ($i.Current.Name -match 'Category = ([^,]+),') {
                $c = $Matches[1].Trim()
                $cats[$c] = 1
            }
        }
        Add-Check '日志分类多于两类（非仅内存缓冲）' ($cats.Count -gt 2) ("分类：" + (($cats.Keys | Sort-Object) -join '/'))
    }
}

# ------------------------------------------------ 触发一次网络检测（走 HttpClient / DNS 路径）
# 按钮名字取自 AutoLoginNet.App\Pages\NetworkPage.xaml 的真实 Content。
$invoked = $false
if (Select-Nav '网络工具') {
    foreach ($btnName in @('测试全部', '开始测试', '刷新网卡与 DNS', '刷新状态')) {
        $btn = Get-Named $btnName
        if ($btn) {
            $p = $null
            if ($btn.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) {
                $p.Invoke()
                Start-Sleep -Seconds 6
                Add-Check "可触发「$btnName」" $true
                $invoked = $true
            }
            break
        }
    }
}
if (-not $invoked) {
    Add-Check '可触发网络检测按钮' $false '未找到 NetworkPage 上的任一已知按钮'
}

# ------------------------------------------------ 崩溃检测
if ($proc.HasExited) {
    Add-Check '全程未崩溃' $false "进程在验证过程中退出，退出码=$($proc.ExitCode)"
}
else {
    Add-Check '全程未崩溃' $true
    try {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
    }
    catch { try { $proc.Kill() } catch { } }
}

try {
    $newErrors = Get-WinEvent -FilterHashtable @{
        LogName   = $evtLog
        StartTime = $startTime
        Level     = 2
    } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'AutoLoginNet' }

    if ($newErrors) {
        foreach ($e in $newErrors) {
            $failures.Add("FAIL  事件日志出现错误：$($e.ProviderName) / $($e.Id)")
            $diagnostics.Add($e.Message)
        }
    }
    else {
        $checks.Add('PASS  事件日志无新增 AutoLoginNet 错误')
    }
}
catch {
    $diagnostics.Add("事件日志读取失败：$($_.Exception.Message)")
}

Remove-VerifyDataDir

[PSCustomObject]@{
    Passed      = ($failures.Count -eq 0)
    Checks      = $checks
    Failures    = $failures
    Diagnostics = $diagnostics
}
