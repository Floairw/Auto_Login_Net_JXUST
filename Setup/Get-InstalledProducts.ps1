# 只读扫描：本机所有「属于我们的」MSI 已安装产品，以及它们各自的 UpgradeCode。
#
# 为什么需要这个：产品码与 UpgradeCode 都不在 ARP 表里，但
#   HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\<打包UpgradeCode>  -> 值名 = 打包产品码
# 把升级链完整登记了下来。因此可以反查：给定产品码，逐个 UpgradeCodes 子键去找它。
#
# 更深一层的动机：1.0.0 与 2.0.7 虽然显示名相同，但它们的 UpgradeCode 并不相同，
# 所以仅靠「同一个 UpgradeCode」并不能发现旧版。检测旧版必须同时覆盖
#   (a) 同 UpgradeCode 的升级链（FindRelatedProducts / 本键），
#   (b) 显示名/发布者相同但 UpgradeCode 不同的历史版本（只能靠 ARP 扫描）。
# 本脚本就是把 (b) 的实际范围摸清楚。

$ErrorActionPreference = 'Stop'

function ConvertTo-PackedGuid([string]$g) {
    $h = $g.Trim('{}').Replace('-', '').ToUpper()
    if ($h.Length -ne 32) { throw "不是合法 GUID: $g" }
    function Reverse-Bytes([string]$s) {
        $b = @()
        for ($i = 0; $i -lt $s.Length; $i += 2) { $b += $s.Substring($i, 2) }
        [array]::Reverse($b)
        return ($b -join '')
    }
    function Swap-Nibbles([string]$s) {
        $o = ''
        for ($i = 0; $i -lt $s.Length; $i += 2) { $o += $s.Substring($i + 1, 1) + $s.Substring($i, 1) }
        return $o
    }
    return (@(
        (Swap-Nibbles (Reverse-Bytes $h.Substring(0, 8))),
        (Swap-Nibbles (Reverse-Bytes $h.Substring(8, 4))),
        (Swap-Nibbles (Reverse-Bytes $h.Substring(12, 4))),
        (Swap-Nibbles $h.Substring(16, 16))
    ) -join '')
}

# 自检：转换规则必须能复现已知键名，否则后面全部结论无效
$check = ConvertTo-PackedGuid '{36D0C2B2-807C-40DF-A422-C228394C6F98}'
if ($check -ne '2B2C0D63C708FD044A222C8293C4F689') {
    Write-Host "自检失败：得到 $check" -ForegroundColor Red
    exit 1
}

Write-Host '=== 打包产品码 -> UpgradeCode 反查表 ==='
# 遍历 UpgradeCodes 下所有子键，建立 打包产品码 -> 打包UpgradeCode 的映射
$prodToUpgrade = @{}
$upgradeKeyRoot = 'HKLM:\SOFTWARE\Classes\Installer\UpgradeCodes'
foreach ($uk in Get-ChildItem $upgradeKeyRoot -ErrorAction SilentlyContinue) {
    $packedUpgrade = $uk.PSChildName
    foreach ($p in (Get-ItemProperty $uk.PSPath).PSObject.Properties) {
        if ($p.Name -like 'PS*') { continue }
        $prodToUpgrade[$p.Name] = $packedUpgrade
    }
}
Write-Host ("  已登记升级链 {0} 条，涉及产品码 {1} 个" -f (Get-ChildItem $upgradeKeyRoot).Count, $prodToUpgrade.Count)
Write-Host ''

Write-Host '=== 本机 ARP 中所有「校园网认证客户端」产品 ==='
$pat = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
$rows = @()
foreach ($k in Get-ChildItem $pat -ErrorAction SilentlyContinue) {
    $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
    if (-not $p) { continue }
    if ($p.DisplayName -notlike '*校园网*' -and $p.Publisher -notlike 'AutoLoginNet*') { continue }

    $pc = $k.PSChildName
    $packedPc = ConvertTo-PackedGuid $pc
    $packedUp = $prodToUpgrade[$packedPc]
    $upgradeReadable = ''
    if ($packedUp) {
        # 反解打包形式以得到可读 GUID（逆运算：先 nibble 翻回，再字节翻回）
        $h = $packedUp
        function Unswap([string]$s) {
            $o = ''
            for ($i = 0; $i -lt $s.Length; $i += 2) { $o += $s.Substring($i + 1, 1) + $s.Substring($i, 1) }
            return $o
        }
        function Unreverse([string]$s) {
            $b = @()
            for ($i = 0; $i -lt $s.Length; $i += 2) { $b += $s.Substring($i, 2) }
            [array]::Reverse($b)
            return ($b -join '')
        }
        $u = (Unreverse (Unswap $h.Substring(0, 8))) +
             (Unreverse (Unswap $h.Substring(8, 4))) +
             (Unreverse (Unswap $h.Substring(12, 4))) +
             (Unswap $h.Substring(16, 16))
        $upgradeReadable = '{' + $u.Substring(0, 8) + '-' + $u.Substring(8, 4) + '-' + $u.Substring(12, 4) + '-' + $u.Substring(16, 4) + '-' + $u.Substring(20, 12) + '}'
    }

    $rows += [pscustomobject]@{
        DisplayName     = $p.DisplayName
        Version         = $p.DisplayVersion
        ProductCode     = $pc
        InstallLocation = $p.InstallLocation
        InstallSource   = $p.InstallSource
        UpgradeCode     = $(if ($upgradeReadable) { $upgradeReadable } else { '(未登记在 UpgradeCodes 中)' })
    }
}

$rows | Sort-Object Version | Format-List

Write-Host '=== 说明 ==='
Write-Host '  UpgradeCode 显示「(未登记在 UpgradeCodes 中)」表示该产品所在升级链没有登记，'
Write-Host '  这通常意味着它已损坏卸载信息，或由非 MSI 方式安装/清理过。'
