$ErrorActionPreference = 'Stop'

$toolsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'rife'))
$modelRoot = Join-Path $toolsRoot 'rife-v4.6'
$cacheRoot = Join-Path $PSScriptRoot 'cache'
$archiveName = 'rife-ncnn-vulkan-20221029-windows.zip'
$archive = Join-Path $cacheRoot $archiveName
$archiveHash = 'd8e4d772d26cd8006ef0ad0bc82eb191b53c68677d1ae2f42506d74cbbbea606'
$downloadUrl = 'https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip'
$rootInArchive = 'rife-ncnn-vulkan-20221029-windows/'

$requiredFiles = @{
    'rife-ncnn-vulkan.exe' = '4b970319db2814c82b15fceed8193151560a676a9eb63f20d4877be77b98f44f'
    'vcomp140.dll' = '54fe6b087528b33c2969143d811eb62f1bd49071d37de9db0745fc079764d698'
    'rife-v4.6\flownet.bin' = 'f334ed2260149ce0188a6dcf049844e8b0cdd912e01cbcfb63553157d2508958'
    'rife-v4.6\flownet.param' = '28df14d57a225725ee5386f52eba422488450d37c9f40800ed4f62e8ba846692'
}

$installed = $true
foreach ($relativePath in $requiredFiles.Keys) {
    $path = Join-Path $toolsRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $requiredFiles[$relativePath]) {
        $installed = $false
        break
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $toolsRoot 'LICENSE-RIFE-MIT.txt') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $toolsRoot 'README-RIFE.md') -PathType Leaf)) {
    $installed = $false
}
if ($installed) {
    Write-Host "The pinned Vulkan frame-interpolation runtime is already available at $toolsRoot"
    exit 0
}

New-Item -ItemType Directory -Force -Path $cacheRoot, $toolsRoot, $modelRoot | Out-Null
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Write-Host 'Downloading the pinned RIFE ncnn Vulkan runtime and model...'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archive -Headers @{ 'User-Agent' = 'LocalVSR-build' }
}

$actualArchiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $archiveHash) {
    throw "The RIFE archive checksum did not match. Expected $archiveHash but received $actualArchiveHash."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @{
    ($rootInArchive + 'rife-ncnn-vulkan.exe') = (Join-Path $toolsRoot 'rife-ncnn-vulkan.exe')
    ($rootInArchive + 'vcomp140.dll') = (Join-Path $toolsRoot 'vcomp140.dll')
    ($rootInArchive + 'rife-v4.6/flownet.bin') = (Join-Path $modelRoot 'flownet.bin')
    ($rootInArchive + 'rife-v4.6/flownet.param') = (Join-Path $modelRoot 'flownet.param')
    ($rootInArchive + 'LICENSE') = (Join-Path $toolsRoot 'LICENSE-RIFE-MIT.txt')
    ($rootInArchive + 'README.md') = (Join-Path $toolsRoot 'README-RIFE.md')
}

$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entryPath in $entries.Keys) {
        $entry = $zip.GetEntry($entryPath)
        if (-not $entry) { throw "The RIFE archive did not contain $entryPath." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $entries[$entryPath], $true)
    }
}
finally {
    $zip.Dispose()
}

foreach ($relativePath in $requiredFiles.Keys) {
    $path = Join-Path $toolsRoot $relativePath
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $requiredFiles[$relativePath]) {
        throw "The installed RIFE file $relativePath failed checksum verification."
    }
}

Write-Host "Installed the pinned Vulkan frame-interpolation runtime at $toolsRoot"
