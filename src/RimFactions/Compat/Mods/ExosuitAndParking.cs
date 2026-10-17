using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// Exosuit Framework (Aoba): Multiplayer Compatibility's patch still looks for the old "WalkerGear" classes and
    /// fails, so the buttons Exosuit doesn't sync itself (ejector bay, suit turret, emergency eject) ran in one game
    /// only. The maintenance bay already syncs itself.
    [CompatFor("aoba.exosuit.framework")]
    public static class ExosuitFramework
    {
        public static void Init()
        {
            int n = 0;
            n += SyncGizmoActions("Exosuit.Building_EjectorBay");
            n += SyncGizmoActions("Mechsuit.CompTurretGun");
            n += SyncMethods.Register("Exosuit.CompSuitModule_Ejectable:EjectAndRecover");
            Log.Message($"[Rim Factions] Exosuit Framework: synced {n} buttons");
        }

        /// The button actions written inline in GetGizmos (instance lambdas that return nothing).
        static int SyncGizmoActions(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) return 0;
            int n = 0;
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name.StartsWith("<GetGizmos>b__") && m.ReturnType == typeof(void)))
                n += SyncMethods.Register(m);
            return n;
        }
    }

    /// Automatic Parking (Rabiosus): its parking controller moved into the vehicle's comp, so Multiplayer
    /// Compatibility's patch fails and saving, clearing or driving to a parking spot happened in one game only.
    [CompatFor("rabiosus.vfautoparking")]
    public static class AutomaticParking
    {
        public static void Init()
        {
            const string comp = "VehicleAutoParking.Core.CompParkingController:";
            int n = SyncMethods.Register(comp + "SaveParkingPosition") + SyncMethods.Register(comp + "ResetParkingPosition")
                  + SyncMethods.Register(comp + "ResetAllParkingPositions") + SyncMethods.Register(comp + "MoveToParkingPosition");
            Log.Message($"[Rim Factions] Automatic Parking: synced {n} actions");
        }
    }

    static class SyncMethods
    {
        public static int Register(string typeColonMethod) => Register(AccessTools.Method(typeColonMethod));

        public static int Register(MethodInfo m)
        {
            if (m == null || !MP.enabled) return 0;
            try { MP.RegisterSyncMethod(m); return 1; }
            catch (Exception e) { Log.Warning($"[Rim Factions] couldn't sync {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); return 0; }
        }
    }
}
