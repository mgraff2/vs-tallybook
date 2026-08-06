using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Tallybook
{
    /// <summary>
    /// Client-side crafting shopping list. See tallybook-mod-spec.md.
    ///
    /// Client-only by construction: the ShouldLoad gate below and "side": "Client" in
    /// modinfo.json are both load-bearing. A dedicated server still unpacks the zip and
    /// loads this assembly, but must never see a single line of Tallybook output — that
    /// silence is pinned as a regression invariant in tools/compat-test.ps1.
    ///
    /// Currently at build-order step 1 (spec §10): a read-only probe validating recipe
    /// registry access and live inventory events. No pinning, HUD, or persistence yet.
    /// </summary>
    public class TallybookModSystem : ModSystem
    {
        // Chat is a terrible place to dump a long list, and a probe that floods it buries the
        // one recipe you actually asked about.
        const int MaxListedOutputs = 8;

        ICoreClientAPI capi;
        RecipeProbe probe;

        // The probe target is what makes the inventory-event wiring observable: with a target
        // set, any carried-inventory change re-counts and reports only when a number actually
        // moved. That "only on real change" filter is the same discipline the HUD will need —
        // SlotModified fires far more often than displayed values change.
        RecipeVariantGroup watched;
        List<Requirement> watchedRequirements = new List<Requirement>();
        readonly Dictionary<string, int> lastCounts = new Dictionary<string, int>();
        readonly HashSet<IInventory> subscribed = new HashSet<IInventory>();

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            probe = new RecipeProbe(api);

            // Registered on capi.ChatCommands, so this is a CLIENT command and players invoke
            // it as ".tallybook". A leading "/" is routed to the server, which has never heard
            // of us (and must not — see the server-side silence invariant) and answers "No
            // such command exists".
            api.ChatCommands.Create("tallybook")
                .WithDescription("Probe grid recipes for an item and show carried-inventory counts")
                .WithExamples(".tallybook bookshelf", ".tallybook off")
                .WithArgs(api.ChatCommands.Parsers.OptionalAll("itemcode"))
                .HandleWith(OnProbeCommand);

            api.Event.PlayerJoin += OnPlayerJoin;
            api.Event.LeaveWorld += Unsubscribe;
        }

        void OnPlayerJoin(IClientPlayer player)
        {
            if (player?.PlayerUID == capi.World?.Player?.PlayerUID) SubscribeToCarriedInventories();
        }

        TextCommandResult OnProbeCommand(TextCommandCallingArgs args)
        {
            // Read the parsed value directly, never gated on args.ArgCount: parsers consume
            // the raw arguments while parsing, so by the time a handler runs ArgCount reads 0
            // even though args[0] holds the value. Gating on it silently discards every
            // argument and makes the command look like it was called bare.
            var query = (args.Parsers.Count > 0 ? args[0] as string : null)?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                return TextCommandResult.Success(
                    $"Tallybook probe. Recipe registry holds {capi.World?.GridRecipes?.Count ?? 0} grid recipes. " +
                    "Usage: .tallybook <part of an item code>, or .tallybook off to stop watching.");
            }

            if (query == "off")
            {
                StopWatching();
                return TextCommandResult.Success("Tallybook: stopped watching.");
            }

            var groups = probe.FindVariantGroups(query);
            if (groups.Count == 0)
            {
                return TextCommandResult.Success($"Tallybook: no grid recipe produces anything matching '{query}'.");
            }

            watched = groups[0];
            watchedRequirements = probe.BuildRequirements(watched);
            lastCounts.Clear();
            SubscribeToCarriedInventories();
            SeedBaseline();

            var sb = new StringBuilder();
            sb.AppendLine($"Tallybook: {groups.Count} recipe(s) matching '{query}'. Watching:");
            sb.Append(DescribeWatched());

            if (groups.Count > 1)
            {
                sb.AppendLine($"Other recipes ({groups.Count - 1}):");
                foreach (var g in groups.Skip(1).Take(MaxListedOutputs))
                {
                    sb.AppendLine($"  {g.OutputName} x{g.OutputQuantity} [{g.Pattern}] " +
                                  $"({g.Recipes.Count} variant(s))");
                }
                // Never truncate silently — a shortened list that doesn't say so reads as
                // "that's all of them".
                int withheld = (groups.Count - 1) - MaxListedOutputs;
                if (withheld > 0) sb.AppendLine($"  ...and {withheld} more not shown — narrow the query.");
            }
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        string DescribeWatched()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Output: {watched.OutputName} x{watched.OutputQuantity} " +
                          $"(code {watched.OutputCode}, {watched.Width}x{watched.Height}, " +
                          $"pattern {watched.Pattern}, {watched.Recipes.Count} variant recipe(s))");

            foreach (var req in watchedRequirements)
            {
                int have = probe.CountCarried(req);
                sb.AppendLine($"    {StatusMark(have, req.Quantity)} {req.DisplayName}  {have}/{req.Quantity}");
            }

            foreach (var tool in probe.BuildRequirements(watched, tools: true))
            {
                bool present = probe.CountCarried(tool) > 0;
                sb.AppendLine($"    {(present ? "[x]" : "[ ]")} requires: {tool.DisplayName} (not consumed)");
            }
            return sb.ToString();
        }

        static string StatusMark(int have, int needed)
        {
            if (have >= needed) return "[x]";   // satisfied
            if (have > 0) return "[~]";         // partial
            return "[ ]";                       // none
        }

        /// <summary>
        /// Record current counts without reporting them, so the next inventory change reports
        /// only what actually moved rather than replaying the whole ingredient list.
        /// </summary>
        void SeedBaseline()
        {
            foreach (var req in watchedRequirements) lastCounts[req.DisplayName] = probe.CountCarried(req);
        }

        void SubscribeToCarriedInventories()
        {
            foreach (var inv in probe.CarriedInventories())
            {
                if (subscribed.Add(inv)) inv.SlotModified += OnSlotModified;
            }
        }

        void StopWatching()
        {
            watched = null;
            watchedRequirements = new List<Requirement>();
            lastCounts.Clear();
        }

        void Unsubscribe()
        {
            foreach (var inv in subscribed) inv.SlotModified -= OnSlotModified;
            subscribed.Clear();
            StopWatching();
        }

        void OnSlotModified(int slotId)
        {
            if (watched == null) return;

            // Backpack slots can appear after login (equipping a bag adds an inventory), so
            // re-scan rather than assuming the login-time set is final.
            SubscribeToCarriedInventories();

            // Re-counting per requirement walks every carried slot again, so this is
            // O(requirements x slots) per event. Fine for one watched recipe; the HUD's merged
            // totals across every pin will need a single inventory pass building a code->count
            // map instead. Event-driven is the requirement (spec §4) — this shape is not.
            foreach (var req in watchedRequirements)
            {
                string key = req.DisplayName;
                int have = probe.CountCarried(req);

                bool known = lastCounts.TryGetValue(key, out int previous);
                if (known && previous == have) continue;
                lastCounts[key] = have;

                // An ingredient we have never counted before is a baseline being recorded,
                // not a change the player caused — recording it silently keeps the first
                // pickup after a probe from reporting every ingredient at once.
                if (!known) continue;

                capi.ShowChatMessage($"Tallybook: {req.DisplayName} {have}/{req.Quantity} {StatusMark(have, req.Quantity)}");
            }
        }

        public override void Dispose()
        {
            if (capi != null) Unsubscribe();
            base.Dispose();
        }
    }
}
