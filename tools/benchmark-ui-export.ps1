param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $InputFile,
    [Parameter(Mandatory)] [string] $OutputFile,
    [string] $ResultFile,
    [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$inputPath = (Resolve-Path -LiteralPath $InputFile).Path
$outputPath = [IO.Path]::GetFullPath($OutputFile)

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ExportBenchmarkUi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr info);
}
'@

[void][ExportBenchmarkUi]::SetProcessDpiAwarenessContext([IntPtr](-4))

function Invoke-Click([int] $X, [int] $Y) {
    [void][ExportBenchmarkUi]::SetCursorPos($X, $Y)
    [ExportBenchmarkUi]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [ExportBenchmarkUi]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}

$app = Start-Process -FilePath $exePath -PassThru
try {
    [void]$app.WaitForInputIdle(15000)
    Start-Sleep -Seconds 2
    $app.Refresh()

    $rect = New-Object ExportBenchmarkUi+RECT
    [void][ExportBenchmarkUi]::GetWindowRect($app.MainWindowHandle, [ref]$rect)
    [void][ExportBenchmarkUi]::SetForegroundWindow($app.MainWindowHandle)
    Invoke-Click ([int](($rect.Left + $rect.Right) / 2)) ([int]($rect.Top + 350))
    Start-Sleep -Seconds 1
    [Windows.Forms.SendKeys]::SendWait($inputPath)
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 2

    $app.Refresh()
    [void][ExportBenchmarkUi]::GetWindowRect($app.MainWindowHandle, [ref]$rect)
    [void][ExportBenchmarkUi]::SetForegroundWindow($app.MainWindowHandle)

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Click ($rect.Right - 130) ($rect.Bottom - 75)

    $started = $false
    $childNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Start-Sleep -Milliseconds 100
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id)" -ErrorAction SilentlyContinue)
        if ($children.Count -gt 0) {
            $started = $true
            foreach ($child in $children) { [void]$childNames.Add($child.Name) }
        }
        if ($started -and $children.Count -eq 0 -and (Test-Path -LiteralPath $outputPath)) {
            break
        }
        if ($app.HasExited) { throw 'The app exited during the export.' }
    }
    $stopwatch.Stop()

    if (-not $started) { throw 'The export processes never started; UI automation did not reach the Export button.' }
    if (-not (Test-Path -LiteralPath $outputPath)) { throw "The export timed out after $TimeoutSeconds seconds." }

    Start-Sleep -Milliseconds 500
    [void][ExportBenchmarkUi]::SetForegroundWindow($app.MainWindowHandle)
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')

    $result = [pscustomobject]@{
        Exe = $exePath
        ElapsedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        OutputBytes = (Get-Item -LiteralPath $outputPath).Length
        ChildProcesses = ($childNames | Sort-Object) -join ', '
    }
    if ($ResultFile) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultFile), ($result | ConvertTo-Json))
    }
    $result
}
finally {
    if (-not $app.HasExited) {
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(3000)) { Stop-Process -Id $app.Id -Force }
    }
}
