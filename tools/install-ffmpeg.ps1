$ErrorActionPreference = 'Stop'

$toolsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'ffmpeg'))
$binRoot = Join-Path $toolsRoot 'bin'
$ffmpeg = Join-Path $binRoot 'ffmpeg.exe'
$ffprobe = Join-Path $binRoot 'ffprobe.exe'
$buildId = 'n6.1.2-2-gb534cc666e-20240831'
$archiveName = 'ffmpeg-n6.1.2-2-gb534cc666e-win64-lgpl-shared-6.1.zip'
$archiveHash = 'f1c49b1016c24c82bc1677b23070bc0fd54db642c94d2ebea73d4219ee5b4dfd'
$downloadUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2024-08-31-12-50/ffmpeg-n6.1.2-2-gb534cc666e-win64-lgpl-shared-6.1.zip'
$runtimeFiles = @(
    'avcodec-60.dll',
    'avdevice-60.dll',
    'avfilter-9.dll',
    'avformat-60.dll',
    'avutil-58.dll',
    'swresample-4.dll',
    'swscale-7.dll'
)

if ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe)) {
    $installedVersion = & $ffmpeg -version 2>$null | Select-Object -First 1
    $runtimeComplete = -not ($runtimeFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $binRoot $_))
    })
    if (($installedVersion -like "*$buildId*") -and $runtimeComplete) {
        Write-Host "The pinned LGPL NVENC FFmpeg build is already available at $binRoot"
        exit 0
    }
}

$cacheRoot = Join-Path $PSScriptRoot 'cache'
$archive = Join-Path $cacheRoot $archiveName
$expanded = Join-Path $cacheRoot 'ffmpeg-lgpl-shared-6.1'
New-Item -ItemType Directory -Force -Path $cacheRoot, $binRoot | Out-Null

if (-not (Test-Path -LiteralPath $archive)) {
    Write-Host 'Downloading the pinned LGPL FFmpeg 6.1 build with NVENC support...'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archive
}

$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $archiveHash) {
    throw "The FFmpeg archive checksum did not match. Expected $archiveHash but received $actualHash."
}

if (-not (Test-Path -LiteralPath $expanded)) {
    Write-Host 'Extracting FFmpeg...'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
}

$distribution = Get-ChildItem -LiteralPath $expanded -Directory | Select-Object -First 1
if (-not $distribution) {
    throw 'The FFmpeg archive did not contain the expected distribution directory.'
}

$oldRuntimeFiles = Get-ChildItem -LiteralPath $binRoot -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(ffmpeg|ffprobe)\.exe$|^(avcodec|avdevice|avfilter|avformat|avutil|swresample|swscale)-\d+\.dll$' }
foreach ($file in $oldRuntimeFiles) {
    Remove-Item -LiteralPath $file.FullName -Force
}

Copy-Item -LiteralPath (Join-Path $distribution.FullName 'bin\ffmpeg.exe') -Destination $ffmpeg
Copy-Item -LiteralPath (Join-Path $distribution.FullName 'bin\ffprobe.exe') -Destination $ffprobe
foreach ($fileName in $runtimeFiles) {
    Copy-Item -LiteralPath (Join-Path $distribution.FullName "bin\$fileName") -Destination (Join-Path $binRoot $fileName)
}
Copy-Item -LiteralPath (Join-Path $distribution.FullName 'LICENSE.txt') -Destination (Join-Path $toolsRoot 'LICENSE-FFmpeg-LGPLv3.txt') -Force

Write-Host "Installed the pinned LGPL FFmpeg tools at $binRoot"
