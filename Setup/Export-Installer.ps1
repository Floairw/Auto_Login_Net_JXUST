<#
.SYNOPSIS
    一键导出校园网认证客户端安装包（MSI + 免安装 ZIP）。

.DESCRIPTION
    按顺序完成：
      1) 编译原生自定义动作（升级时定位旧版目录用）——缺失或落后于源码时强制重编；
      2) dotnet publish 生成发布目录（Release / win-x64）；
      3) 剔除「运行时不会被加载」的文件（Setup\Remove-UnusedPublishFiles.ps1）；
      4) WiX 生成 MSI，并压缩出免安装 ZIP；
      5) 打印产物路径、大小与 SHA256；可选复制到桌面；
      6) 可选（-BundleRuntime）把 Setup\Runtime\ 下的运行时安装器一起放进 dist\Runtime\。

    两种交付形态（同一份产品定义，只是载荷不同）：
      - 默认（自包含）：.NET 运行时打进包，目标机无需预装，安装包约 40 MB，
        产物 AutoLoginNet-<版本>-x64.msi / AutoLoginNet-<版本>-portable-x64.zip；
      - -FrameworkDependent：包内不含运行时，目标机必须先装 .NET 10 与 Windows App Runtime 1.8，
        安装包约 7 MB，产物名带 -fd 后缀，可与自包含版并存。

    裁剪是「先复制一份再裁剪」，原发布目录不会被破坏：
    先裁剪到 dist\AutoLoginNet，MSI/ZIP 都从裁剪后的目录出。

.PARAMETER Version
    版本号。省略时从 AutoLoginNet.App\AutoLoginNet.App.csproj 读取 <Version>。

.PARAMETER FrameworkDependent
    产出框架依赖版（不含 .NET 运行时，约 7 MB，文件名带 -fd 后缀）。

.PARAMETER SelfContained
    显式产出（默认即）自包含版（含 .NET 运行时，约 40 MB）。与 -FrameworkDependent 互斥。

.PARAMETER SkipPrune
    不做裁剪，直接打出完整发布目录（出问题时用来对比排查）。

.PARAMETER Desktop
    构建完成后把 MSI 与 ZIP 复制到桌面。

.PARAMETER SkipVerify
    跳过裁剪后自动运行一次 Verify-PrunedPublish.ps1（不推荐；裁剪安全性靠它兜底）。

.PARAMETER BundleRuntime
    把 Setup\Runtime\ 下的运行时安装器一并复制到 dist\Runtime\。安装器不入库，
    需要先按 Setup\Runtime\README.md 把文件放好。

.PARAMETER Configuration
    构建配置，默认 Release。

.EXAMPLE
    pwsh -NoProfile -File Setup\Export-Installer.ps1

.EXAMPLE
    pwsh -NoProfile -File Setup\Export-Installer.ps1 -Desktop -Version 2.0.8

.EXAMPLE
    pwsh -NoProfile -File Setup\Export-Installer.ps1 -FrameworkDependent -Version 2.0.8

.EXAMPLE
    pwsh -NoProfile -File Setup\Export-Installer.ps1 -SelfContained -Version 2.0.8

.EXAMPLE
    pwsh -NoProfile -File Setup\Export-Installer.ps1 -BundleRuntime
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$FrameworkDependent,
    [switch]$SelfContained,
    [switch]$SkipPrune,
    [switch]$SkipVerify,
    [switch]$Desktop,
    [switch]$BundleRuntime,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'AutoLoginNet.App\AutoLoginNet.App.csproj'
$publishDir = Join-Path $repoRoot 'dist\AutoLoginNet'
$distDir = Join-Path $repoRoot 'dist'
$nativeCaSrc = Join-Path $PSScriptRoot 'CustomActions.Native\UpgradeLocation.cpp'
$nativeCaDll = Join-Path $PSScriptRoot 'CustomActions.Native\bin\AutoLoginNet.SetupActions.Native.dll'

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- 版本号
if (-not $Version) {
    [xml]$csproj = Get-Content -LiteralPath $projectPath -Raw
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "无法从 $projectPath 读取 <Version>" }
    Write-Host "打包版本：$Version（来源：AutoLoginNet.App.csproj）"
}
else {
    Write-Host "打包版本：$Version（命令行指定）"
}

if ($FrameworkDependent -and $SelfContained) {
    throw '-FrameworkDependent 与 -SelfContained 互斥：一次只能导出一种交付形态。'
}

