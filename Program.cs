namespace BilljaminLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apply-launcher-update")
        {
            LauncherSelfUpdater.RunUpdateHelper(args);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
