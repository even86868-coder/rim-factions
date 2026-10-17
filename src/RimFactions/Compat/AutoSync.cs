using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimFactions.Compat
{
    /// system:AutoSyncButtons — a mod with no Multiplayer support at all (neither its own nor a Multiplayer
    /// Compatibility patch) desyncs the moment anyone presses one of its buttons, because the button only runs in the
    /// presser's game. For those mods, Rim Factions finds the buttons' actions (the code written inside GetGizmos) and
    /// has Multiplayer send them to everyone. Buttons that open a window, menu or targeting cursor are left alone:
    /// those need a hand-made patch. Every game registers the same actions in the same order (mod order, then names),
    /// which Multiplayer requires.
    public static class AutoSync
    {
        static readonly string[] GizmoMethods = { "GetGizmos", "CompGetGizmosExtra", "CompGetWornGizmosExtra", "GetWornGizmos", "GetGizmosExtra" };

        /// Mods that never need this (no buttons that change the game, or handled elsewhere).
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "brrainz.harmony", "zetrith.prepatcher", "rwmt.multiplayer", "rwmt.multiplayercompatibility", "melty.rimfactions",
            "unlimitedhugs.hugslib", "edb.preparecarefully",
        };

        static readonly HashSet<Type> SyncableFieldTypes = new HashSet<Type>
        {
            typeof(int), typeof(float), typeof(bool), typeof(string), typeof(long), typeof(double), typeof(byte),
            typeof(IntVec3), typeof(LocalTargetInfo), typeof(Rot4), typeof(Map), typeof(Faction), typeof(Vector3),
        };

        public static void Init()
        {
            if (!MP.enabled) return;
            var covered = CompatCovered();
            var report = new List<string>();
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.IsOfficialMod || Skip.Contains(mod.PackageIdPlayerFacing) || covered.Contains(mod.PackageIdPlayerFacing.ToLowerInvariant())) continue;
                if (mod.assemblies.loadedAssemblies.Count == 0 || mod.assemblies.loadedAssemblies.Any(UsesMultiplayer)) continue;
                int n = 0;
                foreach (var asm in mod.assemblies.loadedAssemblies.OrderBy(a => a.GetName().Name))
                    n += SyncAssembly(asm);
                if (n > 0) report.Add($"{mod.Name}={n}");
            }
            if (report.Count > 0) Log.Message("[Rim Factions] synced buttons of mods without Multiplayer support: " + string.Join(", ", report));
        }

        /// Mods Multiplayer Compatibility has a patch for, and mods Rim Factions patches by hand.
        static HashSet<string> CompatCovered()
        {
            var ids = new HashSet<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Multiplayer_Compat")))
                foreach (var t in SafeTypes(asm))
                    try
                    {
                        foreach (var c in t.GetCustomAttributesData().Where(c => c.AttributeType.Name == "MpCompatForAttribute"))
                            if (c.ConstructorArguments.Count > 0 && c.ConstructorArguments[0].Value is string id) ids.Add(id.ToLowerInvariant());
                    }
                    catch { }
            foreach (var t in SafeTypes(typeof(AutoSync).Assembly))
                if (t.GetCustomAttributes(typeof(CompatForAttribute), false).FirstOrDefault() is CompatForAttribute a) ids.Add(a.packageId.ToLowerInvariant());
            // known mods that are covered, patched or need no syncing ("unsafe" and "caution" ones still get their buttons synced)
            foreach (var def in DefDatabase<LiveCompatDef>.AllDefsListForReading.Where(d => d.status == "covered" || d.status == "patched" || d.status == "safe"))
                ids.Add(def.packageId.ToLowerInvariant());
            return ids;
        }

        static bool UsesMultiplayer(Assembly asm)
        {
            try { return asm.GetReferencedAssemblies().Any(r => r.Name.IndexOf("MultiplayerAPI", StringComparison.OrdinalIgnoreCase) >= 0 || r.Name == "Multiplayer"); }
            catch { return true; }
        }

        static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        static int SyncAssembly(Assembly asm)
        {
            int n = 0;
            foreach (var type in SafeTypes(asm).Where(t => t.IsClass && !t.IsGenericTypeDefinition).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (!typeof(Thing).IsAssignableFrom(type) && !typeof(ThingComp).IsAssignableFrom(type)) continue;
                foreach (var parent in GizmoMethods)
                {
                    var prefix = "<" + parent + ">b__";
                    // actions that only use the thing itself are compiled as methods of the thing's own class
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                 .Where(m => m.Name.StartsWith(prefix)).OrderBy(m => m.Name, StringComparer.Ordinal))
                        if (IsPlainAction(m) && m.GetParameters().All(p => Syncable(p.ParameterType)) && Register(() => MP.RegisterSyncMethod(m))) n++;
                    // actions that also use local values live in a small helper class next to it
                    foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic).Where(t => t.Name.StartsWith("<>c__DisplayClass")).OrderBy(t => t.Name, StringComparer.Ordinal))
                    {
                        if (!nested.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(f => Syncable(f.FieldType))) continue;
                        foreach (var m in nested.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                                     .Where(m => m.Name.StartsWith(prefix)).OrderBy(m => m.Name, StringComparer.Ordinal))
                            if (IsPlainAction(m) && m.GetParameters().Length == 0 && Register(() => MP.RegisterSyncDelegate(type, nested.Name, m.Name))) n++;
                    }
                }
            }
            return n;
        }

        static bool Syncable(Type t) =>
            SyncableFieldTypes.Contains(t) || t.IsEnum || typeof(Thing).IsAssignableFrom(t) || typeof(ThingComp).IsAssignableFrom(t)
            || typeof(Def).IsAssignableFrom(t) || typeof(WorldObject).IsAssignableFrom(t);

        /// Returns nothing and doesn't open anything on screen.
        static bool IsPlainAction(MethodInfo m)
        {
            if (m.ReturnType != typeof(void)) return false;
            try
            {
                foreach (var ins in PatchProcessor.GetOriginalInstructions(m))
                {
                    if (!(ins.operand is MethodBase called)) continue;
                    var owner = called.DeclaringType;
                    if (owner == null) continue;
                    if (owner == typeof(WindowStack) || owner == typeof(Targeter) || typeof(Window).IsAssignableFrom(owner)
                        || owner == typeof(FloatMenu) || owner == typeof(FloatMenuOption) || owner.Name.StartsWith("Dialog_")
                        || owner == typeof(Find) && (called.Name == "get_Targeter" || called.Name == "get_WindowStack")
                        || owner == typeof(GUIUtility) || owner == typeof(GenUI))
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        static bool Register(Action register)
        {
            try { register(); return true; }
            catch (Exception e) { Log.Warning("[Rim Factions] auto-sync skipped a button: " + e.Message); return false; }
        }
    }
}
