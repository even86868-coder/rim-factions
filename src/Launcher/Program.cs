using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

// system:Launcher — keeps the player's own mod list, adds the Rim Factions stack in load order, starts RimWorld.
//   RimFactionsLauncher.exe --game <RimWorld folder> [--host | --join <address>] [--savedatafolder <dir>] [--username <name>] [--no-wait]
// <address> is what the planet host logged: <SteamID64>.steamlobby, steam:<SteamID64>, tcp:<ip>:<port>, a bare SteamID64 or ip[:port].
static class Program
{
    const string Prepatcher = "zetrith.prepatcher";
    const string Multiplayer = "rwmt.multiplayer";
    const string MpCompat = "rwmt.multiplayercompatibility";
    const string RimFactions = "melty.rimfactions";
    static readonly string[] Ours = { Prepatcher, Multiplayer, MpCompat, RimFactions };

    static string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimFactions");
    static string logDir = Path.Combine(dataDir, "logs");

    static int Main(string[] args)
    {
        Directory.CreateDirectory(logDir);
        try
        {
            string game = Arg(args, "--game"), join = Arg(args, "--join"), saveData = Arg(args, "--savedatafolder");
            bool wait = !args.Contains("--no-wait");
            if (game == null || !File.Exists(Path.Combine(game, "RimWorldWin64.exe")))
                throw new Exception("RimWorld folder not found: " + game);

            // The mod writes "Hosting at ..." here; start each session with an empty log so stale addresses never show.
            File.WriteAllText(Path.Combine(logDir, "latest.log"), "");
            // Hosting: the planet's address is this Steam account, known before the game even loads, so joiners can
            // start connecting at once (the mod writes it again once it is hosting, and retries until it is up).
            bool hosting = string.IsNullOrWhiteSpace(join) && args.Contains("--host");
            if (hosting && SteamAccount() is ulong me)
                File.AppendAllText(Path.Combine(logDir, "latest.log"), $"Hosting at {me}.steamlobby{Environment.NewLine}");

            string configDir = saveData != null
                ? Path.Combine(saveData, "Config")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config");
            EnsureModList(configDir, game);

            var gameArgs = new List<string>();
            if (saveData != null) gameArgs.Add($"\"-savedatafolder={saveData}\"");
            // Joining connects to the friend's planet link; playing without a join link hosts one.
            if (!string.IsNullOrWhiteSpace(join)) gameArgs.Add($"\"-rimfactions-join={NormalizeAddress(join.Trim())}\"");
            else if (args.Contains("--host")) gameArgs.Add("-rimfactions-host");
            if (Arg(args, "--username") is string user) gameArgs.Add($"\"-username={user}\"");
            if (Arg(args, "--extra") is string extra) gameArgs.Add(extra);

            var exe = Path.Combine(game, "RimWorldWin64.exe");
            Info($"launch {exe} {string.Join(" ", gameArgs)}");
            var p = Process.Start(new ProcessStartInfo(exe, string.Join(" ", gameArgs)) { WorkingDirectory = game, UseShellExecute = false });
            if (!wait) return 0;

            // Prepatcher and Multiplayer's mod sync may restart the game, so wait until no copy from this folder is left.
            p.WaitForExit();
            Thread.Sleep(5000);
            while (RunningFrom(game)) Thread.Sleep(3000);
            Info("game exited");
            return 0;
        }
        catch (Exception e)
        {
            Info("ERROR " + e);
            return 1;
        }
    }

    /// The Steam account signed in to the running Steam client, as a SteamID64; null when Steam isn't running.
    static ulong? SteamAccount()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                var v = key?.GetValue("ActiveUser");
                long account = v is int i ? (uint)i : v is long l ? l : 0;
                if (account <= 0) return null;
                return 76561197960265728UL + (ulong)account;
            }
        }
        catch { return null; }
    }

    static string NormalizeAddress(string a)
    {
        if (a.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) || a.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) return a;
        if (a.EndsWith(".steamlobby", StringComparison.OrdinalIgnoreCase)) return a.ToLowerInvariant();
        if (Regex.IsMatch(a, @"^\d{15,20}$")) return "steam:" + a;
        return "tcp:" + a;
    }

    /// Adds Prepatcher (before Core), then Multiplayer, Multiplayer Compatibility and Rim Factions right after
    /// Core and the expansions. Every other mod the player has active stays where it is.
    static void EnsureModList(string configDir, string game)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "ModsConfig.xml");
        XDocument doc;
        if (File.Exists(path))
        {
            var backup = Path.Combine(configDir, "ModsConfig.before-RimFactions.xml");
            if (!File.Exists(backup)) File.Copy(path, backup);
            doc = XDocument.Load(path);
        }
        else
        {
            // No config yet: the game fills in owned expansions itself on first start.
            doc = new XDocument(new XElement("ModsConfigData",
                new XElement("version", ReadGameVersion(game)),
                new XElement("activeMods", new XElement("li", "ludeon.rimworld")),
                new XElement("knownExpansions")));
        }

        var active = doc.Root.Element("activeMods") ?? new XElement("activeMods");
        if (active.Parent == null) doc.Root.Add(active);
        var ids = active.Elements("li").Select(e => e.Value.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
        ids.RemoveAll(id => Ours.Contains(id) || Ours.Any(o => id == o + "_steam"));
        if (!ids.Contains("ludeon.rimworld")) ids.Insert(0, "ludeon.rimworld");

        // Owned expansions that are on: keep them before Multiplayer.
        int afterOfficial = ids.FindLastIndex(id => id.StartsWith("ludeon.rimworld")) + 1;
        ids.InsertRange(afterOfficial, new[] { Multiplayer, MpCompat, RimFactions });
        ids.Insert(0, Prepatcher);

        active.RemoveNodes();
        foreach (var id in ids) active.Add(new XElement("li", id));
        doc.Save(path);
        Info("active mods: " + string.Join(", ", ids));
    }

    static string ReadGameVersion(string game)
    {
        try
        {
            var v = File.ReadAllText(Path.Combine(game, "Version.txt")).Trim();
            return v.Split(' ')[0];
        }
        catch { return "1.6"; }
    }

    static bool RunningFrom(string game)
    {
        string exe = Path.GetFullPath(Path.Combine(game, "RimWorldWin64.exe"));
        foreach (var p in Process.GetProcessesByName("RimWorldWin64"))
        {
            try { if (string.Equals(Path.GetFullPath(p.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { return true; }
        }
        return false;
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void Info(string line)
    {
        try { File.AppendAllText(Path.Combine(logDir, "launcher.log"), $"{DateTime.Now:s} {line}{Environment.NewLine}"); } catch { }
    }
}
