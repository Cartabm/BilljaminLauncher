namespace BilljaminLauncher;

internal static class LauncherSettings
{
    // Game releases: keep this channel separate from launcher releases.
    public const string GitHubOwner = "Cartabm";
    public const string GitHubRepo = "Billjamin";

    // Launcher releases live in a separate repository so launcher updates never
    // interfere with the game's manifest.json/game.zip release channel.
    public const string LauncherGitHubOwner = "Cartabm";
    public const string LauncherGitHubRepo = "BilljaminLauncher";
    public const string LauncherVersion = "1.2.0";
    public const string LauncherExecutable = "BilljaminLauncher.exe";
    public const string LauncherManifestAsset = "launcher-manifest.json";

    public const string GameExecutable = "TheTowerOfBilljamin.exe";
    public const string ManifestAsset = "manifest.json";
    public const string ArchiveAsset = "game.zip";

    public static string GameDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TowerOfBilljamin", "Game");

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GitHubOwner) &&
        !string.IsNullOrWhiteSpace(GitHubRepo);
}
