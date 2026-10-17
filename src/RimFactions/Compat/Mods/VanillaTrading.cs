using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace RimFactions.Compat.Mods
{
    /// Vanilla Trading Expanded: the bank, stock market and contract windows changed money and contracts in one
    /// game only, and a trading terminal opened its window on every player's screen. Now:
    ///  - deposits, withdrawals, loans, repayments, share trades and contracts go through Multiplayer's sync;
    ///  - a terminal window opens only for the player who sent the colonist;
    ///  - reward / payment windows the game opens for everyone close for everyone once one player confirms;
    ///  - its gameplay settings (news rate, price swings, contract counts) are the defaults while live.
    [CompatFor("VanillaExpanded.VanillaTradingExpanded")]
    public static class VanillaTrading
    {
        static Type manager, bankType, procType, contractType, companyType, gainsWindow, costsWindow, bankWindow;
        /// Transaction windows opened in every game at once (from game events), in the order they opened.
        static readonly List<object> shared = new List<object>();
        static readonly HashSet<object> performed = new HashSet<object>();
        /// Terminal uses this player ordered: colonist -> chosen bank (null for the other terminal options).
        static readonly Dictionary<int, Faction> myTerminalOrders = new Dictionary<int, Faction>();

        public static void Init()
        {
            manager = AccessTools.TypeByName("VanillaTradingExpanded.TradingManager");
            bankType = AccessTools.TypeByName("VanillaTradingExpanded.Bank");
            procType = AccessTools.TypeByName("VanillaTradingExpanded.TransactionProcess");
            contractType = AccessTools.TypeByName("VanillaTradingExpanded.Contract");
            companyType = AccessTools.TypeByName("VanillaTradingExpanded.Company");
            gainsWindow = AccessTools.TypeByName("VanillaTradingExpanded.Window_PerformTransactionGains");
            costsWindow = AccessTools.TypeByName("VanillaTradingExpanded.Window_PerformTransactionCosts");
            bankWindow = AccessTools.TypeByName("VanillaTradingExpanded.Window_Bank");

            var h = new Harmony("melty.rimfactions.compat.vte");
            HarmonyMethod Of(string name) => new HarmonyMethod(typeof(VanillaTrading), name);

            foreach (var m in new[] { "DepositSilver", "WithdrawSilver", "TakeLoan", "TryRepayFromBalance" })
                h.Patch(AccessTools.Method(bankType, m), prefix: Of(nameof(BankPrefix)));
            h.Patch(AccessTools.Method(manager, "CompleteContract"), prefix: Of(nameof(CompleteContractPrefix)));
            h.Patch(AccessTools.Method("VanillaTradingExpanded.Window_Contracts:DoWindowContents"), prefix: Of(nameof(ContractsWindowPrefix)), postfix: Of(nameof(ContractsWindowPostfix)));

            h.Patch(AccessTools.Method(procType, "PerformTransaction"), prefix: Of(nameof(PerformPrefix)));
            h.Patch(AccessTools.Method(procType, "PostCancel"), prefix: Of(nameof(CancelPrefix)));
            foreach (var w in new[] { gainsWindow, costsWindow })
            {
                foreach (var ctor in AccessTools.GetDeclaredConstructors(w)) h.Patch(ctor, postfix: Of(nameof(WindowCreated)));
                h.Patch(AccessTools.Method(w, "PostClose"), prefix: Of(nameof(WindowClosing)));
            }

            h.Patch(AccessTools.Method("VanillaTradingExpanded.Building_TradingTerminal:GetFloatMenuOptions"), postfix: Of(nameof(TerminalOptions)));
            foreach (var d in new[] { "ContactBank", "OpenContracts", "OpenStockMarket", "ViewNews", "ViewPrices" })
                h.Patch(AccessTools.Method("VanillaTradingExpanded.JobDriver_" + d + ":DoAction"), prefix: Of(nameof(TerminalAction)));
            h.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)), prefix: Of(nameof(Reset)));

            LiveSettings.UseDefaults(AccessTools.Field("VanillaTradingExpanded.VanillaTradingExpandedMod:settings"));
        }

        static bool Local => MP.IsInMultiplayer && MP.InInterface;
        static object Manager => AccessTools.Field(manager, "Instance").GetValue(null);
        static IList Banks => (IList)AccessTools.Property(manager, "Banks").GetValue(Manager);
        static IList Companies => (IList)AccessTools.Field(manager, "companies").GetValue(Manager);
        static IList List(string field) => (IList)AccessTools.Field(manager, field).GetValue(Manager);
        static object BankOf(Faction f) => Banks.Cast<object>().FirstOrDefault(b => Traverse.Create(b).Field("parentFaction").GetValue<Faction>() == f);
        static object Field(object o, string name) => Traverse.Create(o).Field(name).GetValue();

        static void Reset()
        {
            shared.Clear();
            performed.Clear();
            myTerminalOrders.Clear();
        }

        // ---- bank ----

        static bool BankPrefix(object __instance, System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            if (!Local) return true;
            var faction = (Faction)Field(__instance, "parentFaction");
            switch (__originalMethod.Name)
            {
                case "DepositSilver": Deposit(faction, new List<Thing>((List<Thing>)__args[0]), (int)__args[1]); break;
                case "WithdrawSilver": Withdraw(faction, (Pawn)__args[0], (int)__args[1]); break;
                case "TakeLoan":
                    var options = (IList)Field(Field(__instance, "bankExtension"), "loanOptions");
                    TakeLoan(faction, (Pawn)__args[0], (int)__args[1], (int)__args[2], (int)__args[3], options.IndexOf(__args[4]));
                    break;
                case "TryRepayFromBalance": Repay(faction, ((IList)Field(__instance, "loans")).IndexOf(__args[0])); break;
            }
            return false;
        }

        [SyncMethod]
        public static void Deposit(Faction bank, List<Thing> silver, int amount) =>
            AccessTools.Method(bankType, "DepositSilver").Invoke(BankOf(bank), new object[] { silver.Where(t => t != null && !t.Destroyed).ToList(), amount });

        [SyncMethod]
        public static void Withdraw(Faction bank, Pawn negotiator, int amount) =>
            AccessTools.Method(bankType, "WithdrawSilver").Invoke(BankOf(bank), new object[] { negotiator, amount });

        [SyncMethod]
        public static void TakeLoan(Faction bank, Pawn negotiator, int loanAmount, int repayAmount, int repayDate, int option)
        {
            var b = BankOf(bank);
            var options = (IList)Field(Field(b, "bankExtension"), "loanOptions");
            if (option < 0 || option >= options.Count) return;
            AccessTools.Method(bankType, "TakeLoan").Invoke(b, new object[] { negotiator, loanAmount, repayAmount, repayDate, options[option] });
        }

        [SyncMethod]
        public static void Repay(Faction bank, int loan)
        {
            var b = BankOf(bank);
            var loans = (IList)Field(b, "loans");
            if (loan >= 0 && loan < loans.Count) AccessTools.Method(bankType, "TryRepayFromBalance").Invoke(b, new[] { loans[loan] });
        }

        // ---- contracts ----

        static bool CompleteContractPrefix(object contract)
        {
            if (!Local) return true;
            CompleteContract(List("npcSubmittedContracts").IndexOf(contract));
            return false;
        }

        [SyncMethod]
        public static void CompleteContract(int index)
        {
            var list = List("npcSubmittedContracts");
            if (index >= 0 && index < list.Count) AccessTools.Method(manager, "CompleteContract").Invoke(Manager, new[] { list[index] });
        }

        /// The contracts window edits the player's contract list directly: see what it changed, undo it here
        /// and send the change through sync instead.
        static void ContractsWindowPrefix(out List<object> __state) =>
            __state = Local ? List("playerSubmittedContracts").Cast<object>().ToList() : null;

        static void ContractsWindowPostfix(List<object> __state)
        {
            if (__state == null) return;
            var list = List("playerSubmittedContracts");
            var now = list.Cast<object>().ToList();
            if (now.SequenceEqual(__state)) return;
            list.Clear();
            foreach (var c in __state) list.Add(c);
            foreach (var gone in __state.Where(c => !now.Contains(c))) CancelContract(__state.IndexOf(gone));
            foreach (var c in now.Where(c => !__state.Contains(c)))
            {
                var t = Traverse.Create(c);
                AddContract(t.Field("item").GetValue<ThingDef>()?.defName, t.Field("stuff").GetValue<ThingDef>()?.defName,
                    t.Field("amount").GetValue<int>(), t.Field("reward").GetValue<int>(), t.Field("rewardAsFloat").GetValue<float>(),
                    t.Field("expiresInTicks").GetValue<int>());
            }
        }

        [SyncMethod]
        public static void CancelContract(int index)
        {
            var list = List("playerSubmittedContracts");
            if (index >= 0 && index < list.Count) list.RemoveAt(index);
        }

        [SyncMethod]
        public static void AddContract(string item, string stuff, int amount, int reward, float rewardAsFloat, int expiresInTicks)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(item ?? "");
            if (def == null) return;
            var c = Activator.CreateInstance(contractType);
            var t = Traverse.Create(c);
            t.Field("item").SetValue(def);
            t.Field("stuff").SetValue(stuff == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuff));
            t.Field("amount").SetValue(amount);
            t.Field("reward").SetValue(reward);
            t.Field("rewardAsFloat").SetValue(rewardAsFloat);
            t.Field("expiresInTicks").SetValue(expiresInTicks);
            List("playerSubmittedContracts").Add(c);
        }

        // ---- money transfers (stock market trades, contract payments and rewards) ----

        static void WindowCreated(object __instance)
        {
            var proc = Field(__instance, "transactionProcess");
            if (MP.IsInMultiplayer && !MP.InInterface && proc != null && !shared.Contains(proc)) shared.Add(proc);
        }

        static int[] Pairs(object dict, IList keys)
        {
            if (dict == null) return new int[0];
            var r = new List<int>();
            foreach (DictionaryEntry e in (IDictionary)dict) { r.Add(keys.IndexOf(e.Key)); r.Add((int)e.Value); }
            return r.ToArray();
        }

        static object Dict(Type key, int[] pairs, IList keys)
        {
            var d = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(key, typeof(int)));
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                if (pairs[i] >= 0 && pairs[i] < keys.Count) d[keys[pairs[i]]] = pairs[i + 1];
            return d;
        }

        static bool PerformPrefix(object __instance)
        {
            if (!Local) return true;
            if (performed.Contains(__instance)) return false;
            var banks = Banks;
            Perform(shared.IndexOf(__instance),
                Field(__instance, "amountToTransfer") != null, Pairs(Field(__instance, "amountToTransfer"), banks),
                Field(__instance, "amountToSpend") != null, Pairs(Field(__instance, "amountToSpend"), banks),
                Pairs(Field(__instance, "companySharesToBuyOrSell"), Companies));
            // A window-only order (stock market) is done here once sent, so it can't be sent twice.
            if (!shared.Contains(__instance))
                foreach (var f in new[] { "amountToTransfer", "amountToSpend", "companySharesToBuyOrSell" })
                    (Field(__instance, f) as IDictionary)?.Clear();
            return false;
        }

        [SyncMethod]
        public static void Perform(int sharedIndex, bool hasTransfer, int[] transfer, bool hasSpend, int[] spend, int[] shares)
        {
            var proc = sharedIndex >= 0 && sharedIndex < shared.Count ? shared[sharedIndex] : Activator.CreateInstance(procType);
            if (!performed.Add(proc)) return;
            var banks = Banks;
            var t = Traverse.Create(proc);
            t.Field("amountToTransfer").SetValue(hasTransfer ? Dict(bankType, transfer, banks) : null);
            t.Field("amountToSpend").SetValue(hasSpend ? Dict(bankType, spend, banks) : null);
            t.Field("companySharesToBuyOrSell").SetValue(Dict(companyType, shares, Companies));
            AccessTools.Method(procType, "PerformTransaction").Invoke(proc, null);
        }

        static bool CancelPrefix(object __instance)
        {
            if (!Local || !shared.Contains(__instance)) return true;
            Cancel(shared.IndexOf(__instance));
            return false;
        }

        [SyncMethod]
        public static void Cancel(int sharedIndex)
        {
            if (sharedIndex >= 0 && sharedIndex < shared.Count) AccessTools.Method(procType, "PostCancel").Invoke(shared[sharedIndex], null);
        }

        static bool WindowClosing(Window __instance)
        {
            var proc = Field(__instance, "transactionProcess");
            if (!Local || !shared.Contains(proc)) return true;
            Close(shared.IndexOf(proc), __instance.GetType() == costsWindow);
            return false;
        }

        /// Closes this transfer window in every game (each runs its "after closing" step once).
        [SyncMethod]
        public static void Close(int sharedIndex, bool costs)
        {
            if (sharedIndex < 0 || sharedIndex >= shared.Count) return;
            var proc = shared[sharedIndex];
            var type = costs ? costsWindow : gainsWindow;
            var open = Find.WindowStack.Windows.Where(w => w.GetType() == type && Field(w, "transactionProcess") == proc).ToList();
            foreach (var w in open) w.Close(false);
            if (open.Count == 0) AccessTools.Method(procType, "PostClose").Invoke(proc, null);
        }

        // ---- trading terminal ----

        static IEnumerable<FloatMenuOption> TerminalOptions(IEnumerable<FloatMenuOption> __result, Thing __instance, Pawn myPawn)
        {
            foreach (var option in __result)
            {
                if (MP.IsInMultiplayer && option.action != null)
                {
                    var order = option.action;
                    var terminal = Traverse.Create(__instance).Field("currentVisitableFactionBank");
                    option.action = () =>
                    {
                        var before = terminal.GetValue<Faction>();
                        order();
                        myTerminalOrders[myPawn.thingIDNumber] = terminal.GetValue<Faction>();
                        terminal.SetValue(before);   // the terminal's saved choice stays the same in every game
                    };
                }
                yield return option;
            }
        }

        /// Runs in every game when the colonist reaches the terminal: only the player who sent them gets the window.
        static bool TerminalAction(object __instance, Pawn actor)
        {
            if (!MP.IsInMultiplayer) return true;
            if (!myTerminalOrders.TryGetValue(actor.thingIDNumber, out var bank)) return false;
            myTerminalOrders.Remove(actor.thingIDNumber);
            if (__instance.GetType().Name != "JobDriver_ContactBank") return true;
            if (bank != null) Find.WindowStack.Add((Window)Activator.CreateInstance(bankWindow, actor, bank));
            return false;
        }
    }
}
