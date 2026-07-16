using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RtxLocalVideo;

internal sealed record ExportProgress(double Percent, string Message);

internal static class ExportService
{
    public static async Task RunAsync(
        string inputPath,
        string outputPath,
        VideoInfo video,
        ScaleChoice scale,
        int quality,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var decoder = CreateDecoder(inputPath, video);
        var worker = CreateWorker(video, scale);
        var encoder = CreateEncoder(inputPath, outputPath, video, scale, quality);
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
                encoder.StandardError, video.Duration, progress, cancellationToken);

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
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch { /* Leave a locked partial file for diagnostics. */ }
            }
            throw;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Process CreateDecoder(string inputPath, VideoInfo video)
    {
        var info = CreateStartInfo(AppPaths.Ffmpeg, redirectOutput: true, redirectInput: false);
        Add(info, "-hide_banner", "-loglevel", "error", "-nostdin", "-i", inputPath,
            "-map", "0:v:0", "-an", "-sn", "-dn");
        if (video.IsImage)
            Add(info, "-frames:v", "1");
        else
            Add(info, "-vf", $"fps={video.FrameRateNumerator}/{video.FrameRateDenominator}");
        Add(info, "-pix_fmt", "nv12", "-f", "rawvideo", "pipe:1");
        return new Process { StartInfo = info };
    }

    private static Process CreateWorker(VideoInfo video, ScaleChoice scale)
    {
        var info = CreateStartInfo(AppPaths.VsrProcessor, redirectOutput: true, redirectInput: true);
        Add(info,
            "--input-width", video.Width.ToString(CultureInfo.InvariantCulture),
            "--input-height", video.Height.ToString(CultureInfo.InvariantCulture),
            "--output-width", scale.Width.ToString(CultureInfo.InvariantCulture),
            "--output-height", scale.Height.ToString(CultureInfo.InvariantCulture),
            "--fps-numerator", video.FrameRateNumerator.ToString(CultureInfo.InvariantCulture),
            "--fps-denominator", video.FrameRateDenominator.ToString(CultureInfo.InvariantCulture));
        return new Process { StartInfo = info };
    }

    private static Process CreateEncoder(
        string inputPath, string outputPath, VideoInfo video, ScaleChoice scale, int quality)
    {
        var info = CreateStartInfo(AppPaths.Ffmpeg, redirectOutput: false, redirectInput: true);
        Add(info,
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo", "-pix_fmt", "nv12",
            "-video_size", $"{scale.Width}x{scale.Height}",
            "-framerate", $"{video.FrameRateNumerator}/{video.FrameRateDenominator}",
            "-i", "pipe:0");
        if (video.IsImage)
        {
            Add(info, "-frames:v", "1", "-c:v", "png", "-compression_level", "6",
                "-progress", "pipe:2", "-nostats", outputPath);
        }
        else
        {
            Add(info, "-i", inputPath,
                "-map", "0:v:0", "-map", "1:a?", "-map", "1:s?",
                "-map_metadata", "1", "-map_chapters", "1",
                "-c:v", "h264_nvenc",
                "-preset", "p4", "-tune", "hq",
                "-rc", "vbr", "-cq", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0",
                "-spatial_aq", "1", "-aq-strength", "8", "-bf", "3",
                "-c:a", "copy", "-c:s", "copy",
                "-progress", "pipe:2", "-nostats", outputPath);
        }
        return new Process { StartInfo = info };
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, bool redirectOutput, bool redirectInput) => new(fileName)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = redirectInput,
        RedirectStandardOutput = redirectOutput,
        RedirectStandardError = true
    };

    private static void Add(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }

    private static async Task PipeAndCloseAsync(
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

    private static async Task<string> ReadEncoderProgressAsync(
        StreamReader reader,
        TimeSpan duration,
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
            progress?.Report(new ExportProgress(percent, $"RTX VSR + NVENC… {percent:0}%"));
        }
        return otherOutput.ToString();
    }

    private static void AppendUsefulLog(StringBuilder message, string name, string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return;
        var lines = log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        message.AppendLine().Append(name).Append(": ")
            .Append(string.Join(" | ", lines.TakeLast(4)));
    }
}
