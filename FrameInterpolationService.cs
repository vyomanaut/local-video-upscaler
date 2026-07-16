using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace RtxLocalVideo;

internal static class FrameInterpolationService
{
    // One overlap frame joins adjacent chunks without ever staging a whole video.
    private const int SourceIntervalsPerChunk = 32;

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
        if (!AppPaths.FrameInterpolationDependenciesPresent)
            throw new InvalidOperationException(
                "The local frame-interpolation runtime or model is missing from the application folder.");

        if (AppPaths.PersistentFrameInterpolationDependenciesPresent)
        {
            await RunStreamingAsync(
                inputPath, outputPath, video, scale, quality, frameMultiplier,
                range, progress, cancellationToken);
            return;
        }

        var outputFrameRateNumerator = checked(video.FrameRateNumerator * frameMultiplier);
        var decoder = CreateBmpDecoder(inputPath, video, range);
        var worker = ExportService.CreateWorker(video, scale, outputFrameRateNumerator);
        var encoder = ExportService.CreateEncoder(
            inputPath, outputPath, video, scale, quality, outputFrameRateNumerator, range);
        var active = new ActiveProcessSet();
        active.Add(decoder);
        active.Add(worker);
        active.Add(encoder);

        var tempRoot = Path.Combine(
            Path.GetTempPath(), "LocalVSR", $"frame-interpolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            encoder.Start();
            worker.Start();
            decoder.Start();

            using var cancellationRegistration = cancellationToken.Register(active.KillAll);
            var decoderErrors = decoder.StandardError.ReadToEndAsync(cancellationToken);
            var workerErrors = worker.StandardError.ReadToEndAsync(cancellationToken);
            var encoderProgress = ExportService.ReadEncoderProgressAsync(
                encoder.StandardError, range.Duration, true, progress, cancellationToken);
            var workerPipe = ExportService.PipeAndCloseAsync(
                worker.StandardOutput.BaseStream, encoder.StandardInput.BaseStream, cancellationToken);
            var interpolation = InterpolateChunksAsync(
                decoder.StandardOutput.BaseStream,
                worker.StandardInput.BaseStream,
                tempRoot,
                video,
                frameMultiplier,
                outputFrameRateNumerator,
                active,
                progress,
                cancellationToken);

            _ = interpolation.ContinueWith(
                _ => active.KillAll(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await Task.WhenAll(
                decoder.WaitForExitAsync(cancellationToken),
                interpolation,
                worker.WaitForExitAsync(cancellationToken),
                encoder.WaitForExitAsync(cancellationToken),
                workerPipe,
                encoderProgress);

            var decoderLog = await decoderErrors;
            var workerLog = await workerErrors;
            var encoderLog = await encoderProgress;
            if (decoder.ExitCode != 0 || worker.ExitCode != 0 || encoder.ExitCode != 0)
            {
                var message = new StringBuilder("The frame-multiplication export stopped before completion.");
                ExportService.AppendUsefulLog(message, "Decoder", decoderLog);
                ExportService.AppendUsefulLog(message, "VSR", workerLog);
                ExportService.AppendUsefulLog(message, "Encoder", encoderLog);
                throw new InvalidOperationException(message.ToString());
            }

            progress?.Report(new ExportProgress(100, "Export complete"));
        }
        catch
        {
            active.KillAll();
            await active.WaitForAllExitAsync();
            await ExportService.TryDeleteOutputAsync(outputPath);
            throw;
        }
        finally
        {
            active.Dispose();
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task RunStreamingAsync(
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
        progress?.Report(new ExportProgress(0, "Checking NVIDIA hardware decoding…"));
        var useCudaDecode = await CanUseCudaDecodeAsync(
            inputPath, video, range, cancellationToken);
        var outputFrameRateNumerator = checked(video.FrameRateNumerator * frameMultiplier);
        var decoder = CreateRawDecoder(inputPath, video, range, useCudaDecode);
        var interpolator = CreatePersistentInterpolator(video, frameMultiplier);
        var converter = CreateRawConverter(video, outputFrameRateNumerator);
        var worker = ExportService.CreateWorker(video, scale, outputFrameRateNumerator);
        var encoder = ExportService.CreateEncoder(
            inputPath, outputPath, video, scale, quality, outputFrameRateNumerator, range);
        var processes = new[] { decoder, interpolator, converter, worker, encoder };
        var active = new ActiveProcessSet();
        foreach (var process in processes) active.Add(process);

        try
        {
            progress?.Report(new ExportProgress(
                0,
                useCudaDecode
                    ? "Loading the persistent AI model with NVIDIA decoding…"
                    : "Loading the persistent AI model…"));
            encoder.Start();
            worker.Start();
            converter.Start();
            interpolator.Start();
            decoder.Start();

            using var cancellationRegistration = cancellationToken.Register(active.KillAll);
            var decoderErrors = decoder.StandardError.ReadToEndAsync(cancellationToken);
            var interpolatorErrors = interpolator.StandardError.ReadToEndAsync(cancellationToken);
            var converterErrors = converter.StandardError.ReadToEndAsync(cancellationToken);
            var workerErrors = worker.StandardError.ReadToEndAsync(cancellationToken);
            var encoderProgress = ExportService.ReadEncoderProgressAsync(
                encoder.StandardError, range.Duration, true, progress, cancellationToken);

            var decodePipe = ExportService.PipeAndCloseAsync(
                decoder.StandardOutput.BaseStream,
                interpolator.StandardInput.BaseStream,
                cancellationToken);
            var interpolationPipe = ExportService.PipeAndCloseAsync(
                interpolator.StandardOutput.BaseStream,
                converter.StandardInput.BaseStream,
                cancellationToken);
            var conversionPipe = ExportService.PipeAndCloseAsync(
                converter.StandardOutput.BaseStream,
                worker.StandardInput.BaseStream,
                cancellationToken);
            var workerPipe = ExportService.PipeAndCloseAsync(
                worker.StandardOutput.BaseStream,
                encoder.StandardInput.BaseStream,
                cancellationToken);

            await Task.WhenAll(
                processes.Select(process => process.WaitForExitAsync(cancellationToken))
                    .Append(decodePipe)
                    .Append(interpolationPipe)
                    .Append(conversionPipe)
                    .Append(workerPipe)
                    .Append(encoderProgress));

            var decoderLog = await decoderErrors;
            var interpolatorLog = await interpolatorErrors;
            var converterLog = await converterErrors;
            var workerLog = await workerErrors;
            var encoderLog = await encoderProgress;
            if (processes.Any(process => process.ExitCode != 0))
            {
                var message = new StringBuilder(
                    "The streaming frame-multiplication pipeline stopped before completion.");
                ExportService.AppendUsefulLog(message, "Decoder", decoderLog);
                ExportService.AppendUsefulLog(message, "RIFE", interpolatorLog);
                ExportService.AppendUsefulLog(message, "Converter", converterLog);
                ExportService.AppendUsefulLog(message, "VSR", workerLog);
                ExportService.AppendUsefulLog(message, "Encoder", encoderLog);
                throw new InvalidOperationException(message.ToString());
            }

            progress?.Report(new ExportProgress(100, "Export complete"));
        }
        catch
        {
            active.KillAll();
            await active.WaitForAllExitAsync();
            await ExportService.TryDeleteOutputAsync(outputPath);
            throw;
        }
        finally
        {
            active.Dispose();
        }
    }

    private static Process CreateRawDecoder(
        string inputPath,
        VideoInfo video,
        MediaRange range,
        bool useCudaDecode)
    {
        var info = ExportService.CreateStartInfo(
            AppPaths.Ffmpeg, redirectOutput: true, redirectInput: false);
        ExportService.Add(info, "-hide_banner", "-loglevel", "error", "-nostdin");
        if (useCudaDecode)
            ExportService.Add(
                info,
                "-hwaccel", "cuda",
                "-hwaccel_device", "0",
                "-hwaccel_output_format", "cuda");
        ExportService.AddRangeInput(info, inputPath, video, range);
        var sourceRate = $"{video.FrameRateNumerator}/{video.FrameRateDenominator}";
        var filter = useCudaDecode
            ? $"hwdownload,format=nv12,fps={sourceRate},format=bgr24"
            : $"fps={sourceRate}";
        ExportService.Add(
            info,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", filter,
            "-pix_fmt", "bgr24",
            "-fps_mode", "passthrough",
            "-f", "rawvideo", "pipe:1");
        return new Process { StartInfo = info };
    }

    private static Process CreatePersistentInterpolator(VideoInfo video, int frameMultiplier)
    {
        var info = ExportService.CreateStartInfo(
            AppPaths.RifeProcessor, redirectOutput: true, redirectInput: true);
        // Two concurrent inference jobs were fastest on the 8 GB RTX test GPU;
        // three increased contention and memory pressure.
        ExportService.Add(
            info,
            "--width", video.Width.ToString(CultureInfo.InvariantCulture),
            "--height", video.Height.ToString(CultureInfo.InvariantCulture),
            "--multiplier", frameMultiplier.ToString(CultureInfo.InvariantCulture),
            "--jobs", "2",
            "--model", AppPaths.RifeModel);
        var runtimeDirectory = Path.GetDirectoryName(AppPaths.Rife)!;
        info.WorkingDirectory = runtimeDirectory;
        _ = info.Environment.TryGetValue("PATH", out var currentPath);
        info.Environment["PATH"] = runtimeDirectory + Path.PathSeparator +
                                   (currentPath ?? string.Empty);
        return new Process { StartInfo = info };
    }

    private static Process CreateRawConverter(VideoInfo video, int outputFrameRateNumerator)
    {
        var info = ExportService.CreateStartInfo(
            AppPaths.Ffmpeg, redirectOutput: true, redirectInput: true);
        ExportService.Add(
            info,
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pix_fmt", "bgr24",
            "-video_size", $"{video.Width}x{video.Height}",
            "-framerate", $"{outputFrameRateNumerator}/{video.FrameRateDenominator}",
            "-i", "pipe:0",
            "-an", "-pix_fmt", "nv12", "-fps_mode", "passthrough",
            "-f", "rawvideo", "pipe:1");
        return new Process { StartInfo = info };
    }

    private static async Task<bool> CanUseCudaDecodeAsync(
        string inputPath,
        VideoInfo video,
        MediaRange range,
        CancellationToken cancellationToken)
    {
        var info = ExportService.CreateStartInfo(
            AppPaths.Ffmpeg, redirectOutput: false, redirectInput: false);
        ExportService.Add(
            info,
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-hwaccel", "cuda",
            "-hwaccel_device", "0",
            "-hwaccel_output_format", "cuda");
        ExportService.AddRangeInput(info, inputPath, video, range);
        ExportService.Add(
            info,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-frames:v", "1",
            "-vf", "hwdownload,format=nv12",
            "-f", "null", "NUL");

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            });
            _ = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task InterpolateChunksAsync(
        Stream decodedFrames,
        Stream workerInput,
        string tempRoot,
        VideoInfo video,
        int frameMultiplier,
        int outputFrameRateNumerator,
        ActiveProcessSet active,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = Channel.CreateBounded<PreparedChunk>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceChunksAsync(
            decodedFrames, tempRoot, video, frameMultiplier,
            active, progress, chunks.Writer, pipelineCancellation.Token);
        var completed = false;
        try
        {
            await foreach (var chunk in chunks.Reader.ReadAllAsync(pipelineCancellation.Token))
            {
                try
                {
                    await ConvertChunkToNv12Async(
                        chunk.ImagePattern,
                        chunk.LoopSingleFrame,
                        chunk.EmittedFrames,
                        outputFrameRateNumerator,
                        video.FrameRateDenominator,
                        workerInput,
                        active,
                        pipelineCancellation.Token);
                }
                finally
                {
                    TryDeleteDirectory(chunk.Root);
                }
            }
            await producer;
            completed = true;
        }
        finally
        {
            pipelineCancellation.Cancel();
            if (!completed) active.KillAll();
            try { await producer; }
            catch when (!completed) { }
            try { await workerInput.DisposeAsync(); }
            catch { }
        }
    }

    private static async Task ProduceChunksAsync(
        Stream decodedFrames,
        string tempRoot,
        VideoInfo video,
        int frameMultiplier,
        ActiveProcessSet active,
        IProgress<ExportProgress>? progress,
        ChannelWriter<PreparedChunk> writer,
        CancellationToken cancellationToken)
    {
        var carryPath = Path.Combine(tempRoot, "carry.bmp");
        FrameSignature? carrySignature = null;
        var firstChunk = true;
        var endOfInput = false;
        Exception? failure = null;

        try
        {
            for (var chunkIndex = 0; !endOfInput; ++chunkIndex)
            {
                var chunkRoot = Path.Combine(tempRoot, $"chunk-{chunkIndex:0000}");
                var inputDirectory = Path.Combine(chunkRoot, "input");
                var outputDirectory = Path.Combine(chunkRoot, "output");
                ResetChildDirectory(tempRoot, inputDirectory);
                ResetChildDirectory(tempRoot, outputDirectory);
                var sourceFrameCount = 0;
                var signatures = new List<FrameSignature>(SourceIntervalsPerChunk + 1);

                if (!firstChunk && File.Exists(carryPath))
                {
                    File.Copy(carryPath, GetInputFramePath(inputDirectory, ++sourceFrameCount), true);
                    if (carrySignature is not null) signatures.Add(carrySignature);
                }

                while (sourceFrameCount < SourceIntervalsPerChunk + 1)
                {
                    var target = GetInputFramePath(inputDirectory, sourceFrameCount + 1);
                    var signature = await ReadBmpFrameToFileAsync(
                        decodedFrames, target, video, cancellationToken);
                    if (signature is null)
                    {
                        endOfInput = true;
                        break;
                    }
                    sourceFrameCount++;
                    signatures.Add(signature);
                }

                if (sourceFrameCount == 0)
                {
                    TryDeleteDirectory(chunkRoot);
                    break;
                }

                if (firstChunk)
                    progress?.Report(new ExportProgress(0, "Preparing the first AI frame chunk…"));

                int emittedFrames;
                string imagePattern;
                var loopSingleFrame = sourceFrameCount == 1;
                if (loopSingleFrame)
                {
                    emittedFrames = frameMultiplier;
                    imagePattern = GetInputFramePath(inputDirectory, 1);
                }
                else
                {
                    await RunRifeAsync(
                        inputDirectory, outputDirectory, sourceFrameCount, frameMultiplier,
                        video, active, cancellationToken);
                    BypassSceneCuts(outputDirectory, signatures, frameMultiplier);
                    emittedFrames = (endOfInput ? sourceFrameCount : sourceFrameCount - 1) * frameMultiplier;
                    imagePattern = Path.Combine(outputDirectory, "%08d.png");
                }

                if (!endOfInput)
                {
                    File.Copy(
                        GetInputFramePath(inputDirectory, sourceFrameCount),
                        carryPath,
                        true);
                    carrySignature = signatures[^1];
                }

                await writer.WriteAsync(
                    new PreparedChunk(chunkRoot, imagePattern, loopSingleFrame, emittedFrames),
                    cancellationToken);
                firstChunk = false;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static Process CreateBmpDecoder(string inputPath, VideoInfo video, MediaRange range)
    {
        var info = ExportService.CreateStartInfo(AppPaths.Ffmpeg, redirectOutput: true, redirectInput: false);
        ExportService.Add(info, "-hide_banner", "-loglevel", "error", "-nostdin");
        ExportService.AddRangeInput(info, inputPath, video, range);
        ExportService.Add(info,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={video.FrameRateNumerator}/{video.FrameRateDenominator}",
            "-c:v", "bmp", "-pix_fmt", "bgr24", "-fps_mode", "passthrough",
            "-f", "image2pipe", "pipe:1");
        return new Process { StartInfo = info };
    }

    private static async Task RunRifeAsync(
        string inputDirectory,
        string outputDirectory,
        int sourceFrameCount,
        int frameMultiplier,
        VideoInfo video,
        ActiveProcessSet active,
        CancellationToken cancellationToken)
    {
        var info = ExportService.CreateStartInfo(AppPaths.Rife, redirectOutput: false, redirectInput: false);
        info.WorkingDirectory = Path.GetDirectoryName(AppPaths.Rife)!;
        ExportService.Add(info,
            "-i", inputDirectory,
            "-o", outputDirectory,
            "-n", checked(sourceFrameCount * frameMultiplier).ToString(CultureInfo.InvariantCulture),
            "-m", AppPaths.RifeModel,
            "-j", "1:2:2",
            "-f", "%08d.png");
        if (video.Width > 1920 || video.Height > 1080)
            ExportService.Add(info, "-u");

        var process = new Process { StartInfo = info };
        active.Add(process);
        try
        {
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var log = await errors;
            if (process.ExitCode != 0)
            {
                var message = new StringBuilder("The local Vulkan frame-interpolation model failed.");
                ExportService.AppendUsefulLog(message, "RIFE", log);
                throw new InvalidOperationException(message.ToString());
            }

            var expected = checked(sourceFrameCount * frameMultiplier);
            var actual = Directory.EnumerateFiles(outputDirectory, "*.png").Count();
            if (actual != expected)
                throw new InvalidOperationException(
                    $"Frame interpolation returned {actual} frames; {expected} were expected.");
        }
        finally
        {
            active.RemoveAndDispose(process);
        }
    }

    private static async Task ConvertChunkToNv12Async(
        string imagePattern,
        bool loopSingleFrame,
        int frameCount,
        int frameRateNumerator,
        int frameRateDenominator,
        Stream destination,
        ActiveProcessSet active,
        CancellationToken cancellationToken)
    {
        var info = ExportService.CreateStartInfo(AppPaths.Ffmpeg, redirectOutput: true, redirectInput: false);
        ExportService.Add(info, "-hide_banner", "-loglevel", "error", "-nostdin");
        if (loopSingleFrame)
            ExportService.Add(info, "-loop", "1");
        else
            ExportService.Add(info, "-start_number", "1");
        ExportService.Add(info,
            "-framerate", $"{frameRateNumerator}/{frameRateDenominator}",
            "-i", imagePattern,
            "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
            "-an", "-pix_fmt", "nv12", "-fps_mode", "passthrough",
            "-f", "rawvideo", "pipe:1");

        var process = new Process { StartInfo = info };
        active.Add(process);
        try
        {
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(
                    destination, 1024 * 1024, cancellationToken);
            }
            catch (IOException)
            {
                // The downstream worker exit code supplies the useful failure.
            }
            await process.WaitForExitAsync(cancellationToken);
            var log = await errors;
            if (process.ExitCode != 0)
            {
                var message = new StringBuilder("Interpolated frames could not be converted for VSR.");
                ExportService.AppendUsefulLog(message, "Converter", log);
                throw new InvalidOperationException(message.ToString());
            }
        }
        finally
        {
            active.RemoveAndDispose(process);
        }
    }

    private static async Task<FrameSignature?> ReadBmpFrameToFileAsync(
        Stream source,
        string path,
        VideoInfo video,
        CancellationToken cancellationToken)
    {
        const int standardHeaderSize = 54;
        var header = new byte[standardHeaderSize];
        var headerBytes = 0;
        while (headerBytes < header.Length)
        {
            var read = await source.ReadAsync(
                header.AsMemory(headerBytes, header.Length - headerBytes), cancellationToken);
            if (read == 0)
            {
                if (headerBytes == 0) return null;
                throw new InvalidDataException("The frame decoder ended in the middle of a BMP header.");
            }
            headerBytes += read;
        }

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
            throw new InvalidDataException("The frame decoder returned an invalid BMP frame.");

        var frameSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4));
        var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22, 4)));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
        var maximumExpectedSize = checked((long)video.Width * video.Height * 4 + 1024 * 1024);
        if (frameSize < standardHeaderSize || frameSize > maximumExpectedSize ||
            width != video.Width || height != video.Height || bitsPerPixel != 24)
            throw new InvalidDataException($"The frame decoder returned an invalid BMP size ({frameSize} bytes).");

        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.WriteAsync(header, cancellationToken);

