using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TableEdit;

/// <param name="DmgUrl">Download for this Mac's chip, or null when the release has none (or this is not a Mac).</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl,
    string? DmgName, string? DmgUrl, long DmgSize, string? ChecksumsUrl);

/// <summary>
/// Checks GitHub Releases for a newer TableEdit and, on macOS, downloads the matching DMG, verifies it against the
/// release's SHA256SUMS.txt, copies the new app next to the installed one and swaps it in after TableEdit quits.
/// </summary>
public static class Updater
{
    public const string Repository = "ikkit1314/TableEdit";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TableEdit", Current.ToString(3)));
        return client;
    }


    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Reads "v1.4.0", "1.4", "v1.4.0-beta" and similar; null when the tag is not a version.</summary>
    public static Version? ParseTag(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) text = text[..cut];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(Version latest) => latest > Current;

    /// <summary>"arm64" or "x64", matching the DMG names the release workflow publishes.</summary>
    public static string MacArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancel = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No published release was found on GitHub.");
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
            throw new InvalidOperationException("GitHub is limiting update checks right now. Try again in an hour.");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel))!;

        var tag = (string?)json["tag_name"] ?? "";
        var version = ParseTag(tag) ?? throw new InvalidOperationException($"The latest release tag \"{tag}\" is not a version number.");
        string? dmgName = null, dmgUrl = null, sumsUrl = null;
        long dmgSize = 0;
        foreach (var asset in json["assets"]?.AsArray() ?? new JsonArray())
        {
            var name = (string?)asset?["name"] ?? "";
            var url = (string?)asset?["browser_download_url"];
            if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
            else if (OperatingSystem.IsMacOS() && name.EndsWith($"-macos-{MacArchitecture}.dmg", StringComparison.OrdinalIgnoreCase))
            {
                dmgName = name;
                dmgUrl = url;
                dmgSize = (long?)asset?["size"] ?? 0;
            }
        }
        return new ReleaseInfo(version, tag, (string?)json["body"] ?? "", (string?)json["html_url"] ?? ReleasesPage,
            dmgName, dmgUrl, dmgSize, sumsUrl);
    }

    /// <summary>The installed TableEdit.app, when this process runs from one whose folder can be written.</summary>
    public static string? InstalledBundle(out string reason)
    {
        reason = "";
        if (!OperatingSystem.IsMacOS())
        {
            reason = "Automatic install is only available in the macOS app.";
            return null;
        }
        // …/TableEdit.app/Contents/MacOS/
        var macOs = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        var bundle = macOs.Parent?.Parent;
        if (macOs.Name != "MacOS" || bundle == null || !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            reason = "TableEdit is not running from an installed .app, so it can't replace itself.";
            return null;
        }
        if (bundle.FullName.StartsWith("/Volumes/", StringComparison.Ordinal) || bundle.FullName.Contains("/AppTranslocation/"))
        {
            reason = "TableEdit is running from the disk image or a temporary location. Drag it into Applications first.";
            return null;
        }
        var folder = bundle.Parent!.FullName;
        try
        {
            var probe = Path.Combine(folder, $".tableedit-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            reason = $"TableEdit can't write to {folder}. Install the update from the downloaded disk image instead.";
            return null;
        }
        return bundle.FullName;
    }

    /// <summary>Downloads the release's DMG for this Mac and checks it against SHA256SUMS.txt.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken cancel)
    {
        if (release.DmgUrl == null || release.DmgName == null)
            throw new InvalidOperationException($"Release {release.Tag} has no disk image for this Mac ({MacArchitecture}).");

        string? expected = null;
        if (release.ChecksumsUrl != null)
        {
            var sums = await Http.GetStringAsync(release.ChecksumsUrl, cancel);
            foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == release.DmgName) expected = parts[0].ToLowerInvariant();
            }
        }

        var folder = Path.Combine(Path.GetTempPath(), "TableEdit-update");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, release.DmgName);
        var partial = target + ".download";

        using (var response = await Http.GetAsync(release.DmgUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.DmgSize;
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = File.Create(partial);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expected != null && actual != expected)
            {
                output.Close();
                File.Delete(partial);
                throw new InvalidOperationException("The download is damaged (its SHA-256 doesn't match the release). Try again.");
            }
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Mounts the DMG, copies its TableEdit.app next to the installed bundle and checks the copy's version. Returns the
    /// staged bundle. Nothing installed is touched yet, so a failure here leaves the current app working.
    /// </summary>
    public static async Task<string> StageAsync(string dmg, string installedBundle, Version expected, CancellationToken cancel)
    {
        var mount = Path.Combine(Path.GetTempPath(), $"TableEdit-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mount);
        await RunAsync("/usr/bin/hdiutil", cancel, "attach", "-readonly", "-nobrowse", "-noautoopen", "-mountpoint", mount, dmg);
        try
        {
            var source = Path.Combine(mount, "TableEdit.app");
            if (!Directory.Exists(source)) throw new InvalidOperationException("The disk image doesn't contain TableEdit.app.");
            var staged = Path.Combine(Path.GetDirectoryName(installedBundle)!, ".TableEdit-update.app");
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            await RunAsync("/usr/bin/ditto", cancel, source, staged);

            var version = (await RunAsync("/usr/bin/defaults", cancel, "read", Path.Combine(staged, "Contents", "Info"), "CFBundleShortVersionString")).Trim();
            if (ParseTag(version) != expected)
            {
                Directory.Delete(staged, recursive: true);
                throw new InvalidOperationException($"The disk image holds version {version}, not {expected.ToString(3)}.");
            }
            return staged;
        }
        finally
        {
            try { await RunAsync("/usr/bin/hdiutil", CancellationToken.None, "detach", mount, "-quiet"); } catch { /* best effort */ }
            try { Directory.Delete(mount); } catch { /* still mounted or already gone */ }
        }
    }

    /// <summary>
    /// Starts a small script that waits for this process to exit, swaps the staged bundle into place (restoring
    /// the old one if the move fails) and opens the new version. The caller then quits TableEdit.
    /// </summary>
    public static void LaunchSwapAndRelaunch(string staged, string installedBundle)
    {
        const string script = """
            pid="$1"; staged="$2"; app="$3"
            # Refuse anything but two absolute .app bundle paths, so a bad argument can never remove other files.
            case "$app" in /?*.app) ;; *) echo "bad app path: $app"; exit 1 ;; esac
            case "$staged" in /?*.app) ;; *) echo "bad staged path: $staged"; exit 1 ;; esac
            [ -d "$staged" ] || { echo "staged update missing"; exit 1; }
            for _ in $(seq 1 240); do kill -0 "$pid" 2>/dev/null || break; sleep 0.25; done
            backup="${app%.app}.previous.app"
            rm -rf "$backup"
            if mv "$app" "$backup"; then
              if mv "$staged" "$app"; then rm -rf "$backup"; else mv "$backup" "$app"; fi
            fi
            open "$app"
            """;
        var log = Path.Combine(Path.GetTempPath(), "TableEdit-update", "install.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var start = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in new[] { "-c", script + $" >>'{log}' 2>&1", "tableedit-update", Environment.ProcessId.ToString(), staged, installedBundle })
            start.ArgumentList.Add(arg);
        Process.Start(start);
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Process.Start("xdg-open", url);
        }
        catch { /* no browser available */ }
    }

    private static async Task<string> RunAsync(string file, CancellationToken cancel, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var error = process.StandardError.ReadToEndAsync(cancel);
        await process.WaitForExitAsync(cancel);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} failed: {(await error).Trim()}");
        return await output;
    }

    // ───────────────────────────── settings ─────────────────────────────

    private sealed class Settings
    {
        public bool AutoCheck { get; set; } = true;
        public DateTime LastCheck { get; set; }
        public string? SkippedVersion { get; set; }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableEdit", "update.json");

    private static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    private static void Save(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch { /* settings are a convenience */ }
    }

    public static bool AutoCheck
    {
        get => Load().AutoCheck;
        set { var s = Load(); s.AutoCheck = value; Save(s); }
    }

    public static void Skip(Version version)
    {
        var s = Load();
        s.SkippedVersion = version.ToString(3);
        Save(s);
    }

    /// <summary>
    /// Whether to check quietly at launch: only the installed macOS app, at most once a day, and never when
    /// TABLEEDIT_NO_UPDATE_CHECK is set (tests and development runs).
    /// </summary>
    public static bool ShouldAutoCheck()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TABLEEDIT_NO_UPDATE_CHECK"))) return false;
        if (!OperatingSystem.IsMacOS() || !AppContext.BaseDirectory.Contains(".app/Contents/MacOS", StringComparison.Ordinal)) return false;
        var s = Load();
        if (!s.AutoCheck || DateTime.UtcNow - s.LastCheck < TimeSpan.FromHours(20)) return false;
        s.LastCheck = DateTime.UtcNow;
        Save(s);
        return true;
    }

    public static bool IsSkipped(Version version) => Load().SkippedVersion == version.ToString(3);
}
