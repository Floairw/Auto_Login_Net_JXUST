<#
.SYNOPSIS
    生成 MSI 安装包的可复现构建脚本。

.DESCRIPTION
    流程：
      1) dotnet publish 发布（默认全内置；-FrameworkDependent 时不带任何运行时）
      2) 收集发布目录中的全部文件，生成 WiX 组件清单（Install.wxs）
      3) 用 WiX 编译出单个 MSI，并打一份免安装 ZIP

    只交付两种形态：
      - 默认（全内置）：.NET 与 Windows App Runtime 都打进包，目标机零运行时依赖，
        安装包约 47 MB，产物文件名带 -was-fd 后缀。不确定目标机装过什么就用这个。
      - -FrameworkDependent：安装包约 7 MB，目标机必须先装 .NET 10 与 Windows App Runtime 1.8，
        产物文件名带 -fd 后缀。
      两者是**同一个产品、两种载荷**，有意设计成不能同时安装（细节见下方参数区注释）：
        - UpgradeCode 相同：它标识「产品线 / 升级路径」，由固定种子生成，与形态无关，
          所以两种形态落进同一条升级链；
        - ProductCode 由 WiX 每次编译自动生成（模板里不写死），因此不同形态、甚至同一形态
          每次重编出来的 ProductCode 都不一样 —— ProductCode 标识的是「某一次构建的某个包」；
        - 「同一个 UpgradeCode + 相同 Version + AllowSameVersionUpgrades」使装了其中一种再装
          另一种时被判定为**同版本升级**：先卸载旧的再装新的（原地覆盖），而不是并排两份。
      文件名后缀不同，可同时放在 dist 里互不覆盖。

    第三种「自包含 .NET」（只含 .NET、仍需系统预装 Windows App Runtime，约 31 MB）已停止交付：
    它省下的空间有限，却把「装不上」的风险留给了用户。-SelfContained 现在直接报错，
    避免又导出一个谁也说不清该发给谁的中间形态。

    之所以自己收集文件而不用 Heat：Heat 扩展的版本与 WiX 主工具版本强绑定
    （WixToolset.Heat 6.x 要求 WiX 6.x），而本机工具链是 WiX 5.x。
    这里的收集逻辑很简单，自己写反而更可控。

.NOTES
    未签名。安装时 Windows 会弹出 SmartScreen 提示，属预期行为。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Version,
    [string]$Manufacturer = 'AutoLoginNet',
    [string]$ProductName = '校园网认证客户端',
    # 两种交付形态共用同一份产品定义，只靠发布载荷与文件名后缀区分：
    #   默认（不加开关）= 全内置：.NET 与 Windows App Runtime 都进包，目标机零运行时依赖，约 47 MB；
    #   -FrameworkDependent = 框架依赖：需预装 .NET 10 + Windows App Runtime 1.8，约 7 MB。
    # 为什么两种形态必须「二选一、不能同时安装」（有意设计，不是缺陷）：
    #   1) UpgradeCode 是「产品线」的身份，由 Build-Installer 用固定种子 Get-StableGuid 生成，
    #      与开关无关 —— 两种形态、以及以后每个版本，都落在同一条升级链上；
    #   2) 模板里**不写 ProductCode**，WiX 每次编译自动生成一个新的：实测 dist 里
    #      -fd 与 -was-fd 两份 MSI 的 ProductCode 不同，本机已装的旧 2.0.8 又是另一个。
    #      ProductCode 标识「某一次构建出的某个产品包」，本来就不该跨构建复用；
    #   3) 相同 UpgradeCode + 相同 Version + Product.wxs 的 AllowSameVersionUpgrades="yes"，
    #      让「已装其中一种、再装另一种」被 Windows Installer 判为**同版本升级**：卸载旧包、
    #      装上新包，ARP 仍只有一条记录，程序由原生自定义动作装回旧版原目录。
    #   4) 反过来，若给两种形态不同的 UpgradeCode（或不同产品名），它们就成了两个独立产品：
    #      ARP 两条记录、两套开始菜单与开机自启互相覆盖、覆盖升级失效，而「装新版自动找到
    #      旧版装在哪并原地覆盖」正是本项目的核心需求之一。所以「不能同时装」是必须保留的行为。
    #   用户要换形态：先卸载已安装的那个，再装另一个即可（.zip 免安装版不写注册表，不受此限）。
    [switch]$FrameworkDependent,
    # 全内置本身就是默认形态；保留这个开关只为兼容既有命令行与 Setup\AutoLoginNet.Setup.wixproj 里的显式写法。
    [switch]$WindowsAppSDKSelfContained,
    # 已停止交付的形态。留着它只为给出明确报错，而不是悄悄按默认（全内置）导出一个内容不符合同名包。
    [switch]$SelfContained,
    [switch]$SkipPublish,
    # 只生成 WiX 源文件（Setup\obj\Install.wxs 与 Setup\obj\Product.wxs）就退出，把编译交给
    # Setup\AutoLoginNet.Setup.wixproj（MSBuild + WiX SDK）。必须与 -SkipPublish 同用：
    # 发布与裁剪归 Export-Installer.ps1 -PrepareOnly 管，避免两处各发布/裁剪一遍。
    [switch]$GenerateOnly,
    # 用外部（MSBuild 的 WiX 工程）已编译好的 MSI 定稿：跑中文化断言 → 复制成 dist 下的规范
    # 文件名 → 打免安装 ZIP。用于 .wixproj 的生成后步骤，保证两边产出的断言与命名完全一致。
    [string]$FinalizeMsi
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'AutoLoginNet.App\AutoLoginNet.App.csproj'

