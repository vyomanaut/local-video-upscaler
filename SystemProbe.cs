using System.Diagnostics;

namespace RtxLocalVideo;

internal sealed record SystemStatus(
    string? GpuName,
    string? DriverVersion,
    string? NvidiaAppPath)
{
    public bool HasRtxGpu => GpuName?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true;
}

internal static class SystemProbe
{
    private static readonly string[] NvidiaAppCandidates =
    [
        @"C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA App.exe",
        @"C:\Program Files\NVIDIA Corporation\Control Panel Client\nvcplui.exe"
    ];

    public static async Task<SystemStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        var (gpu, driver) = await ReadGpuAsync(cancellationToken);
        return new SystemStatus(
            gpu,
            driver,
            FirstExisting(NvidiaAppCandidates));
    }

    private static async Task<(string? Gpu, string? Driver)> ReadGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi.exe",
                    Arguments = "--query-gpu=name,driver_version --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var firstLine = (await outputTask)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            var parts = firstLine?.Split(',', 2, StringSplitOptions.TrimEntries);
            return parts is { Length: 2 } ? (parts[0], parts[1]) : (firstLine, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? FirstExisting(IEnumerable<string> paths) =>
        paths.FirstOrDefault(File.Exists);
}
