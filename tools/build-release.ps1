param(
    [string]$Version = '0.1.0-beta'
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = Join-Path $repoRoot 'dist'
$publishRoot = Join-Path $repoRoot 'bin\release-package-publish'
$packageName = "LocalVSR-$Version-win-x64"
$packageRoot = Join-Path $distRoot $packageName
$zipPath = Join-Path $distRoot "$packageName.zip"
$sourceRoot = Join-Path $distRoot 'third-party-source'
$cacheRoot = Join-Path $PSScriptRoot 'cache\release-sources'
$runtimeVersion = '8.0.29'

function Assert-UnderRepo([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
        $repoRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped the repository: $fullPath"
    }
    return $fullPath
}

function Reset-Directory([string]$Path) {
    $fullPath = Assert-UnderRepo $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $fullPath | Out-Null
}

function Copy-RequiredFile([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required release file was not found: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Download-Once([string]$Url, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        return
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Invoke-WebRequest -Uri $Url -OutFile $Destination -Headers @{ 'User-Agent' = 'LocalVSR-release-builder' }
}

Write-Host 'Installing the pinned LGPL FFmpeg runtime...'
& (Join-Path $PSScriptRoot 'install-ffmpeg.ps1')

Write-Host 'Building the native VSR worker...'
& (Join-Path $repoRoot 'native\VsrProcessor\build.ps1')

Write-Host 'Publishing the self-contained LocalVSR application...'
Reset-Directory $publishRoot
dotnet publish (Join-Path $repoRoot 'RtxLocalVideo.csproj') `
    -c Release `
    -o $publishRoot `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Reset-Directory $packageRoot
$licenseRoot = Join-Path $packageRoot 'licenses'
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null

Copy-RequiredFile (Join-Path $publishRoot 'LocalVSR.exe') (Join-Path $packageRoot 'LocalVSR.exe')
Copy-RequiredFile (Join-Path $repoRoot 'native\VsrProcessor\VsrProcessor.exe') (Join-Path $packageRoot 'VsrProcessor.exe')

$ffmpegRoot = Join-Path $repoRoot 'tools\ffmpeg'
$ffmpegBin = Join-Path $ffmpegRoot 'bin'
$ffmpegFiles = @(
    'ffmpeg.exe',
    'ffprobe.exe',
    'avcodec-60.dll',
    'avdevice-60.dll',
    'avfilter-9.dll',
    'avformat-60.dll',
    'avutil-58.dll',
    'swresample-4.dll',
    'swscale-7.dll'
)
foreach ($fileName in $ffmpegFiles) {
    Copy-RequiredFile (Join-Path $ffmpegBin $fileName) (Join-Path $packageRoot $fileName)
}

Copy-RequiredFile (Join-Path $repoRoot 'README.md') (Join-Path $packageRoot 'README.md')
Copy-RequiredFile (Join-Path $repoRoot 'LICENSE') (Join-Path $packageRoot 'LICENSE')
Copy-RequiredFile (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.md')
Copy-RequiredFile (Join-Path $ffmpegRoot 'LICENSE-FFmpeg-LGPLv3.txt') (Join-Path $licenseRoot 'FFmpeg-LGPLv3.txt')

$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$coreRuntimeRoot = Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
$desktopRuntimeRoot = Join-Path $nugetRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion"
Copy-RequiredFile (Join-Path $coreRuntimeRoot 'LICENSE.TXT') (Join-Path $licenseRoot 'Microsoft-.NET-Runtime-MIT.txt')
Copy-RequiredFile (Join-Path $coreRuntimeRoot 'THIRD-PARTY-NOTICES.TXT') (Join-Path $licenseRoot 'Microsoft-.NET-Runtime-Third-Party-Notices.txt')
Copy-RequiredFile (Join-Path $desktopRuntimeRoot 'LICENSE') (Join-Path $licenseRoot 'Microsoft-Windows-Desktop-Runtime-MIT.txt')

Write-Host 'Preparing corresponding-source archives...'
Reset-Directory $sourceRoot
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

$ffmpegSourceName = 'FFmpeg-b534cc666e0a770a4bb474d71569378635e9d464.tar.gz'
$buildScriptsName = 'BtbN-FFmpeg-Builds-autobuild-2024-08-31-12-50.tar.gz'
$ffmpegSourceCache = Join-Path $cacheRoot $ffmpegSourceName
$buildScriptsCache = Join-Path $cacheRoot $buildScriptsName
Download-Once `
    'https://api.github.com/repos/FFmpeg/FFmpeg/tarball/b534cc666e0a770a4bb474d71569378635e9d464' `
    $ffmpegSourceCache
Download-Once `
    'https://api.github.com/repos/BtbN/FFmpeg-Builds/tarball/autobuild-2024-08-31-12-50' `
    $buildScriptsCache

Copy-RequiredFile $ffmpegSourceCache (Join-Path $sourceRoot $ffmpegSourceName)
Copy-RequiredFile $buildScriptsCache (Join-Path $sourceRoot $buildScriptsName)
Copy-RequiredFile (Join-Path $repoRoot 'third_party\ffmpeg\SOURCE.md') (Join-Path $sourceRoot 'README.md')

foreach ($sourceArchive in Get-ChildItem -LiteralPath $sourceRoot -File -Filter '*.tar.gz') {
    $hash = (Get-FileHash -LiteralPath $sourceArchive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$($sourceArchive.FullName).sha256", "$hash  $($sourceArchive.Name)`n")
}

if (Test-Path -LiteralPath $zipPath) {
    $verifiedZip = Assert-UnderRepo $zipPath
    Remove-Item -LiteralPath $verifiedZip -Force
}
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$zipHash  $([IO.Path]::GetFileName($zipPath))`n")

Write-Host "Portable release: $zipPath"
Write-Host "SHA-256: $zipHash"
Write-Host "Corresponding source: $sourceRoot"
