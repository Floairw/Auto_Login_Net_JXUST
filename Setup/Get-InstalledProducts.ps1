# 只读扫描：本机所有「属于我们的」MSI 已安装产品、它们的 UpgradeCode，
# 以及每个产品属于哪一代（1.x / 2.x）和它真实的安装目录。
#
# 为什么需要这个：
#   产品码与 UpgradeCode 都不在 ARP 表里，但
#   HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\<打包UpgradeCode>  -> 值名 = 打包产品码
#   把升级链完整登记了下来。因此可以反查：给定产品码，逐个 UpgradeCodes 子键去找它。
#
# 更深一层的动机：1.0.0 与 2.0.7 虽然显示名相同，但它们的 UpgradeCode 并不相同，
# 所以仅靠「同一个 UpgradeCode」并不能发现旧版。检测旧版必须同时覆盖
#   (a) 同 UpgradeCode 的升级链（FindRelatedProducts / 本键），
#   (b) 显示名/发布者相同但 UpgradeCode 不同的历史版本（只能靠 ARP 扫描）。
# 本脚本就是把 (b) 的实际范围摸清楚，并顺带给出每一代次的真实目录。
#
# 为什么真实安装目录不能靠 ARP 的 InstallLocation：
#   历史包（1.0.0 / 2.0.7）都没有写 ARPINSTALLLOCATION，ARP 里该值是空的；而用户可以把
#   程序装到任意目录，目录名也猜不出来（本机 2.0.7 的 MSI 记录成 AutoLoginNet，实际在
#   D:\autologin）。可靠做法与生产代码 Setup\CustomActions.Native\UpgradeLocation.cpp 完全
#   一致：读该产品**缓存 MSI** 的 File 表找到主程序，取出它所属组件，再用
#   MsiGetComponentPath 求该组件的实际落地路径 —— 这是安装时真正用到的路径。
#
# 用法：
#   直接运行（打印第 3 节报告）： pwsh -NoProfile -File Setup\Get-InstalledProducts.ps1
#   被其它脚本复用（只定义函数）： . Setup\Get-InstalledProducts.ps1 -Library
#                                   然后调用 Get-InstalledAutoLoginNetProducts
#
# 为什么用 P/Invoke 而不是 COM：
#   Installer 的 ProductInfo / ComponentPath 是「参数化属性」，PowerShell 直接调用会
#   DISP_E_MEMBERNOTFOUND 或参数不匹配（实测 ComponentPath 用 3 个参数调用恒返回空）。
#   P/Invoke 用的就是 msi.dll 的原始签名，与生产 C++ 代码同源，行为不会漂移。
#   （只有「打开缓存 MSI 读表」用 COM，因为那部分 API 就是 COM 数据库对象。）

[CmdletBinding()]
param(
    # 只定义函数、不输出报告，供其它脚本 dot-source 复用。
    [switch]$Library
)

$ErrorActionPreference = 'Stop'

# 旧版主程序文件名，顺序与生产代码 kAppExecutableNames 一致：下标越小越新。
$script:AppExecutableNames = @(
    'AutoLoginNet.exe',      # 0 = 2.x
    'AutoLoginNet.App.exe'   # 1 = 1.x
)
$script:AppKindNames = @('2x', '1x')
$script:MsiIdentifierQuote = [char]0x60   # MSI SQL 的标识符引号

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

function ConvertFrom-PackedGuid([string]$packed) {
    if ($packed.Length -ne 32) { throw "不是合法的打包 GUID: $packed" }
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
    $h = $packed.ToUpper()
    $u = (Unreverse (Unswap $h.Substring(0, 8))) +
         (Unreverse (Unswap $h.Substring(8, 4))) +
         (Unreverse (Unswap $h.Substring(12, 4))) +
         (Unswap $h.Substring(16, 16))
    return '{' + $u.Substring(0, 8) + '-' + $u.Substring(8, 4) + '-' + $u.Substring(12, 4) + '-' + $u.Substring(16, 4) + '-' + $u.Substring(20, 12) + '}'
}