# 两种形态共用同一套流程，只用后缀区分文件名，这样两版可以同时躺在 dist 里
$flavorSuffix = if ($FrameworkDependent) { '-fd' } else { '' }
$selfContainedArg = if ($FrameworkDependent) { 'false' } else { 'true' }
$flavorLabel = if ($FrameworkDependent) { '框架依赖（需预装运行时）' } else { '自包含（含 .NET 运行时）' }
Write-Host "交付形态：$flavorLabel"

$msiPath = Join-Path $distDir "AutoLoginNet-$Version$flavorSuffix-x64.msi"
$zipPath = Join-Path $distDir "AutoLoginNet-$Version-portable$flavorSuffix-x64.zip"

# ---------------------------------------------------------------- 1. 原生自定义动作
Write-Step '检查原生自定义动作'
$needRebuild = -not (Test-Path -LiteralPath $nativeCaDll)
if (-not $needRebuild) {
    $needRebuild = (Get-Item -LiteralPath $nativeCaSrc).LastWriteTime -gt (Get-Item -LiteralPath $nativeCaDll).LastWriteTime
}
if ($needRebuild) {
    Write-Host '原生自定义动作缺失或已过期，正在编译…'
    $builder = Join-Path $PSScriptRoot 'Build-NativeCustomActions.ps1'
    & pwsh -NoProfile -File $builder -Configuration $Configuration
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $nativeCaDll)) {
        throw '原生自定义动作编译失败——拒绝产出「装得上但升级时另起目录」的安装包'
    }
}
else {
    Write-Host '原生自定义动作已是最新。'
}

# ---------------------------------------------------------------- 2. 发布
Write-Step "发布应用（$Configuration / win-x64 / $flavorLabel）"
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
$publishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', 'win-x64',
    # 命令行开关会覆盖 csproj 的 <SelfContained>，所以这里必须显式传：
    #   true  -> 自包含（.NET 运行时进包，37 MB → 152 MB）
    #   false -> 框架依赖（只出应用，目标机需预装 .NET 10 + Windows App Runtime 1.8）
    '--self-contained', $selfContainedArg,
    "-p:Version=$Version",
    '-o', $publishDir,
    '--nologo'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
$publishedBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
Write-Host "发布完成：$($publishedFiles.Count) 个文件 / $([math]::Round($publishedBytes / 1MB, 2)) MB"

# ---------------------------------------------------------------- 3. 裁剪
if ($SkipPrune) {
    Write-Warning '已指定 -SkipPrune，安装包将包含全部发布文件。'
}
else {
    Write-Step '剔除运行时不会被加载的文件'
    $pruneResult = & (Join-Path $PSScriptRoot 'Remove-UnusedPublishFiles.ps1') -PublishDir $publishDir
    Write-Host "移除 $($pruneResult.RemovedFiles) 个文件 / $($pruneResult.RemovedDirectories) 个目录 / $([math]::Round($pruneResult.RemovedBytes / 1MB, 2)) MB"
    foreach ($w in $pruneResult.Warnings) { Write-Warning $w }

    $after = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
    $afterBytes = ($after | Measure-Object -Property Length -Sum).Sum
    Write-Host "裁剪后：$($after.Count) 个文件 / $([math]::Round($afterBytes / 1MB, 2)) MB"

    $reportPath = Join-Path $distDir "prune-report-$Version$flavorSuffix.txt"
    $report = @(
        "# 裁剪报告 版本 $Version  生成时间 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        "# 裁剪前：$($publishedFiles.Count) 个文件 / $([math]::Round($publishedBytes / 1MB, 2)) MB"
        "# 裁剪后：$($after.Count) 个文件 / $([math]::Round($afterBytes / 1MB, 2)) MB"
        ''
        '## 已删除'
        $pruneResult.Removed
        ''
        '## 明确保留（不得删除）'
        $pruneResult.Kept
    )
    Set-Content -LiteralPath $reportPath -Value $report -Encoding UTF8
    Write-Host "裁剪报告：$reportPath"

    if (-not $SkipVerify) {
        Write-Step '运行裁剪后可用性验证（启动 + 切页 + 读日志 + 网络检测）'
        $verify = & (Join-Path $PSScriptRoot 'Verify-PrunedPublish.ps1') -PublishDir $publishDir
        $verify.Checks | ForEach-Object { Write-Host "  $_" }
        if ($verify.Failures.Count -gt 0) {
            $verify.Failures | ForEach-Object { Write-Warning $_ }
            throw '裁剪后的发布目录未通过运行验证。请用 -SkipPrune 重新导出，或修正 Setup\PublishPruneRules.ps1 后重试。'
        }
        Write-Host '裁剪后可用性验证通过。'
    }
}

