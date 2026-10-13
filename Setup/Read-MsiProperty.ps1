# 读取 MSI 的 Property 表（只读，用于核对 UpgradeCode / ProductCode）。
# 用 [type]::GetTypeFromProgID + InvokeMember 的显式调用方式，避免 PowerShell
# 在 COM 后期绑定上的歧义（之前直接 New-Object 后调 ProductState 会 DISP_E_MEMBERNOTFOUND）。
param(
    [Parameter(Mandatory=$true)][string[]]$MsiPath,
    [string[]]$Properties = @('ProductCode','UpgradeCode','ProductName','ProductVersion','Manufacturer')
)

$ErrorActionPreference = 'Stop'
$t = [type]::GetTypeFromProgID('WindowsInstaller.Installer')
if (-not $t) { throw '找不到 WindowsInstaller.Installer COM 组件' }
$installer = [Activator]::CreateInstance($t)

function Get-MsiProperty($installer, $installerType, [string]$path, [string]$name) {
    $db = $installerType.InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($path, 0))
    $dbType = $db.GetType()
    # SQL 里表名/列名用反引号转义；这里用单引号包字符串字面量
    $sql = 'SELECT `Value` FROM `Property` WHERE `Property`=''' + $name + ''''
    $view = $dbType.InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $viewType = $view.GetType()
    $viewType.InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rec = $viewType.InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $rec) { return $null }
    return $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1))
}

foreach ($p in $MsiPath) {
    Write-Host "=== $p ==="
    if (-not (Test-Path -LiteralPath $p)) { Write-Host '  <文件不存在>'; continue }
    foreach ($prop in $Properties) {
        try {
            $val = Get-MsiProperty $installer $t $p $prop
            if ($null -eq $val) { $val = '<无此属性>' }
            Write-Host ("  {0,-16} = {1}" -f $prop, $val)
        } catch {
            Write-Host ("  {0,-16} = <读取异常: {1}>" -f $prop, $_.Exception.Message)
        }
    }
    Write-Host ''
}