# ---------------------------------------------------------------- 原生互操作
# 这一层就是生产 C++ 里用到的三个 msi.dll 函数，签名保持一致。
function Initialize-MsiInterop {
    if ('AlnMsiInterop' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class AlnMsiInterop
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetComponentPathW(string product, string component, StringBuilder path, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductStateW(string product);

    public static string ProductInfo(string product, string property)
    {
        var buffer = new StringBuilder(1024);
        uint length = (uint)buffer.Capacity;
        uint status = MsiGetProductInfoW(product, property, buffer, ref length);
        return status == 0 ? buffer.ToString() : null;
    }

    public static int ProductState(string product)
    {
        return MsiQueryProductStateW(product);
    }

    // 返回 "state|path"；state 3 = INSTALLSTATE_LOCAL（确实装到本地），-2 = INSTALLSTATE_UNKNOWN。
    public static string ComponentPath(string product, string component)
    {
        var buffer = new StringBuilder(2048);
        uint length = (uint)buffer.Capacity;
        int state = (int)MsiGetComponentPathW(product, component, buffer, ref length);
        return state + "|" + buffer.ToString();
    }
}
'@
}

function Get-MsiProductInfo([string]$productCode, [string]$property) {
    Initialize-MsiInterop
    return [AlnMsiInterop]::ProductInfo($productCode, $property)
}

function Get-MsiProductState([string]$productCode) {
    Initialize-MsiInterop
    return [AlnMsiInterop]::ProductState($productCode)
}

function Get-MsiComponentPath([string]$productCode, [string]$componentId) {
    Initialize-MsiInterop
    return [AlnMsiInterop]::ComponentPath($productCode, $componentId)
}

# ---------------------------------------------------------------- 读缓存 MSI 的表
function Open-InstallerDatabase([string]$msiPath) {
    $installerType = [type]::GetTypeFromProgID('WindowsInstaller.Installer')
    if (-not $installerType) { throw '找不到 WindowsInstaller.Installer COM 组件' }
    $installer = [Activator]::CreateInstance($installerType)
    return $installerType.InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msiPath, 0))
}

function Get-MsiTableRows($database, [string]$sql, [int]$columnCount) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($sql))
    $viewType = $view.GetType()
    # 注意：.NET/COM 方法调用返回 null 时，PowerShell 会把 null 写进管道；
    # 不吞掉它，函数的返回值里就会多出一个 $null 元素，调用方遍历时会炸（Cannot index into a null array）。
    [void]$viewType.InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rows = New-Object System.Collections.ArrayList
    while ($true) {
        $record = $viewType.InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }
        $recordType = $record.GetType()
        $values = @()
        for ($column = 1; $column -le $columnCount; $column++) {
            $values += [string]$recordType.InvokeMember('StringData', 'GetProperty', $null, $record, @($column))
        }
        [void]$rows.Add($values)
    }
    return ,$rows.ToArray()
}

# 读某个已装产品的缓存 MSI：判断它属于哪一代、主程序装在哪。
# 返回对象（字段可能为 $null，表示查不出来 —— 调用方不要把它当成「没装」）。
function Get-ProductInstallInfo([string]$productCode) {
    $info = [pscustomobject]@{
        AppKind           = $null
        PrimaryExecutable = $null
        InstallDirectory  = $null
        CachedMsi         = $null
        ComponentId       = $null
    }

    $info.CachedMsi = Get-MsiProductInfo $productCode 'LocalPackage'
    if (-not $info.CachedMsi -or -not (Test-Path -LiteralPath $info.CachedMsi)) { return $info }

    try { $database = Open-InstallerDatabase $info.CachedMsi } catch { return $info }

    $q = $script:MsiIdentifierQuote
    $fileSql = 'SELECT ' + $q + 'FileName' + $q + ',' + $q + 'Component_' + $q + ' FROM ' + $q + 'File' + $q
    $componentSql = 'SELECT ' + $q + 'Component' + $q + ',' + $q + 'ComponentId' + $q + ' FROM ' + $q + 'Component' + $q

    $componentKey = $null
    foreach ($row in (Get-MsiTableRows $database $fileSql 2)) {
        # FileName 可能带短名前缀，形如 "bjlcd3vk.exe|AutoLoginNet.exe"
        # （生产 C++ 的 StripShortNamePrefix 处理的就是它）。
        $fileName = ([string]$row[0] -split '\|')[-1]
        for ($kind = 0; $kind -lt $script:AppExecutableNames.Count; $kind++) {
            if ($fileName -ine $script:AppExecutableNames[$kind]) { continue }
            if (-not $info.AppKind) {
                $info.AppKind = $script:AppKindNames[$kind]
                $info.PrimaryExecutable = $fileName
                $componentKey = [string]$row[1]
            }
            break
        }
        if ($info.AppKind) { break }
    }

    if (-not $componentKey) { return $info }

    foreach ($row in (Get-MsiTableRows $database $componentSql 2)) {
        if ([string]$row[0] -ieq $componentKey) { $info.ComponentId = [string]$row[1]; break }
    }
    if (-not $info.ComponentId) { return $info }

    # 与生产代码一致：先试裸产品码；实测「产品码 + G」的写法只适用于上下文本身是 SID 的情形。
    foreach ($context in @($productCode, ($productCode + 'G'))) {
        $componentPath = Get-MsiComponentPath $context $info.ComponentId
        $pieces = $componentPath -split '\|', 2
        if ($pieces.Count -ne 2) { continue }
        $state = 0
        [void][int]::TryParse($pieces[0], [ref]$state)
        if ($state -ne 3) { continue }          # 3 = INSTALLSTATE_LOCAL
        if (-not $pieces[1]) { continue }
        $info.InstallDirectory = Split-Path -Parent $pieces[1]
        break
    }

    return $info
}

