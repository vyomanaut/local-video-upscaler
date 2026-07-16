using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace RtxLocalVideo;

internal sealed record VideoInfo(
    int Width,
    int Height,
    int FrameRateNumerator,
    int FrameRateDenominator,
    TimeSpan Duration,
    bool IsImage)
{
    public double FramesPerSecond => (double)FrameRateNumerator / FrameRateDenominator;
}

internal sealed record ScaleChoice(double Factor, int Width, int Height)
{
    public override string ToString() => $"{Factor:0.#}×  —  {Width} × {Height}";
}

internal static class VideoProbe
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    public static async Task<VideoInfo> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(AppPaths.Ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate:format=duration",
            "-of", "json", path
        }) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The video could not be inspected." : error.Trim());

        using var document = JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams")[0];
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        var isImage = ImageExtensions.Contains(Path.GetExtension(path));
        var rateText = stream.TryGetProperty("avg_frame_rate", out var averageRate) && averageRate.GetString() != "0/0"
            ? averageRate.GetString()
            : stream.GetProperty("r_frame_rate").GetString();
        var (numerator, denominator) = isImage ? (1, 1) : ParseRate(rateText ?? "30/1");
        var format = document.RootElement.GetProperty("format");
        var durationText = format.TryGetProperty("duration", out var durationElement)
            ? durationElement.GetString()
            : null;
        _ = double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds);

        if (width < 90 || height < 90 || width % 2 != 0 || height % 2 != 0)
            throw new InvalidOperationException("This first exporter build requires an even-sized video of at least 90 × 90 pixels.");

        return new VideoInfo(width, height, numerator, denominator, TimeSpan.FromSeconds(durationSeconds), isImage);
    }

    public static IReadOnlyList<ScaleChoice> GetScaleChoices(VideoInfo video)
    {
        double[] factors = [1.5, 2, 3, 4];
        return factors
            .Select(factor => new ScaleChoice(
                factor,
                RoundEven(video.Width * factor),
                RoundEven(video.Height * factor)))
            .Where(choice => choice.Width <= 3840 && choice.Height <= 3840)
            .ToArray();
    }

    private static (int Numerator, int Denominator) ParseRate(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var numerator) && numerator > 0 &&
               int.TryParse(parts[1], out var denominator) && denominator > 0
            ? (numerator, denominator)
            : (30, 1);
    }

    private static int RoundEven(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
