# 把产品码/UpgradeCode 转成注册表里那种「打包」形式，便于在
# HKLM\SOFTWARE\Classes\Installer\{Products|UpgradeCodes}\<打包串> 下查表。
#
# 规则（已由两组真实 Products 子键名反推验证）：
#   1) 按 8-4-4-16 切成四组；
#   2) 前 3 组内部的「字节」整体倒序（组 1 是 4 字节倒序，组 2/3 是 2 字节倒序）
#      ——注意是字节倒序，不是字符倒序；**第 4 组不倒序**；
#   3) 然后每个字节内部交换高/低 nibble。
# 等价说法：先把 GUID 的前 3 组做「内存小端布局」（这正是 Windows 存储 GUID
# 的老规矩），第 4 组保持原样，最后把每个字节的 nibble 翻过来。
#
# 脚本自带自检：用两条已知的 Products 键名反推，规则不对就直接失败退出，
# 绝不输出可疑结论。
param(
    [string[]]$Guid = @()
)

$ErrorActionPreference = 'Stop'

function ConvertTo-PackedGuid([string]$g) {
    $h = $g.Trim('{}').Replace('-', '').ToUpper()
    if ($h.Length -ne 32) { throw "不是合法 GUID: $g" }

    # 把一段 hex 按字节倒序
    function Reverse-Bytes([string]$s) {
        $bytes = @()
        for ($i = 0; $i -lt $s.Length; $i += 2) { $bytes += $s.Substring($i, 2) }
        [array]::Reverse($bytes)
        return ($bytes -join '')
    }

    # 把一段 hex 逐字节交换 nibble
    function Swap-Nibbles([string]$s) {
        $o = ''
        for ($i = 0; $i -lt $s.Length; $i += 2) { $o += $s.Substring($i + 1, 1) + $s.Substring($i, 1) }
        return $o
    }

    $parts = @(
        (Swap-Nibbles (Reverse-Bytes $h.Substring(0, 8))),
        (Swap-Nibbles (Reverse-Bytes $h.Substring(8, 4))),
        (Swap-Nibbles (Reverse-Bytes $h.Substring(12, 4))),
        (Swap-Nibbles $h.Substring(16, 16))
    )
    return ($parts -join '')
}

# 自检样本：产品码 -> 已知 Products 子键名
$samples = @(
    @{ Guid = '{36D0C2B2-807C-40DF-A422-C228394C6F98}'; Expect = '2B2C0D63C708FD044A222C8293C4F689'; Note = '2.0.7 产品码' },
    @{ Guid = '{4FEDDE3C-C7B2-48BE-880A-7CB1A377E365}'; Expect = 'C3EDDEF42B7CEB8488A0C71B3A773E56'; Note = '1.0.0 产品码' }
)

Write-Host '=== 转换规则自检 ==='
$allOk = $true
foreach ($s in $samples) {
    $got = ConvertTo-PackedGuid $s.Guid
    $ok = $got -eq $s.Expect
    if (-not $ok) { $allOk = $false }
    Write-Host ("  {0}  {1}" -f $s.Note, $s.Guid)
    Write-Host ("    得到 {0}" -f $got)
    Write-Host ("    期望 {0}  {1}" -f $s.Expect, $(if ($ok) { '[通过]' } else { '[失败]' }))
}
if (-not $allOk) {
    Write-Host ''
    Write-Host '自检未通过 —— 转换规则不正确，以下结果不可信。' -ForegroundColor Red
    exit 1
}
Write-Host '  规则自检通过。'
Write-Host ''

if ($Guid.Count -eq 0) {
    $Guid = @(
        '{80D113DF-2411-D8B9-A63B-B0FCC546EF9B}'   # 当前安装包的 UpgradeCode
    )
}

foreach ($g in $Guid) {
    $packed = ConvertTo-PackedGuid $g
    Write-Host "=== $g ==="
    Write-Host "  打包形式 = $packed"
    foreach ($hive in @('UpgradeCodes', 'Products')) {
        $key = "HKLM:\SOFTWARE\Classes\Installer\$hive\$packed"
        $exists = Test-Path $key
        Write-Host "  $hive 下存在 = $exists"
        if ($exists) {
            (Get-ItemProperty $key).PSObject.Properties |
                Where-Object { $_.Name -notlike 'PS*' } |
                ForEach-Object {
                    $v = $_.Value
                    if ($v -is [byte[]]) { $v = '(二进制 ' + $v.Length + ' 字节: ' + (($v | ForEach-Object { $_.ToString('X2') }) -join '') + ')' }
                    Write-Host ("      {0} = {1}" -f $_.Name, $v)
                }
        }
    }
    Write-Host ''
}