# ---------------------------------------------------------------- 4. 生成 MSI + ZIP
Write-Step '生成 MSI 与免安装 ZIP'
$installer = Join-Path $PSScriptRoot 'Build-Installer.ps1'
$installerArgs = @{ Version = $Version; SkipPublish = $true }
if ($FrameworkDependent) { $installerArgs['FrameworkDependent'] = $true }
& pwsh -NoProfile -File $installer @installerArgs
if ($LASTEXITCODE -ne 0) { throw "生成安装包失败，退出码 $LASTEXITCODE" }

# ---------------------------------------------------------------- 5. 汇总
Write-Step '产物'
$results = @()
foreach ($path in @($msiPath, $zipPath)) {
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $results += [PSCustomObject]@{
            Name   = $item.Name
            SizeMB = [math]::Round($item.Length / 1MB, 2)
            Sha256 = $hash
            Path   = $item.FullName
        }
        Write-Host ("  {0}  {1} MB" -f $item.Name, [math]::Round($item.Length / 1MB, 2))
        Write-Host ("    SHA256 {0}" -f $hash)
    }
    else {
        Write-Warning "未找到产物：$path"
    }
}

if ($Desktop -and $results.Count -gt 0) {
    Write-Step '复制到桌面'
    $desktopDir = [Environment]::GetFolderPath('Desktop')
    foreach ($r in $results) {
        $target = Join-Path $desktopDir $r.Name
        Copy-Item -LiteralPath $r.Path -Destination $target -Force
        Write-Host "  $target"
    }
}

# ---------------------------------------------------------------- 6. 可选：一并分发运行时安装器
# 框架依赖发布（方案 B）下，目标机没装运行时程序就会闪退，所以“应用 + 运行时”一起发
# 才是一个离线可用的交付物。运行时安装器是 160 MB 的二进制、不入库，因此这里的做法是：
#   Setup\Runtime\ 里有就一并拷过去，没有就明确提示——不静默产出「装完打不开」的包。
$runtimeFiles = @()
if ($BundleRuntime) {
    Write-Step '打包前置运行时安装器'
    $runtimeSrc = Join-Path $PSScriptRoot 'Runtime'
    $runtimeOut = Join-Path $distDir 'Runtime'
    $installers = @()
    if (Test-Path -LiteralPath $runtimeSrc) {
        $installers = @(Get-ChildItem -LiteralPath $runtimeSrc -File -Filter '*.exe' |
            Where-Object { $_.Name -match 'windowsappruntimeinstall|windowsdesktop-runtime|dotnet-runtime' })
    }
    if ($installers.Count -eq 0) {
        Write-Warning "Setup\Runtime\ 下没有运行时安装器，本次交付物仍需要用户自行安装运行时。获取方式见 Setup\Runtime\README.md"
    }
    else {
        if (Test-Path -LiteralPath $runtimeOut) { Remove-Item -LiteralPath $runtimeOut -Recurse -Force }
        New-Item -ItemType Directory -Path $runtimeOut -Force | Out-Null
        foreach ($i in $installers) {
            Copy-Item -LiteralPath $i.FullName -Destination (Join-Path $runtimeOut $i.Name) -Force
            $runtimeFiles += Get-Item -LiteralPath (Join-Path $runtimeOut $i.Name)
            Write-Host ("  {0}  {1} MB" -f $i.Name, [math]::Round($i.Length / 1MB, 1))
        }
        $runtimeReadme = Join-Path $runtimeSrc 'README.md'
        if (Test-Path -LiteralPath $runtimeReadme) {
            Copy-Item -LiteralPath $runtimeReadme -Destination (Join-Path $runtimeOut '请先阅读-运行时说明.md') -Force
        }
        Write-Host "  运行时目录：$runtimeOut"
    }
}

if ($results.Count -eq 0) { throw '没有生成任何安装包' }

Write-Host ''
Write-Host '导出完成。' -ForegroundColor Green

[PSCustomObject]@{
    Version  = $Version
    Products = $results
    Pruned   = (-not $SkipPrune)
    Runtimes = $runtimeFiles
}
