namespace RtxLocalVideo;

internal static class AppPaths
{
    private static readonly string[] FfmpegRuntimeFiles =
    [
        "avcodec-60.dll",
        "avdevice-60.dll",
        "avfilter-9.dll",
        "avformat-60.dll",
        "avutil-58.dll",
        "swresample-4.dll",
        "swscale-7.dll"
    ];

    public static string Ffmpeg => FindRequired("ffmpeg.exe", @"tools\ffmpeg\bin\ffmpeg.exe");
    public static string Ffprobe => FindRequired("ffprobe.exe", @"tools\ffmpeg\bin\ffprobe.exe");
    public static string VsrProcessor => FindRequired("VsrProcessor.exe", @"native\VsrProcessor\VsrProcessor.exe");
    public static string RifeProcessor => FindRequired("RifeProcessor.exe", @"native\RifeProcessor\RifeProcessor.exe");
    public static string Rife => FindRequired("rife-ncnn-vulkan.exe", @"tools\rife\rife-ncnn-vulkan.exe");
    public static string RifeModel => FindRequiredDirectory("rife-v4.6", @"tools\rife\rife-v4.6");

    public static bool FrameInterpolationDependenciesPresent =>
        (TryFind("RifeProcessor.exe", @"native\RifeProcessor\RifeProcessor.exe") is not null ||
         TryFind("rife-ncnn-vulkan.exe", @"tools\rife\rife-ncnn-vulkan.exe") is not null) &&
        TryFind("vcomp140.dll", @"tools\rife\vcomp140.dll") is not null &&
        TryFind(@"rife-v4.6\flownet.bin", @"tools\rife\rife-v4.6\flownet.bin") is not null &&
        TryFind(@"rife-v4.6\flownet.param", @"tools\rife\rife-v4.6\flownet.param") is not null;

    public static bool PersistentFrameInterpolationDependenciesPresent =>
        TryFind("RifeProcessor.exe", @"native\RifeProcessor\RifeProcessor.exe") is not null &&
        TryFind("vcomp140.dll", @"tools\rife\vcomp140.dll") is not null &&
        TryFind(@"rife-v4.6\flownet.bin", @"tools\rife\rife-v4.6\flownet.bin") is not null &&
        TryFind(@"rife-v4.6\flownet.param", @"tools\rife\rife-v4.6\flownet.param") is not null;

    public static bool AllDependenciesPresent =>
        TryFind("ffmpeg.exe", @"tools\ffmpeg\bin\ffmpeg.exe") is not null &&
        TryFind("ffprobe.exe", @"tools\ffmpeg\bin\ffprobe.exe") is not null &&
        TryFind("VsrProcessor.exe", @"native\VsrProcessor\VsrProcessor.exe") is not null &&
        FfmpegRuntimeFiles.All(fileName =>
            TryFind(fileName, $@"tools\ffmpeg\bin\{fileName}") is not null);

    private static string FindRequired(string besideApp, string workspaceRelative) =>
        TryFind(besideApp, workspaceRelative) ??
        throw new FileNotFoundException($"Required component {besideApp} was not found.");

    private static string FindRequiredDirectory(string besideApp, string workspaceRelative) =>
        TryFindDirectory(besideApp, workspaceRelative) ??
        throw new DirectoryNotFoundException($"Required component {besideApp} was not found.");

    private static string? TryFind(string besideApp, string workspaceRelative)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, besideApp),
            Path.Combine(Environment.CurrentDirectory, workspaceRelative),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", workspaceRelative)
        };

        // Self-contained single-file apps extract native/content files into the
        // bundle cache. The runtime exposes that directory through this value.
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearchDirectories)
        {
            foreach (var directory in nativeSearchDirectories.Split(
                         Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(directory, besideApp));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private static string? TryFindDirectory(string besideApp, string workspaceRelative)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, besideApp),
            Path.Combine(Environment.CurrentDirectory, workspaceRelative),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", workspaceRelative)
        };

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearchDirectories)
        {
            foreach (var directory in nativeSearchDirectories.Split(
                         Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(directory, besideApp));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(Directory.Exists);
    }
}