if ($SelfContained) {
    throw '不再交付「自包含 .NET」形态（包内含 .NET，但仍要求目标机预装 Windows App Runtime，省不了多少空间却容易装不上）。现在只有两种交付形态：默认的「全内置」与 -FrameworkDependent。'
}

# 文件名后缀：两种形态的产物必须能同时躺在 dist 里，否则后一次导出会覆盖前一次
$flavorSuffix = if ($FrameworkDependent) { '-fd' } else { '-was-fd' }
$flavorLabel = if ($FrameworkDependent) { '框架依赖（需预装运行时）' } else { '全内置（含 .NET 与 Windows App Runtime）' }

# 版本号以 csproj 为唯一来源。此前脚本里另写了一份 '2.0.5' 默认值，结果应用已到 v2.0.7
# 而打出来的安装包仍是 2.0.5（安装包落后了两次功能改动）。未显式传 -Version 时一律读 csproj。
if (-not $Version) {
    $versionXml = [xml](Get-Content $projectPath -Raw)
    $Version = $versionXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "无法从 $projectPath 读取 <Version>" }
}
Write-Host "==> 打包版本：$Version（来源：AutoLoginNet.App.csproj）" -ForegroundColor Cyan

$publishDir = Join-Path $repoRoot 'dist\AutoLoginNet'
$setupDir = $PSScriptRoot
$objDir = Join-Path $setupDir 'obj'
$outDir = Join-Path $repoRoot 'dist'
$msiPath = Join-Path $outDir "AutoLoginNet-$Version$flavorSuffix-x64.msi"
# 用架构短名（x64）而不是完整 RID（win-x64），与 MSI 的命名保持一致
$archLabel = $RuntimeIdentifier -replace '^win-', ''
$zipPath = Join-Path $outDir "AutoLoginNet-$Version-portable$flavorSuffix-$archLabel.zip"

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# 必须跑在 PowerShell 7 上：本脚本是无 BOM 的 UTF-8，Windows PowerShell 5.1 会按 ANSI 代码页读它，
# 中文（产品名「校园网认证客户端」、安装向导文案）会整体乱码，写进 MSI 就是乱码产品名。
# 与其悄悄产出乱码包，不如在这里直接报错。
if ($PSVersionTable.PSEdition -ne 'Core') {
    throw "需要 PowerShell 7（pwsh）运行本脚本；当前宿主是 Windows PowerShell $($PSVersionTable.PSVersion)——它会按 ANSI 代码页读取无 BOM 的 UTF-8 脚本，中文将全部乱码。"
}
# 子脚本一律用「当前宿主」启动，而不是裸写 `pwsh`：从 Visual Studio 的生成菜单触发时，进程 PATH 里
# 未必有 pwsh（本机就没装系统级 PowerShell 7），裸 `pwsh` 会解析不到。
$pwshExe = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $pwshExe)) { $pwshExe = (Get-Process -Id $PID).Path }

