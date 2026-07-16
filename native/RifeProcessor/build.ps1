$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cacheRoot = Join-Path $repoRoot 'tools\cache\rife-worker'
$archiveRoot = Join-Path $cacheRoot 'archives'
$sourceRoot = Join-Path $cacheRoot 'rife-src'
$buildRoot = Join-Path $repoRoot 'obj\rife-worker-build'
$vulkanRoot = Join-Path $cacheRoot 'vulkan-headers'
$vulkanLibRoot = Join-Path $cacheRoot 'vulkan-loader'

$rifeCommit = 'a7532fc3f9f8f008cd6eecd6f2ffe2a9698e0cf7'
$ncnnCommit = 'b4ba207c18d3103d6df890c0e3a97b469b196b26'
$webpCommit = '5abb55823bb6196a918dd87202b2f32bbaff4c18'
$glslangCommit = '86ff4bca1ddc7e2262f119c16e7228d0efb67610'

function Assert-UnderRepo([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
        $repoRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped the repository: $fullPath"
    }
    return $fullPath
}

function Reset-Directory([string] $Path) {
    $fullPath = Assert-UnderRepo $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $fullPath | Out-Null
    return $fullPath
}

function Download-Verified(
    [string] $Url,
    [string] $Destination,
    [string] $ExpectedHash) {
    $destinationPath = Assert-UnderRepo $Destination
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
    if (-not (Test-Path -LiteralPath $destinationPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedHash) {
        Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
        Invoke-WebRequest -Uri $Url -OutFile $destinationPath -Headers @{ 'User-Agent' = 'LocalVSR-build' }
    }
    $actualHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash) {
        throw "Checksum mismatch for $destinationPath. Expected $ExpectedHash but received $actualHash."
    }
    return $destinationPath
}

function Expand-SingleRootArchive([string] $Archive, [string] $Destination) {
    $destinationPath = Reset-Directory $Destination
    $temporaryPath = Reset-Directory ($destinationPath + '-extract')
    try {
        Expand-Archive -LiteralPath $Archive -DestinationPath $temporaryPath -Force
        $roots = @(Get-ChildItem -LiteralPath $temporaryPath -Directory)
        if ($roots.Count -ne 1) {
            throw "Expected one root directory in $Archive."
        }
        Copy-Item -Path (Join-Path $roots[0].FullName '*') -Destination $destinationPath -Recurse -Force
    }
    finally {
        $verifiedTemporaryPath = Assert-UnderRepo $temporaryPath
        if (Test-Path -LiteralPath $verifiedTemporaryPath) {
            Remove-Item -LiteralPath $verifiedTemporaryPath -Recurse -Force
        }
    }
    return $destinationPath
}

New-Item -ItemType Directory -Force -Path $cacheRoot, $archiveRoot | Out-Null

$sourceMarker = Join-Path $sourceRoot '.localvsr-rife-commit'
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'src\rife.cpp') -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceMarker -PathType Leaf) -or
    (Get-Content -LiteralPath $sourceMarker -Raw).Trim() -ne $rifeCommit) {
    Write-Host 'Fetching the pinned RIFE source without its large model history...'
    Reset-Directory $sourceRoot | Out-Null
    git -C $sourceRoot init --quiet
    git -C $sourceRoot remote add origin https://github.com/nihui/rife-ncnn-vulkan.git
    git -C $sourceRoot sparse-checkout init --cone
    git -C $sourceRoot sparse-checkout set src
    git -C $sourceRoot -c core.longpaths=true fetch --quiet --depth 1 --filter=blob:none origin $rifeCommit
    git -C $sourceRoot checkout --quiet --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0 -or (git -C $sourceRoot rev-parse HEAD).Trim() -ne $rifeCommit) {
        throw 'The pinned RIFE source checkout failed.'
    }
    [IO.File]::WriteAllText($sourceMarker, "$rifeCommit`n")
}

$ncnnArchive = Download-Verified `
    "https://github.com/Tencent/ncnn/archive/$ncnnCommit.zip" `
    (Join-Path $archiveRoot 'ncnn-b4ba207.zip') `
    '2254297dfbadcf8ccc46fad783fd471e889faef6a6e61e91aef25143c679c9b6'
$webpArchive = Download-Verified `
    "https://github.com/webmproject/libwebp/archive/$webpCommit.zip" `
    (Join-Path $archiveRoot 'libwebp-5abb558.zip') `
    '51b485c729ac4b3e020c4eef122c6474099844e3102997411c938774f4b1a69f'
