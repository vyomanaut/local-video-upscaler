param(
    [string]$Version = '0.2.0-beta'
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = Join-Path $repoRoot 'dist'
$publishRoot = Join-Path $repoRoot 'obj\release-package-publish'
$cliPublishRoot = Join-Path $repoRoot 'obj\release-cli-publish'
$binRoot = Join-Path $repoRoot 'bin'
$latestRoot = Join-Path $binRoot 'Release'
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

Write-Host 'Installing the pinned Vulkan frame-interpolation runtime...'
& (Join-Path $PSScriptRoot 'install-rife.ps1')

Write-Host 'Building the persistent RIFE streaming worker...'
& (Join-Path $repoRoot 'native\RifeProcessor\build.ps1')

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

Write-Host 'Publishing the self-contained LocalVSR CLI...'
Reset-Directory $cliPublishRoot
dotnet publish (Join-Path $repoRoot 'cli\LocalVSR.Cli.csproj') `
    -c Release `
    -o $cliPublishRoot `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "LocalVSR CLI publish failed with exit code $LASTEXITCODE."
}

Reset-Directory $packageRoot
$licenseRoot = Join-Path $packageRoot 'licenses'
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null

Copy-RequiredFile (Join-Path $publishRoot 'LocalVSR.exe') (Join-Path $packageRoot 'LocalVSR.exe')
Copy-RequiredFile (Join-Path $cliPublishRoot 'LocalVSR.Cli.exe') (Join-Path $packageRoot 'LocalVSR.Cli.exe')
Copy-RequiredFile (Join-Path $repoRoot 'native\VsrProcessor\VsrProcessor.exe') (Join-Path $packageRoot 'VsrProcessor.exe')
Copy-RequiredFile (Join-Path $repoRoot 'native\RifeProcessor\RifeProcessor.exe') (Join-Path $packageRoot 'RifeProcessor.exe')

$rifeRoot = Join-Path $repoRoot 'tools\rife'
$rifeModelRoot = Join-Path $packageRoot 'rife-v4.6'
New-Item -ItemType Directory -Force -Path $rifeModelRoot | Out-Null
Copy-RequiredFile (Join-Path $rifeRoot 'rife-ncnn-vulkan.exe') (Join-Path $packageRoot 'rife-ncnn-vulkan.exe')
Copy-RequiredFile (Join-Path $rifeRoot 'vcomp140.dll') (Join-Path $packageRoot 'vcomp140.dll')
Copy-RequiredFile (Join-Path $rifeRoot 'rife-v4.6\flownet.bin') (Join-Path $rifeModelRoot 'flownet.bin')
Copy-RequiredFile (Join-Path $rifeRoot 'rife-v4.6\flownet.param') (Join-Path $rifeModelRoot 'flownet.param')

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
Copy-RequiredFile (Join-Path $rifeRoot 'LICENSE-RIFE-MIT.txt') (Join-Path $licenseRoot 'RIFE-ncnn-Vulkan-MIT.txt')
Copy-RequiredFile (Join-Path $repoRoot 'third_party\rife\SOURCE.md') (Join-Path $licenseRoot 'RIFE-BINARY-PROVENANCE.md')

$ncnnLicense = Join-Path $cacheRoot 'ncnn-b4ba207-LICENSE.txt'
$libwebpLicense = Join-Path $cacheRoot 'libwebp-5abb558-COPYING.txt'
$practicalRifeLicense = Join-Path $cacheRoot 'Practical-RIFE-17d8c7a-LICENSE.txt'
Download-Once `
    'https://raw.githubusercontent.com/Tencent/ncnn/b4ba207c18d3103d6df890c0e3a97b469b196b26/LICENSE.txt' `
    $ncnnLicense
Download-Once `
    'https://raw.githubusercontent.com/webmproject/libwebp/5abb55823bb6196a918dd87202b2f32bbaff4c18/COPYING' `
    $libwebpLicense
Download-Once `
    'https://raw.githubusercontent.com/hzwer/Practical-RIFE/17d8c7a1005b37f4c97bfee04e316aaec7fdc536/LICENSE' `
    $practicalRifeLicense
Copy-RequiredFile $ncnnLicense (Join-Path $licenseRoot 'ncnn-and-third-party-licenses.txt')
Copy-RequiredFile $libwebpLicense (Join-Path $licenseRoot 'libwebp-COPYING.txt')
Copy-RequiredFile $practicalRifeLicense (Join-Path $licenseRoot 'Practical-RIFE-MIT.txt')

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

Write-Host 'Removing superseded local release artifacts...'
$currentReleaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$currentReleaseNames.Add($packageName)
[void]$currentReleaseNames.Add("$packageName.zip")
[void]$currentReleaseNames.Add("$packageName.zip.sha256")
foreach ($artifact in Get-ChildItem -LiteralPath $distRoot -Force) {
    if ($artifact.Name -like 'LocalVSR-*-win-x64*' -and -not $currentReleaseNames.Contains($artifact.Name)) {
        $verifiedArtifact = Assert-UnderRepo $artifact.FullName
        Remove-Item -LiteralPath $verifiedArtifact -Recurse -Force
    }
}

Write-Host 'Refreshing bin\Release with the single latest runnable build...'
$releaseProcesses = @(Get-Process -Name 'LocalVSR', 'LocalVSR.Cli' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith(
                [IO.Path]::GetFullPath($latestRoot) + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
        }
        catch { $false }
    })
if ($releaseProcesses.Count -gt 0) {
    Write-Warning 'bin\Release is currently running, so its refresh was skipped. The portable package is complete.'
}
else {
    Reset-Directory $binRoot
    New-Item -ItemType Directory -Force -Path $latestRoot | Out-Null
    Get-ChildItem -LiteralPath $packageRoot -Force |
        Copy-Item -Destination $latestRoot -Recurse -Force
}

Write-Host "Portable release: $zipPath"
Write-Host "SHA-256: $zipHash"
if ($releaseProcesses.Count -eq 0) {
    Write-Host "Latest runnable build: $latestRoot"
}
Write-Host "Corresponding source: $sourceRoot"
