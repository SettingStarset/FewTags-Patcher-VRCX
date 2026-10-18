using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VRCXFewTagsPatcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--replace-self")
        {
            SelfUpdater.ReplaceAndRestart(args[1], args[2], int.Parse(args[3]));
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new PatcherForm());
    }
}

internal sealed class PatcherForm : Form
{
    private const string VrcxReleaseApi = "https://api.github.com/repos/Natsumi-sama/VRCX/releases/latest";
    private const string FewTagsBaseUrl = "https://raw.githubusercontent.com/Fewdys/FewTags/main/";
    private const string DataMarker = "<!-- FEWTAGS_DATA_PLACEHOLDER -->";
    private const int MaxDatabaseBytes = 32 * 1024 * 1024;
    private const int MaxUpdaterBytes = 128 * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly TextBox _path = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _status = new() { AutoSize = false, Height = 58, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _apply = new() { Text = "Apply / Update", Width = 128, Height = 36 };
    private readonly Button _restore = new() { Text = "Restore backup", Width = 128, Height = 36 };
    private readonly Button _browse = new() { Text = "Browse…", Width = 96, Height = 28 };

    public PatcherForm()
    {
        Text = "VRCX FewTags Patcher";
        ClientSize = new Size(650, 250);
        MinimumSize = new Size(570, 250);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "FewTags for VRCX",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
            Left = 18,
            Top = 16
        };
        var subtitle = new Label
        {
            Text = $"VRCX {PatcherBuildInfo.VrcxReleaseTag} · patcher {PatcherBuildInfo.PatcherVersion}",
            AutoSize = true,
            Left = 19,
            Top = 47
        };
        var pathLabel = new Label { Text = "VRCX install folder", AutoSize = true, Left = 19, Top = 83 };
        _path.SetBounds(19, 105, 500, 27);
        _path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browse.SetBounds(529, 104, 101, 29);
        _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _browse.Click += (_, _) => BrowseForInstall();
        _status.SetBounds(19, 145, 611, 54);
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _apply.SetBounds(355, 199, 131, 36);
        _apply.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _restore.SetBounds(499, 199, 131, 36);
        _restore.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _apply.Click += async (_, _) => await ApplyAsync();
        _restore.Click += (_, _) => RestoreLatestBackup();
        Controls.AddRange(new Control[] { title, subtitle, pathLabel, _path, _browse, _status, _apply, _restore });

        _path.Text = FindInstall() ?? @"C:\Program Files\VRCX";
        _status.Text = "Choose your VRCX install folder, then select Apply / Update.";
        Shown += async (_, _) => await CheckForPatcherUpdateAsync();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VRCX-FewTags-Patcher/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private void BrowseForInstall()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder containing VRCX.exe",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (Directory.Exists(_path.Text)) dialog.SelectedPath = _path.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
    }

