using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using RimFactions.Net;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// Tags a game as shared: a stable id saved with it, so every player's copy can be matched up.
    public class SharedSaveComp : GameComponent
    {
        public string gameId;
        public bool shared;

        public SharedSaveComp(Game game) { gameId = SharedSaves.NewId(); }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref gameId, "rfSharedId");
            Scribe_Values.Look(ref shared, "rfShared");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && gameId.NullOrEmpty()) gameId = SharedSaves.NewId();
        }
    }

    /// One saved copy of a shared game: who saved it, how far in, and the saves it grew from.
    public class SaveManifest
    {
        public string gameId, name, id, by;
        public long ticks, utc, size;              // utc: DateTime.UtcNow.Ticks
        public List<string> history = new List<string>();   // ids this copy descends from, newest first
        public List<string> players = new List<string>();   // Rim Factions ids of the people playing it

        public bool NewerThan(SaveManifest o) => o == null || (ticks != o.ticks ? ticks > o.ticks : utc > o.utc);
        /// Same line of play: one copy was saved from (a descendant of) the other.
        public bool Related(SaveManifest o) => o == null || id == o.id || history.Contains(o.id) || o.history.Contains(id);

        public IEnumerable<XElement> Fields() => new[]
        {
            new XElement("gameId", gameId), new XElement("name", name), new XElement("id", id), new XElement("by", by),
            new XElement("ticks", ticks), new XElement("utc", utc), new XElement("size", size),
            new XElement("h", string.Join(",", history)), new XElement("players", string.Join(",", players)),
        };

        public XElement ToXml() => new XElement("save", Fields());

        public static SaveManifest From(Func<string, string> get)
        {
            var m = new SaveManifest
            {
                gameId = get("gameId"), name = SharedSaves.SafeName(get("name")), id = get("id"), by = get("by") ?? "?",
                ticks = long.TryParse(get("ticks"), out var t) ? t : 0, utc = long.TryParse(get("utc"), out var u) ? u : 0,
                size = long.TryParse(get("size"), out var s) ? s : 0,
                history = Split(get("h")), players = Split(get("players")),
            };
            return SharedSaves.SafeId(m.gameId) && SharedSaves.SafeId(m.id) && !m.name.NullOrEmpty() ? m : null;
        }

        public static SaveManifest Load(string file)
        {
            try { if (!File.Exists(file)) return null; var x = XElement.Load(file); return From(n => (string)x.Element(n)); }
            catch (Exception e) { Log.Warning($"[Rim Factions] bad shared save manifest {file}: {e.Message}"); return null; }
        }

        public void Save(string file)
        {
            var tmp = file + ".tmp";
            ToXml().Save(tmp);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        static List<string> Split(string s) => (s ?? "").Split(',').Where(SharedSaves.SafeId).ToList();
    }

    /// feature:SharedSaves — a together game stays caught up between players. Each save of a shared game
    /// goes to every online friend over the link (chunked) and to a cloud folder all players share; on load
    /// the newest copy wins (in-game ticks, then real time). Copies played apart are kept, not overwritten.
    public static class SharedSaves
    {
        const int ChunkChars = 240_000;      // keeps each link message well under Steam's ~1 MB packet limit
        const int MaxChunks = 4000;
        const int MaxHistory = 300;

        public static string StatusKey = "RF_SharedStatusIdle";
        public static string[] StatusArgs = new string[0];
        /// Games where two players played separately: gameId -> the other copy's save name. UI can warn.
        public static readonly Dictionary<string, string> Conflicts = new Dictionary<string, string>();

        static readonly ConcurrentQueue<Msg> outgoing = new ConcurrentQueue<Msg>();
        static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        static readonly Dictionary<string, Transfer> transfers = new Dictionary<string, Transfer>();
        static readonly HashSet<string> wanted = new HashSet<string>();
        static readonly object fileLock = new object();

        class Transfer { public SaveManifest m; public string[] parts; public float started; }

        public static string Dir
        {
            get
            {
                // beside this RimWorld profile's saves, so each profile keeps its own record of what it has
                var d = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimFactionsShared");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string CloudDir(string gameId)
        {
            var c = Rules.Local?.cloudFolder;
            if (c.NullOrEmpty() || !Directory.Exists(c)) return null;
            return Path.Combine(c, "RimFactions", gameId);
        }

        static string ManifestFile(string gameId) => Path.Combine(Dir, gameId + ".xml");
        public static SaveManifest Local(string gameId) { lock (fileLock) return SaveManifest.Load(ManifestFile(gameId)); }
        static IEnumerable<SaveManifest> AllLocal() => Directory.GetFiles(Dir, "*.xml").Select(SaveManifest.Load).Where(m => m != null);

        internal static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);
        internal static bool SafeId(string s) => !s.NullOrEmpty() && s.Length <= 40 && s.All(char.IsLetterOrDigit);
        internal static string SafeName(string s)
        {
            if (s.NullOrEmpty()) return null;
            var bad = Path.GetInvalidFileNameChars();
            s = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
            return s.Length == 0 ? null : s.Length > 80 ? s.Substring(0, 80) : s;
        }

        static void SetStatus(string key, params string[] args) { StatusKey = key; StatusArgs = args; }

        public static void Init()
        {
            Link.On("SaveChunk", OnChunk);
            Link.On("SaveHave", OnHave);
            Link.On("SaveWant", OnWant);
            var go = new GameObject("RimFactions.SharedSaves");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Pump>();
        }

        // ---- tagging ----

        public static string GameIdOf(Game game) => game?.GetComponent<SharedSaveComp>()?.gameId;

        /// Marks a game as shared (together games are detected on their own).
        public static void MarkShared(Game game) { var c = game?.GetComponent<SharedSaveComp>(); if (c != null) c.shared = true; }

        public static bool IsShared(Game game)
        {
            if (game == null) return false;
            if (game.GetComponent<SharedSaveComp>()?.shared == true) return true;
            var rf = game.GetComponent<RFGameComp>();
            return rf != null && (rf.togetherOpen || rf.owners.Distinct().Count() > 1);
        }

        /// The shared game id a save on disk belongs to, if this game knows it (for picking the newest on load).
        public static string GameIdOfSave(string saveName)
        {
            lock (fileLock) return AllLocal().FirstOrDefault(m => m.name == saveName)?.gameId;
        }

        // ---- saving ----

        public static void OnSaved(string saveName)
        {
            var game = Current.Game;
            if (game == null || !IsShared(game) || saveName.NullOrEmpty()) return;
            if (MpBridge.InSession && !MpBridge.Hosting) return;   // in a live session the host's save speaks for everyone
            var gameId = GameIdOf(game);
            if (!SafeId(gameId)) return;
            var prev = Local(gameId);
            var rf = game.GetComponent<RFGameComp>();
            var m = new SaveManifest
            {
                gameId = gameId, name = saveName, id = NewId(), by = Link.MyName ?? "?",
                ticks = Find.TickManager.TicksAbs, utc = DateTime.UtcNow.Ticks,
                players = PlayersOf(rf),
            };
            if (prev != null) m.history = new[] { prev.id }.Concat(prev.history).Distinct().Take(MaxHistory).ToList();
            var file = GenFilePaths.FilePathForSavedGame(saveName);
            var cloud = CloudDir(gameId);
            var targets = Targets(m);
            Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    m.size = bytes.Length;
                    lock (fileLock) m.Save(ManifestFile(gameId));
                    if (cloud != null)
                    {
                        try
                        {
                            Directory.CreateDirectory(cloud);
                            File.Copy(file, Path.Combine(cloud, "save.rws"), true);
                            m.Save(Path.Combine(cloud, "manifest.xml"));   // written last: a manifest means the copy is complete
                            mainThread.Enqueue(() => SetStatus("RF_SharedStatusCloud", saveName));
                        }
                        catch (Exception e) { mainThread.Enqueue(() => SetStatus("RF_SharedStatusFailed", e.Message)); }
                    }
                    if (targets.Count > 0) { Queue(m, bytes, targets); mainThread.Enqueue(() => SetStatus("RF_SharedStatusSent", saveName)); }
                }
                catch (Exception e)
                {
                    Log.Warning("[Rim Factions] sharing save failed: " + e);
                    mainThread.Enqueue(() => SetStatus("RF_SharedStatusFailed", e.Message));
                }
            });
        }

        /// Link ids of everyone playing this game: pawn owners are Multiplayer names, so they're matched to the
        /// roster by player name; plus everyone in the lobby that started it.
        static List<string> PlayersOf(RFGameComp rf)
        {
            var names = new HashSet<string>(rf?.owners ?? new List<string>());
            var ids = Roster.Players.Where(f => names.Contains(f.player)).Select(f => f.id).ToList();
            if (Lobby.State != null) ids.AddRange(Lobby.State.Elements("m").Select(e => (string)e.Attribute("id")));
            ids.Add(Link.MyId);
            return ids.Where(SafeId).Distinct().ToList();
        }

        /// Online friends playing this game; everyone ("*") when we don't know who plays it.
        static List<string> Targets(SaveManifest m)
        {
            if (!Link.Connected) return new List<string>();
            var others = m.players.Where(p => p != Link.MyId).ToList();
            if (others.Count == 0) return new List<string> { "*" };
            return Roster.Others.Where(f => f.online && others.Contains(f.id)).Select(f => f.id).ToList();
        }

        static void Queue(SaveManifest m, byte[] raw, List<string> to)
        {
            string data;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress)) gz.Write(raw, 0, raw.Length);
                data = Convert.ToBase64String(ms.ToArray());
            }
            int count = (data.Length + ChunkChars - 1) / ChunkChars;
            if (count > MaxChunks) { Log.Warning($"[Rim Factions] save {m.name} too big to share over the link"); return; }
            foreach (var t in to)
                for (int i = 0; i < count; i++)
                    outgoing.Enqueue(new Msg("SaveChunk", t, m.Fields().Cast<object>().Concat(new object[]
                    {
                        new XElement("index", i), new XElement("count", count),
                        new XElement("data", data.Substring(i * ChunkChars, Math.Min(ChunkChars, data.Length - i * ChunkChars))),
                    }).ToArray()));
        }

        // ---- receiving ----

        /// Whether a copy someone offers is worth having here.
        static bool Wants(SaveManifest r)
        {
            if (r == null) return false;
            var l = Local(r.gameId);
            if (l == null) return r.players.Count == 0 || r.players.Contains(Link.MyId);
            return r.id != l.id && r.NewerThan(l);   // copies played apart are wanted too, and kept beside ours
        }

        static void OnChunk(Msg msg)
        {
            if (msg.From == Link.MyId) return;
            var m = SaveManifest.From(msg.Get);
            int index = msg.GetInt("index", -1), count = msg.GetInt("count", 0);
            var data = msg.Get("data");
            if (m == null || count <= 0 || count > MaxChunks || index < 0 || index >= count || data.NullOrEmpty()) return;

            foreach (var stale in transfers.Where(kv => Time.realtimeSinceStartup - kv.Value.started > 600f).Select(kv => kv.Key).ToList())
                transfers.Remove(stale);
            var key = msg.From + "/" + m.id;
            if (!transfers.TryGetValue(key, out var t))
            {
                if (!Wants(m)) return;
                transfers[key] = t = new Transfer { m = m, parts = new string[count], started = Time.realtimeSinceStartup };
            }
            if (t.parts.Length != count) { transfers.Remove(key); return; }
            t.parts[index] = data;
            if (t.parts.Any(p => p == null)) return;
            transfers.Remove(key);
            wanted.Remove(m.gameId + "/" + m.id);
            var joined = string.Concat(t.parts);
            Task.Run(() => Store(m, joined));
        }

        static void Store(SaveManifest m, string base64)
        {
            try
            {
                byte[] raw;
                using (var ms = new MemoryStream(Convert.FromBase64String(base64)))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var outMs = new MemoryStream()) { gz.CopyTo(outMs); raw = outMs.ToArray(); }
                if (m.size > 0 && raw.Length != m.size) { Log.Warning($"[Rim Factions] shared save {m.name} arrived damaged; ignored"); return; }
                lock (fileLock)
                {
                    var l = SaveManifest.Load(ManifestFile(m.gameId));
                    if (l != null && (l.id == m.id || !m.NewerThan(l))) return;   // never replace a newer copy with an older one
                    if (l != null && !m.Related(l))
                    {
                        var other = SafeName($"{m.name} ({m.by})");
                        WriteSave(other, raw);
                        Log.Warning($"[Rim Factions] shared game {m.gameId}: {m.by} played separately; kept their copy as \"{other}\"");
                        mainThread.Enqueue(() => { Conflicts[m.gameId] = other; SetStatus("RF_SharedStatusConflict", m.by, other); });
                        return;
                    }
                    var name = FreeName(m.name, l);
                    m.name = name;
                    WriteSave(name, raw);
                    m.Save(ManifestFile(m.gameId));
                }
                mainThread.Enqueue(() => SetStatus("RF_SharedStatusReceived", m.name, m.by));
            }
            catch (Exception e) { Log.Warning("[Rim Factions] storing shared save failed: " + e.Message); }
        }

        /// The incoming name, unless a save of that name here belongs to something else.
        static string FreeName(string name, SaveManifest local)
        {
            if (local?.name == name || !File.Exists(GenFilePaths.FilePathForSavedGame(name))) return name;
            if (AllLocal().Any(o => o.name == name && o.gameId == local?.gameId)) return name;
            return SafeName(name + " (shared)");
        }

        static void WriteSave(string name, byte[] raw)
        {
            var path = GenFilePaths.FilePathForSavedGame(name);
            var tmp = path + ".rftmp";
            File.WriteAllBytes(tmp, raw);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---- catching up on connect ----

        static void AnnounceAll(string to = "*")
        {
            List<SaveManifest> mine;
            lock (fileLock) mine = AllLocal().ToList();
            foreach (var m in mine) Link.Send(new Msg("SaveHave", to, m.Fields().ToArray()));
        }

        static void OnHave(Msg msg)
        {
            if (msg.From == Link.MyId) return;
            var r = SaveManifest.From(msg.Get);
            if (r == null) return;
            var l = Local(r.gameId);
            if (Wants(r))
            {
                if (wanted.Add(r.gameId + "/" + r.id))
                    Link.Send(new Msg("SaveWant", msg.From, new XElement("gameId", r.gameId), new XElement("id", r.id)));
            }
            else if (l != null && l.id != r.id && l.NewerThan(r))
                Link.Send(new Msg("SaveHave", msg.From, l.Fields().ToArray()));   // they're behind: tell them what we have
        }

        static void OnWant(Msg msg)
        {
            var gameId = msg.Get("gameId");
            if (!SafeId(gameId)) return;
            var l = Local(gameId);
            if (l == null) return;
            var file = GenFilePaths.FilePathForSavedGame(l.name);
            var to = new List<string> { msg.From };
            Task.Run(() =>
            {
                try { if (File.Exists(file)) Queue(l, File.ReadAllBytes(file), to); }
                catch (Exception e) { Log.Warning("[Rim Factions] sending shared save failed: " + e.Message); }
            });
        }

        // ---- loading ----

        /// The save to load for a shared game: the newest of the local copy (including anything received)
        /// and the cloud folder copy, which is copied into the saves folder first if it wins.
        public static string NewestFor(string gameId)
        {
            if (!SafeId(gameId)) return null;
            var l = Local(gameId);
            var cloud = CloudDir(gameId);
            SaveManifest c = null;
            if (cloud != null)
            {
                c = SaveManifest.Load(Path.Combine(cloud, "manifest.xml"));
                var cf = Path.Combine(cloud, "save.rws");
                if (c != null && (c.gameId != gameId || !File.Exists(cf) || (c.size > 0 && new FileInfo(cf).Length != c.size))) c = null;   // still syncing
            }
            if (c == null || (l != null && (c.id == l.id || !c.NewerThan(l)))) return l?.name;
            try
            {
                lock (fileLock)
                {
                    if (l != null && !c.Related(l))
                    {
                        var other = SafeName($"{c.name} ({c.by})");
                        File.Copy(Path.Combine(cloud, "save.rws"), GenFilePaths.FilePathForSavedGame(other), true);
                        Log.Warning($"[Rim Factions] shared game {gameId}: cloud copy by {c.by} was played separately; loading yours, theirs is \"{other}\"");
                        Conflicts[gameId] = other;
                        SetStatus("RF_SharedStatusConflict", c.by, other);
                        return l.name;
                    }
                    c.name = FreeName(c.name, l);
                    File.Copy(Path.Combine(cloud, "save.rws"), GenFilePaths.FilePathForSavedGame(c.name), true);
                    c.Save(ManifestFile(gameId));
                }
                SetStatus("RF_SharedStatusFromCloud", c.name, c.by);
                return c.name;
            }
            catch (Exception e)
            {
                Log.Warning("[Rim Factions] copying cloud save failed: " + e.Message);
                SetStatus("RF_SharedStatusFailed", e.Message);
                return l?.name;
            }
        }

        // ---- settings ----

        /// Cloud apps found on this PC: name and their synced root folder.
        static IEnumerable<(string app, string root)> CloudRoots()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var od = Environment.GetEnvironmentVariable("OneDrive");
            if (!od.NullOrEmpty() && Directory.Exists(od)) yield return ("OneDrive", od);
            else if (Directory.Exists(Path.Combine(home, "OneDrive"))) yield return ("OneDrive", Path.Combine(home, "OneDrive"));
            foreach (var d in DriveInfo.GetDrives())
            {
                string g = null;
                try { g = Path.Combine(d.RootDirectory.FullName, "My Drive"); if (!Directory.Exists(g)) g = null; } catch { }
                if (g != null) { yield return ("Google Drive", g); break; }
            }
            if (Directory.Exists(Path.Combine(home, "Google Drive"))) yield return ("Google Drive", Path.Combine(home, "Google Drive"));
            if (Directory.Exists(Path.Combine(home, "Dropbox"))) yield return ("Dropbox", Path.Combine(home, "Dropbox"));
        }

        /// Copies every shared game in the cloud folder that's newer than ours into the saves folder, so it shows in
        /// RimWorld's Load list. Runs at the main menu, so a friend's progress is there before anyone loads.
        public static void PullAll()
        {
            var c = Rules.Local?.cloudFolder;
            if (c.NullOrEmpty() || !Directory.Exists(Path.Combine(c, "RimFactions"))) return;
            foreach (var dir in Directory.GetDirectories(Path.Combine(c, "RimFactions")))
            {
                var id = Path.GetFileName(dir);
                if (SafeId(id)) { try { NewestFor(id); } catch (Exception e) { Log.Warning("[Rim Factions] cloud check: " + e.Message); } }
            }
        }

        public static void DrawSettings(Listing_Standard l)
        {
            var s = Rules.Local ?? (Rules.Local = new RimFactionsSettings());
            l.Label("RF_SharedHeader".Translate());
            l.Label("RF_SharedExplain".Translate());
            foreach (var (app, root) in CloudRoots())
                if (l.ButtonText("RF_SharedUse".Translate(app)))
                {
                    s.cloudFolder = Path.Combine(root, "RimFactions Shared");
                    Directory.CreateDirectory(s.cloudFolder);
                    s.Write();
                    PullAll();
                }
            l.Label("RF_SharedFolder".Translate());
            s.cloudFolder = l.TextEntry(s.cloudFolder ?? "").Trim();
            if (!s.cloudFolder.NullOrEmpty() && Directory.Exists(s.cloudFolder) && l.ButtonText("RF_SharedOpen".Translate()))
                Application.OpenURL("file:///" + s.cloudFolder.Replace(Path.DirectorySeparatorChar, '/'));
            if (!s.cloudFolder.NullOrEmpty() && !Directory.Exists(s.cloudFolder)) l.Label("RF_SharedFolderMissing".Translate());
            l.Label("RF_SharedLastSync".Translate() + " " + StatusKey.Translate(StatusArgs.Select(a => (NamedArgument)a).ToArray()));
            if (Conflicts.Count > 0) l.Label("RF_SharedConflictNote".Translate(string.Join(", ", Conflicts.Values)));
        }

        /// Sends queued chunks a few at a time, runs file results on the main thread, and announces our saves on connect.
        class Pump : MonoBehaviour
        {
            bool wasConnected;
            float next, nextPull = 5f;

            void Update()
            {
                while (mainThread.TryDequeue(out var a)) a();
                if (Current.ProgramState == ProgramState.Entry && Time.realtimeSinceStartup > nextPull)
                {
                    nextPull = Time.realtimeSinceStartup + 30f;
                    PullAll();
                }
                var connected = Link.Connected;
                if (connected && !wasConnected) AnnounceAll();
                if (!connected) { while (outgoing.TryDequeue(out _)) { } transfers.Clear(); wanted.Clear(); }
                wasConnected = connected;
                if (!connected || Time.realtimeSinceStartup < next) return;
                if (outgoing.TryDequeue(out var m)) { Link.Send(m); next = Time.realtimeSinceStartup + 0.1f; }
            }
        }
    }
}

namespace RimFactions
{
    /// hooks for feature:SharedSaves: every save of a shared game is shared, every load picks the newest copy.
    [HarmonyLib.HarmonyPatch(typeof(Verse.GameDataSaveLoader), nameof(Verse.GameDataSaveLoader.SaveGame))]
    static class SharedSaveOnSave
    {
        static void Postfix(string fileName) { try { SharedSaves.OnSaved(fileName); } catch (System.Exception e) { Verse.Log.Warning("[Rim Factions] shared save: " + e.Message); } }
    }

    [HarmonyLib.HarmonyPatch(typeof(Verse.GameDataSaveLoader), nameof(Verse.GameDataSaveLoader.LoadGame), typeof(string))]
    static class SharedSaveOnLoad
    {
        static void Prefix(ref string saveFileName)
        {
            try
            {
                var id = SharedSaves.GameIdOfSave(saveFileName);
                if (id == null) return;
                var newest = SharedSaves.NewestFor(id);
                if (!newest.NullOrEmpty() && System.IO.File.Exists(Verse.GenFilePaths.FilePathForSavedGame(newest))) saveFileName = newest;
            }
            catch (System.Exception e) { Verse.Log.Warning("[Rim Factions] shared save: " + e.Message); }
        }
    }
}
