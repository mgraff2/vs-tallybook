using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

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

        /// <summary>Tool rows only: present anywhere in carried inventory. Updated on recount.
        /// Presence-checked, never counted against quantity (spec §4).</summary>
        public bool Present;

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

        /// <summary>
        /// Set only on a pin's *self* requirement — "the pinned item itself", the gather
        /// target for items you find rather than craft. Counting then matches the exact
        /// handbook page rather than the bare code, so owning a 5-plank bookshelf never
        /// reports the 8-plank one as had.
        /// </summary>
        public string SelfPageCode;

        public int VariantCount => ExactCodes.Count + OtherMatchers.Count;

        /// <summary>How many real items actually satisfy this row. For exact codes that is
        /// just how many there are; for a bare wildcard ("plank-*", which the game does NOT
        /// expand because it carries no name) it is what the world turned out to contain.
        /// Uncapped, unlike the icon samples.</summary>
        public int MatchedVariants;

        string key;
        List<ItemStack> sampleStacks;

        /// <summary>
        /// One representative stack per accepted variant, for icon display (capped — an icon
        /// slideshow needs examples, not an exhaustive census). Resolved lazily and cached:
        /// the accepted codes cannot change within a world session.
        /// </summary>
        /// <summary>Use this exact stack as the row's icon (self requirements know precisely
        /// which variant they mean; resolving from a code would show the base variant).</summary>
        public void PresetSampleStack(ItemStack stack)
        {
            if (stack != null) sampleStacks = new List<ItemStack> { stack };
        }

        /// <summary>Icons worked out by asking the world what a wildcard accepts.</summary>
        public void PresetSampleStacks(List<ItemStack> stacks)
        {
            if (stacks != null && stacks.Count > 0) sampleStacks = stacks;
        }

        public List<ItemStack> SampleStacks(IWorldAccessor world, int max = 30)
        {
            if (sampleStacks != null) return sampleStacks;

            var stacks = new List<ItemStack>();
            foreach (var m in OtherMatchers)
            {
                if (m.ResolvedItemStack != null && stacks.Count < max) stacks.Add(m.ResolvedItemStack);
            }
            foreach (var code in ExactCodes.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (stacks.Count >= max) break;
                var loc = new AssetLocation(code);
                var item = world.GetItem(loc);
                if (item != null) { stacks.Add(new ItemStack(item)); continue; }
                var block = world.GetBlock(loc);
                if (block != null) stacks.Add(new ItemStack(block));
            }
            return sampleStacks = stacks;
        }

        /// <summary>
        /// Stable identity across sessions and recompute passes: sorted accepted codes plus
        /// non-exact matcher descriptors. Used to merge HUD rows across pins and to re-attach
        /// persisted expansion state after recipes are re-resolved.
        /// </summary>
        public string Key
        {
            get
            {
                if (key != null) return key;
                if (SelfPageCode != null) return key = $"S|{SelfPageCode}";

                var codes = string.Join(",", ExactCodes.OrderBy(c => c, StringComparer.Ordinal));
                var others = string.Join(",", OtherMatchers
                    .Select(m => $"{m.Type}:{m.MatchingType}:{m.Code}")
                    .OrderBy(s => s, StringComparer.Ordinal));
                key = $"{(IsTool ? "T" : "I")}|{codes}|{others}";
                return key;
            }
        }
    }

    /// <summary>
    /// One pass over carried inventory, answering "how many of X am I holding" for any number
    /// of requirements without rescanning slots. Rebuild per recount, throw away after.
    ///
    /// Slots collapse to one entry per collectible code (sample stack + summed size); matcher
    /// evaluation then runs per distinct code rather than per slot, which is what keeps a
    /// full-list recount cheap on a 40-slot inventory with a long pin list.
    /// </summary>
    public class InventorySnapshot
    {
        /// <summary>
        /// Keyed by handbook page code, not bare item code, so attribute-distinct variants
        /// stay countable apart — that is what lets a "gather this exact item" row be honest.
        /// The bare code rides along because ingredient matching is code-level.
        /// </summary>
        readonly Dictionary<string, (ItemStack Sample, int Total, string Code)> byPage
            = new Dictionary<string, (ItemStack, int, string)>();

        public InventorySnapshot(IEnumerable<IInventory> inventories, IEnumerable<ItemStack> alsoCount = null)
        {
            foreach (var inv in inventories)
            {
                foreach (var slot in inv) Add(slot?.Itemstack);
            }
            if (alsoCount != null)
            {
                foreach (var stack in alsoCount) Add(stack);
            }
        }

        void Add(ItemStack stack)
        {
            var code = stack?.Collectible?.Code?.ToShortString();
            if (code == null) return;
            string page = RecipeProbe.PageCode(stack) ?? code;

            byPage[page] = byPage.TryGetValue(page, out var cur)
                ? (cur.Sample, cur.Total + stack.StackSize, cur.Code)
                : (stack, stack.StackSize, code);
        }

        public int Count(Requirement req)
        {
            // Self requirements name one exact page — a single lookup, and no chance of
            // another variant of the same code counting toward it.
            if (req.SelfPageCode != null)
                return byPage.TryGetValue(req.SelfPageCode, out var self) ? self.Total : 0;

            int total = 0;
            foreach (var entry in byPage)
            {
                if (req.ExactCodes.Contains(entry.Value.Code))
                {
                    total += entry.Value.Total;
                    continue;
                }
                foreach (var m in req.OtherMatchers)
                {
                    if (m.SatisfiesAsIngredient(entry.Value.Sample, false)) { total += entry.Value.Total; break; }
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Recipes that produce the same output in the same grid pattern, differing only by which
    /// variant of an ingredient they use. This — not the raw registry entry — is what a player
    /// means by "a recipe", and what spec §3's picker should be choosing between.
    /// </summary>
    public class RecipeVariantGroup
    {
        public string OutputCode;

        /// <summary>Output identity at handbook-page granularity (code plus distinguishing
        /// attributes). Two recipes can share an output code yet produce different items —
        /// the four bookshelf shapes differ only in output attributes — and each of those is
        /// its own handbook page, so it must be its own group.</summary>
        public string OutputPageCode;

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

        /// <summary>Stable identity for persistence: which recipe choice the player made.
        /// Uses the page code, not the bare output code, so choices for two attribute-distinct
        /// variants of one code never collide.</summary>
        public string Signature => $"{OutputPageCode}|{Pattern}|{Width}x{Height}";

        /// <summary>Short label for the recipe-choice cycler.</summary>
        public string ChoiceLabel(int perCraftTotal)
            => $"{OutputName} x{OutputQuantity} ({perCraftTotal} items/craft)";

        /// <summary>
        /// What this recipe is made *of*, for choosing between alternatives. "1 of 2" says
        /// nothing about which one you mean to gather; "Boards, resin" does. Filled in when
        /// the group is built, since the requirements are worked out there.
        /// </summary>
        public string Materials;
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
                if (ConsumesOwnOutput(r, code)) continue;
                if (!byOutput.TryGetValue(code, out var list))
                {
                    list = new List<GridRecipe>();
                    byOutput[code] = list;
                }
                list.Add(r);
            }
        }

        /// <summary>
        /// True for recipes that consume the very item they produce: slab placement-mode
        /// recipes (1 glass slab → 1 glass slab, rotated), chiseled-block combining, armor
        /// repair. Those convert an item the player already has — they can never help make
        /// one from scratch, so they are excluded from the index entirely. Left in, one of
        /// them can win the cheapest-representative choice and make the list claim "to craft
        /// a glass slab you need a glass slab" — while hiding the real recipe's saw (found by
        /// Mark: the glass slab pin showed no tool). Exact code equality only: a recipe
        /// turning one variant into another (dyeing white wool red) produces something the
        /// player doesn't have yet, and stays.
        /// </summary>
        bool ConsumesOwnOutput(GridRecipe r, string outputCode)
        {
            foreach (var (ing, _) in ConsumedIngredients(r))
            {
                var code = ing.ResolvedItemStack?.Collectible?.Code?.ToShortString()
                           ?? ing.Code?.ToShortString();
                if (code == outputCode) return true;
            }
            return false;
        }

        /// <summary>
        /// Recipe choices for one specific item. This is the lookup the product actually needs:
        /// the handbook hands over the exact stack the player was looking at, so there is
        /// nothing to search for. Empty when nothing crafts it — a valid state, not an error
        /// (loot-only and trader-only items are still worth pinning, spec §11).
        /// </summary>
        public List<RecipeVariantGroup> FindGroupsFor(string shortCode)
        {
            EnsureIndex();

            if (shortCode == null || !byOutput.TryGetValue(shortCode, out var recipes))
                return new List<RecipeVariantGroup>();

            return BuildGroups(recipes);
        }

        /// <summary>
        /// Recipe choices for the exact stack the handbook page showed. When any group's
        /// output matches the stack's page code, only those groups are returned — viewing the
        /// 8-plank bookshelf must never pin the 5-plank one, they are different blocks that
        /// happen to share a code. When none match (the page's stack carries attributes no
        /// recipe output has), all groups for the code are the honest fallback: some recipe
        /// beats "no recipe known" for an item that plainly has one.
        /// </summary>
        public List<RecipeVariantGroup> FindGroupsFor(ItemStack stack)
        {
            var all = FindGroupsFor(stack?.Collectible?.Code?.ToShortString());
            string page = PageCode(stack);
            if (page == null) return all;

            var exact = all.Where(g => g.OutputPageCode == page).ToList();
            return exact.Count > 0 ? exact : all;
        }

        /// <summary>
        /// Stack identity at handbook-page granularity, via the game's own
        /// GuiHandbookItemStackPage.PageCodeForStack — code plus the attributes that make
        /// variants distinct pages, minus attributes the game says never matter
        /// (GlobalConstants.IgnoredStackAttributes). Delegating means "pin exactly the page
        /// the player clicked" stays true even if the handbook's notion of a page changes.
        /// </summary>
        public static string PageCode(ItemStack stack)
        {
            if (stack?.Collectible?.Code == null) return null;
            try { return GuiHandbookItemStackPage.PageCodeForStack(stack); }
            catch { return $"{stack.Class}-{stack.Collectible.Code.ToShortString()}"; }
        }

        /// <summary>
        /// The "I want this item itself" requirement behind every pin — the gather target for
        /// things you find rather than craft (64 low-quality soil, 10 small hides for a
        /// villager), and the have-count for things you do craft. Matches the exact page, so
        /// it never counts a sibling variant.
        /// </summary>
        public Requirement RequirementForStack(ItemStack stack)
        {
            if (stack?.Collectible?.Code == null) return null;

            var req = new Requirement
            {
                Quantity = 1,
                DisplayName = stack.GetName(),
                SelfPageCode = PageCode(stack)
            };
            req.ExactCodes.Add(stack.Collectible.Code.ToShortString());
            req.PresetSampleStack(stack);
            return req;
        }

        /// <summary>Stack attributes as a JSON token for persistence; null when empty.</summary>
        public static string AttributesJson(ItemStack stack)
        {
            var json = (stack?.Attributes as TreeAttribute)?.ToJsonToken();
            return string.IsNullOrEmpty(json) || json == "{}" ? null : json;
        }

        /// <summary>
        /// Recipe choices for expanding an ingredient row (spec §2a). The row may accept many
        /// variants ("Board, any wood"), each with its own registry recipes; here those are
        /// re-collapsed across outputs so the choice reads "Log (any wood) → Board" rather than
        /// one choice per tree. Recipes only merge when their shape and quantities line up, so
        /// a mod wood with a different plank ratio stays a separate, honest choice.
        /// </summary>
        public List<RecipeVariantGroup> FindExpansionGroups(Requirement req)
        {
            EnsureIndex();

            var recipes = req.ExactCodes
                .Where(code => byOutput.ContainsKey(code))
                .SelectMany(code => byOutput[code]);

            return BuildGroups(recipes, collapseOutputs: true);
        }

        List<RecipeVariantGroup> BuildGroups(IEnumerable<GridRecipe> recipes, bool collapseOutputs = false)
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
                //
                // collapseOutputs (expansion lookups): the outputs themselves are variants of
                // the same ingredient row ("Board" in every wood), so grouping by output would
                // recreate the per-wood explosion one level down. Shape+quantity gates in
                // BuildRequirements keep genuinely different recipes from merging.
                //
                // Output identity is the page code, not the bare output code: recipes sharing
                // a code can still produce different items distinguished only by output
                // attributes (the four bookshelf shapes), and those are separate handbook
                // pages — so they must be separate groups here too.
                //
                // MaterialSignature is in the key so that "these are two different recipes"
                // is decided by what they actually take, not by whether an author remembered
                // to set RecipeGroup. Mods that re-add a vanilla item behind a schematic
                // (Better Ruins, the airship mod) frequently do not set it, and without this
                // their recipe lands in the same group as vanilla's, loses the cheapest-layout
                // contest, and is silently dropped by BuildRequirements' shape gate — the
                // player is never told the alternative exists.
                //
                // It is deliberately absent from the collapsed key, and that asymmetry is the
                // whole point of the two modes. Above, output identity is fixed to one page,
                // so differing materials really do mean a different recipe. Collapsed, the
                // outputs are themselves variants of one ingredient row — a chute section in
                // twenty metals — and vanilla authors those per variant rather than with a
                // named wildcard, so each is made of its own metal plate and every one would
                // score a different signature. That produced twenty identical-looking "ways
                // to make Chute Section" (found by Mark, 0.3.4). Pattern and size already
                // separate genuinely different recipes here without splitting variants apart.
                .GroupBy(r => collapseOutputs
                    ? $"{r.RecipeGroup}|{r.IngredientPattern}|{r.Width}x{r.Height}|{OutputQuantity(r)}"
                    : $"{OutputIdentity(r)}|{r.RecipeGroup}|{MaterialSignature(r)}")
                .Select(g =>
                {
                    // Represent the group by its cheapest layout. Layouts producing the same
                    // item can still want different amounts, and a shopping list has to commit
                    // to one number. The smallest is the honest floor: gather this much and
                    // you can definitely build one. Anything larger would send the player
                    // after materials they may not need. (Layouts producing attribute-distinct
                    // items — the bookshelf shapes — never reach this point as one group; the
                    // page-code key above already split them.)
                    var representative = g.OrderBy(TotalIngredientCount).First();
                    return new RecipeVariantGroup
                    {
                        OutputCode = OutputCode(representative),
                        OutputPageCode = OutputIdentity(representative),
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
        /// Every item this client knows more than one genuinely different way to make.
        ///
        /// Exists because "which items actually offer a choice" is a question about the recipe
        /// set the *server* sent, so it cannot be answered by reading mod zips on disk — and
        /// answering it by hand does not survive the next mod being installed. It also fails
        /// loudly if grouping ever regresses into per-variant explosion: a healthy world lists
        /// a few dozen items here, not thousands.
        ///
        /// Walks the whole index, so it is a chat-command diagnostic and nothing else.
        /// </summary>
        public List<string> MultiRecipeReport(int max = 25)
        {
            EnsureIndex();

            var rows = new List<(string Name, int Groups, string Detail)>();
            foreach (var kv in byOutput)
            {
                // One recipe can never be a choice — skip before the expensive part.
                if (kv.Value.Count < 2) continue;

                var groups = BuildGroups(kv.Value);
                if (groups.Count < 2) continue;

                foreach (var g in groups.Take(3)) BuildRequirements(g);
                rows.Add((groups[0].OutputName, groups.Count,
                    string.Join("   /   ", groups.Take(3).Select(g => g.Materials))));
            }

            var lines = new List<string>
            {
                $"{IndexedRecipeCount} recipes, {byOutput.Count} craftable items, "
                + $"{rows.Count} with more than one recipe."
            };
            foreach (var row in rows.OrderByDescending(r => r.Groups).ThenBy(r => r.Name).Take(max))
                lines.Add($"{row.Name} ({row.Groups}): {row.Detail}");

            if (rows.Count > max) lines.Add($"...and {rows.Count - max} more.");
            return lines;
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

        /// <summary>
        /// Is this ingredient kept rather than used up? A tool by the recipe's own flag, or one
        /// the recipe explicitly does not consume. Either wants *one*, present, however many
        /// times you craft.
        ///
        /// `ReturnedStack` is deliberately **not** included, though it looks like it belongs:
        /// it means the ingredient is consumed and something is handed back, which is often a
        /// *different, lesser* item — the hunter backpack takes a huge pelt and returns a small
        /// one. Treating that as a tool would say one huge pelt makes three backpacks.
        /// </summary>
        static bool IsKept(CraftingRecipeIngredient ing)
            => ing.IsTool || !ing.Consume;

        List<(CraftingRecipeIngredient, int)> MergeCells(GridRecipe recipe, bool wantTools)
        {
            var merged = new List<(CraftingRecipeIngredient, int)>();
            if (recipe?.ResolvedIngredients == null) return merged;

            foreach (var ing in recipe.ResolvedIngredients)
            {
                if (ing == null || IsKept(ing) != wantTools) continue;

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
        /// What a recipe is made of, blind to wildcard expansion — the test for "is this the
        /// same recipe or a genuinely different one".
        ///
        /// The trick is that an expanded ingredient always carries the author's name for what
        /// varies ("wood"), because carrying that name is *why* the game expanded it: a bare
        /// wildcard with no name is left unexpanded. So keying on the name rather than the
        /// concrete code collapses all thirty woods of one authored recipe into one token,
        /// while two recipes that really do want different materials keep different tokens.
        /// This cannot explode into per-variant groups the way keying on the code would.
        ///
        /// Kept ingredients (tools, schematics, anything consume:false) are included, without
        /// a quantity since one is all you ever need. They belong here because "craftable with
        /// a schematic you have to find" versus "craftable outright" is exactly the difference
        /// a player needs to be offered rather than have chosen for them.
        /// </summary>
        string MaterialSignature(GridRecipe recipe)
        {
            var parts = new List<string>();
            foreach (var (ing, qty) in ConsumedIngredients(recipe)) parts.Add($"I{qty}x{MaterialToken(ing)}");
            foreach (var (ing, _) in ToolCells(recipe)) parts.Add($"K{MaterialToken(ing)}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        /// <summary>Variant-blind identity of one ingredient: the author's word for what varies
        /// when there is one, else the matcher itself (a bare wildcard, regex or tag condition
        /// is already variant-blind, so its own key is the right token).</summary>
        static string MaterialToken(CraftingRecipeIngredient ing)
            => string.IsNullOrEmpty(ing.Name) ? MatcherKey(ing) : $"{ing.Type}|${ing.Name}";

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

            foreach (var req in reqs)
            {
                ResolveVariants(req);
                req.DisplayName = BuildDisplayName(req);
            }

            // Remember what this recipe takes, so a choice between recipes can be made on
            // what you would have to go and find rather than on a number.
            if (!tools && group.Materials == null)
            {
                // With quantities: the hunter backpack's recipes differ by *how many* pelts,
                // not by which, so a list of bare names makes four distinct recipes read as
                // four identical ones.
                var summary = string.Join(", ",
                    reqs.Select(r => $"{r.Quantity} × {StripVariants(r.DisplayName)}"));

                // And what it takes but does not use up. Two recipes can want the same
                // materials and differ only in demanding a schematic; leaving that out makes
                // the choice between them look like a choice between identical twins.
                var kept = BuildRequirements(group, tools: true)
                    .Select(r => StripVariants(r.DisplayName))
                    .ToList();
                if (kept.Count > 0) summary += $" — needs {string.Join(", ", kept)}";

                group.Materials = summary;
            }
            return reqs;
        }

        /// <summary>
        /// Find the real items behind a row that has no concrete codes of its own.
        ///
        /// A wildcard ingredient the game did not expand ("plank-*" with no name) leaves us a
        /// matcher and nothing to show: no name beyond "any suitable item", and no icon at
        /// all. Asking the world which collectibles the matcher accepts recovers both, and
        /// the answer cannot change within a session, so it is worked out once per row.
        ///
        /// The code is wildcard-matched before building a stack wherever possible — the block
        /// list runs to tens of thousands, and a string compare is far cheaper than
        /// constructing each one to ask properly.
        /// </summary>
        void ResolveVariants(Requirement req)
        {
            if (req.ExactCodes.Count > 0)
            {
                req.MatchedVariants = req.ExactCodes.Count;
                return;
            }

            var matcher = req.OtherMatchers.FirstOrDefault();
            if (matcher?.Code == null && matcher?.Tags == null) return;

            var samples = new List<ItemStack>();
            int found = 0;

            void Consider(CollectibleObject obj)
            {
                if (obj?.Code == null) return;
                if (matcher.Code != null && matcher.Code.Path.Contains('*')
                    && !WildcardUtil.Match(matcher.Code, obj.Code)) return;

                var stack = new ItemStack(obj);
                if (!matcher.SatisfiesAsIngredient(stack, false)) return;

                found++;
                if (samples.Count < 30) samples.Add(stack);
            }

            try
            {
                if (matcher.Type == EnumItemClass.Block)
                {
                    foreach (var block in capi.World.Blocks) Consider(block);
                }
                else
                {
                    foreach (var item in capi.World.Items) Consider(item);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not resolve variants for a row: {0}", e.Message);
            }

            req.MatchedVariants = found;
            if (samples.Count > 0) req.PresetSampleStacks(samples);
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
            // Prefer a real matched item: for a wildcard row it is the only thing that knows
            // the row is about boards rather than "any suitable item".
            var samples = req.SampleStacks(capi.World);
            string name = samples.Count > 0
                ? samples[0].GetName()
                : req.Sample != null ? IngredientName(req.Sample) : NameForCode(req.ExactCodes.FirstOrDefault());

            int variants = Math.Max(req.MatchedVariants, req.VariantCount);
            if (variants <= 1) return name;

            // "Board (Aged oak)" -> "Board", so the row reads as the whole set rather than
            // naming one arbitrary member of it (spec §8).
            int paren = name.IndexOf(" (");
            if (paren > 0) name = name.Substring(0, paren);

            string what = req.VariantLabel;
            return string.IsNullOrEmpty(what)
                ? $"{name} (any, {variants} variants)"
                : $"{name} (any {what}, {variants} variants)";
        }

        /// <summary>"Board (any wood, 12 variants)" → "Board": a materials summary wants the
        /// thing, not its bookkeeping.</summary>
        static string StripVariants(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int paren = name.IndexOf(" (");
            return paren > 0 ? name.Substring(0, paren) : name;
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

        /// <summary>
        /// A readable name for an ingredient. Tag-matched ingredients ("any chisel") have no
        /// resolved stack and no usable code — their Code reads as the bare wildcard "*:*",
        /// which is what a row was showing before this fallback existed. Fall back to the
        /// recipe author's own word for what varies, which is the whole reason that field is
        /// there, and never render a raw wildcard at a player.
        /// </summary>
        string IngredientName(CraftingRecipeIngredient ing)
        {
            var stack = ing.ResolvedItemStack;
            if (stack != null) return stack.GetName();

            string code = ing.Code?.ToShortString();
            if (!string.IsNullOrEmpty(code) && !code.Contains("*")) return NameForCode(code);

            return string.IsNullOrEmpty(ing.Name) ? "any suitable item" : $"any {ing.Name}";
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

        /// <summary>
        /// Everything inside bags strapped to animals that are *yours* and within reach —
        /// the one you are riding, and any you own standing nearby. Opt-in, because "what do
        /// I have on me" is the question this mod answers and reasonable people disagree
        /// about whether the pack mule counts.
        ///
        /// Ownership, not proximity, is the test. Counting any container that happens to be
        /// near would be the nearby-chest scanning the design rejects; a beast you own and
        /// are standing beside is genuinely part of what you are carrying, and the game can
        /// tell us which is which.
        ///
        /// Two hops to the goods: the animal carries an attachable container whose slots hold
        /// the bags, and each bag keeps its contents inside its own itemstack — which is what
        /// IHeldBag reads. Yields nothing when there is no such animal, no container, or when
        /// the client has not been told the contents; none of those is an error.
        ///
        /// Only bags *strapped to a living animal* count. A saddlebag lying on the ground, in
        /// ground storage or in a chest is not reachable from here — those are not entities
        /// with attached gear — which is deliberate: a bag you would have to walk over and
        /// pick up is stock, not something you are carrying.
        /// </summary>
        public IEnumerable<ItemStack> OwnedAnimalBagStacks(double range)
        {
            var me = capi.World?.Player?.Entity;
            if (me?.Pos == null) yield break;

            var animals = capi.World.GetEntitiesAround(me.Pos.XYZ, (float)range, (float)range,
                e => e != null && e != me && IsMine(e, me));
            if (animals == null) yield break;

            foreach (var animal in animals)
            {
                if (animal.SidedProperties?.Behaviors == null) continue;

                foreach (var behavior in animal.SidedProperties.Behaviors)
                {
                    // Attachable specifically — the behaviour that holds gear strapped to the
                    // animal, which is what a saddlebag is. Its parent EntityBehaviorContainer
                    // also covers things that are emphatically not luggage (what the beast has
                    // in its mouth, a player's own inventory), and counting those would put
                    // items in your totals that you cannot reach.
                    var inv = (behavior as EntityBehaviorAttachable)?.Inventory;
                    if (inv == null) continue;

                    foreach (var slot in inv)
                    {
                        var stack = slot?.Itemstack;
                        if (stack?.Collectible == null) continue;

                        var bag = AsBag(stack.Collectible);
                        if (bag == null) continue;

                        ItemStack[] contents;
                        try { contents = bag.GetContents(stack, capi.World); }
                        catch { continue; }
                        if (contents == null) continue;

                        foreach (var held in contents)
                        {
                            if (held?.Collectible != null) yield return held;
                        }
                    }
                }
            }
        }

        /// <summary>Mine to rummage through: the animal under me, or one I own.</summary>
        static bool IsMine(Entity animal, EntityPlayer me)
        {
            try
            {
                if ((me as EntityAgent)?.MountedOn?.Entity == animal) return true;
                return animal.GetBehavior<EntityBehaviorOwnable>()?.IsOwner(me) == true;
            }
            catch { return false; }
        }

        /// <summary>Is there anything worth recounting for out there? Cheap enough to ask on a
        /// timer, which is what the bag path needs — see IncludeMountBags in CLAUDE.md.</summary>
        public bool HasOwnedAnimalNearby(double range)
        {
            var me = capi.World?.Player?.Entity;
            if (me?.Pos == null) return false;

            var found = capi.World.GetEntitiesAround(me.Pos.XYZ, (float)range, (float)range,
                e => e != null && e != me && IsMine(e, me));
            return found != null && found.Length > 0;
        }

        static IHeldBag AsBag(CollectibleObject collectible)
        {
            if (collectible is IHeldBag direct) return direct;
            if (collectible.CollectibleBehaviors == null) return null;

            foreach (var behavior in collectible.CollectibleBehaviors)
            {
                if (behavior is IHeldBag viaBehavior) return viaBehavior;
            }
            return null;
        }

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

        string OutputIdentity(GridRecipe recipe)
            => PageCode(recipe?.Output?.ResolvedItemStack) ?? "?";

        /// <summary>Total items consumed by a recipe, used to pick a group's cheapest layout.</summary>
        int TotalIngredientCount(GridRecipe recipe)
            => ConsumedIngredients(recipe).Sum(c => c.Quantity);

        /// <summary>
        /// Per-cell variant stacks for drawing a group's crafting grid: one list per cell of
        /// the representative layout (row-major, empty list = empty cell), each holding the
        /// distinct stacks that cell accepts across the group's variants. Only recipes with
        /// the representative's exact layout contribute — the group can hold other layouts of
        /// the same item, and the drawn grid must be the one the requirement numbers describe.
        /// Variant order is recipe order in every cell and in OutputStacks, so a bookshelf's
        /// plank cells and its output cycle woods in lockstep.
        /// </summary>
        public List<List<ItemStack>> CellStacks(RecipeVariantGroup group, int maxPerCell = 20)
        {
            var cells = new List<List<ItemStack>>();
            int n = (group?.Width ?? 0) * (group?.Height ?? 0);
            if (n == 0) return cells;

            var sameLayout = SameLayoutRecipes(group);
            for (int i = 0; i < n; i++)
            {
                var seen = new HashSet<string>();
                var stacks = new List<ItemStack>();
                foreach (var r in sameLayout)
                {
                    var ing = r.ResolvedIngredients != null && i < r.ResolvedIngredients.Length
                        ? r.ResolvedIngredients[i]
                        : null;
                    var st = ing?.ResolvedItemStack;
                    var code = st?.Collectible?.Code?.ToShortString();
                    if (code == null || stacks.Count >= maxPerCell || !seen.Add(code)) continue;
                    stacks.Add(st);
                }
                cells.Add(stacks);
            }
            return cells;
        }

        /// <summary>Distinct output stacks across the group's same-layout variants, in the
        /// same recipe order as CellStacks so cycling icons stay in sync.</summary>
        public List<ItemStack> OutputStacks(RecipeVariantGroup group, int max = 20)
        {
            var seen = new HashSet<string>();
            var stacks = new List<ItemStack>();
            foreach (var r in SameLayoutRecipes(group))
            {
                var st = r.Output?.ResolvedItemStack;
                var code = st?.Collectible?.Code?.ToShortString();
                if (code == null || stacks.Count >= max || !seen.Add(code)) continue;
                stacks.Add(st);
            }
            return stacks;
        }

        static List<GridRecipe> SameLayoutRecipes(RecipeVariantGroup group)
        {
            if (group?.Recipes == null) return new List<GridRecipe>();
            return group.Recipes
                .Where(r => r.Width == group.Width && r.Height == group.Height
                            && r.IngredientPattern == group.Pattern)
                .ToList();
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
