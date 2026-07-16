using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RtxLocalVideo;

internal sealed record ExportProgress(double Percent, string Message);

internal readonly record struct MediaRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public static MediaRange Full(VideoInfo video) => new(TimeSpan.Zero, video.Duration);
}

internal static class ExportService
{
    public static async Task RunAsync(
        string inputPath,
        string outputPath,
        VideoInfo video,
        ScaleChoice scale,
        int quality,
        int frameMultiplier,
        MediaRange range,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (frameMultiplier is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(frameMultiplier));
        if (video.IsImage) frameMultiplier = 1;
        ValidateRange(video, range);

        if (frameMultiplier > 1)
        {
            await FrameInterpolationService.RunAsync(
                inputPath, outputPath, video, scale, quality, frameMultiplier,
                range, progress, cancellationToken);
            return;
        }

        var outputFrameRateNumerator = checked(video.FrameRateNumerator * frameMultiplier);
        var decoder = CreateDecoder(inputPath, video, range);
        var worker = CreateWorker(video, scale, outputFrameRateNumerator);
        var encoder = CreateEncoder(
            inputPath, outputPath, video, scale, quality, outputFrameRateNumerator, range);
        var processes = new[] { decoder, worker, encoder };

        try
        {
            encoder.Start();
            worker.Start();
            decoder.Start();

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                foreach (var process in processes)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* Process may exit between the check and kill. */ }
                }
            });

            var decoderErrors = decoder.StandardError.ReadToEndAsync(cancellationToken);
            var workerErrors = worker.StandardError.ReadToEndAsync(cancellationToken);
            var encoderProgress = ReadEncoderProgressAsync(
                encoder.StandardError, range.Duration, frameMultiplier > 1, progress, cancellationToken);

            var decoderPipe = PipeAndCloseAsync(
                decoder.StandardOutput.BaseStream, worker.StandardInput.BaseStream, cancellationToken);
            var workerPipe = PipeAndCloseAsync(
                worker.StandardOutput.BaseStream, encoder.StandardInput.BaseStream, cancellationToken);

            await Task.WhenAll(
                decoder.WaitForExitAsync(cancellationToken),
                worker.WaitForExitAsync(cancellationToken),
                encoder.WaitForExitAsync(cancellationToken),
                decoderPipe,
                workerPipe,
                encoderProgress);

            var decoderLog = await decoderErrors;
            var workerLog = await workerErrors;
            var encoderLog = await encoderProgress;
            if (decoder.ExitCode != 0 || worker.ExitCode != 0 || encoder.ExitCode != 0)
            {
                var message = new StringBuilder("The export pipeline stopped before completion.");
                AppendUsefulLog(message, "Decoder", decoderLog);
                AppendUsefulLog(message, "VSR", workerLog);
                AppendUsefulLog(message, "Encoder", encoderLog);
                throw new InvalidOperationException(message.ToString());
            }

            progress?.Report(new ExportProgress(100, "Export complete"));
        }
        catch
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            }
            await Task.WhenAll(processes.Select(WaitForExitIgnoringErrorsAsync));
            await TryDeleteOutputAsync(outputPath);
            throw;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Process CreateDecoder(string inputPath, VideoInfo video, MediaRange range)
    {
        var info = CreateStartInfo(AppPaths.Ffmpeg, redirectOutput: true, redirectInput: false);
        Add(info, "-hide_banner", "-loglevel", "error", "-nostdin");
        AddRangeInput(info, inputPath, video, range);
        Add(info, "-map", "0:v:0", "-an", "-sn", "-dn");
        if (video.IsImage)
            Add(info, "-frames:v", "1");
        else
        {
            var sourceRate = $"{video.FrameRateNumerator}/{video.FrameRateDenominator}";
            Add(info, "-vf", $"fps={sourceRate}");
        }
        Add(info, "-pix_fmt", "nv12");
        if (!video.IsImage)
            Add(info, "-fps_mode", "passthrough");
        Add(info, "-f", "rawvideo", "pipe:1");
        return new Process { StartInfo = info };
    }

    internal static Process CreateWorker(
        VideoInfo video,
        ScaleChoice scale,
        int outputFrameRateNumerator)
    {
        var info = CreateStartInfo(AppPaths.VsrProcessor, redirectOutput: true, redirectInput: true);
        Add(info,
            "--input-width", video.Width.ToString(CultureInfo.InvariantCulture),
            "--input-height", video.Height.ToString(CultureInfo.InvariantCulture),
            "--output-width", scale.Width.ToString(CultureInfo.InvariantCulture),
            "--output-height", scale.Height.ToString(CultureInfo.InvariantCulture),
            "--fps-numerator", outputFrameRateNumerator.ToString(CultureInfo.InvariantCulture),
            "--fps-denominator", video.FrameRateDenominator.ToString(CultureInfo.InvariantCulture));
        return new Process { StartInfo = info };
    }

    internal static Process CreateEncoder(
        string inputPath,
        string outputPath,
        VideoInfo video,
        ScaleChoice scale,
        int quality,
        int outputFrameRateNumerator,
        MediaRange range)
    {
        var info = CreateStartInfo(AppPaths.Ffmpeg, redirectOutput: false, redirectInput: true);
        Add(info,
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo", "-pix_fmt", "nv12",
            "-video_size", $"{scale.Width}x{scale.Height}",
            "-framerate", $"{outputFrameRateNumerator}/{video.FrameRateDenominator}",
            "-i", "pipe:0");
        if (video.IsImage)
        {
            Add(info, "-frames:v", "1", "-c:v", "png", "-compression_level", "6",
                "-progress", "pipe:2", "-nostats", outputPath);
        }
        else
        {
            AddRangeInput(info, inputPath, video, range);
            Add(info,
                "-map", "0:v:0", "-map", "1:a?", "-map", "1:s?",
                "-map_metadata", "1", "-map_chapters", "1",
                "-c:v", "h264_nvenc",
                "-preset", "p4", "-tune", "hq",
                "-rc", "vbr", "-cq", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0",
                "-spatial_aq", "1", "-aq-strength", "8", "-bf", "3",
                "-c:a", "copy", "-c:s", "copy",
                "-fps_mode", "passthrough",
                "-progress", "pipe:2", "-nostats", outputPath);
        }
        return new Process { StartInfo = info };
    }

    internal static void AddRangeInput(
        ProcessStartInfo info,
        string inputPath,
        VideoInfo video,
        MediaRange range)
    {
        if (!video.IsImage && range.Start > TimeSpan.Zero)
            Add(info, "-ss", FormatTimeArgument(range.Start));
        if (!video.IsImage)
            Add(info, "-t", FormatTimeArgument(range.Duration));
        Add(info, "-i", inputPath);
    }

    private static string FormatTimeArgument(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static void ValidateRange(VideoInfo video, MediaRange range)
    {
        if (video.IsImage) return;
        if (range.Start < TimeSpan.Zero || range.End <= range.Start)
            throw new ArgumentOutOfRangeException(nameof(range), "The export end time must be after its start time.");
        var tolerance = TimeSpan.FromSeconds(1d / Math.Max(1d, video.FramesPerSecond));
        if (range.End > video.Duration + tolerance)
            throw new ArgumentOutOfRangeException(nameof(range), "The export end time exceeds the video duration.");
    }

    internal static ProcessStartInfo CreateStartInfo(string fileName, bool redirectOutput, bool redirectInput) => new(fileName)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = redirectInput,
        RedirectStandardOutput = redirectOutput,
        RedirectStandardError = true
    };

    internal static void Add(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }

    internal static async Task PipeAndCloseAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try { await source.CopyToAsync(destination, 1024 * 1024, cancellationToken); }
        catch (IOException) { /* A downstream process closed; exit codes provide the useful error. */ }
        finally
        {
            try { await destination.DisposeAsync(); }
            catch { }
        }
    }

    internal static async Task TryDeleteOutputAsync(string outputPath)
    {
        for (var attempt = 0; attempt < 8; ++attempt)
        {
            if (!File.Exists(outputPath)) return;
            try
            {
                File.Delete(outputPath);
                return;
            }
            catch (IOException) when (attempt < 7)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 7)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task WaitForExitIgnoringErrorsAsync(Process process)
    {
        try
        {
            if (!process.HasExited) await process.WaitForExitAsync();
        }
        catch { }
    }

    internal static async Task<string> ReadEncoderProgressAsync(
        StreamReader reader,
        TimeSpan duration,
        bool frameInterpolationEnabled,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var otherOutput = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (!line.Contains('=')) otherOutput.AppendLine(line);
                continue;
            }
            if (!long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds)) continue;
            var percent = duration.TotalSeconds <= 0
                ? 0
                : Math.Clamp(microseconds / 1_000_000d / duration.TotalSeconds * 100d, 0, 99.5);
            var stage = frameInterpolationEnabled
                ? "Frame interpolation + RTX VSR + NVENC"
                : "RTX VSR + NVENC";
            progress?.Report(new ExportProgress(percent, $"{stage}… {percent:0}%"));
        }
        return otherOutput.ToString();
    }

    internal static void AppendUsefulLog(StringBuilder message, string name, string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return;
        var lines = log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        message.AppendLine().Append(name).Append(": ")
            .Append(string.Join(" | ", lines.TakeLast(4)));
    }
}
