<#
.SYNOPSIS
    对已发布的目录做「剔除运行时不会被加载的文件」处理，并报告裁剪结果。

.DESCRIPTION
    只做删除，不做重排/重命名，因此对 MSI 打包脚本（逐文件生成 Component）完全透明。
    每条删除都记录原因；被 $MustKeep 命中的文件即使同时命中删除规则也会保留并告警。

    设计原则：宁少删不多删。WinUI 3 自包含运行时存在大量按名字动态加载的原生 DLL，
    静态分析无法证明其未被使用；一旦误删就是「应用打不开」，因此本脚本只处理
    有确凿证据（程序集引用闭包、内嵌 Reg-Free WinRT 清单、源码 grep）的文件。

.PARAMETER PublishDir
    发布目录（Setup\Build-Installer.ps1 的 $publishDir）。脚本会就地修改它。

.PARAMETER SkipCultures
    是否删除非 zh-CN / en 的语言资源目录。默认删除。

.PARAMETER SkipWinmd
    是否删除 *.winmd。默认【不】删除：WindowsAppRuntime 可能按命名空间查找元数据，
    需要先用 Verify-PrunedPublish.ps1 在真实运行中验证通过，才建议开启。

.OUTPUTS
    PSCustomObject：RemovedFiles / RemovedBytes / RemovedDirectories / Kept / Warnings。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [bool]$SkipCultures = $true,

    [bool]$SkipWinmd = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PublishPruneRules.ps1')

function Get-DirectorySize {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 0L }
    return [long]((Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum)
}

$removed = [System.Collections.Generic.List[string]]::new()
$kept = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$removedBytes = 0L
$removedDirs = 0

function Test-IsMustKeep {
    param([string]$RelativePath)
    $leaf = Split-Path -Leaf $RelativePath
    foreach ($rule in $MustKeep) {
        if ($leaf -like $rule.Wildcard -or $RelativePath -like $rule.Wildcard) {
            return $rule
        }
    }
    return $null
}

function Remove-PrunedFile {
    param([System.IO.FileInfo]$File, [string]$Reason, [string]$PublishRoot)
    $relative = [System.IO.Path]::GetRelativePath($PublishRoot, $File.FullName)
    $mk = Test-IsMustKeep $relative
    if ($mk) {
        $warnings.Add("拒绝删除「$relative」：命中必须保留规则（$($mk.Reason)）")
        return $false
    }
    $script:removedBytes += $File.Length
    $removed.Add("$relative  [$([math]::Round($File.Length / 1KB, 1)) KB]  $Reason")
    Remove-Item -LiteralPath $File.FullName -Force
    return $true
}

# ------------------------------------------------ 1. 语言资源目录
# 不依赖硬编码的语言清单：直接枚举发布根下「名字长得像区域性、且只含 .mui」的目录。
# 这样可以自动覆盖框架将来新增/改名/改大小写的语言目录。
if ($SkipCultures) {
    # 注意必须写成 [a-zA-Z]：写 [a-z] 会漏掉 ca-Es-VALENCIA 这类大小写混排的区域性名
    # （PowerShell 的 -match 默认大小写不敏感只影响字面字符，不影响字符类范围）。
    $cultureLike = '^[a-zA-Z]{2,3}(-[a-zA-Z]{2,8})*$'
    $keepLower = $KeepCultures | ForEach-Object { $_.ToLowerInvariant() }

    foreach ($dir in @(Get-ChildItem -LiteralPath $PublishDir -Directory | Sort-Object Name)) {
        if ($dir.Name -notmatch $cultureLike) { continue }
        if ($keepLower -contains $dir.Name.ToLowerInvariant()) { continue }

        # 安全闸：只删「里面全是 .mui」的目录。将来若框架往这些目录放了别的东西，
        # 这里会跳过并告警，避免误删真实资源。
        $children = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -File)
        $nonMui = @($children | Where-Object { $_.Extension -ne '.mui' })
        if ($nonMui.Count -gt 0) {
            $warnings.Add("跳过语言目录「$($dir.Name)」：含非 .mui 文件（$($nonMui[0].Name)）")
            continue
        }
        if ($children.Count -eq 0) {
            Remove-Item -LiteralPath $dir.FullName -Recurse -Force
            $removedDirs++
            continue
        }

        $size = ($children | Measure-Object -Property Length -Sum).Sum
        $removedBytes += $size
        $removed.Add("$($dir.Name)\*  ($($children.Count) 个 .mui, $([math]::Round($size / 1KB, 1)) KB)  非中文语言资源，无 .resw/.resx，文案全程硬编码中文")
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force
        $removedDirs++
    }
}

