# 编译原生 MSI 自定义动作。
#
# 为什么要有独立脚本，而不是把编译命令塞进 Build-Installer.ps1：
#   打包脚本已经够长，且这个 DLL 单独可测（见 Test-CustomActionActivation.ps1）；
#   分开之后「改自定义动作」和「重打安装包」是两件可以各自验证的事。
#
# 为什么用 cl.exe 而不是 CMake/MSBuild 工程：
#   只有一个 .cpp、零第三方依赖，一条编译命令就够了。引入构建系统只会让
#   打包链多一层必须同步维护的配置。
#
# 依赖：VS 生成工具（含 C++ 工具集）+ Windows SDK。找不到就明确报错，不静默跳过。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$sourceFile = Join-Path $PSScriptRoot 'CustomActions.Native\UpgradeLocation.cpp'
$outputDir = Join-Path $PSScriptRoot 'CustomActions.Native\bin'
$outputDll = Join-Path $outputDir 'AutoLoginNet.SetupActions.Native.dll'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not (Test-Path $sourceFile)) { throw "找不到源文件：$sourceFile" }

# 定位 vcvars64.bat：优先 vswhere，失败再查常见安装位置。
Write-Step "定位 MSVC 工具链"
$vcvars = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $installPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($installPath) {
        $candidate = Join-Path $installPath 'VC\Auxiliary\Build\vcvars64.bat'
        if (Test-Path $candidate) { $vcvars = $candidate }
    }
}

if (-not $vcvars) {
    $roots = @(
        'C:\Program Files (x86)\Microsoft Visual Studio',
        'C:\Program Files\Microsoft Visual Studio',
        'D:\VisualStudio'
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $found = Get-ChildItem $root -Recurse -Filter 'vcvars64.bat' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $vcvars = $found.FullName; break }
    }
}

if (-not $vcvars) {
    throw '找不到 vcvars64.bat —— 需要安装 Visual Studio 生成工具并勾选 C++ 桌面开发工作负载，MSI 自定义动作无法编译。'
}
Write-Host "    $vcvars"

# 清理旧产物，避免拿上一次的 DLL 交付（构建脚本最容易出的一类假成功）。
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Get-ChildItem $outputDir -File -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Step "编译原生自定义动作（$Configuration）"
$optimize = if ($Configuration -eq 'Debug') { '/Od /Zi' } else { '/O2' }

# /utf-8 是必须的：源文件里有中文注释，在 936 代码页下注释末字节可能被当作行继续符，
# 把下一行代码吞掉（这个坑已经踩过一次）。
# 输出路径不加引号：本仓库路径不含空格，而 /Fo"...bin\" 的结尾反斜杠会把引号转义掉。
$compilerArgs = "/nologo /utf-8 $optimize /GS /W4 /DUNICODE /D_UNICODE /LD `"$sourceFile`" /Fo$outputDir\ /Fe$outputDll /link /MACHINE:X64 msi.lib advapi32.lib"
$command = "call `"$vcvars`" >nul 2>&1 && cl $compilerArgs"

cmd /c $command | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cl.exe 编译失败（退出码 $LASTEXITCODE）" }
if (-not (Test-Path $outputDll)) { throw "编译报告成功但没有产物：$outputDll" }

$size = [math]::Round((Get-Item $outputDll).Length / 1KB, 1)
Write-Host "    产物 $outputDll（$size KB）" -ForegroundColor Green

# 确认导出名与 Product.wxs.template 里 DllEntry 写的名字一致。
# 导出名写错是这类自定义动作最典型、也最难在生产环境察觉的失败：
# 安装包能编译、能装，只是升级时静默地不生效。
Write-Step "校验导出表"
# 从 vcvars64.bat 往上找到 VS 安装根目录，再往下找 dumpbin。
# 不要用 "Split-Path 两次再 Recurse"：vcvars64.bat 位于
#   <VS>\VC\Auxiliary\Build\，倒数第二级是 VC\Auxiliary，那里没有 dumpbin。
$vsRoot = Split-Path (Split-Path (Split-Path $vcvars -Parent) -Parent) -Parent
$dumpbin = Get-ChildItem $vsRoot -Recurse -Filter 'dumpbin.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1
if (-not $dumpbin) {
    # 退路：常见安装根目录
    foreach ($root in @('C:\Program Files (x86)\Microsoft Visual Studio', 'C:\Program Files\Microsoft Visual Studio')) {
        if (-not (Test-Path $root)) { continue }
        $dumpbin = Get-ChildItem $root -Recurse -Filter 'dumpbin.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1
        if ($dumpbin) { break }
    }
}

if (-not $dumpbin) {
    Write-Host '    未找到 dumpbin，跳过导出校验（编译已成功）' -ForegroundColor Yellow
} else {
    $exports = & $dumpbin.FullName /exports $outputDll 2>&1 | Out-String
    foreach ($name in @('DetectPreviousInstallLocation')) {
        if ($exports -match "\s$name\s*$" -or $exports -match "\s$name\s") {
            Write-Host "    ✓ 导出 $name"
        } else {
            throw "产物里没有导出 $name —— DllEntry 会找不到入口，自定义动作会静默失效。"
        }
    }
}

Write-Host ''
Write-Host '原生自定义动作已就绪。' -ForegroundColor Green

# ------------------------------------------------ 诊断构建（带自检入口）
# 单独编一份带 ALN_SELFTEST 的 DLL，交付产物里绝不包含自检入口。
# 这样 Test-NativeDetection.ps1 能直接调用本文件真正的解析函数做验证，
# 而不需要另写一份复刻逻辑（复刻逻辑与实际代码不一致正是上一轮 bug 的来源）。
Write-Step "编译诊断版（含自检入口）"
$diagDll = Join-Path $outputDir 'AutoLoginNet.SetupActions.Native.selftest.dll'
if (Test-Path $diagDll) { Remove-Item $diagDll -Force }

$diagArgs = "/nologo /utf-8 $optimize /GS /W4 /DUNICODE /D_UNICODE /DALN_SELFTEST /LD `"$sourceFile`" /Fo$outputDir\selftest\ /Fe$diagDll /link /MACHINE:X64 msi.lib advapi32.lib"
# /Fo 的目标目录必须已存在
New-Item -ItemType Directory -Path (Join-Path $outputDir 'selftest') -Force | Out-Null
cmd /c "call `"$vcvars`" >nul 2>&1 && cl $diagArgs" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "诊断版编译失败（退出码 $LASTEXITCODE）" }

Write-Host "    产物 $diagDll"
Write-Host ''
Write-Host '完成：交付 DLL 不含自检入口，诊断 DLL 单独一份。' -ForegroundColor Green
