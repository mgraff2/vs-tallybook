using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>
    /// Build-order step 1 (spec §10): read-only validation of the two API unknowns —
    /// client-side recipe registry access, and live inventory counting. Nothing here mutates
    /// game state; it only reads registries and inventories and reports what it finds.
    ///
    /// This is also the seed of the §8 normalization layer. Everything a later recipe system
    /// (smithing, clay forming, barrel, cooking) will need is expressed here in grid-recipe
    /// terms first: an ingredient list of (matcher, quantity), a separate tool list, and an
    /// output quantity. Keep that shape when generalizing.
    /// </summary>
    public class RecipeProbe
    {
        readonly ICoreClientAPI capi;

        public RecipeProbe(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        /// <summary>
        /// Grid recipes whose output collectible code contains <paramref name="codePart"/>.
        /// Substring rather than exact match so the command is usable by hand ("spile"
        /// finding "game:spile-steel").
        /// </summary>
        public List<GridRecipe> FindRecipesProducing(string codePart)
        {
            var all = capi.World?.GridRecipes;
            if (all == null) return new List<GridRecipe>();

            return all.Where(r =>
            {
                var code = r?.Output?.ResolvedItemStack?.Collectible?.Code;
                return code != null && code.ToShortString().Contains(codePart);
            }).ToList();
        }

        /// <summary>
        /// Consumed ingredients for a recipe, merged by matcher so a recipe listing the same
        /// item in four grid cells reports "4" once rather than "1" four times.
        ///
        /// ResolvedIngredients is a grid-shaped array and is sparse — empty cells are null.
        /// Tools are excluded here and reported separately: per spec §4 they are presence
        /// checks, never counted against quantity.
        /// </summary>
        public List<(CraftingRecipeIngredient Ingredient, int Quantity)> ConsumedIngredients(GridRecipe recipe)
        {
            var merged = new List<(CraftingRecipeIngredient, int)>();
            if (recipe?.ResolvedIngredients == null) return merged;

            foreach (var ing in recipe.ResolvedIngredients)
            {
                if (ing == null || ing.IsTool) continue;

                string key = MatcherKey(ing);
                int idx = merged.FindIndex(m => MatcherKey(m.Item1) == key);
                if (idx >= 0) merged[idx] = (merged[idx].Item1, merged[idx].Item2 + ing.Quantity);
                else merged.Add((ing, ing.Quantity));
            }
            return merged;
        }

        public List<CraftingRecipeIngredient> Tools(GridRecipe recipe)
        {
            if (recipe?.ResolvedIngredients == null) return new List<CraftingRecipeIngredient>();
            return recipe.ResolvedIngredients
                .Where(i => i != null && i.IsTool)
                .GroupBy(MatcherKey)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Identity of an ingredient's matcher, for merging duplicate grid cells. Includes the
        /// match type and tag condition, not just the code: 1.22 matches by Exact, Wildcard,
        /// NamedWildcard, AdvancedWildcard, Regex or TagsOnly, and two ingredients sharing a
        /// code under different match types accept different things.
        /// </summary>
        static string MatcherKey(CraftingRecipeIngredient ing)
            => $"{ing.Type}|{ing.MatchingType}|{ing.Code}|{ing.Tags}";

        /// <summary>
        /// How many of this ingredient the player is carrying. Spec §4: hotbar + backpacks
        /// only — carried inventory, not nearby containers. Non-exact ingredients ("any
        /// plank") count every qualifying item collectively.
        /// </summary>
        public int CountCarried(CraftingRecipeIngredient ing)
        {
            int total = 0;
            foreach (var inv in CarriedInventories())
            {
                foreach (var slot in inv)
                {
                    var stack = slot?.Itemstack;
                    if (stack != null && Matches(ing, stack)) total += stack.StackSize;
                }
            }
            return total;
        }

        /// <summary>
        /// Delegates to the game's own ingredient matcher rather than reimplementing it. That
        /// is deliberate and worth keeping: it covers all six EnumRecipeMatchType modes and
        /// the tag system for free, and it can never disagree with what the crafting grid
        /// actually accepts — a mod that says "you have the materials" when the grid refuses
        /// them is worse than useless.
        ///
        /// checkStackSize is false because we sum across slots ourselves; asking whether one
        /// slot alone satisfies the whole requirement would undercount every split stack.
        /// </summary>
        public bool Matches(CraftingRecipeIngredient ing, ItemStack stack)
            => stack?.Collectible != null && ing.SatisfiesAsIngredient(stack, false);

        public IEnumerable<IInventory> CarriedInventories()
        {
            var mgr = capi.World?.Player?.InventoryManager;
            if (mgr?.Inventories == null) yield break;

            foreach (var inv in mgr.Inventories.Values)
            {
                if (inv == null) continue;
                if (inv.ClassName == GlobalConstants.hotBarInvClassName ||
                    inv.ClassName == GlobalConstants.backpackInvClassName)
                {
                    yield return inv;
                }
            }
        }

        public string DisplayName(CraftingRecipeIngredient ing)
        {
            if (ing.MatchingType != EnumRecipeMatchType.Exact)
            {
                // Spec §8: show the wildcard's friendly name, not a resolved example — the row
                // stands for the whole set of qualifying items, and naming one of them reads
                // as "fetch this specific thing".
                var label = ing.Name ?? ing.Code?.ToShortString() ?? ing.Tags.ToString() ?? "?";
                return $"{label} (any)";
            }

            var stack = ing.ResolvedItemStack;
            if (stack != null) return stack.GetName();
            return ing.Code?.ToString() ?? "?";
        }

        public string OutputCode(GridRecipe recipe)
            => recipe?.Output?.ResolvedItemStack?.Collectible?.Code?.ToShortString() ?? "?";

        /// <summary>Output count per craft — the divisor in the §2a deficit math.</summary>
        public int OutputQuantity(GridRecipe recipe)
        {
            var outp = recipe?.Output;
            if (outp == null) return 1;
            // Guarded so a zero here can never become a divide-by-zero in the expansion math.
            return outp.Quantity > 0 ? outp.Quantity : 1;
        }
    }
}
