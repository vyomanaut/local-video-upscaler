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

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
