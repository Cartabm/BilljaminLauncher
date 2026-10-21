using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BilljaminLauncher;

internal sealed class LauncherUpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal static class LauncherSelfUpdater
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BilljaminLauncher", LauncherSettings.LauncherVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    // Returns true when a helper was started and this instance should close.
    // If GitHub is unavailable or no launcher release exists yet, the game launcher continues normally.
    public static async Task<bool> CheckAndStageUpdateAsync()
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) ||
            !Path.GetFileName(currentExe).Equals(LauncherSettings.LauncherExecutable, StringComparison.OrdinalIgnoreCase))
            return false; // Do not attempt self-update when running under dotnet run / a debugger.

        try
        {
            string apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(LauncherSettings.LauncherGitHubOwner)}/{Uri.EscapeDataString(LauncherSettings.LauncherGitHubRepo)}/releases/latest";
            using var response = await Http.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;
            using var releaseStream = await response.Content.ReadAsStreamAsync();
            using var release = await JsonDocument.ParseAsync(releaseStream);
            if (!release.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return false;

            Uri? manifestUrl = null;
            Uri? exeUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (name == LauncherSettings.LauncherManifestAsset) manifestUrl = uri;
                if (name == LauncherSettings.LauncherExecutable) exeUrl = uri;
            }
            if (manifestUrl is null || exeUrl is null) return false;

            using var manifestResponse = await Http.GetAsync(manifestUrl);
            if (!manifestResponse.IsSuccessStatusCode) return false;
            await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync();
            var manifest = await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(manifestStream, JsonOptions);
            if (manifest is null || manifest.File != LauncherSettings.LauncherExecutable ||
                !IsHash(manifest.Sha256) || !Version.TryParse(NormalizeVersion(manifest.Version), out var latestVersion) ||
                !Version.TryParse(NormalizeVersion(LauncherSettings.LauncherVersion), out var currentVersion) ||
                latestVersion <= currentVersion)
                return false;

            string tempRoot = Path.Combine(Path.GetTempPath(), "BilljaminLauncherUpdate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string stagedExe = Path.Combine(tempRoot, LauncherSettings.LauncherExecutable + ".new");
            string helperExe = Path.Combine(tempRoot, "BilljaminLauncherUpdateHelper.exe");
            try
            {
                using var download = await Http.GetAsync(exeUrl, HttpCompletionOption.ResponseHeadersRead);
                download.EnsureSuccessStatusCode();
                await using (var input = await download.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(stagedExe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await input.CopyToAsync(output);

                if (!string.Equals(await HashFileAsync(stagedExe), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Launcher update checksum did not match. The update was cancelled.");

                // A copied helper can run while Windows locks the original executable.
                File.Copy(currentExe, helperExe, overwrite: false);
                var start = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--apply-launcher-update");
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                start.ArgumentList.Add(Path.GetFullPath(currentExe));
                start.ArgumentList.Add(Path.GetFullPath(stagedExe));
                start.ArgumentList.Add(Path.GetFullPath(helperExe));
                Process.Start(start);
                return true;
            }
            catch
            {
                TryDeleteDirectory(tempRoot);
                throw;
            }
        }
        catch (Exception ex)
        {
            // Offline or a not-yet-created launcher repository should not block game access.
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TowerOfBilljamin", "Launcher");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "launcher-update.log"), $"[{DateTimeOffset.Now:O}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch { }
            return false;
        }
    }

    // Args: --apply-launcher-update <parentPid> <targetExe> <stagedExe> <helperExe>
    public static void RunUpdateHelper(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[1], out int parentPid)) return;
        string target = Path.GetFullPath(args[2]);
        string staged = Path.GetFullPath(args[3]);
        string helper = Path.GetFullPath(args[4]);
        string backup = target + ".previous";
        try
        {
            try { using var parent = Process.GetProcessById(parentPid); if (!parent.WaitForExit(60000)) return; }
            catch (ArgumentException) { /* Parent already exited. */ }

            if (!File.Exists(staged) || !File.Exists(target)) throw new FileNotFoundException("Launcher update staging file or current launcher was missing.");
            TryDeleteFile(backup);
            File.Copy(target, backup, overwrite: true);
            try
            {
                File.Copy(staged, target, overwrite: true);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                TryDeleteFile(backup);
            }
            catch
            {
                if (File.Exists(backup)) File.Copy(backup, target, overwrite: true);
                throw;
            }
        }
        catch (Exception ex)
        {
            try { MessageBox.Show("The launcher update could not be applied. Your previous launcher was preserved if possible.\n\n" + ex.Message, "Billjamin Launcher Update", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            try { if (File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }
        finally
        {
            TryDeleteFile(staged);
            // The helper executable is locked until this process exits; leave it in temp for OS cleanup.
        }
    }

    private static string NormalizeVersion(string value) => value.Trim().TrimStart('v', 'V');
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
}