$glslangArchive = Download-Verified `
    "https://github.com/KhronosGroup/glslang/archive/$glslangCommit.zip" `
    (Join-Path $archiveRoot 'glslang-86ff4bc.zip') `
    '9c65247a1634f41909ce5f2df07a7af4c476fe8b9c4532edf33ad27fcb0b376a'
$vulkanArchive = Download-Verified `
    'https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/v1.3.239.zip' `
    (Join-Path $archiveRoot 'Vulkan-Headers-v1.3.239.zip') `
    'c856f1b7be986159b4dcc3687902a54d208993449ed41c12b6c2e51be5cfa430'

$ncnnRoot = Join-Path $sourceRoot 'src\ncnn'
$webpRoot = Join-Path $sourceRoot 'src\libwebp'
if (-not (Test-Path -LiteralPath (Join-Path $ncnnRoot 'CMakeLists.txt') -PathType Leaf)) {
    Write-Host 'Expanding the pinned ncnn and glslang sources...'
    Expand-SingleRootArchive $ncnnArchive $ncnnRoot | Out-Null
    Expand-SingleRootArchive $glslangArchive (Join-Path $ncnnRoot 'glslang') | Out-Null
}
if (-not (Test-Path -LiteralPath (Join-Path $webpRoot 'CMakeLists.txt') -PathType Leaf)) {
    Write-Host 'Expanding the pinned libwebp source...'
    Expand-SingleRootArchive $webpArchive $webpRoot | Out-Null
}
if (-not (Test-Path -LiteralPath (Join-Path $vulkanRoot 'include\vulkan\vulkan.h') -PathType Leaf)) {
    Write-Host 'Expanding the pinned Vulkan headers...'
    Expand-SingleRootArchive $vulkanArchive $vulkanRoot | Out-Null
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw 'Visual Studio C++ build tools were not found.'
}
$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$nativeToolRoot = Get-ChildItem -LiteralPath (Join-Path $vsPath 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64' } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'dumpbin.exe') } |
    Select-Object -First 1
if (-not $nativeToolRoot -or -not (Test-Path -LiteralPath $cmake -PathType Leaf)) {
    throw 'The Visual Studio CMake or x64 native tools were not found.'
}

New-Item -ItemType Directory -Force -Path $vulkanLibRoot | Out-Null
$vulkanDef = Join-Path $vulkanLibRoot 'vulkan-1.def'
$vulkanLib = Join-Path $vulkanLibRoot 'vulkan-1.lib'
$dumpbin = Join-Path $nativeToolRoot 'dumpbin.exe'
$libexe = Join-Path $nativeToolRoot 'lib.exe'
$exports = @(& $dumpbin /nologo /exports "$env:WINDIR\System32\vulkan-1.dll" |
    ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)') { $Matches[1] }
    } | Sort-Object -Unique)
if ($exports.Count -lt 100) {
    throw "Only $($exports.Count) Vulkan loader exports were found."
}
[IO.File]::WriteAllLines($vulkanDef, @('LIBRARY vulkan-1.dll', 'EXPORTS') +
    @($exports | ForEach-Object { "    $_" }))
& $libexe /nologo "/def:$vulkanDef" /machine:x64 "/out:$vulkanLib"
if ($LASTEXITCODE -ne 0) {
    throw 'The Vulkan loader import library could not be generated.'
}

Write-Host 'Building the persistent RIFE streaming worker...'
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$cmakeRifeSource = ([IO.Path]::GetFullPath((Join-Path $sourceRoot 'src'))).Replace('\', '/')
$cmakeVulkanHeaders = ([IO.Path]::GetFullPath((Join-Path $vulkanRoot 'include'))).Replace('\', '/')
$cmakeVulkanLibrary = ([IO.Path]::GetFullPath($vulkanLib)).Replace('\', '/')
& $cmake -S $PSScriptRoot -B $buildRoot -G 'Visual Studio 17 2022' -A x64 `
    "-DRIFE_SOURCE_DIR=$cmakeRifeSource" `
    "-DVulkan_INCLUDE_DIR=$cmakeVulkanHeaders" `
    "-DVulkan_LIBRARY=$cmakeVulkanLibrary"
if ($LASTEXITCODE -ne 0) {
    throw "RifeProcessor CMake configuration failed with exit code $LASTEXITCODE."
}
& $cmake --build $buildRoot --config Release --target RifeProcessor --parallel 8 -- /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    throw "RifeProcessor build failed with exit code $LASTEXITCODE."
}

$builtWorker = Join-Path $buildRoot 'Release\RifeProcessor.exe'
$outputWorker = Join-Path $PSScriptRoot 'RifeProcessor.exe'
if (-not (Test-Path -LiteralPath $builtWorker -PathType Leaf)) {
    throw "The RifeProcessor build did not produce $builtWorker."
}
Copy-Item -LiteralPath $builtWorker -Destination $outputWorker -Force
Write-Host "Built $outputWorker"
