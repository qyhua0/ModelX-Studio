# =============================================================================
# 作者：zhenhua
# 功能：一次性把 llama.cpp 官方 Windows 预编译构建取到 runtime\llama，让引擎随程序分发。
#       程序运行时不再联网下载引擎，所以 clone 仓库或打包发布前要先跑这个脚本。
#       默认只取 CPU 版；-Cuda / -Vulkan 各再加一份加速后端（都可以跟 -CPU 一起用）。
#       只提取 llama-server.exe、*.dll 与上游 LICENSE* 文本，其余命令行工具不落地。
#       同时写 runtime\llama\VERSION.txt 记录构建号 / 资产名 / SHA256 / 时间，程序界面
#       显示的引擎版本号就读这个文件。
# 用法：powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1
#       powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Vulkan
#       powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Cuda -Vulkan
#       powershell -ExecutionPolicy Bypass -File scripts\get-llama.ps1 -Build 12387 -Proxy http://127.0.0.1:7890
#       参数一个都不给 = 只装 CPU 版。旧参数 -Cuda 含义不变，可以照旧用。
# 许可：本脚本随 ModelX Studio 一起分发，禁止商用（见仓库根 LICENSE）。
#       下载的 llama.cpp 二进制按上游 MIT 许可分发（见 runtime\llama\LICENSE-MIT-llama-cpp.txt）。
# =============================================================================
#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Cuda,
    [switch]$Vulkan,
    [string]$Build = '',
    [string]$CudaAssetName = '',
    [string]$Proxy = '',
    [string]$OutRoot = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($OutRoot)) {
    $OutRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime\llama'
}

# GitHub REST API 必须带 User-Agent，.NET 的 HttpClient / Invoke-* 默认不发，一律 403
$ua = 'ModelX Studio/1.0 (llama.cpp engine fetcher)'
$api = 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15'

function Get-RemoteFile($url, $dest, $what) {
    $p = @{ Uri = $url; OutFile = $dest; UseBasicParsing = $true; Headers = @{ 'User-Agent' = $ua } }
    if ($Proxy -ne '') { $p['Proxy'] = $Proxy }
    try {
        Invoke-WebRequest @p
    } catch {
        throw ("下载 {0} 失败：{1}`n  地址 {2}`n常见原因：没有外网、公司代理需要 -Proxy、或该构建没有这个资产（换 -Build 指定别的构建号）。" -f $what, $_.Exception.Message, $url)
    }
}

