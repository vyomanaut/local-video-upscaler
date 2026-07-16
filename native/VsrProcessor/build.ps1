$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw 'Visual Studio C++ build tools were not found.'
}

$devCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'VsrProcessor.cpp'
$output = Join-Path $PSScriptRoot 'VsrProcessor.exe'
$command = "`"$devCmd`" -arch=x64 -host_arch=x64 && cl.exe /nologo /std:c++20 /EHsc /W4 /O2 /DNOMINMAX /DUNICODE /D_UNICODE `"$source`" /Fe:`"$output`" /link d3d11.lib dxgi.lib"

& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) {
    throw "C++ build failed with exit code $LASTEXITCODE."
}

Write-Host "Built $output"
