using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RimFactions.Net;
using UnityEngine;
using Verse;

namespace RimFactions
{
    /// Melty's test mode: while %LOCALAPPDATA%\Melty\test\rim-factions.json exists, report the recipe's
    /// test.checks to the file it names ("pass <name>" or "fail <name>: why"). Players never have that file.
    public class MeltyTest : MonoBehaviour
    {
        static string checksPath;
        bool hosted, linked;
        float started, failAt;

        public static void Init()
        {
            try
            {
                var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Melty", "test", "rim-factions.json");
                if (!File.Exists(cfg)) return;
                var m = Regex.Match(File.ReadAllText(cfg), "\"checks\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (!m.Success) return;
                checksPath = Regex.Unescape(m.Groups[1].Value);
                var go = new GameObject("RimFactions.MeltyTest");
                DontDestroyOnLoad(go);
                go.AddComponent<MeltyTest>();
            }
            catch (Exception e) { Log.Warning("[Rim Factions] Melty test mode: " + e.Message); }
        }

        static void Report(string line)
        {
            try { File.AppendAllText(checksPath, line + Environment.NewLine); } catch { }
        }

        void Update()
        {
            if (started == 0) { started = Time.realtimeSinceStartup; failAt = started + 240f; }

            // planet-hosted: the hub is open and its join address went to the log Melty reads
            if (!hosted && Link.Role == LinkRole.Hub && Link.HubAddress != null)
            {
                hosted = true;
                if (Link.HubAddress.EndsWith(".steamlobby")) Report("pass planet-hosted");
                else Report("fail planet-hosted: Steam isn't running, so no join address for friends");
            }

            // colony-linked: a colony is loaded and the hub lists it on the planet
            if (!linked && Current.ProgramState == ProgramState.Playing && Link.Connected)
            {
                var me = Roster.Get(Link.MyId);
                if (me != null && !me.colony.NullOrEmpty() && me.tile >= 0)
                {
                    linked = true;
                    Report("pass colony-linked");
                }
                else if (Time.frameCount % 600 == 0) Link.SendHello();
            }

            if (Time.realtimeSinceStartup > failAt && (!hosted || !linked))
            {
                if (!hosted) Report("fail planet-hosted: the planet never opened (state " + Link.Status + ")");
                if (!linked) Report("fail colony-linked: no colony reached the planet roster");
                hosted = linked = true;
            }
        }
    }
}