function Install-Engine($version, $assetName, $subDir, $downloadUrl) {
    if ([string]::IsNullOrWhiteSpace($downloadUrl)) {
        throw ("内部错误：{0} 没有解析到下载地址。" -f $assetName)
    }
    $dest = Join-Path $OutRoot $subDir
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $zip = Join-Path $env:TEMP $assetName
    $unz = Join-Path $env:TEMP ($assetName + '.unzip')

    Write-Host ("下载 {0} …" -f $assetName) -NoNewline
    Get-RemoteFile $downloadUrl $zip $assetName
    $sha = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
    Write-Host (" 完成，SHA256 {0}…" -f $sha.Substring(0, 12))

    if (Test-Path $unz) { Remove-Item $unz -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $unz -Force

    # 只要服务端可执行文件 + 依赖 dll + 上游许可文本；官方 zip 里还有一层
    # llama-bXXXX-bin-win-cpu-x64 目录，这里展平到 runtime\llama\<subDir>
    $keep = Get-ChildItem -Path $unz -Recurse -File | Where-Object {
        $_.Name -eq 'llama-server.exe' -or $_.Extension -eq '.dll' -or $_.Name -like 'LICENSE*'
    }
    if (-not ($keep | Where-Object { $_.Name -eq 'llama-server.exe' })) {
        throw ("{0} 里没找到 llama-server.exe，资产结构可能变了，请手动解压。" -f $assetName)
    }
    $bytes = 0
    $count = 0
    foreach ($f in $keep) {
        Copy-Item -Path $f.FullName -Destination (Join-Path $dest $f.Name) -Force
        if ($f.Name -notlike 'LICENSE*') { $bytes += $f.Length }
        $count += 1
    }

    # MIT 的条件就是每个副本都带着许可文本。上游 zip 里的 LICENSE 万一缺失，
    # 用仓库里那份兜底，绝不允许「只拷 exe 和 dll」。
    if (-not (Get-ChildItem -Path $dest -Filter 'LICENSE*' -File | Where-Object { $_.Length -gt 500 })) {
        $fallback = Join-Path $OutRoot 'LICENSE-MIT-llama-cpp.txt'
        if (Test-Path $fallback) {
            Copy-Item -Path $fallback -Destination (Join-Path $dest 'LICENSE-llama-cpp.txt') -Force
            Write-Host '  （zip 里没有许可文本，已用仓库内那份补上）'
        } else {
            Write-Warning ("请手动把 llama.cpp 的 MIT 许可文本放进 {0}，MIT 要求每个副本都带许可。" -f $dest)
        }
    }

    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Remove-Item $unz -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ("  已放好 {0} 个文件，二进制共 {1:N1} MB -> {2}" -f $count, ($bytes / 1MB), $dest)
    return [pscustomobject]@{ Variant = $subDir; Asset = $assetName; Sha256 = $sha; Bytes = $bytes }
}

function Get-DownloadUrl($version, $assetName) {
    'https://github.com/ggml-org/llama.cpp/releases/download/{0}/{1}' -f $version, $assetName
}

Write-Host 'ModelX Studio · llama.cpp 引擎取包'
Write-Host ("目标目录 {0}" -f $OutRoot)

$version = ''
$cpuAsset = ''
$cpuUrl = ''
$cudaAsset = ''
$cudaUrl = ''
$vulkanAsset = ''
$vulkanUrl = ''

if ($Build -ne '') {
    # 已知构建号：直接拼下载直链，不依赖 API（API 限流时也能用）
    $version = 'b' + ($Build -replace '^b', '')
    $cpuAsset = 'llama-{0}-bin-win-cpu-x64.zip' -f $version
    $cpuUrl = Get-DownloadUrl $version $cpuAsset
    if ($Cuda) {
        $cudaAsset = if ($CudaAssetName -ne '') { $CudaAssetName } else { 'llama-{0}-bin-win-cuda-12.4-x64.zip' -f $version }
        $cudaUrl = Get-DownloadUrl $version $cudaAsset
    }
    if ($Vulkan) {
        $vulkanAsset = 'llama-{0}-bin-win-vulkan-x64.zip' -f $version
        $vulkanUrl = Get-DownloadUrl $version $vulkanAsset
    }
} else {
    Write-Host '正在查询 llama.cpp 最新版本…'
    try {
        $releases = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = $ua; 'Accept' = 'application/vnd.github+json' }
    } catch {
        throw ("查询 GitHub Releases 失败：{0}`n常见原因：无外网、公司代理、或该 API 每小时 60 次限流。`n可以改成手动指定构建号：-Build 12387（直链下载不需要 API），或用 -Proxy http://127.0.0.1:7890" -f $_.Exception.Message)
    }
    $rel = $releases | Where-Object { -not $_.prerelease } | Select-Object -First 1
    if (-not $rel) { $rel = $releases | Select-Object -First 1 }
    if (-not $rel) { throw 'GitHub 返回了空的 release 列表。' }

    $version = $rel.tag_name

    $cpu = $rel.assets | Where-Object { $_.name -match '^llama-b\d+-bin-win-cpu-x64\.zip$' } | Select-Object -First 1
    if (-not $cpu) { throw ("{0} 里没有 CPU 版资产。" -f $version) }
    $cpuAsset = $cpu.name
    $cpuUrl = $cpu.browser_download_url

    if ($Cuda) {
        # 同一构建常有 cuda-11 / 12.x / 13.x 多个资产。程序在多个 CUDA 版之间固定选
        # 主版本号最小的那个（对驱动版本要求最宽），所以这里也优先 12，再退 11，最后退任意。
        $cuda = $rel.assets | Where-Object { $_.name -match '^llama-b\d+-bin-win-cuda' -and $_.name -match 'cuda-12' } | Select-Object -First 1
        if (-not $cuda) { $cuda = $rel.assets | Where-Object { $_.name -match '^llama-b\d+-bin-win-cuda' } | Select-Object -First 1 }
        if ($cuda) { $cudaAsset = $cuda.name; $cudaUrl = $cuda.browser_download_url }
        else { Write-Warning '该构建没有 CUDA 版资产，跳过。' }
    }

    if ($Vulkan) {
        # Vulkan 版覆盖 A 卡 / Intel 核显 / N 卡，是不装 N 卡时最划算的加速后端
        $vk = $rel.assets | Where-Object { $_.name -match '^llama-b\d+-bin-win-vulkan-x64\.zip$' } | Select-Object -First 1
        if ($vk) { $vulkanAsset = $vk.name; $vulkanUrl = $vk.browser_download_url }
        else { Write-Warning '该构建没有 Vulkan 版资产，跳过。' }
    }
}

