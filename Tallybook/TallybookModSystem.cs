using System;
using System.Collections.Generic;
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
        GridRecipe watchedRecipe;
        Dictionary<string, int> lastCounts = new Dictionary<string, int>();
        readonly HashSet<IInventory> subscribed = new HashSet<IInventory>();

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            probe = new RecipeProbe(api);

            api.ChatCommands.Create("tallybook")
                .WithDescription("Probe grid recipes for an item and show carried-inventory counts")
                .WithExamples("/tallybook spile", "/tallybook off")
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
            var query = (args.ArgCount > 0 ? args[0] as string : null)?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                return TextCommandResult.Success(
                    $"Tallybook probe. Recipe registry holds {capi.World?.GridRecipes?.Count ?? 0} grid recipes. " +
                    "Usage: /tallybook <part of an item code>, or /tallybook off to stop watching.");
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

            // Watch the first match; the rest are listed so multi-recipe items (the §3 recipe
            // picker case) are visible from the start rather than a later surprise.
            watchedRecipe = recipes[0];
            lastCounts.Clear();
            SubscribeToCarriedInventories();
            SeedBaseline(watchedRecipe);

            var sb = new StringBuilder();
            sb.AppendLine($"Tallybook: {recipes.Count} recipe(s) matching '{query}'. Watching the first:");
            sb.Append(DescribeRecipe(watchedRecipe));

            if (recipes.Count > 1)
            {
                sb.AppendLine("Other recipes producing a match:");
                for (int i = 1; i < recipes.Count; i++)
                {
                    var code = recipes[i].Output?.ResolvedItemStack?.Collectible?.Code;
                    sb.AppendLine($"  [{i}] {code} x{probe.OutputQuantity(recipes[i])}");
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
