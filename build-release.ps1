<#
.SYNOPSIS
    构建 LingeringDawn Frp 的发布压缩包。

.DESCRIPTION
    流程：确定版本号 → dotnet publish → 放入 frpc.exe 与 README → 打成 zip → 生成 SHA256。
    产物位于 <仓库根>\dist\ 下：

        LingeringDawnFrp-<版本>-<运行时>\            发布目录（解压后即为此结构）
        LingeringDawnFrp-<版本>-<运行时>.zip         分发包
        LingeringDawnFrp-<版本>-<运行时>.zip.sha256  校验值

.PARAMETER Version
    版本号。不填则读取 csproj 里的 <Version>。

.PARAMETER Runtime
    目标运行时，默认 win-x64。

.PARAMETER SelfContained
    自包含发布（体积大很多，但目标机无需安装 .NET 8 桌面运行时）。

.PARAMETER SkipZip
    只发布、不打包（调试打包脚本时用）。

.EXAMPLE
    .\build-release.ps1

.EXAMPLE
    .\build-release.ps1 -Version 1.2.0 -SelfContained
#>
[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SelfContained,

    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "  $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "  $Text" }

$repoRoot = $PSScriptRoot
$projectDir = Join-Path $repoRoot 'LingeringDawnFrp'
$csproj = Join-Path $projectDir 'LingeringDawnFrp.csproj'
$distDir = Join-Path $repoRoot 'dist'

if (-not (Test-Path -LiteralPath $csproj)) {
    throw "找不到项目文件：$csproj"
}

# ---------------------------------------------------------------- 版本号
Write-Step '确定版本号'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $match = Select-String -LiteralPath $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    $Version = if ($match) { $match.Matches[0].Groups[1].Value.Trim() } else { '1.0.0' }
}

Write-Info "版本：$Version"
Write-Info "运行时：$Runtime（自包含：$($SelfContained.IsPresent)）"

# ---------------------------------------------------------------- frpc.exe
Write-Step '查找 frpc.exe'

$frpcCandidates = @(
    (Join-Path $projectDir 'frpc.exe'),
    (Join-Path $projectDir 'bin\Debug\net8.0-windows\frpc.exe'),
    (Join-Path $projectDir "bin\Release\net8.0-windows\$Runtime\publish\frpc.exe"),
    (Join-Path $projectDir "bin\Release\net8.0-windows\frpc.exe")
)

$frpcPath = $frpcCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $frpcPath) {
    throw @"
未找到 frpc.exe，发布包必须带上它。
请把 frpc.exe 放到以下任一位置后重试：
$(($frpcCandidates | ForEach-Object { "  - $_" }) -join "`n")
"@
}

Write-Ok "使用：$frpcPath（$([math]::Round((Get-Item -LiteralPath $frpcPath).Length / 1MB, 2)) MB）"

# ---------------------------------------------------------------- 发布
Write-Step 'dotnet publish'

$stageName = "LingeringDawnFrp-$Version-$Runtime"
$stageDir = Join-Path $distDir $stageName

if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$publishArgs = @(
    'publish', $csproj,
    '-c', 'Release',
    '-r', $Runtime,
    "--self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
    "-p:Version=$Version",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $stageDir,
    '--nologo'
)

Write-Info "dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

# ---------------------------------------------------------------- 补充文件
Write-Step '整理发布目录'

Copy-Item -LiteralPath $frpcPath -Destination (Join-Path $stageDir 'frpc.exe') -Force
Write-Ok '已放入 frpc.exe'

$readme = Join-Path $projectDir 'README.md'
if (Test-Path -LiteralPath $readme) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $stageDir 'README.md') -Force
    Write-Ok '已放入 README.md'
}

$pdbCount = 0
Get-ChildItem -LiteralPath $stageDir -Filter '*.pdb' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -LiteralPath $_.FullName -Force
    $pdbCount++
}
if ($pdbCount -gt 0) { Write-Ok "已清理 $pdbCount 个 .pdb 调试符号文件" }

$exePath = Join-Path $stageDir 'LingeringDawnFrp.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "发布目录里没有 LingeringDawnFrp.exe，发布可能未成功：$stageDir"
}
Write-Ok '已确认 LingeringDawnFrp.exe 存在'

# ---------------------------------------------------------------- 打包
if ($SkipZip) {
    Write-Step '已跳过打包（-SkipZip）'
    Write-Info "发布目录：$stageDir"
    exit 0
}

Write-Step '生成压缩包'

$zipPath = Join-Path $distDir "$stageName.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# -Path 传目录本身，zip 内会带一层同名根目录，解压更整洁
Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Ok "已生成：$zipPath"

$zipItem = Get-Item -LiteralPath $zipPath
Write-Info "大小：$([math]::Round($zipItem.Length / 1MB, 2)) MB"

# ---------------------------------------------------------------- 校验值
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$hashFile = "$zipPath.sha256"
"$hash  $($zipItem.Name)" | Set-Content -LiteralPath $hashFile -Encoding ascii
Write-Ok "SHA256：$hash"

# ---------------------------------------------------------------- 结果核对
Write-Step '结果核对'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = $archive.Entries
    Write-Info "压缩包内条目数：$($entries.Count)"

    foreach ($required in @('frpc.exe', 'LingeringDawnFrp.exe', 'README.md', 'frpc.ini.placeholder')) {
        if ($required -eq 'frpc.ini.placeholder') { continue }
        $hit = $entries | Where-Object { $_.FullName -like "*/$required" } | Select-Object -First 1
        if ($hit) {
            Write-Ok "包含 $required（$([math]::Round($hit.Length / 1KB, 1)) KB）"
        }
        else {
            Write-Host "  缺少 $required ！" -ForegroundColor Red
        }
    }
}
finally {
    $archive.Dispose()
}

$fileCount = (Get-ChildItem -LiteralPath $stageDir -Recurse -File).Count

Write-Host ''
Write-Host '打包完成。' -ForegroundColor Green
Write-Info "发布目录：$stageDir（$fileCount 个文件）"
Write-Info "压缩包　：$zipPath"
Write-Info "校验值　：$hashFile"
