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

# 让 MSVC 编译器跑起来：把命令写进临时 .cmd，再交给 cmd.exe 执行。
#
# 为什么不直接写 `cmd /c "call "带空格的vcvars64.bat" >nul 2>&1 && cl ..."`：
#   那条命令里嵌套了引号，而 cmd.exe 的引号规则和 PowerShell 向原生程序传参的规则不是同一套。
#   实测：从 MSBuild 的 Exec 里这样调用会退化成 `此时不应有 \common。`，退出码 255、没有任何输出，
#   看起来像编译器崩了，其实是命令行被拆错（同样一条命令在别的宿主里又能跑通，极难排查）。
#   写成 .cmd 文件后，引号只由 cmd 自己按常规规则解析，与调用方是谁无关。
function Invoke-MsvcCommand([string]$CommandLine, [string]$Description) {
    # 先把 PATH 里明显不能用的条目剔掉，再交给 cmd。
    # 为什么必须做：PATH 里只要有一个坏条目（含引号的残渣，例如手工拼 PATH 时留下的
    #   `$env:VCPKG_ROOT\bin;$env:PATH"`，或没被展开的 `%Zulu_Java%`），vcvars64.bat 里的批处理
    #   就会被解析错，表现为 `此时不应有 \common。` + 退出码 255 + **完全没有任何输出**——
    #   看起来像编译器崩了，实际与编译无关（实测：清掉坏条目后同样命令立刻成功）。
    # 只丢「明显不可用」的：含 " % $ 的，以及指向不存在目录的。其余条目原样保留。
    $allEntries = $env:PATH -split ';'
    $usableEntries = $allEntries | Where-Object {
        $entry = $_.Trim()
        if ($entry -eq '' -or $entry -match '["%$]') { return $false }
        Test-Path -LiteralPath $entry
    }
    if ($usableEntries.Count -lt $allEntries.Count) {
        Write-Host "    提示：PATH 里有 $($allEntries.Count - $usableEntries.Count) 个不可用条目（含引号/百分号或目录不存在），本步已剔除" -ForegroundColor DarkGray
    }

    $scriptFile = Join-Path ([System.IO.Path]::GetTempPath()) ('aln-msvc-' + [guid]::NewGuid().ToString('N') + '.cmd')
    # 用 ANSI（本机 936）写：.cmd 是按 OEM 码页读的，路径里一旦有中文，UTF-8 无 BOM 会被读花。
    $lines = @(
        '@echo off'
        "set `"PATH=$($usableEntries -join ';')`""
        "call `"$vcvars`" >nul 2>&1"
        $CommandLine
        'exit /b %errorlevel%'
    )
    Set-Content -Path $scriptFile -Value ($lines -join "`r`n") -Encoding ansi
    try {
        cmd.exe /c $scriptFile | Out-Host
    } finally {
        Remove-Item $scriptFile -Force -ErrorAction SilentlyContinue
    }
    if ($LASTEXITCODE -ne 0) { throw "$Description 失败（退出码 $LASTEXITCODE）" }
}

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

# 先编到 staging，成功之后才覆盖交付 DLL。
# 反面教材（已经踩过一次）：原先的写法是一上来就把 bin 里的旧文件全删掉再编译，
# 结果 cl 报错时 bin 里既没有新 DLL、旧 DLL 也一起没了，连回退都做不了。
$stagingDir = Join-Path $outputDir 'staging'
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
# 旧链接中间产物要清，但交付 DLL 与诊断 DLL 不在这里（它们在 $outputDir 根下）。
foreach ($pattern in @('*.lib', '*.exp', '*.obj')) {
    Get-ChildItem -Path (Join-Path $outputDir $pattern) -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

Write-Step "编译原生自定义动作（$Configuration）"
$optimize = if ($Configuration -eq 'Debug') { '/Od /Zi' } else { '/O2' }

# /utf-8 是必须的：源文件里有中文注释，在 936 代码页下注释末字节可能被当作行继续符，
# 把下一行代码吞掉（这个坑已经踩过一次）。
# 输出路径不加引号：本仓库路径不含空格，而 /Fo"...bin\" 的结尾反斜杠会把引号转义掉。
$dllName = 'AutoLoginNet.SetupActions.Native.dll'
$compilerArgs = "/nologo /utf-8 $optimize /GS /W4 /DUNICODE /D_UNICODE /LD `"$sourceFile`" /Fo$stagingDir\ /Fe$stagingDir\$dllName /link /MACHINE:X64 msi.lib advapi32.lib"
Invoke-MsvcCommand -CommandLine "cl $compilerArgs" -Description 'cl.exe 编译'

$stagedDll = Join-Path $stagingDir $dllName
if (-not (Test-Path $stagedDll)) { throw "编译报告成功但没有产物：$stagedDll" }
Copy-Item $stagedDll $outputDll -Force

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
$diagName = 'AutoLoginNet.SetupActions.Native.selftest.dll'
$diagStagingDir = Join-Path $stagingDir 'selftest'
New-Item -ItemType Directory -Path $diagStagingDir -Force | Out-Null

$diagArgs = "/nologo /utf-8 $optimize /GS /W4 /DUNICODE /D_UNICODE /DALN_SELFTEST /LD `"$sourceFile`" /Fo$diagStagingDir\ /Fe$diagStagingDir\$diagName /link /MACHINE:X64 msi.lib advapi32.lib"
Invoke-MsvcCommand -CommandLine "cl $diagArgs" -Description '诊断版编译'

$stagedDiag = Join-Path $diagStagingDir $diagName
if (-not (Test-Path $stagedDiag)) { throw "编译报告成功但没有诊断版产物：$stagedDiag" }
Copy-Item $stagedDiag $diagDll -Force

Write-Host "    产物 $diagDll"
# 中间产物（.obj/.lib/.exp）留在 staging 里没有意义，清掉免得 bin 越积越乱。
Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '完成：交付 DLL 不含自检入口，诊断 DLL 单独一份。' -ForegroundColor Green