if ($GenerateOnly -and -not $SkipPublish) {
    throw '-GenerateOnly 必须与 -SkipPublish 一起用：发布与裁剪由 Export-Installer.ps1 -PrepareOnly 负责，本脚本只在已裁剪的发布目录上生成 WiX 源文件。'
}
if ($GenerateOnly -and $FinalizeMsi) { throw '-GenerateOnly 与 -FinalizeMsi 互斥。' }

# ------------------------------------------------- 安装包中文化断言（失败即中止）
# 这几项没法靠“编译成功”证明，而中文界面又特别容易悄悄退回英文，所以在打包流程里
# 直接读 MSI 的表来断言。COM 包装写在 Setup\Test-InstallerLocalization.ps1 里：
# 函数一旦用 return 抛数组，PowerShell 会把单行结果拆成标量、取值全变空，
# 单独成文件既好写也好单独跑（`pwsh -File Setup\Test-InstallerLocalization.ps1 -Msi <路径>`）。
function Assert-InstallerLocalization([string]$Msi) {
    Write-Step '校验安装包中文化'
    $report = & (Join-Path $setupDir 'Test-InstallerLocalization.ps1') -Msi $Msi -LicenseText (Join-Path $setupDir 'LicenseText.txt')
    $failures = @($report.Checks | Where-Object { -not $_.Ok })
    foreach ($check in $report.Checks) {
        $mark = if ($check.Ok) { '  通过' } else { '  失败' }
        $color = if ($check.Ok) { 'DarkGray' } else { 'Red' }
        Write-Host "$mark  $($check.Name)  — $($check.Detail)" -ForegroundColor $color
    }
    if ($failures.Count -gt 0) { throw "安装包中文化校验失败（$($failures.Count) 项）" }
}

# --------------------------------------------------------------- 免安装压缩包
# 与 MSI 同源的发布负载，解压即用（不需要管理员权限）。
# 用 .NET 的 ZipFile 而不是 Compress-Archive：后者对同一批文件要慢数倍，
# 且 Optimal 级别在这里压不出更小的结果。
function New-PortableZip([string]$PublishDir, [string]$Zip) {
    Write-Step '生成免安装压缩包'
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PublishDir,
        $Zip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)   # $false: 不把 publishDir 这一层目录名包进去
}

function Show-InstallerSummary {
    $msi = Get-Item $msiPath
    $zip = Get-Item $zipPath
    $payloadMb = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ""
    Write-Host "产物已生成（$flavorLabel）：" -ForegroundColor Green
    Write-Host "  MSI      $([math]::Round($msi.Length / 1MB, 1)) MB  $($msi.Name)"
    Write-Host "  免安装包  $([math]::Round($zip.Length / 1MB, 1)) MB  $($zip.Name)"
    Write-Host "  解包后    $payloadMb MB  ($((Get-ChildItem $publishDir -Recurse -File).Count) 个文件)"
    Write-Host "  输出目录  $outDir"
}

