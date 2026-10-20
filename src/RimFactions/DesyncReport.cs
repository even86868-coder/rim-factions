using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimFactions.Net;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// feature:DesyncReport — when Multiplayer shows its "Desynced" window, Rim Factions asks the player whether to send
    /// the desync report to the mod's author. Only on yes: the newest report from Multiplayer's MpDesyncs folder (or,
    /// for the host, who gets no report file, the end of Player.log) goes to the author's Discord channel. The webhook
    /// address ships in the mod as report-webhook.txt; without that file this does nothing.
    public static class DesyncReport
    {
        static bool offerPending;
        static int sentThisSession;
        static DateTime desyncAt;

        static string Webhook
        {
            get
            {
                try
                {
                    var root = LoadedModManager.GetMod<RimFactionsMod>()?.Content?.RootDir;
                    var f = root == null ? null : Path.Combine(root, "report-webhook.txt");
                    var url = f != null && File.Exists(f) ? File.ReadAllText(f).Trim() : null;
                    return url != null && (url.StartsWith("https://discord.com/api/webhooks/") || url.StartsWith("https://discordapp.com/api/webhooks/")) ? url : null;
                }
                catch { return null; }
            }
        }

        public static void Init(Harmony h)
        {
            var window = AccessTools.TypeByName("Multiplayer.Client.DesyncedWindow");
            if (window == null) return;
            foreach (var ctor in AccessTools.GetDeclaredConstructors(window))
                h.Patch(ctor, postfix: new HarmonyMethod(typeof(DesyncReport), nameof(Desynced)));
        }

        static void Desynced()
        {
            if (Webhook == null || sentThisSession >= 3) return;
            desyncAt = DateTime.Now;
            offerPending = true;
        }

        /// Called every frame: the offer opens once the desync window is up.
        public static void Update()
        {
            if (!offerPending || Find.WindowStack == null) return;
            offerPending = false;
            Find.WindowStack.Add(new Dialog_MessageBox("RF_DesyncSendAsk".Translate(), "RF_DesyncSend".Translate(), Send, "RF_NotNow".Translate(),
                title: "RF_DesyncSendTitle".Translate()));
        }

        static void Send()
        {
            sentThisSession++;
            var url = Webhook;
            var since = desyncAt;
            var info = $"Rim Factions {RimFactionsMod.Version} - {(MpBridge.Hosting ? "host" : "friend")} - {MpBridge.MyUsername ?? Link.MyName} - {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
            var dataDir = GenFilePaths.SaveDataFolderPath;
            var log = Path.Combine(Application.persistentDataPath, "Player.log");
            Messages.Message("RF_DesyncSending".Translate(), MessageTypeDefOf.NeutralEvent, false);
            new Thread(() =>
            {
                try
                {
                    // Multiplayer writes the friend's report a moment after the window opens: wait for it a little
                    string file = null;
                    var dir = Path.Combine(dataDir, "MpDesyncs");
                    for (int i = 0; i < 20 && file == null; i++)
                    {
                        file = Directory.Exists(dir)
                            ? new DirectoryInfo(dir).GetFiles("Desync-*.zip").Where(f => f.LastWriteTime >= since.AddSeconds(-30)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName
                            : null;
                        if (file == null) Thread.Sleep(1000);
                    }
                    byte[] bytes; string name;
                    if (file != null) { bytes = File.ReadAllBytes(file); name = Path.GetFileName(file); }
                    else
                    {
                        // the host has no report file: the end of its Player.log (Rim Factions' sync check is in there)
                        using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            long take = Math.Min(fs.Length, 6L * 1024 * 1024);
                            fs.Seek(-take, SeekOrigin.End);
                            bytes = new byte[take];
                            int read = 0;
                            while (read < take) { int n = fs.Read(bytes, read, (int)take - read); if (n <= 0) break; read += n; }
                        }
                        name = "host-Player.log";
                    }
                    Post(url, info + (file != null ? " - desync report" : " - host log"), name, bytes);
                    Done(true);
                }
                catch (Exception e)
                {
                    Log.Warning("[Rim Factions] couldn't send the desync report: " + e.Message);
                    Done(false);
                }
            }) { IsBackground = true }.Start();
        }

        static void Done(bool ok) =>
            LongEventHandler.ExecuteWhenFinished(() => Messages.Message((ok ? "RF_DesyncSent" : "RF_DesyncSendFailed").Translate(),
                ok ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput, false));

        /// A Discord webhook message with one file attached.
        public static void Post(string url, string text, string fileName, byte[] file)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var boundary = "----RimFactions" + Guid.NewGuid().ToString("N");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Timeout = 60000;
            using (var body = new MemoryStream())
            {
                void W(string s) { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); }
                var json = "{\"content\":\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
                W($"--{boundary}\r\nContent-Disposition: form-data; name=\"payload_json\"\r\nContent-Type: application/json\r\n\r\n{json}\r\n");
                W($"--{boundary}\r\nContent-Disposition: form-data; name=\"files[0]\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\n");
                body.Write(file, 0, file.Length);
                W($"\r\n--{boundary}--\r\n");
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) body.WriteTo(s);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
                if ((int)resp.StatusCode >= 300) throw new Exception("HTTP " + (int)resp.StatusCode);
        }
    }
}
