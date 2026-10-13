# 用 WindowsInstaller COM 直接打开本机缓存的历史 MSI，看它到底把主程序装到哪个目录。
# 这是排查「DefaultDir 是短名」这类问题的唯一可靠手段：直接把表打出来。
param(
    [string[]]$MsiPath = @(),
    [string[]]$ProductCode = @(
        '{4FEDDE3C-C7B2-48BE-880A-7CB1A377E365}',  # 1.0.0
        '{36D0C2B2-807C-40DF-A422-C228394C6F98}'   # 2.0.7
    )
)

# SQL 里的标识符要加反引号；用单引号字符串避免 PowerShell 把反引号当转义符。
$q = [char]0x60   # `

function Select-One($database, $sql) {
    $view = $database.OpenView($sql)
    $view.Execute()
    $rec = $view.Fetch()
    if ($null -eq $rec) { return $null }
    return $rec.StringData(1)
}

$installer = New-Object -ComObject WindowsInstaller.Installer

# 组装要检查的 MSI：显式传入的路径优先，否则按产品码枚举已安装产品的缓存 MSI。
#
# 关于枚举：ProductInfo 是 Installer 的参数化属性，不是方法。
# 写成 $installer.ProductInfo($code,'LocalPackage') 会报「找不到成员」，
# 直接传缓存 MSI 路径最省事（本机缓存路径就是 C:\Windows\Installer\{ProductCode}）。
$targets = @()
foreach ($p in $MsiPath) { $targets += [pscustomobject]@{ Label = $p; Path = $p } }

if ($targets.Count -eq 0) {
    foreach ($code in $ProductCode) {
        $cachePath = Join-Path "$env:WINDIR\Installer" $code
        if (Test-Path -LiteralPath $cachePath) {
            $targets += [pscustomobject]@{ Label = "$code (缓存)"; Path = $cachePath }
        } else {
            Write-Host "================ $code ================"
            Write-Host "  (缓存 MSI 不存在：$cachePath)"
            Write-Host ''
        }
    }
}

foreach ($t in $targets) {
    $code = $t.Label
    $package = $t.Path
    Write-Host "================ $code ================"
    Write-Host "MSI = $package"
    if (-not (Test-Path -LiteralPath $package)) { Write-Host "  (文件不存在)"; Write-Host ''; continue }

    $db = $installer.OpenDatabase($package, 0)

    Write-Host "--- Property 表（目录 + 身份）---"
    $sql = 'SELECT ' + $q + 'Property' + $q + ',' + $q + 'Value' + $q + ' FROM ' + $q + 'Property' + $q +
           ' WHERE ' + $q + 'Property' + $q + "='INSTALLFOLDER' OR " + $q + 'Property' + $q + "='WIXUI_INSTALLDIR' OR " +
           $q + 'Property' + $q + "='ARPINSTALLLOCATION' OR " + $q + 'Property' + $q + "='ALLUSERS' OR " +
           $q + 'Property' + $q + "='ProductCode' OR " + $q + 'Property' + $q + "='UpgradeCode' OR " +
           $q + 'Property' + $q + "='ProductName' OR " + $q + 'Property' + $q + "='ProductVersion'"
    $view = $db.OpenView($sql)
    $view.Execute()
    while ($true) {
        $rec = $view.Fetch()
        if ($null -eq $rec) { break }
        Write-Host ("    {0} = {1}" -f $rec.StringData(1), $rec.StringData(2))
    }

    Write-Host "--- Upgrade 表（升级链）---"
    $viewU = $db.OpenView('SELECT ' + $q + 'UpgradeCode' + $q + ',' + $q + 'VersionMin' + $q + ',' + $q + 'VersionMax' + $q + ',' + $q + 'Attributes' + $q + ',' + $q + 'ActionProperty' + $q + ' FROM ' + $q + 'Upgrade' + $q)
    $viewU.Execute()
    $anyU = $false
    while ($true) {
        $rec = $viewU.Fetch()
        if ($null -eq $rec) { break }
        $anyU = $true
        Write-Host ("    UpgradeCode={0} min='{1}' max='{2}' attr={3} action={4}" -f $rec.StringData(1), $rec.StringData(2), $rec.StringData(3), $rec.StringData(4), $rec.StringData(5))
    }
    if (-not $anyU) { Write-Host '    (无 Upgrade 表行)' }

    Write-Host "--- Directory 表（前 30 行）---"
    $view2 = $db.OpenView('SELECT ' + $q + 'Directory' + $q + ',' + $q + 'Directory_Parent' + $q + ',' + $q + 'DefaultDir' + $q + ' FROM ' + $q + 'Directory' + $q)
    $view2.Execute()
    $n = 0
    while ($n -lt 30) {
        $rec = $view2.Fetch()
        if ($null -eq $rec) { break }
        Write-Host ("    {0,-28} parent={1,-24} DefaultDir={2}" -f $rec.StringData(1), $rec.StringData(2), $rec.StringData(3))
        $n++
    }

    Write-Host "--- 主程序文件所在目录 ---"
    $view3 = $db.OpenView('SELECT ' + $q + 'Component' + $q + ',' + $q + 'Directory_' + $q + ',' + $q + 'FileName' + $q +
                          ' FROM ' + $q + 'File' + $q + ' WHERE ' + $q + 'FileName' + $q + "='AutoLoginNet.exe' OR " +
                          $q + 'FileName' + $q + "='AutoLoginNet.App.exe'")
    $view3.Execute()
    while ($true) {
        $rec = $view3.Fetch()
        if ($null -eq $rec) { break }
        $dir = $rec.StringData(2)
        $dirDefault  = Select-One $db ('SELECT ' + $q + 'DefaultDir' + $q + ' FROM ' + $q + 'Directory' + $q + " WHERE " + $q + 'Directory' + $q + "='" + $dir + "'")
        $dirParent   = Select-One $db ('SELECT ' + $q + 'Directory_Parent' + $q + ' FROM ' + $q + 'Directory' + $q + " WHERE " + $q + 'Directory' + $q + "='" + $dir + "'")
        $parentDefault = '(无)'
        if ($dirParent) {
            $parentDefault = Select-One $db ('SELECT ' + $q + 'DefaultDir' + $q + ' FROM ' + $q + 'Directory' + $q + " WHERE " + $q + 'Directory' + $q + "='" + $dirParent + "'")
        }
        Write-Host ("    文件 {0} -> 目录 {1} DefaultDir='{2}' parent={3} parentDefault='{4}'" -f $rec.StringData(3), $dir, $dirDefault, $dirParent, $parentDefault)
    }
    Write-Host ""
}
