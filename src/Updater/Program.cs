using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Win32;

/// Rim Factions Updater: keeps everyone testing on the same Rim Factions build, straight from the GitHub repository
/// (release/RimFactions.zip and release/version.txt on the main branch), without waiting for Melty's review.
static class Program
{
    /// owner/name of the GitHub repository. A repo.txt next to the exe overrides it.
    const string DefaultRepo = "even86868-coder/rim-factions";
    static readonly string[] BundledMods = { "RimFactions", "Multiplayer", "MultiplayerCompatibility", "Prepatcher" };
    static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimFactions", "updater-rimworld-path.txt");

    static Form form;
    static Label installedLabel, latestLabel, statusLabel, folderLabel;
    static Button updateButton, startButton;
    static string gameDir, latest;

    [STAThread]
    static void Main()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles();
        form = new Form { Text = "Rim Factions Updater", Width = 480, Height = 300, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, StartPosition = FormStartPosition.CenterScreen };
        var title = new Label { Text = "Rim Factions", Font = new Font(SystemFonts.DefaultFont.FontFamily, 16, FontStyle.Bold), Left = 16, Top = 12, AutoSize = true };
        folderLabel = new Label { Left = 16, Top = 50, Width = 440, Height = 32 };
        installedLabel = new Label { Left = 16, Top = 86, Width = 440 };
        latestLabel = new Label { Left = 16, Top = 110, Width = 440 };
        updateButton = new Button { Text = "Install / Update", Left = 16, Top = 146, Width = 200, Height = 36 };
        startButton = new Button { Text = "Start RimWorld", Left = 232, Top = 146, Width = 200, Height = 36 };
        var findButton = new LinkLabel { Text = "Wrong RimWorld folder? Pick it", Left = 16, Top = 192, AutoSize = true };
        statusLabel = new Label { Left = 16, Top = 220, Width = 440, Height = 36 };
        form.Controls.AddRange(new Control[] { title, folderLabel, installedLabel, latestLabel, updateButton, startButton, findButton, statusLabel });

        updateButton.Click += async (s, e) => await Update();
        startButton.Click += (s, e) => { Process.Start("steam://rungameid/294100"); statusLabel.Text = "Starting RimWorld through Steam..."; };
        findButton.LinkClicked += (s, e) => PickFolder();
        form.Shown += async (s, e) => { gameDir = FindRimWorld(); await Refresh(); };
        Application.Run(form);
    }

    static string Repo
    {
        get
        {
            var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repo.txt");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : DefaultRepo;
        }
    }

    static string Raw(string path) => $"https://raw.githubusercontent.com/{Repo}/main/{path}";

    static async Task Refresh()
    {
        folderLabel.Text = gameDir != null ? "RimWorld: " + gameDir : "RimWorld not found. Click \"Pick it\" below.";
        installedLabel.Text = "Installed: " + (InstalledVersion() ?? "not installed");
        latestLabel.Text = "Newest on GitHub: checking...";
        try
        {
            using (var wc = new WebClient())
                latest = (await wc.DownloadStringTaskAsync(Raw("release/version.txt") + "?t=" + DateTime.UtcNow.Ticks)).Trim();
            latestLabel.Text = "Newest on GitHub: " + latest;
            var inst = InstalledVersion();
            statusLabel.Text = inst == latest ? "You're up to date." : "An update is ready: press Install / Update.";
        }
        catch (Exception e) { latestLabel.Text = "Newest on GitHub: couldn't check (" + e.Message + ")"; }
        updateButton.Enabled = gameDir != null;
    }

    static string InstalledVersion()
    {
        if (gameDir == null) return null;
        var about = Path.Combine(gameDir, "Mods", "RimFactions", "About", "About.xml");
        try { return File.Exists(about) ? (string)XElement.Load(about).Element("modVersion") : null; } catch { return null; }
    }

    static async Task Update()
    {
        if (Process.GetProcessesByName("RimWorldWin64").Any())
        {
            MessageBox.Show(form, "Close RimWorld first, then press Install / Update again.", "Rim Factions Updater");
            return;
        }
        updateButton.Enabled = false;
        var tmp = Path.Combine(Path.GetTempPath(), "RimFactionsUpdate");
        try
        {
            statusLabel.Text = "Downloading...";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var zip = Path.Combine(tmp, "RimFactions.zip");
            using (var wc = new WebClient())
            {
                wc.DownloadProgressChanged += (s, e) => statusLabel.Text = $"Downloading... {e.ProgressPercentage}%";
                await wc.DownloadFileTaskAsync(Raw("release/RimFactions.zip") + "?t=" + DateTime.UtcNow.Ticks, zip);
            }
            statusLabel.Text = "Installing...";
            var unpacked = Path.Combine(tmp, "x");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked));
            var mods = Path.Combine(gameDir, "Mods");
            Directory.CreateDirectory(mods);
            foreach (var name in BundledMods)
            {
                var from = Path.Combine(unpacked, "Mods", name);
                if (!Directory.Exists(from)) continue;
                var to = Path.Combine(mods, name);
                // a Workshop ID the creator's own copy carries (RimWorld's uploader writes it) survives the update
                var idFile = Path.Combine(to, "About", "PublishedFileId.txt");
                var keepId = File.Exists(idFile) ? File.ReadAllText(idFile) : null;
                if (Directory.Exists(to)) Directory.Delete(to, true);
                CopyDir(from, to);
                if (keepId != null) { Directory.CreateDirectory(Path.GetDirectoryName(idFile)); File.WriteAllText(idFile, keepId); }
            }
            statusLabel.Text = "Installed " + (InstalledVersion() ?? "?") + ". Start RimWorld from Steam or with the button.";
        }
        catch (Exception e) { statusLabel.Text = "Update failed: " + e.Message; }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
            await Refresh();
        }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// RimWorld's folder: the one picked before, else every Steam library's steamapps/common/RimWorld.
    static string FindRimWorld()
    {
        try { if (File.Exists(StatePath) && Directory.Exists(File.ReadAllText(StatePath).Trim())) return File.ReadAllText(StatePath).Trim(); } catch { }
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam == null) return null;
        var libs = new[] { steam.Replace('/', '\\') }.ToList();
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        return libs.Select(l => Path.Combine(l, "steamapps", "common", "RimWorld"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "RimWorldWin64.exe")));
    }

    static void PickFolder()
    {
        using (var dlg = new FolderBrowserDialog { Description = "Pick RimWorld's folder (the one with RimWorldWin64.exe)" })
        {
            if (dlg.ShowDialog(form) != DialogResult.OK) return;
            if (!File.Exists(Path.Combine(dlg.SelectedPath, "RimWorldWin64.exe"))) { MessageBox.Show(form, "That folder doesn't have RimWorldWin64.exe in it.", "Rim Factions Updater"); return; }
            gameDir = dlg.SelectedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, gameDir);
            _ = Refresh();
        }
    }
}