# 发布目录内容必须与本次要求的形态一致，否则会把上一次导出的形态套上本次的文件名
# （例如把「中间形态」印成「全内置」，或者拿自包含载荷去印 -fd 的名字）。
# 两条路径都要跑：命令行 -SkipPublish 复用目录时，以及 -FinalizeMsi 定稿 MSBuild 产物时
# ——后者尤其重要：MSBuild 的增量判断不看交付形态，形态一换载荷就可能还是上一次的。
function Assert-PayloadFlavor {
    $hasDotNetRuntime = Test-Path (Join-Path $publishDir 'coreclr.dll')
    $hasAppSdkRuntime = Test-Path (Join-Path $publishDir 'Microsoft.ui.xaml.dll')
    $expected = if ($FrameworkDependent) { @($false, $false) } else { @($true, $true) }
    if ($hasDotNetRuntime -ne $expected[0] -or $hasAppSdkRuntime -ne $expected[1]) {
        throw ("发布目录与要求的形态不一致（期望 .NET运行时=$($expected[0]) / WindowsAppRuntime=$($expected[1])，" +
            "实际 .NET运行时=$hasDotNetRuntime / WindowsAppRuntime=$hasAppSdkRuntime）。" +
            "全内置形态要求在 publish 时同时给 --self-contained true 与 -p:WindowsAppSDKSelfContained=true" +
            "（只给后者会退化成「有 Windows App Runtime、没有 .NET」的中间形态）。" +
            "请去掉 -SkipPublish（MSBuild 侧用 -p:SetupSkipPublish=false）重新发布，或删掉 $publishDir 后重试。")
    }
}

# ------------------------------------------------- 定稿外部编译的 MSI（.wixproj 生成后步骤）
# 走这条路时 WiX 源文件由 MSBuild 编译，本脚本不再发布/裁剪/编译，只做「校验 + 落盘 + 打包」，
# 从而保证 VS 生成菜单与命令行脚本产出的断言、文件名、ZIP 完全一致。
if ($FinalizeMsi) {
    if (-not (Test-Path -LiteralPath $FinalizeMsi)) {
        throw "找不到 MSBuild 产出的 MSI：$FinalizeMsi（先构建 Setup\AutoLoginNet.Setup.wixproj）"
    }
    if (-not (Test-Path -LiteralPath $publishDir)) {
        throw "找不到发布目录 $publishDir —— 免安装 ZIP 需要它。请先跑 Export-Installer.ps1 -PrepareOnly 准备载荷。"
    }
    Write-Host "==> 定稿 MSBuild 编译的安装包：$FinalizeMsi" -ForegroundColor Cyan
    # 先确认载荷形态、再校验中文化、最后才落盘：dist 里永远不出现「形态不符」或「没过断言」的安装包
    Assert-PayloadFlavor
    Assert-InstallerLocalization $FinalizeMsi
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    Write-Step "复制到 dist 并规范命名"
    Copy-Item -LiteralPath $FinalizeMsi -Destination $msiPath -Force
    New-PortableZip -PublishDir $publishDir -Zip $zipPath
    Show-InstallerSummary
    return
}

