using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>
    /// One ingredient row: a quantity plus every item that can satisfy it.
    ///
    /// The list of acceptable items exists because the game pre-expands wildcard recipes.
    /// A recipe written as "plank-*" does not stay one recipe in the registry — it becomes one
    /// concrete recipe per wood. Collapsing those back into a single row is what lets the list
    /// say "Board (any wood) 20/7" instead of naming one arbitrary wood and claiming 0/7 while
    /// the player is carrying twenty perfectly good boards of a different tree.
    /// </summary>
    public class Requirement
    {
        public string DisplayName;
        public int Quantity;
        public bool IsTool;

        /// <summary>First ingredient seen for this row, used for naming.</summary>
        public CraftingRecipeIngredient Sample;

        /// <summary>The recipe author's word for what varies ("wood"), taken from the
        /// ingredient's name field. Survives wildcard expansion, so it is how a collapsed row
        /// can still say "any wood" rather than the anonymous "any".</summary>
        public string VariantLabel;

        /// <summary>Exact-match codes, held as a set so counting stays cheap when a
        /// requirement accepts a hundred wood variants.</summary>
        public HashSet<string> ExactCodes = new HashSet<string>();

        /// <summary>Matchers that are not plain code equality (wildcard, regex, tags) and must
        /// be asked one at a time.</summary>
        public List<CraftingRecipeIngredient> OtherMatchers = new List<CraftingRecipeIngredient>();

        public int VariantCount => ExactCodes.Count + OtherMatchers.Count;
    }

    /// <summary>
    /// Recipes that produce the same output in the same grid pattern, differing only by which
    /// variant of an ingredient they use. This — not the raw registry entry — is what a player
    /// means by "a recipe", and what spec §3's picker should be choosing between.
    /// </summary>
    public class RecipeVariantGroup
    {
        public string OutputCode;
        public string OutputName;
        public int OutputQuantity;
        public ItemStack OutputStack;
        public string Pattern;
        public int Width;
        public int Height;

        /// <summary>How many distinct grid arrangements make this item. Shown as a count only —
        /// the handbook is where the arrangements themselves belong.</summary>
        public int LayoutCount;

        public List<GridRecipe> Recipes = new List<GridRecipe>();
    }

    /// <summary>
    /// Build-order step 1 (spec §10): read-only validation of the two API unknowns —
    /// client-side recipe registry access, and live inventory counting. Nothing here mutates
    /// game state; it only reads registries and inventories and reports what it finds.
    ///
    /// This is also the seed of the §8 normalization layer. Everything a later recipe system
    /// (smithing, clay forming, barrel, cooking) will need is expressed here in grid-recipe
    /// terms first: a requirement list of (matchers, quantity), a separate tool list, and an
    /// output quantity. Keep that shape when generalizing.
    /// </summary>
    public class RecipeProbe
    {
        readonly ICoreClientAPI capi;

        /// <summary>
        /// Output code -> recipes producing it. A modded client carries ~30,000 grid recipes
        /// and rescanning all of them per lookup is the kind of cost that is invisible in a
        /// chat command and ruinous in a HUD that refreshes on every inventory change. Built
        /// once, then reused.
        /// </summary>
        Dictionary<string, List<GridRecipe>> byOutput;

        public RecipeProbe(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        /// <summary>
        /// Drop the index. Recipes arrive from the server on join, so a different server or
        /// world means a different recipe set.
        /// </summary>
        public void InvalidateIndex() => byOutput = null;

        public int IndexedRecipeCount => capi.World?.GridRecipes?.Count ?? 0;

        void EnsureIndex()
        {
            if (byOutput != null) return;

            byOutput = new Dictionary<string, List<GridRecipe>>();
            var all = capi.World?.GridRecipes;
            if (all == null) return;

            foreach (var r in all)
            {
                string code = OutputCode(r);
                if (code == "?") continue;
                if (!byOutput.TryGetValue(code, out var list))
                {
                    list = new List<GridRecipe>();
                    byOutput[code] = list;
                }
                list.Add(r);
            }
        }

        /// <summary>
        /// Recipes for one specific item. This is the lookup the product actually needs: the
        /// handbook hands over the exact stack the player was looking at, so there is nothing
        /// to search for. Returns null when nothing crafts it — a valid state, not an error
        /// (loot-only and trader-only items are still worth pinning, spec §11).
        /// </summary>
        public RecipeVariantGroup FindGroupFor(ItemStack stack)
        {
            EnsureIndex();

            var code = stack?.Collectible?.Code?.ToShortString();
            if (code == null || !byOutput.TryGetValue(code, out var recipes)) return null;

            return BuildGroups(recipes).FirstOrDefault();
        }

        /// <summary>
        /// Substring search over output codes. Kept for the diagnostic command only — the
        /// player-facing path is FindGroupFor, driven by a handbook click.
        /// </summary>
        public List<RecipeVariantGroup> FindVariantGroups(string codePart)
        {
            EnsureIndex();

            return BuildGroups(byOutput.Where(kv => kv.Key.Contains(codePart)).SelectMany(kv => kv.Value));
        }

        List<RecipeVariantGroup> BuildGroups(IEnumerable<GridRecipe> recipes)
        {
            return recipes
                // One group per item, matching how a player thinks about it — and how the
                // handbook presents it. Grid layout is deliberately NOT part of the key: an
                // item craftable in four arrangements is still one thing to go shopping for,
                // and the handbook is the right place to see the arrangements.
                //
                // RecipeGroup stays in the key because the game documents it as the author's
                // way to split handbook previews apart; two recipes an author deliberately
                // separated should not be silently recombined here.
                .GroupBy(r => $"{OutputCode(r)}|{r.RecipeGroup}")
                .Select(g =>
                {
                    // Represent the group by its cheapest layout. Layouts can want very
                    // different amounts (the bookshelf ones need 7, 8, 8 and 5 planks), and a
                    // shopping list has to commit to one number. The smallest is the honest
                    // floor: gather this much and you can definitely build one. Anything larger
                    // would send the player after materials they may not need.
                    var representative = g.OrderBy(TotalIngredientCount).First();
                    return new RecipeVariantGroup
                    {
                        OutputCode = OutputCode(representative),
                        OutputName = representative.Output?.ResolvedItemStack?.GetName() ?? "?",
                        OutputQuantity = OutputQuantity(representative),
                        OutputStack = representative.Output?.ResolvedItemStack,
                        Pattern = representative.IngredientPattern,
                        Width = representative.Width,
                        Height = representative.Height,
                        LayoutCount = g.Select(r => r.IngredientPattern).Distinct().Count(),
                        // Representative first: BuildRequirements takes its shape as the row
                        // template and merges only same-shaped variants into it.
                        Recipes = g.OrderBy(TotalIngredientCount).ToList()
                    };
                })
                .OrderByDescending(g => g.Recipes.Count)
                .ToList();
        }

        /// <summary>
        /// Consumed ingredients for one recipe, merged by matcher so a recipe listing the same
        /// item in seven grid cells reports "7" once rather than "1" seven times.
        ///
        /// ResolvedIngredients is a grid-shaped array and is sparse — empty cells are null.
        /// Tools are excluded here and reported separately: per spec §4 they are presence
        /// checks, never counted against quantity.
        /// </summary>
        public List<(CraftingRecipeIngredient Ingredient, int Quantity)> ConsumedIngredients(GridRecipe recipe)
            => MergeCells(recipe, wantTools: false);

        public List<(CraftingRecipeIngredient Ingredient, int Quantity)> ToolCells(GridRecipe recipe)
            => MergeCells(recipe, wantTools: true);

        List<(CraftingRecipeIngredient, int)> MergeCells(GridRecipe recipe, bool wantTools)
        {
            var merged = new List<(CraftingRecipeIngredient, int)>();
            if (recipe?.ResolvedIngredients == null) return merged;

            foreach (var ing in recipe.ResolvedIngredients)
            {
                if (ing == null || ing.IsTool != wantTools) continue;

                string key = MatcherKey(ing);
                int idx = merged.FindIndex(m => MatcherKey(m.Item1) == key);
                if (idx >= 0) merged[idx] = (merged[idx].Item1, merged[idx].Item2 + ing.Quantity);
                else merged.Add((ing, ing.Quantity));
            }
            return merged;
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
        /// Collapse a variant group into one requirement per ingredient row, each accepting
        /// every variant the group's recipes accept.
        /// </summary>
        public List<Requirement> BuildRequirements(RecipeVariantGroup group, bool tools = false)
        {
            var reqs = new List<Requirement>();
            if (group?.Recipes == null || group.Recipes.Count == 0) return reqs;

            var baseCells = tools ? ToolCells(group.Recipes[0]) : ConsumedIngredients(group.Recipes[0]);
            foreach (var (ing, qty) in baseCells)
            {
                var req = new Requirement { Quantity = qty, IsTool = tools };
                AddMatcher(req, ing);
                reqs.Add(req);
            }

            foreach (var recipe in group.Recipes.Skip(1))
            {
                var cells = tools ? ToolCells(recipe) : ConsumedIngredients(recipe);
                // Only merge when the shape lines up. A mismatch means these are not really the
                // same recipe in different clothes, and guessing an alignment would silently
                // attach a variant to the wrong ingredient row.
                if (cells.Count != reqs.Count) continue;

                for (int i = 0; i < cells.Count; i++)
                {
                    if (cells[i].Quantity != reqs[i].Quantity) continue;
                    AddMatcher(reqs[i], cells[i].Ingredient);
                }
            }

            foreach (var req in reqs) req.DisplayName = BuildDisplayName(req);
            return reqs;
        }

        void AddMatcher(Requirement req, CraftingRecipeIngredient ing)
        {
            if (req.Sample == null) req.Sample = ing;
            if (string.IsNullOrEmpty(req.VariantLabel)) req.VariantLabel = ing.Name;

            if (ing.MatchingType == EnumRecipeMatchType.Exact)
            {
                var code = ing.ResolvedItemStack?.Collectible?.Code?.ToShortString() ?? ing.Code?.ToShortString();
                if (code != null) { req.ExactCodes.Add(code); return; }
            }
            req.OtherMatchers.Add(ing);
        }

        string BuildDisplayName(Requirement req)
        {
            string name = req.Sample != null ? IngredientName(req.Sample) : NameForCode(req.ExactCodes.FirstOrDefault());

            if (req.VariantCount <= 1) return name;

            // "Board (Aged oak)" -> "Board", so the row reads as the whole set rather than
            // naming one arbitrary member of it (spec §8).
            int paren = name.IndexOf(" (");
            if (paren > 0) name = name.Substring(0, paren);

            string what = req.VariantLabel;
            return string.IsNullOrEmpty(what)
                ? $"{name} (any, {req.VariantCount} variants)"
                : $"{name} (any {what}, {req.VariantCount} variants)";
        }

        string NameForCode(string shortCode)
        {
            if (shortCode == null) return "?";
            var loc = new AssetLocation(shortCode);
            var item = capi.World.GetItem(loc);
            if (item != null) return new ItemStack(item).GetName();
            var block = capi.World.GetBlock(loc);
            if (block != null) return new ItemStack(block).GetName();
            return shortCode;
        }

        string IngredientName(CraftingRecipeIngredient ing)
        {
            var stack = ing.ResolvedItemStack;
            if (stack != null) return stack.GetName();
            return ing.Code?.ToShortString() ?? "?";
        }

        /// <summary>
        /// How many of this requirement the player is carrying. Spec §4: hotbar + backpacks
        /// only — carried inventory, not nearby containers. Every accepted variant counts
        /// collectively, and each slot is counted at most once even if several matchers accept
        /// it.
        /// </summary>
        public int CountCarried(Requirement req)
        {
            int total = 0;
            foreach (var inv in CarriedInventories())
            {
                foreach (var slot in inv)
                {
                    var stack = slot?.Itemstack;
                    if (stack?.Collectible?.Code == null) continue;

                    if (req.ExactCodes.Contains(stack.Collectible.Code.ToShortString()))
                    {
                        total += stack.StackSize;
                        continue;
                    }
                    foreach (var m in req.OtherMatchers)
                    {
                        if (Matches(m, stack)) { total += stack.StackSize; break; }
                    }
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

        public string OutputCode(GridRecipe recipe)
            => recipe?.Output?.ResolvedItemStack?.Collectible?.Code?.ToShortString() ?? "?";

        /// <summary>
        /// How many of an ingredient are needed to end up with <paramref name="wanted"/> of the
        /// output. Crafts are whole: wanting 3 planks from a recipe that yields 4 still costs a
        /// full craft's ingredients (spec §2a).
        /// </summary>
        public static int NeededFor(Requirement req, int wanted, int outputQuantity)
        {
            if (outputQuantity < 1) outputQuantity = 1;
            int crafts = (wanted + outputQuantity - 1) / outputQuantity;
            return req.Quantity * Math.Max(1, crafts);
        }

        /// <summary>Total items consumed by a recipe, used to pick a group's cheapest layout.</summary>
        int TotalIngredientCount(GridRecipe recipe)
            => ConsumedIngredients(recipe).Sum(c => c.Quantity);

        /// <summary>
        /// A clickable handbook link for chat, e.g. "handbook://block-bookshelf". The game's own
        /// assets use a bare path for the default domain and "domain:path" otherwise. Returns
        /// null when there is nothing to link to, so callers can omit the link rather than
        /// print a dead one.
        /// </summary>
        public string HandbookLink(ItemStack stack)
        {
            var code = stack?.Collectible?.Code;
            if (code == null) return null;

            string kind = stack.Class == EnumItemClass.Block ? "block" : "item";
            string path = code.Domain == "game" ? code.Path : $"{code.Domain}:{code.Path}";
            return $"handbook://{kind}-{path}";
        }

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
