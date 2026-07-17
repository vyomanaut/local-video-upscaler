using System.Globalization;
using System.Text.Json;

namespace RtxLocalVideo;

internal static class CliProgram
{
    private const int UsageExitCode = 2;
    private const int DependencyExitCode = 3;
    private const int ExportExitCode = 4;
    private const int CancelledExitCode = 130;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Run LocalVSR.Cli.exe --help for usage.");
            return UsageExitCode;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        try
        {
            var inputPath = Path.GetFullPath(options.InputPath!);
            if (!File.Exists(inputPath))
                throw new CliUsageException($"Input file does not exist: {inputPath}");

            var media = await VideoProbe.ReadAsync(inputPath);
            if (options.Command == CliCommand.Probe)
            {
                WriteProbe(media, inputPath, options.Json);
                return 0;
            }

            return await RunExportAsync(options, inputPath, media);
        }
        catch (CliUsageException ex)
        {
            WriteError(options.Json, "invalid_arguments", ex.Message);
            return UsageExitCode;
        }
        catch (FileNotFoundException ex)
        {
            WriteError(options.Json, "missing_dependency", ex.Message);
            return DependencyExitCode;
        }
        catch (DirectoryNotFoundException ex)
        {
            WriteError(options.Json, "missing_dependency", ex.Message);
            return DependencyExitCode;
        }
        catch (OperationCanceledException)
        {
            WriteError(options.Json, "cancelled", "Export cancelled; partial output was removed.");
            return CancelledExitCode;
        }
        catch (Exception ex)
        {
            WriteError(options.Json, "export_failed", ex.Message);
            return ExportExitCode;
        }
    }

    private static async Task<int> RunExportAsync(CliOptions options, string inputPath, VideoInfo media)
    {
        if (!AppPaths.AllDependenciesPresent)
            throw new FileNotFoundException("FFmpeg or the native VSR worker is missing beside LocalVSR.Cli.exe.");
        if (media.IsImage && options.FrameMultiplier != 1)
            throw new CliUsageException("Frame multiplication is available only for videos.");
        if (!media.IsImage && media.FramesPerSecond * options.FrameMultiplier > 240.001)
            throw new CliUsageException("The requested frame multiplier would exceed the 240 FPS limit.");
        if (options.FrameMultiplier > 1 && !AppPaths.FrameInterpolationDependenciesPresent)
            throw new FileNotFoundException("The RIFE worker or v4.6 model is missing beside LocalVSR.Cli.exe.");

        var scales = VideoProbe.GetScaleChoices(media);
        var scale = scales.FirstOrDefault(candidate =>
            Math.Abs(candidate.Factor - options.Scale) < 0.001) ??
            throw new CliUsageException(
                $"Scale {options.Scale:0.#}x is unavailable for this input. Available: " +
                string.Join(", ", scales.Select(candidate => $"{candidate.Factor:0.#}x")));
        var range = CreateRange(media, options.Start, options.End);
        var outputPath = ResolveOutputPath(options, inputPath, media, scale, range);

        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("The output path must differ from the input path.");
        if (File.Exists(outputPath) && !options.Overwrite)
            throw new CliUsageException($"Output already exists; pass --overwrite to replace it: {outputPath}");
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new CliUsageException("The output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        TemporaryNvidiaVsrLevel? levelOverride = null;
        string? restoreWarning = null;
        try
        {
            if (options.VsrLevel.HasValue)
            {
                if (!options.Quiet)
                    Console.Error.WriteLine(
                        $"Applying NVIDIA VSR {NvidiaVsrSettings.FormatLevel(options.VsrLevel.Value)}…");
                levelOverride = NvidiaVsrSettings.ApplyTemporary(options.VsrLevel.Value);
            }

            var progress = options.Quiet ? null : new ConsoleExportProgress(options.Json);
            await ExportService.RunAsync(
                inputPath,
                outputPath,
                media,
                scale,
                options.EncodeQuality,
                options.FrameMultiplier,
                range,
                progress,
                cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            levelOverride?.Dispose();
            restoreWarning = levelOverride?.RestoreWarning;
        }

        var result = new
        {
            status = "ok",
            input = inputPath,
            output = outputPath,
            inputWidth = media.Width,
            inputHeight = media.Height,
            outputWidth = scale.Width,
            outputHeight = scale.Height,
            scale = scale.Factor,
            frameMultiplier = options.FrameMultiplier,
            effectiveFps = media.IsImage ? (double?)null : media.FramesPerSecond * options.FrameMultiplier,
            rangeStartSeconds = media.IsImage ? (double?)null : range.Start.TotalSeconds,
            rangeEndSeconds = media.IsImage ? (double?)null : range.End.TotalSeconds,
            restoreWarning
        };
        if (options.Json)
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        else
        {
            Console.WriteLine(outputPath);
            if (restoreWarning is not null) Console.Error.WriteLine($"Warning: {restoreWarning}");
        }
        return 0;
    }

    private static MediaRange CreateRange(VideoInfo media, string? startText, string? endText)
    {
        if (media.IsImage)
        {
            if (startText is not null || endText is not null)
                throw new CliUsageException("--start and --end cannot be used with still images.");
            return MediaRange.Full(media);
        }

        var start = startText is null ? TimeSpan.Zero : ParseTime(startText, "start");
        var end = endText is null ? media.Duration : ParseTime(endText, "end");
        var frameTolerance = TimeSpan.FromSeconds(1d / Math.Max(1d, media.FramesPerSecond));
        if (end > media.Duration && end <= media.Duration + frameTolerance)
            end = media.Duration;
        if (start < TimeSpan.Zero || end <= start)
            throw new CliUsageException("The end time must be later than the start time.");
        if (start >= media.Duration)
            throw new CliUsageException("The start time must be before the end of the video.");
        if (end > media.Duration)
            throw new CliUsageException($"The end time exceeds the video duration ({media.Duration.TotalSeconds:0.###}s).");
        return new MediaRange(start, end);
    }

    private static TimeSpan ParseTime(string text, string optionName)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3 ||
            !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0)
            throw new CliUsageException($"--{optionName} must use seconds, MM:SS, or HH:MM:SS.");

        var hours = 0;
        var minutes = 0;
        if (parts.Length >= 2 &&
            (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes < 0) ||
            parts.Length == 3 &&
            (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0) ||
            parts.Length >= 2 && seconds >= 60 ||
            parts.Length == 3 && minutes >= 60)
            throw new CliUsageException($"--{optionName} must use seconds, MM:SS, or HH:MM:SS.");

        return TimeSpan.FromSeconds(hours * 3600d + minutes * 60d + seconds);
    }

    private static string ResolveOutputPath(
        CliOptions options, string inputPath, VideoInfo media, ScaleChoice scale, MediaRange range)
    {
        if (options.OutputPath is not null)
        {
            var outputPath = Path.GetFullPath(options.OutputPath);
            var requiredExtension = media.IsImage ? ".png" : ".mkv";
            if (!Path.GetExtension(outputPath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
                throw new CliUsageException($"Output must use the {requiredExtension} extension.");
            return outputPath;
        }

        var directory = Path.GetDirectoryName(inputPath)!;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var frameRateSuffix = options.FrameMultiplier > 1 ? $"-FPS-{options.FrameMultiplier}x" : string.Empty;
        var customRange = !media.IsImage &&
                          (range.Start > TimeSpan.FromMilliseconds(1) ||
                           range.End < media.Duration - TimeSpan.FromMilliseconds(1));
        var rangeSuffix = customRange ? "-clip" : string.Empty;
        var extension = media.IsImage ? ".png" : ".mkv";
        return Path.Combine(
            directory,
            $"{stem}.VSR-{scale.Factor:0.#}x{frameRateSuffix}{rangeSuffix}{extension}");
    }

    private static void WriteProbe(VideoInfo media, string inputPath, bool json)
    {
        var result = new
        {
            status = "ok",
            input = inputPath,
            isImage = media.IsImage,
            width = media.Width,
            height = media.Height,
            processingWidth = media.ProcessingWidth,
            processingHeight = media.ProcessingHeight,
            durationSeconds = media.IsImage ? (double?)null : media.Duration.TotalSeconds,
            framesPerSecond = media.IsImage ? (double?)null : media.FramesPerSecond,
            scales = VideoProbe.GetScaleChoices(media).Select(scale => new
            {
                factor = scale.Factor,
                width = scale.Width,
                height = scale.Height
            })
        };
        Console.WriteLine(json
            ? JsonSerializer.Serialize(result, JsonOptions)
            : $"{media.Width}x{media.Height}, " +
              (media.IsImage ? "image" : $"{media.FramesPerSecond:0.###} fps, {media.Duration.TotalSeconds:0.###}s"));
    }

    private static void WriteError(bool json, string code, string message)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { status = "error", code, message }, JsonOptions));
        else
            Console.Error.WriteLine($"Error: {message}");
    }

    private const string HelpText = """
LocalVSR headless CLI

Usage:
  LocalVSR.Cli.exe upscale <input> [options]
  LocalVSR.Cli.exe probe <input> [--json]

Export options:
  -o, --output <path>           Output .mkv for video or .png for images
      --scale <1.5|2|3|4>       Upscale factor (default: 2)
      --vsr-quality <1-4|auto|current>
                                 NVIDIA VSR level (default: 4); current does not change it
      --frame-multiplier <1|2|4> AI video FPS multiplier (default: 1)
      --encode-quality <value>   highest, balanced, smaller, or CQ 0-51 (default: balanced)
      --start <time>             Start in seconds, MM:SS, or HH:MM:SS
      --end <time>               End in seconds, MM:SS, or HH:MM:SS
      --overwrite                Replace an existing output file
      --json                     Machine-readable final result
      --quiet                    Suppress progress on stderr
  -h, --help                     Show this help

Examples:
  LocalVSR.Cli.exe upscale video.mp4 --scale 2 --vsr-quality 4 --overwrite
  LocalVSR.Cli.exe upscale clip.mp4 --frame-multiplier 2 --start 10 --end 20 --json
  LocalVSR.Cli.exe upscale image.png --scale 4 --vsr-quality current
  LocalVSR.Cli.exe probe video.mp4 --json

Exit codes: 0 success, 2 invalid arguments, 3 missing dependency,
            4 export failure, 130 cancelled.
""";

    private sealed class ConsoleExportProgress(bool json) : IProgress<ExportProgress>
    {
        private int lastPercent = -1;
        private string? lastMessage;

        public void Report(ExportProgress value)
        {
            var percent = (int)Math.Clamp(Math.Round(value.Percent), 0, 100);
            if (percent == lastPercent && value.Message == lastMessage) return;
            lastPercent = percent;
            lastMessage = value.Message;
            Console.Error.WriteLine(json
                ? JsonSerializer.Serialize(new { type = "progress", percent, message = value.Message })
                : $"[{percent,3}%] {value.Message}");
        }
    }

    private enum CliCommand { Upscale, Probe }

    private sealed record CliOptions(
        CliCommand Command,
        string? InputPath,
        string? OutputPath,
        double Scale,
        int? VsrLevel,
        int FrameMultiplier,
        int EncodeQuality,
        string? Start,
        string? End,
        bool Overwrite,
        bool Json,
        bool Quiet,
        bool ShowHelp)
    {
        public static CliOptions Parse(string[] args)
        {
            if (args.Length == 0)
                return Defaults(showHelp: true);

            var command = CliCommand.Upscale;
            var index = 0;
            if (args[0].Equals("probe", StringComparison.OrdinalIgnoreCase))
            {
                command = CliCommand.Probe;
                index++;
            }
            else if (args[0].Equals("upscale", StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }

            string? input = null;
            string? output = null;
            var scale = 2d;
            int? vsrLevel = 4;
            var frameMultiplier = 1;
            var encodeQuality = 21;
            string? start = null;
            string? end = null;
            var overwrite = false;
            var json = false;
            var quiet = false;
            var help = false;

            while (index < args.Length)
            {
                var argument = args[index++];
                string Value() => index < args.Length
                    ? args[index++]
                    : throw new CliUsageException($"{argument} requires a value.");

                switch (argument.ToLowerInvariant())
                {
                    case "-h":
                    case "--help": help = true; break;
                    case "-i":
                    case "--input": input = Value(); break;
                    case "-o":
                    case "--output": output = Value(); break;
                    case "--scale":
                        if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out scale) ||
                            !new[] { 1.5, 2d, 3d, 4d }.Contains(scale))
                            throw new CliUsageException("--scale must be 1.5, 2, 3, or 4.");
                        break;
                    case "--vsr-quality":
                        var qualityText = Value();
                        vsrLevel = qualityText.ToLowerInvariant() switch
                        {
                            "auto" => 5,
                            "current" => null,
                            _ when int.TryParse(qualityText, out var level) && level is >= 1 and <= 4 => level,
                            _ => throw new CliUsageException("--vsr-quality must be 1-4, auto, or current.")
                        };
                        break;
                    case "--frame-multiplier":
                        if (!int.TryParse(Value(), out frameMultiplier) || frameMultiplier is not (1 or 2 or 4))
                            throw new CliUsageException("--frame-multiplier must be 1, 2, or 4.");
                        break;
                    case "--encode-quality":
                        var encodeText = Value();
                        encodeQuality = encodeText.ToLowerInvariant() switch
                        {
                            "highest" => 16,
                            "balanced" => 21,
                            "smaller" => 24,
                            _ when int.TryParse(encodeText, out var cq) && cq is >= 0 and <= 51 => cq,
                            _ => throw new CliUsageException(
                                "--encode-quality must be highest, balanced, smaller, or CQ 0-51.")
                        };
                        break;
                    case "--start": start = Value(); break;
                    case "--end": end = Value(); break;
                    case "--overwrite": overwrite = true; break;
                    case "--json": json = true; break;
                    case "--quiet":
                    case "--no-progress": quiet = true; break;
                    default:
                        if (!argument.StartsWith('-') && input is null)
                            input = argument;
                        else
                            throw new CliUsageException($"Unknown argument: {argument}");
                        break;
                }
            }

            if (!help && string.IsNullOrWhiteSpace(input))
                throw new CliUsageException("An input file is required.");
            if (command == CliCommand.Probe && output is not null)
                throw new CliUsageException("The probe command does not accept --output.");
            return new CliOptions(
                command, input, output, scale, vsrLevel, frameMultiplier, encodeQuality,
                start, end, overwrite, json, quiet, help);
        }

        private static CliOptions Defaults(bool showHelp) => new(
            CliCommand.Upscale, null, null, 2, 4, 1, 21,
            null, null, false, false, false, showHelp);
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