# ---------------------------------------------------------------- 1) 发布
if (-not $SkipPublish) {
    Write-Step "发布应用（$flavorLabel $RuntimeIdentifier）"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # 这里必须显式传 --self-contained：命令行开关会覆盖 csproj 里的 <SelfContained>。
    #   true  -> .NET 运行时进包（发布目录 100~160 MB）
    #   false -> 框架依赖，只出应用（发布目录约 37 MB）
    $selfContainedArg = if ($FrameworkDependent) { 'false' } else { 'true' }
    $extraArgs = @()
    if (-not $FrameworkDependent) {
        # 全内置（默认形态）：把 Windows App Runtime 的原生层（Microsoft.ui.xaml.dll 等约 50 MB）也打进包，
        # 目标机零运行时依赖。注意：<WindowsAppSDKSelfContained> 不能只靠命令行打开——
        # 实测 -p:SelfContained=false -p:WindowsAppSDKSelfContained=true 会退化成
        # 中间形态（93.88 MB：Microsoft.ui.xaml.dll 在、coreclr.dll 不在），
        # 所以这里必须同时给 --self-contained true 与 -p:WindowsAppSDKSelfContained=true。
        $extraArgs += '-p:WindowsAppSDKSelfContained=true'
    }
    dotnet publish $projectPath -c $Configuration -r $RuntimeIdentifier --self-contained $selfContainedArg `
        -p:Version=$Version @extraArgs -o $publishDir | Out-Host

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
}

if (-not (Test-Path (Join-Path $publishDir 'AutoLoginNet.exe'))) {
    throw "未找到发布产物：$publishDir\AutoLoginNet.exe"
}

# 发布包必须包含 WinUI 的编译 XAML 与资源索引，否则运行时会以 0xC000027B 崩溃
foreach ($required in @('AutoLoginNet.pri', 'App.xbf')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "发布产物缺少 $required —— 安装包会装出一个无法启动的程序"
    }
}

# 形态自检：-SkipPublish 复用已有发布目录时，目录内容必须与本次要求的形态一致
Assert-PayloadFlavor

# ------------------------------------------------- 2) 生成 WiX 组件清单
Write-Step "收集文件并生成 WiX 组件清单"

function Get-StableGuid([string]$seed) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        return [Guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($seed))).ToString().ToUpperInvariant()
    }
    finally { $md5.Dispose() }
}

function ConvertTo-XmlText([string]$value) { [System.Security.SecurityElement]::Escape($value) }

function Get-DirectoryId([string]$relativeDir) {
    if ([string]::IsNullOrEmpty($relativeDir)) { return 'INSTALLFOLDER' }
    return New-WixId 'dir' $relativeDir.TrimEnd('\')
}

<#
    WiX 标识符上限为 72 个字符。发布包里存在超长文件名
    （例如 WindowsAppSdk.AppxDeploymentExtensions.Desktop-EventLog-Instrumentation.dll），
    直接转换会超限并报 WIX1026。
    这里保留可读前缀 + 稳定短哈希：既满足长度限制，又不会因截断而彼此冲突。
#>
function New-WixId([string]$prefix, [string]$key) {
    $slug = ($key -replace '[^A-Za-z0-9]', '_')
    $hash = (Get-StableGuid "id/$key").Substring(0, 8)

    $maxSlug = 72 - ($prefix.Length + 2 + $hash.Length)
    if ($slug.Length -gt $maxSlug) { $slug = $slug.Substring(0, $maxSlug) }
    if ([string]::IsNullOrEmpty($slug)) { $slug = 'x' }

    return "${prefix}_${slug}_$hash"
}

$files = Get-ChildItem $publishDir -Recurse -File | ForEach-Object {
    [pscustomobject]@{
        FullPath = $_.FullName
        Relative = $_.FullName.Substring($publishDir.Length).TrimStart('\')
        Directory = [System.IO.Path]::GetDirectoryName($_.FullName.Substring($publishDir.Length).TrimStart('\'))
        Size = $_.Length
    }
} | Sort-Object Relative

if ($files.Count -eq 0) { throw "发布目录为空：$publishDir" }

# 目录树：按层级生成，WiX 要求父目录先于子目录声明
$directories = @{}
foreach ($file in $files) {
    if ([string]::IsNullOrEmpty($file.Directory)) { continue }
    $parts = $file.Directory.Split('\')
    for ($i = 1; $i -le $parts.Count; $i++) {
        $sub = ($parts[0..($i - 1)] -join '\')
        if (-not $directories.ContainsKey($sub)) {
            $parent = if ($i -eq 1) { '' } else { ($parts[0..($i - 2)] -join '\') }
            $directories[$sub] = [pscustomobject]@{
                Relative = $sub
                Id = Get-DirectoryId $sub
                ParentId = Get-DirectoryId $parent
                Name = $parts[$i - 1]
            }
        }
    }
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!-- 本文件由 Setup\Build-Installer.ps1 自动生成，请勿手工编辑 -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
[void]$sb.AppendLine('      <Directory Id="INSTALLFOLDER" Name="AutoLoginNet">')

foreach ($dir in ($directories.Values | Sort-Object Relative)) {
    [void]$sb.AppendLine("        <Directory Id=""$($dir.Id)"" Name=""$(ConvertTo-XmlText $dir.Name)"" />")
}
[void]$sb.AppendLine('      </Directory>')
[void]$sb.AppendLine('    </StandardDirectory>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine()
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')

$grouped = $files | Group-Object Directory
foreach ($group in $grouped) {
    $dirId = Get-DirectoryId $group.Name
    foreach ($file in $group.Group) {
        $componentId = New-WixId 'cmp' $file.Relative
        $fileId = New-WixId 'fil' $file.Relative
        $source = ConvertTo-XmlText $file.FullPath
        $name = ConvertTo-XmlText ([System.IO.Path]::GetFileName($file.Relative))

        # 组件 GUID 由文件相对路径派生成确定值：同一文件重复构建得到同一 GUID，
        # 便于升级安装识别；用自动 GUID（*）在跨构建时反而不稳定。
        $guid = Get-StableGuid "AutoLoginNet/$($file.Relative)"

        [void]$sb.AppendLine("      <Component Id=""$componentId"" Directory=""$dirId"" Guid=""$guid"">")
        [void]$sb.AppendLine("        <File Id=""$fileId"" Source=""$source"" Name=""$name"" KeyPath=""yes"" />")
        [void]$sb.AppendLine('      </Component>')
    }
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

New-Item -ItemType Directory -Path $objDir -Force | Out-Null
$installWxs = Join-Path $objDir 'Install.wxs'
Set-Content -Path $installWxs -Value $sb.ToString() -Encoding UTF8
Write-Host "    文件 $($files.Count) 个，目录 $($directories.Count) 个 -> $installWxs"

# ------------------------------------------------------- 3) 生成产品定义
Write-Step "生成产品定义 (Product.wxs)"
# Icon 的 SourceFile 不走 -bindpath 解析，必须给绝对路径
$iconPath = (Resolve-Path (Join-Path $repoRoot 'AutoLoginNet.App\Assets\app.ico')).Path

# 升级时定位旧版目录的自定义动作。原生 DLL 必须存在且是最新的：
# 缺失时若静默跳过，打出来的安装包会「装得上、但升级时另起目录」，
# 这正是用户报的那个问题，所以绝不降级处理。
#
# 缺失或落后于源码时自动重新编译，而不是让调用者去猜还要先跑哪个脚本。
$nativeCaDll = Join-Path $setupDir 'CustomActions.Native\bin\AutoLoginNet.SetupActions.Native.dll'
$nativeCaSrc = Join-Path $setupDir 'CustomActions.Native\UpgradeLocation.cpp'
$nativeCaBuilder = Join-Path $setupDir 'Build-NativeCustomActions.ps1'
$needsNativeBuild = -not (Test-Path $nativeCaDll)
if (-not $needsNativeBuild -and (Test-Path $nativeCaSrc)) {
    $needsNativeBuild = (Get-Item $nativeCaSrc).LastWriteTime -gt (Get-Item $nativeCaDll).LastWriteTime
}
if ($needsNativeBuild) {
    if (-not (Test-Path $nativeCaBuilder)) {
        throw "缺少原生自定义动作构建脚本：$nativeCaBuilder"
    }
    Write-Host '  原生自定义动作缺失或已过期，正在编译…' -ForegroundColor DarkGray
    & $pwshExe -NoProfile -File $nativeCaBuilder
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $nativeCaDll)) {
        throw "原生自定义动作编译失败（见上方输出）：$nativeCaDll"
    }
}

$productTemplate = Get-Content (Join-Path $setupDir 'Product.wxs.template') -Raw

# 许可协议正文与界面的中文文案。两者都不是可选装饰：
#   license.rtf   —— 安装向导「最终用户许可协议」页的正文（WixUILicenseRtf 指向它）。
#   WixUI_zh-CN.wxl —— 向导全部内置对话框的中文串；缺了它安装界面会退回英文。
# 协议正文的维护入口只有 Setup\LicenseText.txt（纯文本）；license.rtf 是生成物、不进版本库，
# 所以这里缺了就自动调 Setup\New-LicenseRtf.ps1 生成——否则「代码仓库干净时会话里打不出包」。
$licenseRtfPath = Join-Path $setupDir 'license.rtf'
$licenseTextPath = Join-Path $setupDir 'LicenseText.txt'
$wxlPath = Join-Path $setupDir 'WixUI_zh-CN.wxl'
foreach ($required in @($licenseTextPath, $wxlPath)) {
    if (-not (Test-Path $required)) { throw "缺少安装向导文案文件：$required" }
}
$rtfGenerator = Join-Path $setupDir 'New-LicenseRtf.ps1'
$rtfMissing = -not (Test-Path $licenseRtfPath)
$rtfStale = (-not $rtfMissing) -and
            ((Get-Item $licenseRtfPath).LastWriteTime -lt (Get-Item $licenseTextPath).LastWriteTime)
if ($rtfMissing -or $rtfStale) {
    if (-not (Test-Path $rtfGenerator)) { throw "缺少 RTF 生成脚本：$rtfGenerator" }
    if ($rtfMissing) { Write-Host '  许可协议 RTF 不存在，正在从 LicenseText.txt 生成…' -ForegroundColor DarkGray }
    else { Write-Host '  许可协议 RTF 比正文旧，正在重新生成…' -ForegroundColor DarkGray }
    & $pwshExe -NoProfile -File $rtfGenerator
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $licenseRtfPath)) {
        throw "生成许可协议 RTF 失败（见上方输出）：$licenseRtfPath"
    }
}
$productWxs = $productTemplate.
    Replace('{{VERSION}}', $Version).
    Replace('{{MANUFACTURER}}', (ConvertTo-XmlText $Manufacturer)).
    Replace('{{PRODUCTNAME}}', (ConvertTo-XmlText $ProductName)).
    Replace('{{UPGRADECODE}}', (Get-StableGuid 'AutoLoginNet/UpgradeCode')).
    Replace('{{NATIVECAPATH}}', (ConvertTo-XmlText $nativeCaDll)).
    Replace('{{ICONPATH}}', (ConvertTo-XmlText $iconPath)).
    Replace('{{LICENSERTF}}', (ConvertTo-XmlText $licenseRtfPath))
$productPath = Join-Path $objDir 'Product.wxs'
Set-Content -Path $productPath -Value $productWxs -Encoding UTF8

# ------------------------------------------------- 只生成源文件（.wixproj 生成前步骤）
# WiX 源文件已就绪，编译交给 MSBuild/WiX SDK。这里必须停下：若继续用 wix.exe 再编一遍，
# 就会产出第二个 %(CultureGroup) 路径下的 MSI，和 MSBuild 的产物抢 dist 里的同名文件。
if ($GenerateOnly) {
    Write-Host ""
    Write-Host "WiX 源文件已生成（$flavorLabel）：" -ForegroundColor Green
    Write-Host "  $installWxs"
    Write-Host "  $productPath"
    Write-Host "  发布目录  $publishDir（$((Get-ChildItem $publishDir -Recurse -File).Count) 个文件）"
    exit 0
}

# --------------------------------------------------------------- 4) 编译
Write-Step "编译 MSI"
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = 'wix' }

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
if (Test-Path $msiPath) { Remove-Item $msiPath -Force }

# -loc 是必须的，且 .wxl 不会被自动发现：实验证明把 .wxl 放在 .wxs 同目录里，WiX
# 连看都不看（同目录放一个故意写坏的 .wxl，编译照样成功）；只有显式 -loc 才会生效。
& $wix build $productPath $installWxs -arch x64 -out $msiPath -bindpath $publishDir -ext WixToolset.UI.wixext -loc $wxlPath
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

# WiX 会顺带产出 .wixpdb 调试数据库，它只用于分析安装包，不是可用产物，别留在交付目录里
Get-ChildItem $outDir -Filter '*.wixpdb' -ErrorAction SilentlyContinue | Remove-Item -Force

# ------------------------------------------------- 4.5) 安装包中文化断言（失败即中止）
Assert-InstallerLocalization $msiPath

# --------------------------------------------------------------- 5) 免安装压缩包
New-PortableZip -PublishDir $publishDir -Zip $zipPath
Show-InstallerSummary