$records = @()
$records += Install-Engine $version $cpuAsset 'cpu' $cpuUrl

if ($cudaAsset -ne '') {
    # 目录名带上 CUDA 主版本号，两份 CUDA 并存时程序知道该挑哪一份
    $cuFlavor = 'cuda'
    if ($cudaAsset -match 'cuda[-_]?(\d+(?:\.\d+)?)') { $cuFlavor = 'cuda-' + $Matches[1] + '-x64' }
    $records += Install-Engine $version $cudaAsset (Join-Path 'cuda' $cuFlavor) $cudaUrl
}

if ($vulkanAsset -ne '') {
    $records += Install-Engine $version $vulkanAsset 'vulkan' $vulkanUrl
}

# VERSION.txt：程序用它显示引擎版本，也方便复盘装的是哪一次构建。
# 注意程序用正则 b\d{3,6} 取第一个匹配，所以本文件里第一处 b+数字 必须就是构建号。
$lines = @(
    '# ModelX Studio 的推理引擎记录（由 scripts/get-llama.ps1 生成，手工放置也可以照这个格式写）',
    'version={0}' -f $version,
    'updated={0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
    'source=https://github.com/ggml-org/llama.cpp/releases/tag/{0}' -f $version,
    'license=MIT (https://github.com/ggml-org/llama.cpp/blob/master/LICENSE)'
)
foreach ($r in $records) {
    $lines += ''
    $lines += '[{0}]' -f $r.Variant
    $lines += 'asset={0}' -f $r.Asset
    $lines += 'sha256={0}' -f $r.Sha256
    $lines += 'zip_bytes={0}' -f $r.Bytes
}
$lines += ''
$lines += 'note=llama.cpp 二进制按上游 MIT 许可随包分发；每个变体目录都要带许可文本，见 THIRD_PARTY_NOTICES.md。'
Set-Content -Path (Join-Path $OutRoot 'VERSION.txt') -Value $lines -Encoding UTF8

Write-Host ''
Write-Host ("完成。引擎构建 {0}" -f $version)
foreach ($r in $records) { Write-Host ("  {0}" -f (Join-Path $OutRoot $r.Variant)) }
Write-Host '接下来：重新构建/启动 ModelX Studio，「模型库」页面顶部的缺失提示就会消失。'
if (-not ($Cuda -or $Vulkan)) {
    Write-Host '提示：再加 -Vulkan 可以同时覆盖 A 卡 / Intel 核显（约 100 MB），加 -Cuda 覆盖 NVIDIA（约 700 MB）。'
}