# ------------------------------------------------ 2. 具体文件规则
foreach ($rule in $StripFileRules) {
    $wildcard = $rule.Wildcard
    # 文件名通配在「发布根」和「任意子目录」两处都匹配（如 NpuDetect\NPUDetect.dll）
    $hits = @()
    if ($wildcard -like '*\*') {
        $hits = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter (Split-Path -Leaf $wildcard) -ErrorAction SilentlyContinue |
            Where-Object {
                $rel = [System.IO.Path]::GetRelativePath($PublishDir, $_.FullName)
                $rel -like $wildcard -or $rel -like "*$wildcard"
            })
    }
    else {
        $hits = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $wildcard -ErrorAction SilentlyContinue)
    }

    foreach ($hit in $hits) {
        [void](Remove-PrunedFile -File $hit -Reason "$($rule.Name)：$($rule.Reason)" -PublishRoot $PublishDir)
    }
}

# ------------------------------------------------ 3. *.winmd（默认保留）
if ($SkipWinmd) {
    foreach ($winmd in @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter '*.winmd')) {
        [void](Remove-PrunedFile -File $winmd -Reason 'WinRT 元数据（已经过 Verify-PrunedPublish.ps1 实测验证）' -PublishRoot $PublishDir)
    }
}

# ------------------------------------------------ 4. 清理空目录
$emptyDirs = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -Directory |
    Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File).Count -eq 0 } |
    Sort-Object { $_.FullName.Length } -Descending)
foreach ($d in $emptyDirs) {
    if (Test-Path -LiteralPath $d.FullName) {
        Remove-Item -LiteralPath $d.FullName -Recurse -Force
        $removedDirs++
    }
}

# ------------------------------------------------ 5. 必须保留项复核
# 清单里可能同时含文件名（WinUIEdit.dll）与通配（*.pri、*.winmd），所以两处都按通配查。
foreach ($rule in $MustKeep) {
    $any = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $rule.Wildcard)
    if ($any.Count -eq 0) {
        # 框架依赖发布下，一部分「必须保留项」由系统运行时提供，包内本来就没有，
        # 这不是问题；只有既没在包里、又不在系统提供清单里才是真异常。
        $isSystemProvided = @($MustKeepSystemProvided | Where-Object { $_ -eq $rule.Wildcard }).Count -gt 0
        if (-not $isSystemProvided) {
            $warnings.Add("必须保留项「$($rule.Wildcard)」在发布目录中不存在")
        }
    }
    $kept.Add("$($rule.Wildcard)  →  $($rule.Reason)")
}

# ------------------------------------------------ 6. 框架依赖模式的反向校验
# 这些文件由系统安装的 Windows App Runtime 提供，不该出现在框架依赖发布目录里。
# 一旦出现，最可能的原因是发布命令忘了传 --self-contained false（体积会从 37 MB 涨回 152 MB）。
foreach ($name in $MustKeepSystemProvided) {
    $hits = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue)
    if ($hits.Count -gt 0) {
        $warnings.Add("「$name」出现在发布目录里——本次发布可能不是框架依赖模式（检查 dotnet publish 是否传了 --self-contained false）")
    }
}

[PSCustomObject]@{
    RemovedFiles       = $removed.Count
    RemovedDirectories = $removedDirs
    RemovedBytes       = $removedBytes
    Removed            = $removed
    Kept               = $kept
    Warnings           = $warnings
}