# ---------------------------------------------------------------- 对外主函数
# 返回本机所有「属于我们的」MSI 产品（含 1.x / 2.x 代次与真实目录）。
function Get-InstalledAutoLoginNetProducts {
    Initialize-MsiInterop

    # 打包产品码 -> 打包 UpgradeCode 反查表
    $prodToUpgrade = @{}
    $upgradeKeyRoot = 'HKLM:\SOFTWARE\Classes\Installer\UpgradeCodes'
    foreach ($upgradeKey in Get-ChildItem $upgradeKeyRoot -ErrorAction SilentlyContinue) {
        $packedUpgrade = $upgradeKey.PSChildName
        foreach ($property in (Get-ItemProperty $upgradeKey.PSPath -ErrorAction SilentlyContinue).PSObject.Properties) {
            if ($property.Name -like 'PS*') { continue }
            $prodToUpgrade[$property.Name] = $packedUpgrade
        }
    }

    $rows = @()
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $uninstallRoots) {
        foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            if ($props.DisplayName -notlike '*校园网*' -and $props.Publisher -notlike 'AutoLoginNet*') { continue }

            # 只认 MSI 产品码（Uninstall 下还有非 MSI 安装项）
            $productCode = $key.PSChildName
            if ($productCode -notmatch '^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$') { continue }
            if (@($rows | Where-Object { $_.ProductCode -ieq $productCode }).Count -gt 0) { continue }

            $packedProduct = ConvertTo-PackedGuid $productCode
            $packedUpgrade = $prodToUpgrade[$packedProduct]
            $upgradeReadable = if ($packedUpgrade) { ConvertFrom-PackedGuid $packedUpgrade } else { $null }

            $install = Get-ProductInstallInfo $productCode
            $installDirectory = $install.InstallDirectory
            if (-not $installDirectory -and $props.InstallLocation) {
                $installDirectory = ([string]$props.InstallLocation).TrimEnd('\')
            }

            $rows += [pscustomobject]@{
                DisplayName       = $props.DisplayName
                Version           = $props.DisplayVersion
                ProductCode       = $productCode
                InstallLocation   = $props.InstallLocation
                InstallSource     = $props.InstallSource
                UpgradeCode       = $(if ($upgradeReadable) { $upgradeReadable } else { '(未登记在 UpgradeCodes 中)' })
                AppKind           = $install.AppKind
                PrimaryExecutable = $install.PrimaryExecutable
                InstallDirectory  = $installDirectory
                CachedMsi         = $install.CachedMsi
            }
        }
    }

    return @($rows | Sort-Object Version)
}

# 自检：转换规则必须能复现已知键名，否则后面全部结论无效
$check = ConvertTo-PackedGuid '{36D0C2B2-807C-40DF-A422-C228394C6F98}'
if ($check -ne '2B2C0D63C708FD044A222C8293C4F689') {
    Write-Host "自检失败：得到 $check" -ForegroundColor Red
    exit 1
}

# 被其它脚本 dot-source 复用时到此为止，不打印报告。
if ($Library) { return }

# ---------------------------------------------------------------- 报告
$products = @(Get-InstalledAutoLoginNetProducts)
Write-Host '=== 本机 ARP 中所有「校园网认证客户端」产品 ==='
if ($products.Count -eq 0) {
    Write-Host '  （本机没有已安装的产品）'
}
foreach ($p in $products) {
    Write-Host ''
    Write-Host ("  显示名       : {0}" -f $p.DisplayName)
    Write-Host ("  版本         : {0}" -f $p.Version)
    Write-Host ("  代次         : {0}" -f $(if ($p.AppKind) { $p.AppKind } else { '(无法判定：缓存 MSI 不可读)' }))
    Write-Host ("  产品码       : {0}" -f $p.ProductCode)
    Write-Host ("  真实安装目录 : {0}" -f $(if ($p.InstallDirectory) { $p.InstallDirectory } else { '(未知)' }))
    Write-Host ("  ARP 记录目录 : {0}" -f $p.InstallLocation)
    Write-Host ("  缓存 MSI     : {0}" -f $p.CachedMsi)
    Write-Host ("  UpgradeCode  : {0}" -f $p.UpgradeCode)
}

Write-Host ''
Write-Host '=== 说明 ==='
Write-Host '  「代次」由缓存 MSI 的 File 表判定：含 AutoLoginNet.exe = 2.x，含 AutoLoginNet.App.exe = 1.x。'
Write-Host '  「真实安装目录」由 MsiGetComponentPath 求主程序组件的落地路径得到，与生产自定义动作同源。'
Write-Host '  UpgradeCode 显示「(未登记在 UpgradeCodes 中)」表示该产品所在升级链没有登记，'
Write-Host '  这通常意味着它已损坏卸载信息，或由非 MSI 方式安装/清理过。'