    private async Task ApplyAsync()
    {
        if (!TryGetInstall(out var target)) return;
        SetBusy(true);
        var stage = Path.Combine(Path.GetTempPath(), "VRCX-FewTags-" + Guid.NewGuid().ToString("N"));
        try
        {
            _status.Text = "Checking the VRCX release and installed version…";
            await EnsurePayloadSupportsLatestAsync(target);

            _status.Text = "Downloading and validating the latest FewTags databases…";
            var v2Bytes = await DownloadDatabaseAsync("FewTagsv2.json");
            var legacyBytes = await DownloadDatabaseAsync("FewTags.json");

            _status.Text = "Close VRCX if it is open…";
            if (!await StopRunningVrcxAsync())
            {
                _status.Text = "VRCX is still open. Close it, then apply the patch again.";
                return;
            }

            Directory.CreateDirectory(stage);
            var archivePath = Path.Combine(stage, "payload.zip");
            await ExtractEmbeddedPayloadAsync(archivePath);
            var payloadRoot = Path.Combine(stage, "payload");
            Directory.CreateDirectory(payloadRoot);
            ZipFile.ExtractToDirectory(archivePath, payloadRoot);
            var htmlRoot = Path.Combine(payloadRoot, "html");
            var indexPath = Path.Combine(htmlRoot, "index.html");
            if (!File.Exists(indexPath)) throw new InvalidDataException("The built VRCX frontend is missing index.html.");

            _status.Text = "Embedding the current databases and preparing a backup…";
            InjectDatabase(indexPath, v2Bytes, legacyBytes);
            var destinationHtml = Path.Combine(target, "html");
            if (!Directory.Exists(destinationHtml)) throw new DirectoryNotFoundException("The selected VRCX install has no html folder.");
            var changedFiles = FindChangedFiles(htmlRoot, destinationHtml);
            if (changedFiles.Count == 0)
            {
                _status.Text = "VRCX already has the current FewTags integration.";
                return;
            }

            var backupPath = Path.Combine(target, "FewTagsVRCXExeBackup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backupPath);
            var entries = new List<BackupEntry>();
            foreach (var file in changedFiles)
            {
                var relative = file.RelativePath;
                var previousPath = Path.Combine(destinationHtml, relative);
                var existed = File.Exists(previousPath);
                entries.Add(new BackupEntry(relative, existed));
                if (!existed) continue;
                var backupFile = SafeCombine(backupPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                File.Copy(previousPath, backupFile, overwrite: true);
            }
            await File.WriteAllTextAsync(Path.Combine(backupPath, "manifest.json"), JsonSerializer.Serialize(entries));

            try
            {
                foreach (var file in changedFiles)
                {
                    var destination = SafeCombine(destinationHtml, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file.SourcePath, destination, overwrite: true);
                }
            }
            catch
            {
                RestoreBackup(backupPath, destinationHtml);
                throw;
            }

            _status.Text = $"FewTags applied. Updated {changedFiles.Count} files; backup saved to {Path.GetFileName(backupPath)}.";
            MessageBox.Show(this,
                $"FewTags was applied to the selected VRCX installation.\n\nUpdated files: {changedFiles.Count}\nBackup: {backupPath}\n\nRelaunch VRCX to see the tags.",
                "VRCX updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "The patch could not be completed.";
            MessageBox.Show(this, ex.Message, "Patch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
            SetBusy(false);
        }
    }

    private void RestoreLatestBackup()
    {
        if (!TryGetInstall(out var target)) return;
        if (IsVrcxRunning())
        {
            MessageBox.Show(this, "Close VRCX before restoring its frontend files.", "VRCX is open", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var backup = Directory.Exists(target)
            ? Directory.EnumerateDirectories(target, "FewTagsVRCXExeBackup-*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (backup is null)
        {
            MessageBox.Show(this, "No VRCX FewTags backup was found in that folder.", "Backup not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            RestoreBackup(backup, Path.Combine(target, "html"));
            _status.Text = $"Restored {Path.GetFileName(backup)}. Relaunch VRCX.";
            MessageBox.Show(this, $"Restored the frontend from:\n{backup}", "Restore complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool TryGetInstall(out string target)
    {
        target = string.Empty;
        try { target = Path.GetFullPath(_path.Text.Trim().Trim('"')); }
        catch
        {
            MessageBox.Show(this, "Select a valid VRCX install folder.", "Invalid folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (!File.Exists(Path.Combine(target, "VRCX.exe")) || !Directory.Exists(Path.Combine(target, "html")))
        {
            MessageBox.Show(this, "That folder must contain both VRCX.exe and its html folder.", "VRCX not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private static string? FindInstall()
    {
        var processPath = Process.GetProcessesByName("VRCX")
            .Select(process => { try { return process.MainModule?.FileName; } catch { return null; } })
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (processPath is not null) return Path.GetDirectoryName(processPath);
        foreach (var candidate in new[] { @"C:\Program Files\VRCX", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VRCX") })
            if (File.Exists(Path.Combine(candidate, "VRCX.exe"))) return candidate;
        return null;
    }

    private static async Task EnsurePayloadSupportsLatestAsync(string target)
    {
        using var response = await Http.GetAsync(VrcxReleaseApi);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var latestTag = release.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        if (TryReleaseTime(latestTag, out var latestTime) && TryReleaseTime(PatcherBuildInfo.VrcxReleaseTag, out var payloadTime) && latestTime > payloadTime)
            throw new InvalidOperationException($"A newer VRCX release ({latestTag}) exists than this patcher's frontend ({PatcherBuildInfo.VrcxReleaseTag}). Wait for a compatible patcher update before applying.");
        if (!TryReleaseTime(latestTag, out _) && !string.Equals(latestTag, PatcherBuildInfo.VrcxReleaseTag, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The latest VRCX release could not be compared safely with the frontend included in this patcher.");

        var versionPath = Path.Combine(target, "Version");
        var installedVersionText = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : string.Empty;
        if (Version.TryParse(installedVersionText, out var installedVersion) &&
            Version.TryParse(PatcherBuildInfo.VrcxPackageVersion, out var payloadVersion) && installedVersion > payloadVersion)
            throw new InvalidOperationException($"This installation is VRCX {installedVersion}, newer than the patcher's frontend ({payloadVersion}). The patch was stopped to avoid a downgrade.");
    }

    private static bool TryReleaseTime(string tag, out DateTime time)
    {
        var match = Regex.Match(tag, @"^(\d{4}-\d{2}-\d{2}T\d{2})\.(\d{2})-");
        return DateTime.TryParseExact(match.Success ? match.Groups[1].Value + ":" + match.Groups[2].Value : string.Empty,
            "yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out time);
    }

    private static async Task<byte[]> DownloadDatabaseAsync(string fileName)
    {
        using var response = await Http.GetAsync(FewTagsBaseUrl + fileName, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxDatabaseBytes)
            throw new InvalidDataException($"The latest {fileName} exceeded the {MaxDatabaseBytes / 1024 / 1024} MB limit.");
        await using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > MaxDatabaseBytes) throw new InvalidDataException($"The latest {fileName} exceeded the size limit.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0 ||
            !records.EnumerateArray().Any(record => record.TryGetProperty("UserID", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())))
            throw new InvalidDataException($"GitHub returned an invalid {fileName}; VRCX was not changed.");
        return bytes;
    }

    private static void InjectDatabase(string indexPath, byte[] v2Bytes, byte[] legacyBytes)
    {
        var html = File.ReadAllText(indexPath);
        if (html.CountSubstring(DataMarker) != 1) throw new InvalidDataException("The VRCX frontend does not contain exactly one FewTags data marker.");
        using var v2 = JsonDocument.Parse(v2Bytes);
        using var legacy = JsonDocument.Parse(legacyBytes);
        var data = JsonSerializer.Serialize(new { v2 = v2.RootElement, legacy = legacy.RootElement }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
        var script = "<script>window.__VRCX_FEWTAGS__=" + data + ";</script>";
        File.WriteAllText(indexPath, html.Replace(DataMarker, script, StringComparison.Ordinal), new UTF8Encoding(false));
    }

    private static List<PayloadFile> FindChangedFiles(string sourceRoot, string destinationRoot)
    {
        var result = new List<PayloadFile>();
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = SafeCombine(destinationRoot, relative);
            if (!File.Exists(destination) || !FilesEqual(source, destination)) result.Add(new PayloadFile(source, relative));
        }
        return result;
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An unsafe path was found in the VRCX payload.");
        return fullPath;
    }

    private static bool FilesEqual(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length) return false;
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(firstStream), SHA256.HashData(secondStream));
    }

    private static void RestoreBackup(string backupPath, string destinationRoot)
    {
        var manifestPath = Path.Combine(backupPath, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("The selected backup has no manifest and cannot be restored safely.");
        var entries = JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("The backup manifest is invalid.");
        foreach (var entry in entries)
        {
            var destination = SafeCombine(destinationRoot, entry.RelativePath);
            if (entry.HadOriginal)
            {
                var saved = SafeCombine(backupPath, entry.RelativePath);
                if (!File.Exists(saved)) throw new FileNotFoundException("A saved file is missing from the backup.", saved);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(saved, destination, overwrite: true);
            }
            else if (File.Exists(destination)) File.Delete(destination);
        }
    }

    private static async Task<bool> StopRunningVrcxAsync()
    {
        foreach (var process in Process.GetProcessesByName("VRCX"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    process.CloseMainWindow();
                    if (!await Task.Run(() => process.WaitForExit(12_000))) return false;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
        }
        return true;
    }

    private static bool IsVrcxRunning() => Process.GetProcessesByName("VRCX").Any(process => { using (process) return !process.HasExited; });

    private async Task CheckForPatcherUpdateAsync()
    {
        if (string.Equals(PatcherBuildInfo.PatcherRepository, "unset", StringComparison.Ordinal))
        {
            _status.Text = "Patcher self-updates will be enabled after its GitHub release repository is configured.";
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var response = await Http.GetAsync($"https://api.github.com/repos/{PatcherBuildInfo.PatcherRepository}/releases/latest", timeout.Token);
            if (!response.IsSuccessStatusCode) return;
            using var release = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (!SelfUpdater.IsNewerPatcherTag(tag, PatcherBuildInfo.PatcherVersion)) return;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return;
            var exe = assets.EnumerateArray().FirstOrDefault(asset => asset.GetProperty("name").GetString() == "VRCX-FewTags-Patcher.exe");
            var hash = assets.EnumerateArray().FirstOrDefault(asset => asset.GetProperty("name").GetString() == "VRCX-FewTags-Patcher.exe.sha256");
            if (exe.ValueKind != JsonValueKind.Object || hash.ValueKind != JsonValueKind.Object) return;
            if (MessageBox.Show(this, $"A newer patcher ({tag}) is available. Download and install it?", "Patcher update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

            var exeUrl = exe.GetProperty("browser_download_url").GetString() ?? string.Empty;
            var hashUrl = hash.GetProperty("browser_download_url").GetString() ?? string.Empty;
            var allowedPrefix = $"https://github.com/{PatcherBuildInfo.PatcherRepository}/releases/download/";
            if (!exeUrl.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) || !hashUrl.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release contains an unexpected patcher download URL.");

            _status.Text = "Downloading and verifying the patcher update…";
            var exeBytes = await DownloadLimitedAsync(exeUrl, MaxUpdaterBytes);
            var hashBytes = await DownloadLimitedAsync(hashUrl, 4096);
            var checksumText = Encoding.UTF8.GetString(hashBytes);
            var match = Regex.Match(checksumText, @"^(?<hash>[a-fA-F0-9]{64})\s+\*?VRCX-FewTags-Patcher\.exe\s*$");
            if (!match.Success) throw new InvalidDataException("The patcher release checksum file is malformed.");
            var expected = Convert.FromHexString(match.Groups["hash"].Value);
            var actual = SHA256.HashData(exeBytes);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("The patcher update failed its SHA-256 check.");

            var current = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the current patcher executable.");
            var staged = Path.Combine(Path.GetTempPath(), "VRCX-FewTags-Patcher-" + Guid.NewGuid().ToString("N") + ".exe");
            await File.WriteAllBytesAsync(staged, exeBytes);
            var updaterPath = Path.Combine(Path.GetTempPath(), "VRCX-FewTags-Updater-" + Guid.NewGuid().ToString("N") + ".exe");
            await File.WriteAllBytesAsync(updaterPath, SelfUpdater.CreateUpdaterStub());
            var updater = new ProcessStartInfo(updaterPath) { UseShellExecute = true, Verb = "runas" };
            updater.ArgumentList.Add("--replace-self");
            updater.ArgumentList.Add(current);
            updater.ArgumentList.Add(staged);
            updater.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(updater);
            Application.Exit();
        }
        catch (Exception ex)
        {
            _status.Text = "Patcher update check failed; you can still apply the installed version.";
            Debug.WriteLine(ex);
        }
    }

    private static async Task<byte[]> DownloadLimitedAsync(string url, int limit)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("A patcher update file exceeded the size limit.");
        await using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("A patcher update file exceeded the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task ExtractEmbeddedPayloadAsync(string destination)
    {
        await using var input = typeof(PatcherForm).Assembly.GetManifestResourceStream("VRCX-FewTags-html.zip")
            ?? throw new InvalidOperationException("The VRCX frontend payload is missing from this patcher.");
        await using var output = File.Create(destination);
        await input.CopyToAsync(output);
    }

    private void SetBusy(bool busy)
    {
        _apply.Enabled = !busy;
        _restore.Enabled = !busy;
        _browse.Enabled = !busy;
        _path.Enabled = !busy;
    }

    private sealed record PayloadFile(string SourcePath, string RelativePath);
    private sealed record BackupEntry(string RelativePath, bool HadOriginal);
}

internal static class SelfUpdater
{
    public static void ReplaceAndRestart(string currentPath, string downloadedPath, int parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            if (!parent.WaitForExit(60_000)) return;
        }
        catch (ArgumentException) { }

        var nextPath = currentPath + ".new";
        try
        {
            File.Copy(downloadedPath, nextPath, overwrite: true);
            File.Move(nextPath, currentPath, overwrite: true);
            Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = true, Verb = "runas" });
        }
        catch
        {
            try { if (File.Exists(nextPath)) File.Delete(nextPath); } catch { }
            Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = true, Verb = "runas" });
        }
        finally { try { File.Delete(downloadedPath); } catch { } }
    }

    public static bool IsNewerPatcherTag(string? candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var candidateMatch = Regex.Match(candidate, @"^vrcx-fewtags-(?<release>\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}-[0-9a-f]{7,})-(?<source>[0-9a-f]{8})$", RegexOptions.IgnoreCase);
        var currentMatch = Regex.Match(current, @"^vrcx-fewtags-(?<release>\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}-[0-9a-f]{7,})-(?<source>[0-9a-f]{8})$", RegexOptions.IgnoreCase);
        if (!candidateMatch.Success || !currentMatch.Success) return false;
        if (!TryReleaseTime(candidateMatch.Groups["release"].Value, out var candidateTime) ||
            !TryReleaseTime(currentMatch.Groups["release"].Value, out var currentTime)) return false;
        return candidateTime > currentTime || (candidateTime == currentTime &&
            !string.Equals(candidateMatch.Groups["source"].Value, currentMatch.Groups["source"].Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReleaseTime(string tag, out DateTime time)
    {
        var match = Regex.Match(tag, @"^(\d{4}-\d{2}-\d{2}T\d{2})\.(\d{2})-");
        return DateTime.TryParseExact(match.Success ? match.Groups[1].Value + ":" + match.Groups[2].Value : string.Empty,
            "yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out time);
    }

    public static byte[] CreateUpdaterStub()
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the .NET app host.");
        var bytes = File.ReadAllBytes(host);
        return bytes;
    }
}

internal static class StringExtensions
{
    public static int CountSubstring(this string value, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }
}