        var remaining = checked((int)frameSize - header.Length);
        var rowStride = checked((width * 3 + 3) / 4 * 4);
        var sampleStepX = Math.Max(1, width / 64);
        var sampleStepY = Math.Max(1, height / 36);
        var sampledRgb = new List<byte>(64 * 36 * 3);
        var histogram = new int[16 * 3];
        long frameOffset = header.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(1024 * 1024, remaining));
        try
        {
            while (remaining > 0)
            {
                var requested = Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                    throw new InvalidDataException("The frame decoder ended in the middle of a BMP frame.");
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                for (var index = 0; index + 2 < read; ++index)
                {
                    var absoluteOffset = frameOffset + index;
                    if (absoluteOffset < pixelOffset) continue;
                    var pixelDataOffset = absoluteOffset - pixelOffset;
                    var row = pixelDataOffset / rowStride;
                    var rowOffset = pixelDataOffset % rowStride;
                    if (row >= height || rowOffset >= width * 3 || rowOffset % 3 != 0) continue;
                    var x = rowOffset / 3;
                    if (x % sampleStepX != 0 || row % sampleStepY != 0) continue;

                    var blue = buffer[index];
                    var green = buffer[index + 1];
                    var red = buffer[index + 2];
                    sampledRgb.Add(red);
                    sampledRgb.Add(green);
                    sampledRgb.Add(blue);
                    histogram[red >> 4]++;
                    histogram[16 + (green >> 4)]++;
                    histogram[32 + (blue >> 4)]++;
                }

                frameOffset += read;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new FrameSignature([.. sampledRgb], histogram);
    }

    private static void BypassSceneCuts(
        string outputDirectory,
        IReadOnlyList<FrameSignature> signatures,
        int frameMultiplier)
    {
        for (var sourceIndex = 0; sourceIndex + 1 < signatures.Count; ++sourceIndex)
        {
            if (!IsSceneCut(signatures[sourceIndex], signatures[sourceIndex + 1])) continue;
            var previousFrame = Path.Combine(
                outputDirectory, $"{sourceIndex * frameMultiplier + 1:00000000}.png");
            for (var intermediate = 1; intermediate < frameMultiplier; ++intermediate)
            {
                var generatedFrame = Path.Combine(
                    outputDirectory,
                    $"{sourceIndex * frameMultiplier + intermediate + 1:00000000}.png");
                File.Copy(previousFrame, generatedFrame, true);
            }
        }
    }

    private static bool IsSceneCut(FrameSignature first, FrameSignature second)
    {
        var sampleBytes = Math.Min(first.SampledRgb.Length, second.SampledRgb.Length);
        if (sampleBytes == 0) return false;

        long absoluteDifference = 0;
        for (var index = 0; index < sampleBytes; ++index)
            absoluteDifference += Math.Abs(first.SampledRgb[index] - second.SampledRgb[index]);
        var meanRgbDifference = absoluteDifference / (double)sampleBytes;

        long histogramDifference = 0;
        for (var index = 0; index < first.Histogram.Length; ++index)
            histogramDifference += Math.Abs(first.Histogram[index] - second.Histogram[index]);
        var samplesPerFrame = sampleBytes / 3d;
        var normalizedHistogramDifference = histogramDifference / (samplesPerFrame * 6d);

        return meanRgbDifference >= 45d && normalizedHistogramDifference >= 0.25d;
    }

    private static string GetInputFramePath(string directory, int index) =>
        Path.Combine(directory, $"{index:00000000}.bmp");

    private static void ResetChildDirectory(string root, string directory)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A frame-interpolation temporary path escaped its workspace.");
        if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, true);
        Directory.CreateDirectory(fullDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { /* Leave temporary diagnostics if a third-party process still holds a file. */ }
    }

    private sealed record PreparedChunk(
        string Root,
        string ImagePattern,
        bool LoopSingleFrame,
        int EmittedFrames);

    private sealed class ActiveProcessSet : IDisposable
    {
        private readonly object gate = new();
        private readonly HashSet<Process> processes = [];

        public void Add(Process process)
        {
            lock (gate) processes.Add(process);
        }

        public void RemoveAndDispose(Process process)
        {
            lock (gate) processes.Remove(process);
            process.Dispose();
        }

        public void KillAll()
        {
            Process[] snapshot;
            lock (gate) snapshot = [.. processes];
            foreach (var process in snapshot)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            }
        }

        public async Task WaitForAllExitAsync()
        {
            Process[] snapshot;
            lock (gate) snapshot = [.. processes];
            foreach (var process in snapshot)
            {
                try
                {
                    if (!process.HasExited) await process.WaitForExitAsync();
                }
                catch { }
            }
        }

        public void Dispose()
        {
            Process[] snapshot;
            lock (gate)
            {
                snapshot = [.. processes];
                processes.Clear();
            }
            foreach (var process in snapshot) process.Dispose();
        }
    }

    private sealed record FrameSignature(byte[] SampledRgb, int[] Histogram);
}
