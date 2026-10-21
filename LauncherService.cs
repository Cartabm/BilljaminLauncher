using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BilljaminLauncher;

public sealed class GameManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("archive_sha256")] public string ArchiveSha256 { get; set; } = "";
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = new();
}

public sealed record RemoteRelease(GameManifest Manifest, Uri ArchiveUrl);
public sealed record VerificationResult(int Good, int MissingOrBad)
{
    public bool IsValid => MissingOrBad == 0;
}

public sealed class LauncherService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const long MaxManifestBytes = 8 * 1024 * 1024;

    public LauncherService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BilljaminLauncher", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<RemoteRelease> GetLatestAsync(CancellationToken ct)
    {
        if (!LauncherSettings.IsConfigured)
            throw new InvalidOperationException("Edit LauncherSettings.cs and set GitHubOwner and GitHubRepo first.");

        var api = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(LauncherSettings.GitHubOwner)}/{Uri.EscapeDataString(LauncherSettings.GitHubRepo)}/releases/latest");
        using var response = await _http.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var release = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var assets = release.RootElement.GetProperty("assets");
        Uri? manifestUrl = null, archiveUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (name == LauncherSettings.ManifestAsset) manifestUrl = uri;
            if (name == LauncherSettings.ArchiveAsset) archiveUrl = uri;
        }
        if (manifestUrl is null || archiveUrl is null)
            throw new InvalidDataException("Latest GitHub release must contain manifest.json and game.zip assets.");

        using var manifestResponse = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        manifestResponse.EnsureSuccessStatusCode();
        if (manifestResponse.Content.Headers.ContentLength is > MaxManifestBytes)
            throw new InvalidDataException("Manifest is too large.");
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await manifestStream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > MaxManifestBytes) throw new InvalidDataException("Manifest is too large.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(buffer, JsonOptions, ct)
            ?? throw new InvalidDataException("Manifest is empty.");
        ValidateManifest(manifest);
        return new RemoteRelease(manifest, archiveUrl);
    }

    public static string GetLocalVersion()
    {
        var path = Path.Combine(LauncherSettings.GameDirectory, ".installed-version");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "Not installed";
    }

    public async Task<VerificationResult> VerifyAsync(GameManifest manifest, IProgress<string>? progress, CancellationToken ct)
    {
        ValidateManifest(manifest);
        int good = 0, bad = 0;
        foreach (var (relative, expected) in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string path = ResolveSafePath(LauncherSettings.GameDirectory, relative);
            if (File.Exists(path) && await HashFileAsync(path, ct) == expected) good++;
            else bad++;
            progress?.Report($"Verified {good + bad}/{manifest.Files.Count} files; {bad} need repair");
        }
        return new VerificationResult(good, bad);
    }

    public async Task InstallAsync(RemoteRelease release, IProgress<string>? progress, IProgress<int>? percentage, CancellationToken ct)
    {
        ValidateManifest(release.Manifest);
        var destination = LauncherSettings.GameDirectory;
        var parent = Directory.GetParent(destination)!.FullName;
        Directory.CreateDirectory(parent);
        var id = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(parent, $".download-{id}.zip");
        var stage = Path.Combine(parent, $".stage-{id}");
        var backup = Path.Combine(parent, $".backup-{id}");
        bool movedOld = false;
        bool committed = false;
        try
        {
            progress?.Report("Downloading complete game archive...");
            using (var response = await _http.GetAsync(release.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true);
                byte[] block = new byte[1024 * 128];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(block, ct)) > 0)
                {
                    await output.WriteAsync(block.AsMemory(0, read), ct);
                    done += read;
                    if (total is > 0) percentage?.Report((int)Math.Clamp(done * 100 / total.Value, 0, 100));
                    progress?.Report(total is > 0 ? $"Downloaded {done / 1048576.0:F1} / {total.Value / 1048576.0:F1} MB" : $"Downloaded {done / 1048576.0:F1} MB");
                }
            }
            progress?.Report("Checking archive SHA-256...");
            if (await HashFileAsync(archivePath, ct) != release.Manifest.ArchiveSha256)
                throw new InvalidDataException("Downloaded archive SHA-256 mismatch. Installation cancelled.");

            Directory.CreateDirectory(stage);
            progress?.Report("Extracting and verifying files...");
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string relative = entry.FullName.Replace('\\', '/');
                    if (!release.Manifest.Files.ContainsKey(relative) || !seen.Add(relative))
                        throw new InvalidDataException($"Archive contains an unexpected or duplicate file: {relative}");
                    string file = ResolveSafePath(stage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    entry.ExtractToFile(file, overwrite: false);
                    if (await HashFileAsync(file, ct) != release.Manifest.Files[relative])
                        throw new InvalidDataException($"Extracted file failed SHA-256: {relative}");
                }
                if (seen.Count != release.Manifest.Files.Count)
                    throw new InvalidDataException("Archive is missing files listed in manifest.");
            }
            await File.WriteAllTextAsync(Path.Combine(stage, ".installed-version"), release.Manifest.Version, ct);
            ct.ThrowIfCancellationRequested();
            progress?.Report("Installing verified build...");
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedOld = true;
            }
            try
            {
                Directory.Move(stage, destination);
                committed = true;
            }
            catch
            {
                if (movedOld && !Directory.Exists(destination)) Directory.Move(backup, destination);
                throw;
            }
            progress?.Report($"Ready to play — version {release.Manifest.Version}");
            percentage?.Report(100);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stage);
            if (committed) TryDeleteDirectory(backup);
        }
    }

    public static void LaunchGame()
    {
        string path = Path.Combine(LauncherSettings.GameDirectory, LauncherSettings.GameExecutable);
        if (!File.Exists(path)) throw new FileNotFoundException("Game executable is missing. Run Verify / Repair.", path);
        Process.Start(new ProcessStartInfo(path) { WorkingDirectory = LauncherSettings.GameDirectory, UseShellExecute = true });
    }

    private static void ValidateManifest(GameManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 80)
            throw new InvalidDataException("Invalid manifest version.");
        if (!IsHash(manifest.ArchiveSha256)) throw new InvalidDataException("Invalid archive SHA-256.");
        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > 100000)
            throw new InvalidDataException("Manifest has no files or too many files.");
        if (!manifest.Files.ContainsKey(LauncherSettings.GameExecutable))
            throw new InvalidDataException($"Manifest must contain {LauncherSettings.GameExecutable}.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in manifest.Files)
        {
            _ = ResolveSafePath(Path.GetTempPath(), relative);
            if (!paths.Add(relative) || !IsHash(hash)) throw new InvalidDataException($"Invalid or duplicate manifest entry: {relative}");
        }
    }

    private static string ResolveSafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/') ||
            relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or "..") ||
            relative.Equals(".installed-version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe file path: {relative}");
        string basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe file path: {relative}");
        return full;
    }

    private static bool IsHash(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        byte[] digest = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(digest);
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
    public void Dispose() => _http.Dispose();
}
