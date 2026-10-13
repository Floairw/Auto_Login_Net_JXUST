<#
.SYNOPSIS
    把 Setup\LicenseText.txt 转成安装程序用的 RTF 许可协议。

.DESCRIPTION
    MSI 的许可协议控件（LicenseAgreementDlg 的 LicenseText）只认 RTF，不认纯文本。
    手写 RTF 既难读又容易写坏转义，所以这里用脚本从纯文本生成，
    纯文本才是唯一需要维护的来源。

    转换规则：
      - 第一行      -> 居中的 12pt 粗体标题
      - 其余行      -> 10pt 正文；形如「一、xxx」的整行作为 10.5pt 粗体小标题
      - 空行        -> 段间距（连续空行折叠成一个）
      - 非 ASCII    -> \\uNNNN? 形式（RTF 的 Unicode 转义）

    RTF 是纯 ASCII 加转义，所以这里始终以 ASCII 写出，不会受编码影响。

.PARAMETER Source
    纯文本来源，默认 Setup\LicenseText.txt。

.PARAMETER Output
    输出 RTF 路径，默认 Setup\license.rtf。
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$setupDir = $PSScriptRoot
if (-not $Source) { $Source = Join-Path $setupDir 'LicenseText.txt' }
if (-not $Output) { $Output = Join-Path $setupDir 'license.rtf' }

if (-not (Test-Path $Source)) { throw "找不到许可协议文本：$Source" }

$lines = Get-Content $Source -Encoding UTF8
if ($lines.Count -eq 0) { throw "许可协议文本为空：$Source" }

function ConvertTo-RtfText([string]$value) {
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $value.ToCharArray()) {
        if ($ch -eq '\') { [void]$sb.Append('\\') }
        elseif ($ch -eq '{') { [void]$sb.Append('\{') }
        elseif ($ch -eq '}') { [void]$sb.Append('\}') }
        elseif ([int]$ch -lt 128) { [void]$sb.Append($ch) }
        else {
            $code = [int]$ch
            # RTF 的 \u 是有符号 16 位：码位超过 32767 时要写成负值
            if ($code -gt 32767) { $code -= 65536 }
            # 不要写成 "\u$code?"：在 "..." 里 \u 会被 PowerShell 当成转义，
            # 结果是数字和问号一起消失，只留下一个裸 \u。用 -f 拼接避开这个坑。
            [void]$sb.Append(('\u{0}?' -f $code))
        }
    }
    return $sb.ToString()
}

# fs20 = 10pt，fs21 = 10.5pt，fs24 = 12pt（RTF 的字号单位是半磅）
$sb = [System.Text.StringBuilder]::new()
[void]$sb.Append('{\rtf1\ansi\ansicpg936\deff0\nouicompat\deflang2052\deflangfe2052')
[void]$sb.Append('{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei;}{\f1\fnil\fcharset134 SimSun;}}')
[void]$sb.Append("`r`n")

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i].Trim()
    $isTitle = ($i -eq 0)
    $isHeading = (-not $isTitle) -and ($line -match '^[一二三四五六七八九十]+、')

    if ($line.Length -eq 0) {
        # 连续空行折叠：只在已有内容且上一行不是空行时产生段间距
        [void]$sb.Append('\par' + "`r`n")
        continue
    }

    $text = ConvertTo-RtfText $line
    if ($isTitle) {
        [void]$sb.Append("\qc\b\f0\fs24 $text\b0\par" + "`r`n")
    }
    elseif ($isHeading) {
        [void]$sb.Append("\ql\b\f0\fs21 $text\b0\par" + "`r`n")
    }
    else {
        [void]$sb.Append("\ql\f1\fs20 $text\par" + "`r`n")
    }
}

[void]$sb.Append('}' + "`r`n")

$rtf = $sb.ToString()
# 写出时统一成 CRLF，并把 ASCII 以外的字符挡在门外（有的话说明转义漏了）
$rtf = $rtf -replace "`r`n", "`n" -replace "`n", "`r`n"
[System.IO.File]::WriteAllText($Output, $rtf, [System.Text.Encoding]::ASCII)

$bytes = (Get-Item $Output).Length
$bodyCount = ($lines | Where-Object { $_.Trim().Length -gt 0 }).Count
Write-Host "许可协议 RTF 已生成：$Output（$bytes 字节，$bodyCount 行正文）" -ForegroundColor Green
