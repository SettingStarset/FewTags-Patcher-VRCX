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
    private const string FewTagsBaseUrl = "https://raw.githubusercontent.com/Fewdys/FewTags/main/";
    private const string DataMarker = "<!-- FEWTAGS_DATA_PLACEHOLDER -->";
    private const int MaxDatabaseBytes = 32 * 1024 * 1024;
    private const int MaxUpdaterBytes = 128 * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly PayloadVariant[] PayloadVariants =
    {
        new("Nightly", PatcherBuildInfo.VrcxNightlyReleaseTag, PatcherBuildInfo.VrcxNightlyPackageVersion,
            PatcherBuildInfo.VrcxNightlyExeSha256, PatcherBuildInfo.VrcxNightlyDllSha256, "VRCX-FewTags-html-nightly.zip"),
        new("Stable", PatcherBuildInfo.VrcxStableReleaseTag, PatcherBuildInfo.VrcxStablePackageVersion,
            PatcherBuildInfo.VrcxStableExeSha256, PatcherBuildInfo.VrcxStableDllSha256, "VRCX-FewTags-html-stable.zip")
    };

    private static readonly Color Ink = Color.FromArgb(29, 39, 55);
    private static readonly Color Muted = Color.FromArgb(103, 116, 133);
    private static readonly Color Accent = Color.FromArgb(78, 111, 230);
    private static readonly Color Surface = Color.White;
    private static readonly Color Canvas = Color.FromArgb(244, 247, 251);
    private readonly TextBox _path = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "VRCX install folder" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink };
    private readonly Label _compatibility = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 4, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24, Visible = false };
    private readonly Button _apply = new() { Text = "Apply / Update", AutoSize = true, MinimumSize = new Size(150, 40), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, UseVisualStyleBackColor = false, AccessibleName = "Apply or update FewTags" };
    private readonly Button _restore = new() { Text = "Restore backup", AutoSize = true, MinimumSize = new Size(140, 40), FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, AccessibleName = "Restore the latest VRCX backup" };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true, MinimumSize = new Size(98, 34), FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink };
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 12000, InitialDelay = 500, ReshowDelay = 100 };
    private readonly System.Windows.Forms.Timer _pathCheckTimer = new() { Interval = 450 };

    public PatcherForm()
    {
        Text = "VRCX FewTags Patcher";
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(660, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        BackColor = Canvas;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        AcceptButton = _apply;
        CancelButton = _restore;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(22), BackColor = Canvas };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        var hero = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Padding = new Padding(22, 13, 22, 12) };
        var heroLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Ink };
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        var title = new Label { Text = "FewTags for VRCX", Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };
        var subtitle = new Label { Text = "Patch your existing VRCX install · Supports stable and nightly builds", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(206, 216, 232), Font = new Font("Segoe UI", 9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        heroLayout.Controls.Add(title, 0, 0);
        heroLayout.Controls.Add(subtitle, 0, 1);
        hero.Controls.Add(heroLayout);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 8, 0, 8), BackColor = Canvas };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var pathLabel = new Label { Text = "VRCX INSTALL FOLDER", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Canvas };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        _browse.Click += (_, _) => BrowseForInstall();
        _browse.Margin = new Padding(10, 2, 0, 2);
        pathRow.Controls.Add(_path, 0, 0);
        pathRow.Controls.Add(_browse, 1, 0);

        var versions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Canvas, Padding = new Padding(0, 12, 0, 8) };
        versions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        versions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        versions.Controls.Add(CreateVersionCard("STABLE", "vrcx-team/VRCX", PatcherBuildInfo.VrcxStableReleaseTag, Color.FromArgb(39, 153, 119)), 0, 0);
        versions.Controls.Add(CreateVersionCard("NIGHTLY", "Natsumi-sama/VRCX", PatcherBuildInfo.VrcxNightlyReleaseTag, Accent), 1, 0);

        var statusCard = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(14, 8, 14, 8) };
        _status.Text = "Choose your VRCX folder, then apply the FewTags integration.";
        _compatibility.Text = "Only the exact supported stable and nightly builds will be changed.";
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Surface };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        statusLayout.Controls.Add(_status, 0, 0);
        statusLayout.Controls.Add(_compatibility, 0, 1);
        statusCard.Controls.Add(statusLayout);
        statusCard.Controls.Add(_progress);

        content.Controls.Add(pathLabel, 0, 0);
        content.Controls.Add(pathRow, 0, 1);
        content.Controls.Add(versions, 0, 2);
        content.Controls.Add(statusCard, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Canvas, Padding = new Padding(0, 10, 0, 0) };
        _apply.Click += async (_, _) => await ApplyAsync();
        _restore.Click += (_, _) => RestoreLatestBackup();
        _apply.FlatAppearance.BorderSize = 0;
        _apply.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 94, 208);
        _restore.FlatAppearance.BorderColor = Color.FromArgb(218, 225, 235);
        _toolTip.SetToolTip(_path, "Select the folder containing VRCX.exe and the html folder.");
        _toolTip.SetToolTip(_restore, "Restore the most recent frontend backup from this install folder.");
        actions.Controls.Add(_restore);
        actions.Controls.Add(_apply);

        shell.Controls.Add(hero, 0, 0);
        shell.Controls.Add(content, 0, 1);
        shell.Controls.Add(actions, 0, 2);
        Controls.Add(shell);

        _path.Text = FindInstall() ?? @"C:\Program Files\VRCX";
        _pathCheckTimer.Tick += (_, _) =>
        {
            _pathCheckTimer.Stop();
            RefreshCompatibility();
        };
        _path.TextChanged += (_, _) =>
        {
            _pathCheckTimer.Stop();
            _pathCheckTimer.Start();
        };
        _path.Validated += (_, _) => RefreshCompatibility();
        _path.AccessibleDescription = "Folder containing VRCX.exe, VRCX.dll, and the html subfolder.";
        _apply.TabIndex = 3;
        _restore.TabIndex = 4;
        _browse.TabIndex = 2;
        _path.TabIndex = 1;
        RefreshCompatibility();
        Shown += async (_, _) => await CheckForPatcherUpdateAsync();
    }

    private static Control CreateVersionCard(string channel, string repository, string releaseTag, Color accent)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(12, 6, 8, 6), MinimumSize = new Size(0, 84) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 53));
        var stripe = new Panel { Dock = DockStyle.Fill, BackColor = accent, Margin = new Padding(0, 1, 0, 1) };
        var name = new Label { Text = $"{channel}  ·  {repository}", Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
        var version = new Label { Text = releaseTag, Dock = DockStyle.Fill, ForeColor = Ink, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
        layout.Controls.Add(stripe, 0, 0);
        layout.SetRowSpan(stripe, 2);
        layout.Controls.Add(name, 1, 0);
        layout.Controls.Add(version, 1, 1);
        card.Controls.Add(layout);
        return card;
    }

    private void RefreshCompatibility()
    {
        if (!IsHandleCreated) return;
        try
        {
            var target = Path.GetFullPath(_path.Text.Trim().Trim('"'));
            if (!File.Exists(Path.Combine(target, "VRCX.exe")) || !File.Exists(Path.Combine(target, "VRCX.dll")))
            {
                _compatibility.Text = "Select a folder containing VRCX.exe and VRCX.dll to check build compatibility.";
                _compatibility.ForeColor = Muted;
                return;
            }
            var exeHash = HashFile(Path.Combine(target, "VRCX.exe"));
            var dllHash = HashFile(Path.Combine(target, "VRCX.dll"));
            var match = PayloadVariants.FirstOrDefault(variant => string.Equals(variant.ExeSha256, exeHash, StringComparison.OrdinalIgnoreCase) && string.Equals(variant.DllSha256, dllHash, StringComparison.OrdinalIgnoreCase));
            _compatibility.Text = match is null ? "Build not recognized · Apply will leave this VRCX installation unchanged." : $"{match.Channel} build recognized · {match.ReleaseTag}";
            _compatibility.ForeColor = match is null ? Color.FromArgb(180, 74, 70) : Color.FromArgb(39, 137, 105);
        }
        catch
        {
            _compatibility.Text = "Select a valid VRCX install folder to check compatibility.";
            _compatibility.ForeColor = Muted;
        }
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
            _status.Text = "Matching the installed VRCX executable to a compatible frontend…";
            _compatibility.Text = "Checking exact VRCX build fingerprints…";
            var variant = ResolveCompatiblePayload(target);
            _compatibility.Text = $"{variant.Channel} build recognized · {variant.ReleaseTag}";
            _compatibility.ForeColor = Color.FromArgb(39, 137, 105);

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
            await ExtractEmbeddedPayloadAsync(archivePath, variant.ResourceName);
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
                _compatibility.Text = $"{variant.Channel} build · no frontend changes needed.";
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

            _status.Text = $"FewTags applied to the {variant.Channel} build. Updated {changedFiles.Count} files; backup saved to {Path.GetFileName(backupPath)}.";
            _compatibility.Text = $"{variant.Channel} build patched · {variant.ReleaseTag} · restart VRCX to see your tags.";
            _toolTip.SetToolTip(_status, $"Backup saved to {backupPath}");
        }
        catch (Exception ex)
        {
            _status.Text = "The patch could not be completed.";
            _compatibility.Text = ex.Message;
            _compatibility.ForeColor = Color.FromArgb(180, 74, 70);
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
            ? Directory.EnumerateDirectories(target)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    var recognized = name.StartsWith("FewTagsVRCXExeBackup-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("FewTagsVRCXBackup-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("FewTagsVRCXScriptBackup-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("FewTagsPatchBackup-", StringComparison.OrdinalIgnoreCase);
                    return recognized && (File.Exists(Path.Combine(path, "manifest.json")) ||
                        File.Exists(Path.Combine(path, "html", "index.html")));
                })
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (backup is null)
        {
            MessageBox.Show(this, "No VRCX FewTags backup was found in that folder.", "Backup not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            if (File.Exists(Path.Combine(backup, "manifest.json")))
                RestoreBackup(backup, Path.Combine(target, "html"));
            else
                RestoreWholeHtmlBackup(backup, target);
            _status.Text = $"Restored {Path.GetFileName(backup)}. Relaunch VRCX.";
            _compatibility.Text = $"Restored frontend backup · {Path.GetFileName(backup)}";
            _compatibility.ForeColor = Color.FromArgb(39, 137, 105);
            _toolTip.SetToolTip(_status, backup);
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

    private static PayloadVariant ResolveCompatiblePayload(string target)
    {
        var exePath = Path.Combine(target, "VRCX.exe");
        var dllPath = Path.Combine(target, "VRCX.dll");
        if (!File.Exists(exePath) || !File.Exists(dllPath))
            throw new InvalidDataException("This VRCX install is missing VRCX.exe or VRCX.dll, so its frontend build cannot be matched safely.");

        var exeHash = HashFile(exePath);
        var dllHash = HashFile(dllPath);
        var matches = PayloadVariants.Where(variant =>
            string.Equals(variant.ExeSha256, exeHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(variant.DllSha256, dllHash, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) return matches[0];

        var supported = string.Join("\n", PayloadVariants.Select(variant =>
            $"• {variant.Channel}: {variant.ReleaseTag} (VRCX package {variant.PackageVersion})"));
        throw new InvalidOperationException(
            $"This VRCX executable does not match either bundled frontend. No files were changed.\n\n" +
            $"Detected VRCX.exe SHA-256: {exeHash}\nDetected VRCX.dll SHA-256: {dllHash}\n\n" +
            $"This patcher supports these exact builds:\n{supported}\n\nUpdate VRCX to one of those releases, then apply again.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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

    private static void RestoreWholeHtmlBackup(string backupPath, string installRoot)
    {
        var savedHtml = Path.Combine(backupPath, "html");
        var destination = Path.Combine(installRoot, "html");
        if (!File.Exists(Path.Combine(savedHtml, "index.html")))
            throw new InvalidDataException("The selected full frontend backup is incomplete.");

        var stage = Path.Combine(installRoot, "FewTagsVRCXRestoreStage-" + Guid.NewGuid().ToString("N"));
        var displaced = Path.Combine(installRoot, "FewTagsVRCXBeforeRestore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var source in Directory.EnumerateFiles(savedHtml, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(savedHtml, source);
                var staged = SafeCombine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(source, staged, overwrite: true);
            }
            if (!File.Exists(Path.Combine(stage, "index.html")))
                throw new InvalidDataException("The staged frontend backup is incomplete.");
            Directory.Move(destination, displaced);
            try
            {
                Directory.Move(stage, destination);
            }
            catch
            {
                if (Directory.Exists(destination))
                    Directory.Move(destination, Path.Combine(installRoot, "FewTagsVRCXRestoreFailed-" + Guid.NewGuid().ToString("N")));
                Directory.Move(displaced, destination);
                throw;
            }
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
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

    private static async Task ExtractEmbeddedPayloadAsync(string destination, string resourceName)
    {
        await using var input = typeof(PatcherForm).Assembly.GetManifestResourceStream(resourceName)
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
        _progress.Visible = busy;
        UseWaitCursor = busy;
    }

    private sealed record PayloadVariant(string Channel, string ReleaseTag, string PackageVersion, string ExeSha256, string DllSha256, string ResourceName);
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
        if (!TryParsePatcherTag(candidate, out var candidateNightly, out var candidateStable, out var candidateSource) ||
            !TryParsePatcherTag(current, out var currentNightly, out var currentStable, out var currentSource)) return false;
        if (candidateNightly != currentNightly) return candidateNightly > currentNightly;
        if (candidateStable != currentStable) return candidateStable > currentStable;
        return !string.Equals(candidateSource, currentSource, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParsePatcherTag(string value, out DateTime nightly, out DateTime stable, out string source)
    {
        nightly = default;
        stable = DateTime.MinValue;
        source = string.Empty;
        var currentFormat = Regex.Match(value,
            @"^vrcx-fewtags-(?<nightly>\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}-[0-9a-f]{7,})-(?<stable>v?\d{4}\.\d{2}\.\d{2})-(?<source>[0-9a-f]{8})$",
            RegexOptions.IgnoreCase);
        var previousFormat = Regex.Match(value,
            @"^vrcx-fewtags-(?<nightly>\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}-[0-9a-f]{7,})-(?<source>[0-9a-f]{8})$",
            RegexOptions.IgnoreCase);
        var match = currentFormat.Success ? currentFormat : previousFormat;
        if (!match.Success || !TryReleaseTime(match.Groups["nightly"].Value, out nightly)) return false;
        if (currentFormat.Success && !DateTime.TryParseExact(currentFormat.Groups["stable"].Value.TrimStart('v', 'V'),
                "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out stable)) return false;
        source = match.Groups["source"].Value;
        return true;
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
