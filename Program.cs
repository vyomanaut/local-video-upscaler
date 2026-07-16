namespace RtxLocalVideo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--check-dependencies", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = AppPaths.AllDependenciesPresent ? 0 : 2;
            return;
        }
        if (args.Contains("--check-frame-interpolation-dependencies", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = AppPaths.FrameInterpolationDependenciesPresent ? 0 : 3;
            return;
        }

        ApplicationConfiguration.Initialize();
        var initialMediaPath = args.FirstOrDefault(argument =>
            !argument.StartsWith("--", StringComparison.Ordinal) && File.Exists(argument));
        Application.Run(new MainForm(initialMediaPath));
    }
}
