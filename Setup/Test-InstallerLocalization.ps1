# 断言安装包确实已经中文化：界面串、许可协议正文、ProductLanguage。
#
# 为什么单独成文件：这几项没法靠「wix build 成功」证明，而中文界面又特别容易悄悄
# 退回英文（比如 -loc 忘了传、WixUILicenseRtf 没生效），所以由 Build-Installer.ps1
# 在打包末尾调用本脚本，任何一项不通过就直接中止打包。
# 也可以手动单独跑：pwsh -File Setup\Test-InstallerLocalization.ps1 -Msi dist\xxx.msi
#
# 三个踩过的坑，别再改回去：
#   1) COM 反射调用必须把参数包成 [object[]]，否则报 "OpenView,Sql"（参数个数不对）；
#   2) 函数里用 Write-Host 回显带单引号的 SQL 会让整次调用返回值变空，
#      于是所有断言静默 FAIL。所以查询函数只往管道输出、不回显 SQL；
#   3) 查询函数若用 `return $rows` 抛出数组，PowerShell 会把「单行结果」拆成标量，
#      $rows[0] 取到的是那个字符串本身而不是行，取值全变空。所以这里坚持
#      「函数只往管道输出，调用方一律用 @() 收集」。
param(
    [Parameter(Mandatory)][string]$Msi,
    [string]$LicenseText,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
if (-not $LicenseText) { $LicenseText = Join-Path $PSScriptRoot 'LicenseText.txt' }

$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, [object[]]@($Msi, 0))

function Invoke-MsiQuery([string]$sql) {
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, [object[]]@($sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $rec) { break }
        $n = $rec.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $rec, $null)
        $vals = @()
        for ($i = 1; $i -le $n; $i++) {
            $vals += $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, [object[]]@($i))
        }
        Write-Output (, ([string[]]$vals))
    }
}
function Get-MsiValue([string]$sql) {
    $row = @(Invoke-MsiQuery $sql) | Select-Object -First 1
    if ($null -eq $row) { return '' }
    $fields = @($row)
    if ($fields.Count -eq 0) { return '' }
    return [string]$fields[0]
}

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    # 必须 [void]：否则 Add() 的返回值会混进返回给调用方的管道输出
    [void]$checks.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
}
function Test-Cell([string]$name, [string]$sql, [string]$expected) {
    $v = Get-MsiValue $sql
    Add-Check $name ($v.Contains($expected)) $v
}

# ---------------------------------------------------------------- 1) 安装界面中文化
Test-Cell '许可协议页标题' "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='Title'" '最终用户许可协议'
Test-Cell '许可协议页说明' "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='Description'" '请认真阅读'
Test-Cell '接受协议勾选框' "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='LicenseAcceptedCheckBox'" '我接受许可协议'
Test-Cell '欢迎页标题' "SELECT Text FROM Control WHERE Dialog_='WelcomeDlg' AND Control='Title'" '欢迎使用'
Test-Cell '目标文件夹页' "SELECT Text FROM Control WHERE Dialog_='InstallDirDlg' AND Control='Title'" '目标文件夹'
Test-Cell '就绪安装页' "SELECT Text FROM Control WHERE Dialog_='VerifyReadyDlg' AND Control='Install'" '安装'
Test-Cell '完成页标题' "SELECT Text FROM Control WHERE Dialog_='ExitDialog' AND Control='Title'" '安装向导已完成'
Test-Cell '下一步按钮' "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='Next'" '下一步'
Test-Cell '取消按钮' "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='Cancel'" '取消'
Test-Cell '进度页状态标签' "SELECT Text FROM Control WHERE Dialog_='ProgressDlg' AND Control='StatusLabel'" '状态'

# ---------------------------------------------------------------- 2) 许可协议正文
$rtf = Get-MsiValue "SELECT Text FROM Control WHERE Dialog_='LicenseAgreementDlg' AND Control='LicenseText'"
Add-Check '协议正文控件存在' ($rtf.Length -gt 1000) ("RTF 长度 " + $rtf.Length)

# RTF 里中文以 \uNNNN? 形式存放，把中文按同样的方式编码后去正文里找，
# 这样能证明「页面上的字」确实来自 LicenseText.txt，而不是只看长度。
function ConvertTo-RtfText([string]$value) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $value.ToCharArray()) {
        $code = [int]$ch
        if ($ch -eq '\') { [void]$sb.Append('\\') }
        elseif ($ch -eq '{') { [void]$sb.Append('\{') }
        elseif ($ch -eq '}') { [void]$sb.Append('\}') }
        elseif ($code -gt 127) {
            if ($code -gt 32767) { $code -= 65536 }
            # 不要写成 "\u$code?"：在 "..." 里 \u 会被 PowerShell 当成转义，
            # 结果是数字和问号一起消失，只留下一个裸 \u。用 -f 拼接避开这个坑。
            [void]$sb.Append(('\u{0}?' -f $code))
        }
        else { [void]$sb.Append($ch) }
    }
    return $sb.ToString()
}

# 注意：正文在 RTF 里不是「裸文本」，每一行前面都有段落样式前缀（\ql\f1\fs20 等），
# 中文又写成 \uNNNN? 转义，所以直接拿「裸文本」去找永远找不到（旧版就是这么写的，
# 断言形同虚设）。这里把样式前缀一起放进探针，找到的才是同一样式下、逐字对应的那一行。
$sourceLines = @(Get-Content -LiteralPath $LicenseText -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
$missing = @()
foreach ($line in $sourceLines) {
    $frag = ConvertTo-RtfText $line.Trim()
    $probe = $frag.Substring(0, [Math]::Min(60, $frag.Length))
    $hit = @('\fs20 ', '\fs21 ', '\fs24 ' | Where-Object { $rtf.Contains($_ + $probe) }).Count -gt 0
    if (-not $hit) { $missing += $line.Trim().Substring(0, [Math]::Min(24, $line.Trim().Length)) }
}
Add-Check '协议正文逐行包含' ($missing.Count -eq 0) ("正文非空行 $($sourceLines.Count) 条，缺失 $($missing.Count) 条" + $(if ($missing.Count) { '：' + ($missing -join ' / ') } else { '' }))

# ---------------------------------------------------------------- 3) 产品标识
$lang = Get-MsiValue "SELECT Value FROM Property WHERE Property='ProductLanguage'"
Add-Check 'ProductLanguage' ($lang -eq '2052') $lang

if ($LogFile) {
    $lines = $checks | ForEach-Object { ('  {0}  {1}  — {2}' -f $(if ($_.Ok) { 'PASS' } else { 'FAIL' }), $_.Name, $_.Detail) }
    Set-Content -LiteralPath $LogFile -Value $lines -Encoding UTF8
}

# 只输出一个报告对象：调用方用 -Msi/LicenseText 参数拿到的就是它
Write-Output ([pscustomobject]@{
    Msi      = $Msi
    Checks   = $checks.ToArray()
    Failures = @($checks | Where-Object { -not $_.Ok })
})
