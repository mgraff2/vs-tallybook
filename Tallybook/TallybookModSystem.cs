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
        ICoreClientAPI capi;
        RecipeProbe probe;

        // The probe target is what makes the inventory-event wiring observable: with a target
        // set, any carried-inventory change re-counts and reports only when a number actually
        // moved. That "only on real change" filter is the same discipline the HUD will need —
        // SlotModified fires far more often than displayed values change.
        // Chat is a terrible place to dump a long list, and a probe that floods it buries the
        // one recipe you actually asked about.
        const int MaxListedOutputs = 8;

        GridRecipe watchedRecipe;
        Dictionary<string, int> lastCounts = new Dictionary<string, int>();
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
                .WithExamples(".tallybook spile", ".tallybook off")
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
                watchedRecipe = null;
                lastCounts.Clear();
                return TextCommandResult.Success("Tallybook: stopped watching.");
            }

            var recipes = probe.FindRecipesProducing(query);
            if (recipes.Count == 0)
            {
                return TextCommandResult.Success($"Tallybook: no grid recipe produces anything matching '{query}'.");
            }

            // Group by output code before reporting. A substring query like "bookshelf" matches
            // every wood and orientation variant, and listing each one floods the chat with
            // hundreds of near-identical lines that answer nothing. Group, cap, and say plainly
            // how many were withheld — a truncated list that doesn't admit it is a lie.
            var groups = recipes
                .GroupBy(r => probe.OutputCode(r))
                .OrderBy(g => g.Key)
                .ToList();

            watchedRecipe = recipes[0];
            lastCounts.Clear();
            SubscribeToCarriedInventories();
            SeedBaseline(watchedRecipe);

            var sb = new StringBuilder();
            sb.AppendLine($"Tallybook: {recipes.Count} recipe(s) producing {groups.Count} distinct " +
                          $"item(s) matching '{query}'. Watching:");
            sb.Append(DescribeRecipe(watchedRecipe));

            string watchedCode = probe.OutputCode(watchedRecipe);
            var others = groups.Where(g => g.Key != watchedCode).ToList();
            if (others.Count > 0)
            {
                sb.AppendLine($"Other matching outputs ({others.Count}):");
                foreach (var g in others.Take(MaxListedOutputs))
                {
                    sb.AppendLine($"  {g.Key} ({g.Count()} recipe(s))");
                }
                int withheld = others.Count - MaxListedOutputs;
                if (withheld > 0)
                {
                    sb.AppendLine($"  ...and {withheld} more not shown — narrow the query to reach them.");
                }
            }
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        string DescribeRecipe(GridRecipe recipe)
        {
            var sb = new StringBuilder();
            var outStack = recipe.Output?.ResolvedItemStack;
            sb.AppendLine($"  Output: {outStack?.GetName() ?? "?"} x{probe.OutputQuantity(recipe)} " +
                          $"(code {outStack?.Collectible?.Code}, shapeless={recipe.Shapeless}, {recipe.Width}x{recipe.Height})");

            foreach (var (ing, qty) in probe.ConsumedIngredients(recipe))
            {
                int have = probe.CountCarried(ing);
                sb.AppendLine($"    {StatusMark(have, qty)} {probe.DisplayName(ing)}  {have}/{qty}");
            }

            foreach (var tool in probe.Tools(recipe))
            {
                bool present = probe.CountCarried(tool) > 0;
                sb.AppendLine($"    {(present ? "[x]" : "[ ]")} requires: {probe.DisplayName(tool)} (not consumed)");
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
        void SeedBaseline(GridRecipe recipe)
        {
            foreach (var (ing, _) in probe.ConsumedIngredients(recipe))
            {
                lastCounts[probe.DisplayName(ing)] = probe.CountCarried(ing);
            }
        }

        void SubscribeToCarriedInventories()
        {
            foreach (var inv in probe.CarriedInventories())
            {
                if (subscribed.Add(inv)) inv.SlotModified += OnSlotModified;
            }
        }

        void Unsubscribe()
        {
            foreach (var inv in subscribed) inv.SlotModified -= OnSlotModified;
            subscribed.Clear();
            watchedRecipe = null;
            lastCounts.Clear();
        }

        void OnSlotModified(int slotId)
        {
            if (watchedRecipe == null) return;

            // Backpack slots can appear after login (equipping a bag adds an inventory), so
            // re-scan rather than assuming the login-time set is final.
            SubscribeToCarriedInventories();

            // Re-counting per ingredient walks every carried slot again, so this is
            // O(ingredients x slots) per event. Fine for one watched recipe; the HUD's merged
            // totals across every pin will need a single inventory pass building a code->count
            // map instead. Event-driven is the requirement (spec §4) — this shape is not.

            foreach (var (ing, qty) in probe.ConsumedIngredients(watchedRecipe))
            {
                string key = probe.DisplayName(ing);
                int have = probe.CountCarried(ing);

                bool known = lastCounts.TryGetValue(key, out int previous);
                if (known && previous == have) continue;
                lastCounts[key] = have;

                // An ingredient we have never counted before is a baseline being recorded,
                // not a change the player caused — recording it silently keeps the first
                // pickup after a probe from reporting every ingredient at once.
                if (!known) continue;

                capi.ShowChatMessage($"Tallybook: {probe.DisplayName(ing)} {have}/{qty} {StatusMark(have, qty)}");
            }
        }

        public override void Dispose()
        {
            if (capi != null) Unsubscribe();
            base.Dispose();
        }
    }
}
