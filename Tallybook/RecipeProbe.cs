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
        /// <summary>What the row is called. A uniform-material row answers with the wood
        /// the count actually settled on — "Board (Oak)" beats "Board (one wood, 14
        /// variants)" the moment the build knows its wood (Mark). The stored name stays
        /// the honest fallback while nothing is carried and no wood is chosen yet.</summary>
        public string DisplayName
        {
            get
            {
                if (!UniformVariants || CountedMaterial == null || displayName == null)
                    return displayName;
                int cut = displayName.IndexOf(" (one ", StringComparison.Ordinal);
                string baseName = cut < 0 ? displayName : displayName.Substring(0, cut);
                string wood = Lang.GetIfExists("material-" + CountedMaterial)
                    ?? char.ToUpperInvariant(CountedMaterial[0]) + CountedMaterial.Substring(1);
                return $"{baseName} ({wood})";
            }
            set => displayName = value;
        }
        string displayName;

        /// <summary>The material the last count settled on for this uniform row ("oak"),
        /// written by the counting pass — null until something is carried.</summary>
        public string CountedMaterial;
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

        /// <summary>
        /// An errand's self row: the stack the NPC's dialogue actually asked for, matched by
        /// the game's own hand-over rule instead of by handbook page.
        ///
        /// The two answers usually agree, and where they part the dialogue's is the one the
        /// player lives with — it decides whether the turn-in line appears at all. It is
        /// looser about attributes in one direction (a globe carrying only its type still
        /// satisfies a request for a *collected* globe: the game asks whether what you hold
        /// is a subset of what was asked for) and stricter in another (a tool below 95%
        /// durability, or food past fresh, is refused outright). Counting goods the trader
        /// will turn away is the same lie as counting an empty bowl as a bowl of water.
        /// </summary>
        public ItemStack QuestWantStack;

        /// <summary>The variants this row accepts must all be ONE material (a construction
        /// site binds its wood at the first delivery): the count is the best single
        /// material carried, never a mixed sum — twelve oak and twelve birch planks are
        /// twelve toward the boat, not twenty-four.</summary>
        public bool UniformVariants;

        /// <summary>Where the material sits in this row's code ("plank-" + wood, or
        /// "debarkedlog-" + wood + "-ud") — what lets counts group by WOOD rather than by
        /// full code, and lets sibling rows agree on the same wood.</summary>
        public string UniformPrefix, UniformSuffix;

        /// <summary>Every wood-bound row of the same build, this one included. The build
        /// is ONE wood throughout, so the counted wood is chosen JOINTLY — the single
        /// wood that carries the whole build furthest — never per row (Mark: oak boards
        /// and birch beams must not both read as progress).</summary>
        public List<Requirement> UniformSet;

        // ---- liquid requirements (recipes whose ingredient is really "a container OF X") --

        /// <summary>
        /// Non-null when this row's real demand is a liquid: the recipe's own requiresContent
        /// matcher (from the ingredient's recipeAttributes, or the recipe-level
        /// liquidContainerProps fallback). The container matchers stay in
        /// ExactCodes/OtherMatchers — the liquid only counts while it sits in an accepted
        /// vessel, exactly as the crafting grid demands.
        /// </summary>
        public JsonItemStack LiquidMatcher;

        /// <summary>Resolved sample of the liquid itself, for the row's name and icon.</summary>
        public ItemStack LiquidStack;

        /// <summary>Litres one craft consumes; display only — counting runs in portion items.</summary>
        public float LitresPerCraft;

        /// <summary>The liquid's own items-per-litre (waterTightContainerProps). Quantity,
        /// Have and Needed are all in portion items; this converts them back for display.</summary>
        public float ItemsPerLitre = 1f;

        /// <summary>Grid cells this ingredient occupied per craft, before Quantity was
        /// rescaled to portion items — what recipe-variant merging must compare against.</summary>
        public int CellQuantity;

        /// <summary>A liquid demand of any recipe kind — every builder that creates one sets
        /// the liquid sample, whichever matcher variant it carries.</summary>
        public bool IsLiquid => LiquidStack != null;

        public string LiquidCode => LiquidStack?.Collectible?.Code?.ToShortString();

        /// <summary>The self row of a pinned liquid also counts what is inside carried
        /// containers — a portion can never sit in a bare slot, so without this a pinned
        /// liquid could never be "had". Off for errand pins: a hand-over check inspects slot
        /// stacks, and a jug of honey does not satisfy a request for honey.</summary>
        public bool CountContainerContents;

        /// <summary>Cooking-pot rows only: the recipe's own ingredient, whose Matches() is the
        /// game's matcher for what may go in the pot — solids and liquid contents alike.</summary>
        public CookingRecipeIngredient CookingIngredient;

        /// <summary>Barrel rows only: the recipe's own ingredient, asked directly whether a
        /// container's *contents* satisfy it (BarrelRecipeIngredient is a
        /// CraftingRecipeIngredient, so this is again the game's matcher, not ours).</summary>
        public CraftingRecipeIngredient LiquidContentMatcher;

        /// <summary>Liquid rows that accept the liquid from any container. Cooking wants the
        /// liquid poured into the pot, so unlike a grid recipe no particular vessel is part of
        /// the demand.</summary>
        public bool AnyVessel;

        /// <summary>Self rows of pinned liquids: display in litres even though the row is not
        /// an ingredient demand — nobody discusses two hundred "portions" of acid.</summary>
        public bool ShowLitres;

        /// <summary>
        /// Alloy rows: the metal in any meltable form. Keyed by item code, valued at the
        /// metal units that one item carries (ingot 100, nugget 5) — counting weighs each
        /// carried stack by its metal content, exactly as the crucible will.
        /// </summary>
        public Dictionary<string, int> UnitsPerItem;

        /// <summary>have/needed for display: litres for liquid rows, metal units for alloy
        /// rows, plain items otherwise.</summary>
        public string CountText(int have, int needed)
            => IsLiquid || ShowLitres ? $"{LitresText(have)}/{LitresText(needed)} L"
             : UnitsPerItem != null ? $"{have}/{needed} u"
             : $"{have}/{needed}";

        public string LitresText(int items)
        {
            float ipl = ItemsPerLitre <= 0 ? 1f : ItemsPerLitre;
            return (items / ipl).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

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
                // An errand row counts the same page by a different rule, so it must not
                // pool with a personal goal for the same item — same reason uniform rows
                // stay apart from ordinary ones below.
                if (SelfPageCode != null) return key = $"S|{SelfPageCode}{(QuestWantStack != null ? "|Q" : "")}";

                var codes = string.Join(",", ExactCodes.OrderBy(c => c, StringComparer.Ordinal));
                var others = string.Join(",", OtherMatchers
                    .Select(m => $"{m.Type}:{m.MatchingType}:{m.Code}")
                    .OrderBy(s => s, StringComparer.Ordinal));
                // Uniform rows must never pool with ordinary ones: a build's "Board (one
                // wood)" and a bookshelf's "Board (any wood)" match the same items but
                // count them by different rules.
                key = $"{(IsTool ? "T" : "I")}{(UniformVariants ? "U" : "")}|{codes}|{others}";
                // "Bucket of water" and "empty bucket" share container matchers but are
                // different demands — they must never merge into one HUD row.
                if (IsLiquid)
                {
                    key += $"|L:{LiquidCode}:{LitresPerCraft.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
                }
                // Nor may a units-counted metal row merge with an item-counted row that
                // happens to accept the same codes.
                if (UnitsPerItem != null) key += "|U";
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

        /// <summary>Liquids found inside carried containers: the vessel, its contents, and how
        /// many portion items that is. Kept separate from byPage on purpose — liquid in a jug
        /// is not an item in a slot, and folding it into the general pool would let a quest
        /// hand-over or an ordinary ingredient row count goods the game would refuse.</summary>
        readonly List<(ItemStack Container, ItemStack Content, int Items)> liquids
            = new List<(ItemStack, ItemStack, int)>();

        /// <summary>Contained liquid totals by the content's page code, for self rows.</summary>
        readonly Dictionary<string, int> liquidByPage = new Dictionary<string, int>();

        readonly IWorldAccessor world;

        public InventorySnapshot(IWorldAccessor world, IEnumerable<IInventory> inventories,
            IEnumerable<ItemStack> alsoCount = null)
        {
            this.world = world;
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

            if (stack.Collectible is BlockLiquidContainerBase container)
            {
                ItemStack content = null;
                try { content = container.GetContent(stack); } catch { /* unreadable contents count as none */ }
                if (content?.Collectible?.Code == null || content.StackSize <= 0) return;

                // Contents ride on each container item; identical filled containers that
                // stack share the same per-item contents.
                int items = content.StackSize * Math.Max(1, stack.StackSize);
                liquids.Add((stack, content, items));

                string contentPage = RecipeProbe.PageCode(content) ?? content.Collectible.Code.ToShortString();
                liquidByPage[contentPage] = liquidByPage.TryGetValue(contentPage, out var have)
                    ? have + items
                    : items;
            }
        }

        public int Count(Requirement req)
        {
            // An errand counts by the dialogue's rule — the same rule the hand-over line
            // will apply a moment later, when it decides whether to appear at all.
            if (req.QuestWantStack != null) return CountQuestMatches(req.QuestWantStack);

            // Self requirements name one exact page — a single lookup, and no chance of
            // another variant of the same code counting toward it.
            if (req.SelfPageCode != null)
            {
                int n = byPage.TryGetValue(req.SelfPageCode, out var self) ? self.Total : 0;
                if (req.CountContainerContents && liquidByPage.TryGetValue(req.SelfPageCode, out var contained))
                    n += contained;
                return n;
            }

            // Alloy rows count metal units, each carried item weighed by its content —
            // an ingot is worth twenty nuggets, and the crucible agrees.
            if (req.UnitsPerItem != null)
            {
                int units = 0;
                foreach (var entry in byPage)
                {
                    if (req.UnitsPerItem.TryGetValue(entry.Value.Code, out var per))
                        units += entry.Value.Total * per;
                }
                return units;
            }

            // A liquid row counts the liquid, and only while it sits in a vessel the recipe
            // accepts — an empty bowl satisfies the container matcher and the crafting grid
            // still refuses it, so it must contribute nothing here.
            if (req.IsLiquid)
            {
                int held = 0;
                foreach (var (containerStack, content, items) in liquids)
                {
                    // Grid recipes demand a specific vessel in the grid; pots, barrels and
                    // boilers take the liquid poured from anything.
                    if (!req.AnyVessel && !ContainerMatches(req, containerStack)) continue;
                    bool matches;
                    try
                    {
                        // Whichever matcher the recipe kind actually owns, in that order —
                        // and each of them is the game's, never a reimplementation. The code
                        // fallback serves synthesized recipes (distillation) whose input is
                        // one exact liquid.
                        if (req.CookingIngredient != null) matches = req.CookingIngredient.Matches(content);
                        else if (req.LiquidContentMatcher != null) matches = req.LiquidContentMatcher.SatisfiesAsIngredient(content, false);
                        else if (req.LiquidMatcher != null) matches = req.LiquidMatcher.Matches(world, content);
                        else matches = content.Collectible?.Code != null
                            && req.ExactCodes.Contains(content.Collectible.Code.ToShortString());
                    }
                    catch { matches = false; }
                    if (matches) held += items;
                }
                return held;
            }

            if (req.UniformVariants) return CountUniform(req);

            int total = 0;
            foreach (var entry in byPage)
            {
                if (req.ExactCodes.Contains(entry.Value.Code))
                {
                    total += entry.Value.Total;
                    continue;
                }
                bool counted = false;
                foreach (var m in req.OtherMatchers)
                {
                    if (m.SatisfiesAsIngredient(entry.Value.Sample, false)) { total += entry.Value.Total; counted = true; break; }
                }
                // Cooking rows delegate to the pot's own matcher, which covers anything the
                // exact codes above did not (attribute-distinct or wildcarded valid stacks).
                if (!counted && req.CookingIngredient != null)
                {
                    bool ok;
                    try { ok = req.CookingIngredient.Matches(entry.Value.Sample); }
                    catch { ok = false; }
                    if (ok) total += entry.Value.Total;
                }
            }
            return total;
        }

        /// <summary>
        /// The dialogue's own hand-over test, applied to everything carried.
        ///
        /// Lifted from <c>DialogueComponent.matches</c> (decompile-verified 1.22.7), which is
        /// what the turn-in line is gated on: the wanted stack equals what is carried once a
        /// fixed set of attributes is ignored, OR what is carried is an attribute *subset* of
        /// what was asked for — plus a freshness gate that quietly refuses worn tools and
        /// spoiled food. Delegating to the game's own comparisons is the same discipline the
        /// crafting rows follow with SatisfiesAsIngredient: it is the only way the count can
        /// never disagree with the mechanism it is predicting.
        ///
        /// The one deliberate divergence, unchanged: the game wants the whole quantity in a
        /// SINGLE slot, and we sum across slots. Splitting ten gears 5+5 is the player's to
        /// notice; telling them they own nothing would be worse.
        /// </summary>
        int CountQuestMatches(ItemStack want)
        {
            int total = 0;
            foreach (var entry in byPage)
            {
                var carried = entry.Value.Sample;
                if (carried?.Collectible == null) continue;
                bool ok;
                try
                {
                    ok = (want.Equals(world, carried, QuestIgnoredAttributes) || carried.Satisfies(want))
                         && carried.Collectible.IsReasonablyFresh(world, carried);
                }
                catch { ok = false; }
                if (ok) total += entry.Value.Total;
            }
            return total;
        }

        /// <summary>What the dialogue runner ignores when comparing a carried stack to a
        /// requested one — the engine's own ignored set plus the five it appends itself
        /// (backpack, condition, durability, randomX, randomZ). Its own list is a private
        /// method, so this mirrors it rather than calling it; re-check it against
        /// <c>DialogueComponent.getIgnoreAttrs</c> when the game version moves.</summary>
        static readonly string[] QuestIgnoredAttributes = GlobalConstants.IgnoredStackAttributes
            .Concat(new[] { "backpack", "condition", "durability", "randomX", "randomZ" })
            .ToArray();

        // ---- uniform-material counting (construction builds) --------------------------

        /// <summary>One choice per build per snapshot: the counted wood is decided once
        /// for the whole sibling set and cached, so every row of the build answers with
        /// the same wood inside one recount.</summary>
        readonly Dictionary<object, string> jointMaterial = new Dictionary<object, string>();

        /// <summary>
        /// The site takes ONE material throughout, so a mixed pile must not read as
        /// progress it cannot be. With siblings, the material is chosen JOINTLY — the
        /// single wood covering most of the whole build (each row's contribution capped
        /// at what it needs) — so oak boards and birch beams never both count (Mark);
        /// alone, the row's own best material stands.
        /// </summary>
        int CountUniform(Requirement req)
        {
            var mine = PerMaterialCounts(req);

            string material;
            if (req.UniformSet != null && req.UniformSet.Count > 1)
            {
                if (!jointMaterial.TryGetValue(req.UniformSet, out material))
                {
                    var progress = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var sibling in req.UniformSet)
                    {
                        foreach (var kv in PerMaterialCounts(sibling))
                        {
                            progress.TryGetValue(kv.Key, out int cur);
                            progress[kv.Key] = cur + Math.Min(kv.Value, Math.Max(1, sibling.Quantity));
                        }
                    }
                    material = progress.Count == 0 ? null
                        : progress.OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
                    jointMaterial[req.UniformSet] = material;
                }
            }
            else
            {
                material = mine.Count == 0 ? null
                    : mine.OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            }

            // The display follows the decision: the row names the wood it is counting.
            req.CountedMaterial = material;
            return material != null && mine.TryGetValue(material, out int n) ? n : 0;
        }

        Dictionary<string, int> PerMaterialCounts(Requirement req)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in byPage)
            {
                bool matched = req.ExactCodes.Contains(entry.Value.Code);
                if (!matched)
                {
                    foreach (var m in req.OtherMatchers)
                    {
                        if (m.SatisfiesAsIngredient(entry.Value.Sample, false)) { matched = true; break; }
                    }
                }
                if (!matched) continue;

                string material = MaterialOf(req, entry.Value.Code);
                if (material == null) continue;
                counts.TryGetValue(material, out int cur);
                counts[material] = cur + entry.Value.Total;
            }
            return counts;
        }

        /// <summary>The material token inside a matched code, by the row's own pattern:
        /// "debarkedlog-" + X + "-ud" against debarkedlog-aged-ud gives "aged"; a capture
        /// spanning further segments keeps its first ("oak-ud" → "oak" — orientations of
        /// one wood are still one wood). No pattern: the whole code is its own bucket.</summary>
        static string MaterialOf(Requirement req, string shortCode)
        {
            string path = shortCode;
            int colon = path.IndexOf(':');
            if (colon >= 0) path = path.Substring(colon + 1);

            if (req.UniformPrefix == null) return path;
            if (!path.StartsWith(req.UniformPrefix) || !path.EndsWith(req.UniformSuffix ?? "")
                || path.Length <= req.UniformPrefix.Length + (req.UniformSuffix?.Length ?? 0))
                return null;

            string captured = path.Substring(req.UniformPrefix.Length,
                path.Length - req.UniformPrefix.Length - (req.UniformSuffix?.Length ?? 0));
            int dash = captured.IndexOf('-');
            return dash < 0 ? captured : captured.Substring(0, dash);
        }

        static bool ContainerMatches(Requirement req, ItemStack containerStack)
        {
            var code = containerStack?.Collectible?.Code?.ToShortString();
            if (code != null && req.ExactCodes.Contains(code)) return true;
            foreach (var m in req.OtherMatchers)
            {
                if (m.SatisfiesAsIngredient(containerStack, false)) return true;
            }
            return false;
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

        /// <summary>Items-per-litre of a liquid output (0 for solids) — what lets choosers
        /// and labels say "makes 1 L" instead of "makes 100 ×".</summary>
        public float OutputItemsPerLitre;

        /// <summary>"Makes 1 L of Aqua Vitae" / "Makes 4 × Plank" — the output stated in the
        /// unit the player thinks in.</summary>
        public string MakesLabel()
        {
            if (OutputItemsPerLitre > 0)
            {
                string litres = (OutputQuantity / OutputItemsPerLitre)
                    .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                return $"Makes {litres} L of {OutputName}";
            }
            return $"Makes {OutputQuantity} × {OutputName}";
        }
        public string Pattern;
        public int Width;
        public int Height;

        /// <summary>How many distinct grid arrangements make this item. Shown as a count only —
        /// the handbook is where the arrangements themselves belong.</summary>
        public int LayoutCount;

        /// <summary>Built by the collapsed (expansion) grouping, where members are variants of
        /// one ingredient row and their materials legitimately differ per member. Non-collapsed
        /// groups carry the material signature in their key instead — which is what makes the
        /// stricter merge gate in BuildRequirements safe to apply to them and wrong here.</summary>
        public bool Collapsed;

        /// <summary>Non-null for a cooking-pot recipe (vanilla 1.22 cooks acids, glue, potash
        /// and the like into real items via cooksInto). Such a group has no grid recipes;
        /// Pattern carries the recipe code so Signature stays unique and persistable.</summary>
        public CookingRecipe Cooking;

        /// <summary>Cooking groups: servings one pot cooks at once (the best pot this world
        /// has — vanilla's clay pot does 6). One serving = one OutputQuantity of output, so
        /// this is what turns "how many servings" into "how many pot loads".</summary>
        public int ServingsPerBatch;

        /// <summary>Non-null for a sealed-barrel recipe (ferments, tannin, lime water, dyes…).
        /// Pattern carries the recipe code for a stable Signature, as with cooking.</summary>
        public BarrelRecipe Barrel;

        /// <summary>Barrel groups: in-game hours the barrel stays sealed per batch.</summary>
        public double SealHours;

        /// <summary>Barrel groups: litres the biggest barrel in this world holds — the batch
        /// cap that turns "how many crafts" into "how many seals".</summary>
        public float BatchLitres;

        /// <summary>Barrel groups: the largest litre amount one craft moves through the
        /// barrel (max of output and any liquid ingredient) — the divisor against
        /// BatchLitres when working out crafts per seal.</summary>
        public float LitresPerCraft;

        /// <summary>Non-null for a synthesized distillation recipe: the liquid that goes into
        /// the boiler, yielding this group's output at DistillRatio litres per litre.</summary>
        public ItemStack DistillFrom;
        public float DistillRatio;

        /// <summary>Non-null for a synthesized one-item conversion — fruit press, quern
        /// grinding, pulverizer crushing: the item that goes in, one per craft.</summary>
        public ItemStack PressFrom;
        public float PressLitresPerItem;

        /// <summary>Human word for how a synthesized conversion happens ("ground in a
        /// quern"); the Materials suffix for the kinds that share the one-item shape.</summary>
        public string MethodLabel;

        /// <summary>Input items one craft consumes for synthesized conversions — 1 for
        /// press/grind/crush, the smelt ratio for smelting (20 nuggets per copper ingot).</summary>
        public float InputsPerCraft = 1f;

        /// <summary>Non-null for a crucible alloy (bismuth bronze from copper+zinc+bismuth).
        /// One craft is one output; unit demands scale linearly.</summary>
        public AlloyRecipe Alloy;

        /// <summary>Non-null for an anvil smithing recipe (iron bloom → ingot, ingots →
        /// plate). Input count comes from the recipe's voxels, by the game's own math.</summary>
        public SmithingRecipe Smithing;

        /// <summary>Non-null for a construction-site build (vanilla sailboat, Shipwright's
        /// boats): everything the site's stages will ask for, summed across stages. Each
        /// entry keeps the game's own ConstructionIngredient (a CraftingRecipeIngredient)
        /// plus the raw authored code — the raw form is what lets a chosen material be
        /// substituted back in ("plank-{wood}" → "plank-oak").</summary>
        public List<ConstructionMat> Construction;

        /// <summary>The build's bound material ("oak"), when the player committed to one —
        /// bound rows then name and count that material only. Null: any one material,
        /// counted as the best single variant carried.</summary>
        public string BuildMaterial;

        /// <summary>What the material variable is called in the stage data ("wood") and
        /// which values this world offers for it — the selector's label and entries.</summary>
        public string BuildMaterialName;
        public List<string> BuildMaterialChoices;

        /// <summary>The method family this group belongs to — the section header when a
        /// chooser mixes kinds ("Alloyed in a crucible" vs "Smelted…" vs "Crafting grid").</summary>
        public string KindLabel()
        {
            if (Construction != null) return "Built at a construction site";
            if (Alloy != null) return "Alloyed in a crucible";
            if (Smithing != null) return "Smithed on an anvil";
            if (Cooking != null) return "Cooked in a pot";
            if (Barrel != null) return SealHours > 0 ? "Sealed in a barrel" : "Mixed in a barrel";
            if (DistillFrom != null) return "Distilled in a boiler";
            if (!string.IsNullOrEmpty(MethodLabel))
                return char.ToUpper(MethodLabel[0]) + MethodLabel.Substring(1);
            return "Crafting grid";
        }

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

        /// <summary>The ingredient list alone — Materials without the method/tools tail —
        /// for compact chooser rows where the tail is shared by the whole category.</summary>
        public string MaterialsBrief;
    }

    /// <summary>One summed construction demand: the working ingredient and the code as
    /// the stage file wrote it — "plank-{wood}" survives here so a bound material can
    /// be substituted where the placeholder sat.</summary>
    public class ConstructionMat
    {
        public ConstructionIngredient Ing;
        public string RawPath;
        public string RawDomain;
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
        public void InvalidateIndex()
        {
            byOutput = null;
            cookingByOutput = null;
            barrelByOutput = null;
            distillByOutput = null;
            pressByOutput = null;
            grindByOutput = null;
            crushByOutput = null;
            smeltByOutput = null;
            alloyByOutput = null;
            smithByOutput = null;
            constructions = null;
            maxCookingServings = 0;
            maxBarrelLitres = 0;
            liquidContainerOptions = null;
            pathCategories.Clear();
            familySamples.Clear();
            liquidDemands.Clear();
            liquidContainerMatchers.Clear();
            matcherSamples.Clear();
        }

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

            IndexCookingRecipes();
            IndexBarrelRecipes();
            IndexAlloyRecipes();
            IndexSmithingRecipes();
            IndexAttributeRecipes();
        }

        /// <summary>
        /// Sealed-barrel recipes — ferments, tannin, lime water, dyes, cheese and some thirty
        /// other products. Same client-resident registry family as cooking
        /// (RecipeRegistrySystem.BarrelRecipes), and BarrelRecipe.FromBytes resolves both
        /// ingredients and output on the client (verified in the 1.22.6 decompile). Wildcard
        /// ingredients arrive pre-expanded per variant, exactly like grid recipes.
        /// </summary>
        void IndexBarrelRecipes()
        {
            barrelByOutput = new Dictionary<string, List<BarrelRecipe>>();
            List<BarrelRecipe> recipes = null;
            try { recipes = capi.ModLoader.GetModSystem<RecipeRegistrySystem>()?.BarrelRecipes; }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] barrel recipes unavailable: {0}", e.Message);
            }
            if (recipes == null) return;

            foreach (var r in recipes)
            {
                if (r == null || !r.Enabled) continue;
                var code = r.Output?.ResolvedItemstack?.Collectible?.Code?.ToShortString();
                if (code == null) continue;
                if (!barrelByOutput.TryGetValue(code, out var list))
                    barrelByOutput[code] = list = new List<BarrelRecipe>();
                list.Add(r);
            }
        }

        /// <summary>
        /// The two recipe kinds that are not recipes at all but collectible attributes:
        /// distillation (distillationProps on the liquid the boiler consumes — read exactly
        /// as BlockEntityBoiler reads it) and fruit pressing (juiceableProperties on the item
        /// the press squeezes — as BlockEntityFruitPress reads it). One scan over the world's
        /// collectibles per session, indexed by what each one *produces*.
        /// </summary>
        void IndexAttributeRecipes()
        {
            distillByOutput = new Dictionary<string, List<(ItemStack, ItemStack, float)>>();
            pressByOutput = new Dictionary<string, List<(ItemStack, ItemStack, float)>>();
            grindByOutput = new Dictionary<string, List<(ItemStack, ItemStack, float)>>();
            crushByOutput = new Dictionary<string, List<(ItemStack, ItemStack, float)>>();
            smeltByOutput = new Dictionary<string, List<(ItemStack, ItemStack, float)>>();
            try
            {
                foreach (var obj in AllCollectibles())
                {
                    var attrs = obj?.Attributes;
                    if (attrs == null || obj.Code == null) continue;

                    if (attrs["distillationProps"].Exists)
                    {
                        var props = attrs["distillationProps"].AsObject<DistillationProps>(null);
                        if (props?.DistilledStack != null && props.Ratio > 0
                            && props.DistilledStack.Resolve(capi.World, "[tallybook] distillation"))
                        {
                            Index(distillByOutput, props.DistilledStack.ResolvedItemstack,
                                new ItemStack(obj), props.Ratio);
                        }
                    }

                    if (attrs["juiceableProperties"].Exists)
                    {
                        var props = attrs["juiceableProperties"]
                            .AsObject<JuiceableProperties>(null, obj.Code.Domain);
                        if (props?.LitresPerItem != null && props.LitresPerItem > 0
                            && props.LiquidStack != null
                            && props.LiquidStack.Resolve(capi.World, "[tallybook] juiceable"))
                        {
                            Index(pressByOutput, props.LiquidStack.ResolvedItemstack,
                                new ItemStack(obj), (float)props.LitresPerItem);
                        }
                    }
                }

                // Grinding, crushing and smelting are first-class collectible fields rather
                // than attributes — sulfur chunks grind into powder, ores crush into grits,
                // and twenty copper nuggets smelt into an ingot. Factor = output items per
                // input item for grind/crush, inputs per craft (the smelt ratio) for smelt.
                foreach (var obj in AllCollectibles())
                {
                    if (obj?.Code == null) continue;

                    var ground = obj.GrindingProps?.GroundStack;
                    if (ground != null && ground.Resolve(capi.World, "[tallybook] grinding"))
                    {
                        Index(grindByOutput, ground.ResolvedItemstack, new ItemStack(obj),
                            Math.Max(1, ground.ResolvedItemstack.StackSize));
                    }

                    var crushed = obj.CrushingProps?.CrushedStack;
                    if (crushed != null && crushed.Resolve(capi.World, "[tallybook] crushing"))
                    {
                        float per = Math.Max(1f,
                            crushed.ResolvedItemstack.StackSize * (obj.CrushingProps.Quantity?.avg ?? 1f));
                        Index(crushByOutput, crushed.ResolvedItemstack, new ItemStack(obj), per);
                    }

                    // Everything burnable has CombustibleProps; only entries that actually
                    // smelt INTO something are recipes (firewood burns to nothing) — and an
                    // item that smelts into itself (an ingot melted for casting) is the
                    // crucible's bookkeeping, not a way to obtain one. Same rule as the
                    // self-consuming grid pseudo-recipes.
                    var smelted = obj.CombustibleProps?.SmeltedStack;
                    if (smelted != null && smelted.Resolve(capi.World, "[tallybook] smelting")
                        && smelted.ResolvedItemstack?.Collectible?.Code?.ToShortString()
                           != obj.Code.ToShortString())
                    {
                        Index(smeltByOutput, smelted.ResolvedItemstack, new ItemStack(obj),
                            Math.Max(1, obj.CombustibleProps.SmeltedRatio));
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not scan distill/press attributes: {0}", e.Message);
            }

            static void Index(Dictionary<string, List<(ItemStack, ItemStack, float)>> dict,
                ItemStack output, ItemStack input, float factor)
            {
                var code = output?.Collectible?.Code?.ToShortString();
                if (code == null) return;
                if (!dict.TryGetValue(code, out var list))
                    dict[code] = list = new List<(ItemStack, ItemStack, float)>();
                list.Add((input, output, factor));
            }
        }

        IEnumerable<CollectibleObject> AllCollectibles()
        {
            foreach (var item in capi.World.Items)
            {
                if (item != null) yield return item;
            }
            foreach (var block in capi.World.Blocks)
            {
                if (block != null) yield return block;
            }
        }

        Dictionary<string, List<BarrelRecipe>> barrelByOutput;
        Dictionary<string, List<(ItemStack Input, ItemStack Output, float Factor)>> distillByOutput;
        Dictionary<string, List<(ItemStack Input, ItemStack Output, float Factor)>> pressByOutput;
        Dictionary<string, List<(ItemStack Input, ItemStack Output, float Factor)>> grindByOutput;
        Dictionary<string, List<(ItemStack Input, ItemStack Output, float Factor)>> crushByOutput;
        Dictionary<string, List<(ItemStack Input, ItemStack Output, float Factor)>> smeltByOutput;
        Dictionary<string, List<AlloyRecipe>> alloyByOutput;

        /// <summary>
        /// Crucible alloys (RecipeRegistrySystem.MetalAlloys, client-synced like the rest):
        /// the one way an alloy ingot is actually created — every smeltable "source" for one
        /// is scrap recovery.
        /// </summary>
        void IndexAlloyRecipes()
        {
            alloyByOutput = new Dictionary<string, List<AlloyRecipe>>();
            List<AlloyRecipe> recipes = null;
            try { recipes = capi.ModLoader.GetModSystem<RecipeRegistrySystem>()?.MetalAlloys; }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] alloy recipes unavailable: {0}", e.Message);
            }
            if (recipes == null) return;

            foreach (var r in recipes)
            {
                if (r == null || !r.Enabled) continue;
                if (r.Output?.ResolvedItemstack == null)
                {
                    try { r.Output?.Resolve(capi.World, "[tallybook] alloy output"); } catch { }
                }
                var code = r.Output?.ResolvedItemstack?.Collectible?.Code?.ToShortString();
                if (code == null) continue;
                if (!alloyByOutput.TryGetValue(code, out var list))
                    alloyByOutput[code] = list = new List<AlloyRecipe>();
                list.Add(r);
            }
        }

        Dictionary<string, List<SmithingRecipe>> smithByOutput;

        /// <summary>
        /// Anvil smithing (RecipeRegistrySystem via GetSmithingRecipes, client-synced,
        /// wildcards pre-expanded like grid recipes): iron bloom → ingot, ingots → plates,
        /// chains, tools. The step that closes the iron chain — a bloomery's bloom is
        /// hammered, never melted.
        /// </summary>
        void IndexSmithingRecipes()
        {
            smithByOutput = new Dictionary<string, List<SmithingRecipe>>();
            List<SmithingRecipe> recipes = null;
            try { recipes = capi.GetSmithingRecipes(); }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] smithing recipes unavailable: {0}", e.Message);
            }
            if (recipes == null) return;

            foreach (var r in recipes)
            {
                if (r == null || !r.Enabled) continue;
                var code = r.Output?.ResolvedItemstack?.Collectible?.Code?.ToShortString();
                if (code == null) continue;
                if (!smithByOutput.TryGetValue(code, out var list))
                    smithByOutput[code] = list = new List<SmithingRecipe>();
                list.Add(r);
            }
        }

        List<RecipeVariantGroup> SmithGroupsFor(string shortCode)
        {
            var groups = new List<RecipeVariantGroup>();
            if (shortCode == null || smithByOutput == null
                || !smithByOutput.TryGetValue(shortCode, out var recipes)) return groups;

            foreach (var r in recipes)
            {
                var output = r.Output?.ResolvedItemstack;
                var inputCode = r.Ingredient?.ResolvedItemStack?.Collectible?.Code?.ToShortString()
                    ?? r.Ingredient?.Code?.ToShortString();
                if (output == null || inputCode == null) continue;

                groups.Add(new RecipeVariantGroup
                {
                    Smithing = r,
                    OutputCode = shortCode,
                    OutputPageCode = PageCode(output) ?? shortCode,
                    OutputName = output.GetName(),
                    OutputQuantity = Math.Max(1, output.StackSize),
                    OutputStack = output,
                    Pattern = "smith:" + inputCode,
                    Width = 0,
                    Height = 0,
                    LayoutCount = 1,
                    MethodLabel = "smithed on an anvil"
                });
            }
            return groups;
        }

        /// <summary>
        /// Requirement row for a smithing recipe: the workpiece material, counted the way
        /// the handbook counts it — the recipe's voxels divided by what one input item
        /// provides, asked of the item itself (IAnvilWorkable.VoxelCountForHandbook).
        /// </summary>
        List<Requirement> BuildSmithingRequirements(RecipeVariantGroup group, bool tools)
        {
            var reqs = new List<Requirement>();
            if (tools) return reqs;

            var recipe = group.Smithing;
            var ing = recipe.Ingredient;
            if (ing == null) return reqs;

            int qty = 1;
            try
            {
                int voxels = recipe.Voxels.Cast<bool>().Count(v => v);
                var stack = ing.ResolvedItemStack;
                int per = stack?.Collectible?.GetCollectibleInterface<IAnvilWorkable>()
                    ?.VoxelCountForHandbook(stack) ?? 42;
                if (per > 0) qty = Math.Max(1, (int)Math.Ceiling(voxels / (double)per));
            }
            catch { /* one workpiece is the safe floor */ }

            var req = new Requirement { Quantity = qty, CellQuantity = qty };
            AddMatcher(req, ing);
            ResolveVariants(req);
            req.DisplayName = BuildDisplayName(req);
            reqs.Add(req);

            if (group.Materials == null)
            {
                group.MaterialsBrief = $"{qty} × {StripVariants(req.DisplayName)}";
                group.Materials = group.MaterialsBrief + " — smithed on an anvil";
            }
            return reqs;
        }

        List<RecipeVariantGroup> AlloyGroupsFor(string shortCode)
        {
            var groups = new List<RecipeVariantGroup>();
            if (shortCode == null || alloyByOutput == null
                || !alloyByOutput.TryGetValue(shortCode, out var recipes)) return groups;

            foreach (var r in recipes)
            {
                var output = r.Output?.ResolvedItemstack;
                if (output == null || r.Ingredients == null || r.Ingredients.Length == 0) continue;

                groups.Add(new RecipeVariantGroup
                {
                    Alloy = r,
                    OutputCode = shortCode,
                    OutputPageCode = PageCode(output) ?? shortCode,
                    OutputName = output.GetName(),
                    // Alloying is continuous — the crucible makes exactly as many units as
                    // you feed it, so one craft is ONE output and the unit demands scale
                    // linearly. (A first version batched crafts up to whole-item counts and
                    // charged a 20-ingot batch for wanting one — found by Mark against a
                    // reference alloy calculator.)
                    OutputQuantity = Math.Max(1, output.StackSize),
                    OutputStack = output,
                    Pattern = "alloy:" + shortCode,
                    Width = 0,
                    Height = 0,
                    LayoutCount = 1
                });
            }
            return groups;
        }

        List<RecipeVariantGroup> BarrelGroupsFor(string shortCode)
        {
            var groups = new List<RecipeVariantGroup>();
            if (shortCode == null || barrelByOutput == null
                || !barrelByOutput.TryGetValue(shortCode, out var recipes)) return groups;

            foreach (var r in recipes)
            {
                var output = r.Output?.ResolvedItemstack;
                if (output == null) continue;

                // One craft = the recipe's base quantities. A liquid output's true quantity
                // is its litres (the resolved stack size does not carry them); solids use
                // the stack size as-is.
                float outIpl = ContainableProps(output)?.ItemsPerLitre ?? 0f;
                float outLitres = r.Output.Litres;
                int outputItems = outLitres > 0 && outIpl > 0
                    ? Math.Max(1, (int)Math.Round(outLitres * outIpl))
                    : Math.Max(1, output.StackSize);

                float maxLitres = outLitres;
                foreach (var ing in r.Ingredients ?? Array.Empty<BarrelRecipeIngredient>())
                {
                    if (ing != null && ing.Litres > maxLitres) maxLitres = ing.Litres;
                }

                groups.Add(new RecipeVariantGroup
                {
                    Barrel = r,
                    SealHours = r.SealHours,
                    BatchLitres = MaxBarrelLitres(),
                    LitresPerCraft = maxLitres,
                    OutputCode = shortCode,
                    OutputPageCode = PageCode(output) ?? shortCode,
                    OutputName = output.GetName(),
                    OutputQuantity = outputItems,
                    OutputStack = output,
                    Pattern = "barrel:" + r.Code,
                    Width = 0,
                    Height = 0,
                    LayoutCount = 1,
                    OutputItemsPerLitre = outLitres > 0 ? outIpl : 0f
                });
            }
            return groups;
        }

        List<RecipeVariantGroup> DistillGroupsFor(string shortCode)
            => AttributeGroupsFor(shortCode, distillByOutput, "distill:", null);

        List<RecipeVariantGroup> PressGroupsFor(string shortCode)
            => AttributeGroupsFor(shortCode, pressByOutput, "press:", "squeezed in a fruit press");

        List<RecipeVariantGroup> GrindGroupsFor(string shortCode)
            => AttributeGroupsFor(shortCode, grindByOutput, "grind:", "ground in a quern");

        List<RecipeVariantGroup> CrushGroupsFor(string shortCode)
            => AttributeGroupsFor(shortCode, crushByOutput, "crush:", "crushed in a pulverizer");

        List<RecipeVariantGroup> SmeltGroupsFor(string shortCode)
            => AttributeGroupsFor(shortCode, smeltByOutput, "smelt:", null);

        /// <summary>The human word for how this input smelts, from its own combustible
        /// props: cooked / baked / fired / smelted (in a crucible when it says so).</summary>
        static string SmeltLabel(ItemStack input)
        {
            var props = input?.Collectible?.CombustibleProps;
            if (props == null) return "smelted";
            switch (props.SmeltingType)
            {
                case EnumSmeltType.Cook: return "cooked over fire";
                case EnumSmeltType.Bake: return "baked";
                case EnumSmeltType.Fire: return "fired";
                case EnumSmeltType.Convert: return "converted by heat";
                default: return props.RequiresContainer ? "smelted in a crucible" : "smelted";
            }
        }

        /// <summary>
        /// Groups for the synthesized recipe kinds. One craft is defined per kind:
        /// distillation makes one litre of output (so the input row reads 1/ratio litres —
        /// 20 L of cider per litre of brandy at the vanilla 0.05); the one-item conversions
        /// (press, grind, crush) consume one input item, yielding factor litres of juice or
        /// factor output items respectively.
        /// </summary>
        List<RecipeVariantGroup> AttributeGroupsFor(string shortCode,
            Dictionary<string, List<(ItemStack Input, ItemStack Output, float Factor)>> index,
            string patternPrefix, string methodLabel)
        {
            var groups = new List<RecipeVariantGroup>();
            if (shortCode == null || index == null || !index.TryGetValue(shortCode, out var entries))
                return groups;

            foreach (var (input, output, factor) in entries)
            {
                string inputCode = input?.Collectible?.Code?.ToShortString();
                if (inputCode == null || output == null) continue;

                bool distill = patternPrefix[0] == 'd';
                bool press = patternPrefix[0] == 'p';
                bool smelt = patternPrefix[0] == 's';
                float outIpl = ContainableProps(output)?.ItemsPerLitre ?? 1f;
                int outputItems = distill
                    ? Math.Max(1, (int)Math.Round(outIpl))                 // 1 L of spirit
                    : press
                        ? Math.Max(1, (int)Math.Round(factor * outIpl))    // litres from one item
                        : smelt
                            ? Math.Max(1, output.StackSize)                // ratio inputs → the smelted stack
                            : Math.Max(1, (int)Math.Round(factor));        // items from one item

                var group = new RecipeVariantGroup
                {
                    OutputCode = shortCode,
                    OutputPageCode = PageCode(output) ?? shortCode,
                    OutputName = output.GetName(),
                    OutputQuantity = outputItems,
                    OutputStack = output,
                    Pattern = patternPrefix + inputCode,
                    Width = 0,
                    Height = 0,
                    LayoutCount = 1,
                    OutputItemsPerLitre = ContainableProps(output)?.ItemsPerLitre ?? 0f,
                    MethodLabel = smelt ? SmeltLabel(input) : methodLabel,
                    InputsPerCraft = smelt ? Math.Max(1f, factor) : 1f
                };
                if (distill) { group.DistillFrom = input; group.DistillRatio = factor; }
                else { group.PressFrom = input; group.PressLitresPerItem = factor; }
                groups.Add(group);
            }
            return groups;
        }

        // ---- chooser path categories -----------------------------------------------------

        readonly Dictionary<string, string> pathCategories = new Dictionary<string, string>();

        /// <summary>
        /// A category label for one recipe path, derived from what its chain bottoms out in —
        /// built for choosers with dozens of entries (Aqua Vitae distills from thirty-two
        /// spirits; the paths ARE grain vs fruit vs honey, but only the far end of each chain
        /// knows which). Follows single-ingredient conversions through every recipe index
        /// (spirit → cider → juice → apple stops at "Fruit"; the grain mash stops at
        /// "Flour + Water"; mead stops at "Honey"), bounded and cycle-guarded. Where a chain
        /// forks or ends, the ingredient *code families* label the category — data, not
        /// name-matching. No recipe kind or item is special-cased, so any future giant
        /// chooser gets grouped the same way.
        /// </summary>
        public string PathCategory(RecipeVariantGroup group)
        {
            if (group?.Signature == null) return "";
            if (pathCategories.TryGetValue(group.Signature, out var cached)) return cached;

            string label;
            try { label = CategoryWalk(group, depth: 5, seen: new HashSet<string>()); }
            catch { label = ""; }
            return pathCategories[group.Signature] = label ?? "";
        }

        string CategoryWalk(RecipeVariantGroup group, int depth, HashSet<string> seen)
        {
            var reqs = BuildRequirements(group);
            if (reqs.Count == 0) return "";

            // A single consumed ingredient is a conversion step, not an origin — walk
            // through it while the trail stays unambiguous.
            if (reqs.Count == 1)
            {
                // The game's own food classification is the category a player means:
                // cider declares Fruit (mead included) or Grain per variant, so every
                // spirit path resolves one hop down — while spirits themselves say
                // NoNutrition and the walk continues through them.
                string food = FoodCategoryLabel(reqs[0]);
                if (food != null) return food;

                string code = reqs[0].LiquidCode ?? reqs[0].ExactCodes.FirstOrDefault();
                if (code == null || !seen.Add(code)) return FamilyLabel(reqs[0]);

                if (depth > 0)
                {
                    var producers = FindGroupsFor(code);
                    if (producers.Count == 1) return CategoryWalk(producers[0], depth - 1, seen);

                    // A fork (several ways to make the intermediate) only matters if the
                    // branches disagree about where they come from — apple juice made two
                    // ways is still apples both ways.
                    if (producers.Count > 1 && producers.Count <= 6)
                    {
                        string agreed = null;
                        bool unanimous = true;
                        foreach (var p in producers)
                        {
                            var branch = CategoryWalk(p, depth - 1, new HashSet<string>(seen));
                            if (string.IsNullOrEmpty(branch) || (agreed != null && branch != agreed))
                            {
                                unanimous = false;
                                break;
                            }
                            agreed = branch;
                        }
                        if (unanimous && agreed != null) return agreed;
                    }
                }
                return FamilyLabel(reqs[0]);
            }

            // Several ingredients: this recipe's shape is the origin story.
            var parts = reqs.Select(FamilyLabel)
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct()
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();
            return string.Join(" + ", parts);
        }

        /// <summary>The game's own food classification for a requirement's item, localized
        /// ("Fruit", "Grain") — null when the item declares none worth grouping by.
        /// Liquids carry theirs in nutritionPropsPerLitre, solids on the collectible.</summary>
        string FoodCategoryLabel(Requirement req)
        {
            var stack = req.LiquidStack ?? req.SampleStacks(capi.World).FirstOrDefault();
            if (stack?.Collectible == null) return null;

            EnumFoodCategory? category = null;
            try
            {
                var perLitre = ContainableProps(stack)?.NutritionPropsPerLitre;
                if (perLitre != null) category = perLitre.FoodCategory;
                else if (stack.Collectible.NutritionProps != null)
                    category = stack.Collectible.NutritionProps.FoodCategory;
            }
            catch { return null; }

            if (category == null || category == EnumFoodCategory.Unknown
                || category == EnumFoodCategory.NoNutrition) return null;

            string key = "foodcategory-" + category.ToString().ToLowerInvariant();
            string label = Lang.Get(key);
            return label == key ? category.ToString() : label;
        }

        /// <summary>
        /// The family word for one requirement: what its code's variant family is called.
        /// "flour-rye" and its siblings share the name tail "flour" → "Flour"; families
        /// whose variant names share nothing fall back to the capitalized code segment
        /// ("fruit-apple"/"fruit-blueberry" → "Fruit"); a dashless code is its own family
        /// and keeps its display name ("honeyportion" → "Honey").
        /// </summary>
        string FamilyLabel(Requirement req)
        {
            // The food classification outranks code families here too: a chain that dead
            // ends at "juiceportion-apple" is still Fruit by the game's own account.
            string food = FoodCategoryLabel(req);
            if (food != null) return food;

            string code = req.LiquidCode ?? req.ExactCodes.FirstOrDefault();
            if (code == null) return StripVariants(req.DisplayName);

            string path = new AssetLocation(code).Path;
            int dash = path.IndexOf('-');
            if (dash <= 0)
            {
                var name = req.LiquidStack?.GetName()
                    ?? req.SampleStacks(capi.World).FirstOrDefault()?.GetName();
                return name ?? NameForCode(code);
            }

            string family = path.Substring(0, dash);
            var samples = FamilySamples(family, max: 4);
            string shared = SharedNameTail(samples);
            if (!string.IsNullOrEmpty(shared))
                return char.ToUpper(shared[0]) + shared.Substring(1);
            return char.ToUpper(family[0]) + family.Substring(1);
        }

        readonly Dictionary<string, List<ItemStack>> familySamples = new Dictionary<string, List<ItemStack>>();

        List<ItemStack> FamilySamples(string family, int max)
        {
            if (familySamples.TryGetValue(family, out var cached)) return cached;

            var samples = new List<ItemStack>();
            string prefix = family + "-";
            try
            {
                foreach (var obj in AllCollectibles())
                {
                    if (obj.Code?.Path == null || !obj.Code.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    samples.Add(new ItemStack(obj));
                    if (samples.Count >= max) break;
                }
            }
            catch { /* fewer samples only weakens the label */ }
            return familySamples[family] = samples;
        }

        float maxBarrelLitres;

        /// <summary>Litres the biggest barrel in this world holds — read off the barrel
        /// blocks themselves (BlockBarrel is what sealed recipes run in), so a modded larger
        /// barrel raises every batch estimate with no work here.</summary>
        float MaxBarrelLitres()
        {
            if (maxBarrelLitres > 0) return maxBarrelLitres;

            float max = 0;
            try
            {
                foreach (var block in capi.World.Blocks)
                {
                    if (block is BlockBarrel barrel && barrel.CapacityLitres > max)
                        max = barrel.CapacityLitres;
                }
            }
            catch { /* fall through to the vanilla default */ }

            return maxBarrelLitres = max > 0 ? max : 50f;
        }

        /// <summary>
        /// Cooking-pot recipes that cook into a real item (cooksInto) — vanilla 1.22 makes
        /// acids, glue, potash, sulfate, leather and more this way, and mods use the same
        /// mechanism. Meal recipes (no cooksInto) are deliberately skipped: their output is a
        /// meal container whose identity lives in attributes, a different product entirely.
        /// The registry is client-resident and arrives resolved (RecipeRegistrySystem via
        /// ApiAdditions.GetCookingRecipes; CookingRecipe.FromBytes resolves ingredients and
        /// cooksInto — verified in the 1.22.6 decompile).
        /// </summary>
        void IndexCookingRecipes()
        {
            cookingByOutput = new Dictionary<string, List<CookingRecipe>>();
            List<CookingRecipe> recipes = null;
            try { recipes = capi.GetCookingRecipes(); }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] cooking recipes unavailable: {0}", e.Message);
            }
            if (recipes == null) return;

            foreach (var r in recipes)
            {
                if (r == null || !r.Enabled) continue;
                var output = r.CooksInto?.ResolvedItemstack;
                var code = output?.Collectible?.Code?.ToShortString();
                if (code == null) continue;
                if (!cookingByOutput.TryGetValue(code, out var list))
                {
                    list = new List<CookingRecipe>();
                    cookingByOutput[code] = list;
                }
                list.Add(r);
            }
        }

        Dictionary<string, List<CookingRecipe>> cookingByOutput;

        List<RecipeVariantGroup> CookingGroupsFor(string shortCode)
        {
            var groups = new List<RecipeVariantGroup>();
            if (shortCode == null || cookingByOutput == null
                || !cookingByOutput.TryGetValue(shortCode, out var recipes)) return groups;

            foreach (var r in recipes)
            {
                var output = r.CooksInto?.ResolvedItemstack;
                if (output == null) continue;
                groups.Add(new RecipeVariantGroup
                {
                    Cooking = r,
                    OutputCode = shortCode,
                    OutputPageCode = PageCode(output) ?? shortCode,
                    OutputName = output.GetName(),
                    OutputQuantity = Math.Max(1, output.StackSize),
                    OutputStack = output,
                    // No grid: the recipe code keeps Signature unique and stable across
                    // sessions, which is all Pattern is used for on this group.
                    Pattern = "cooking:" + r.Code,
                    Width = 0,
                    Height = 0,
                    LayoutCount = 1,
                    ServingsPerBatch = MaxCookingServings(),
                    OutputItemsPerLitre = ContainableProps(output)?.ItemsPerLitre ?? 0f
                });
            }
            return groups;
        }

        /// <summary>One choice in the volume calculator: a container family and its size.</summary>
        public class ContainerOption
        {
            public ItemStack Sample;
            public string Name;
            public float CapacityLitres;
        }

        List<ContainerOption> liquidContainerOptions;

        /// <summary>
        /// Every container in this world that can hold a liquid, one row per container
        /// *family* — sorted by capacity descending, which puts the barrel first without
        /// matching any name. Family is the code's first path segment plus capacity, not the
        /// display name: thirty jug colours already share a name, but Eternal Stew's
        /// cauldrons are "Copper cauldron", "Iron cauldron", … and name-keyed dedupe listed
        /// every metal (found by Mark). A merged family is labelled by the words its
        /// variants share ("Cauldron"), the same trick the shears tool row uses. Containers
        /// in VS are liquid-agnostic (containable-ness lives on the liquid), so one list
        /// serves every liquid. Cached per world.
        /// </summary>
        public List<ContainerOption> LiquidContainerOptions()
        {
            if (liquidContainerOptions != null) return liquidContainerOptions;

            var families = new Dictionary<string, (List<ItemStack> Samples, float Capacity)>();
            try
            {
                foreach (var block in capi.World.Blocks)
                {
                    if (!(block is BlockLiquidContainerBase container) || block.Code == null) continue;
                    float capacity = container.CapacityLitres;
                    if (capacity <= 0) continue;

                    string path = block.Code.Path;
                    int dash = path.IndexOf('-');
                    string family = $"{block.Code.Domain}:{(dash > 0 ? path.Substring(0, dash) : path)}|{capacity}";

                    if (!families.TryGetValue(family, out var entry))
                        families[family] = entry = (new List<ItemStack>(), capacity);
                    if (entry.Samples.Count < 8) entry.Samples.Add(new ItemStack(block));
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not list liquid containers: {0}", e.Message);
            }

            var options = new List<ContainerOption>();
            foreach (var entry in families.Values)
            {
                var names = entry.Samples.Select(s => s.GetName())
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                if (names.Count == 0) continue;

                string name = names.Count == 1 ? names[0] : SharedNameTail(entry.Samples);
                if (string.IsNullOrEmpty(name)) name = names[0];
                else if (char.IsLower(name[0])) name = char.ToUpper(name[0]) + name.Substring(1);

                options.Add(new ContainerOption
                {
                    Sample = entry.Samples[0],
                    Name = name,
                    CapacityLitres = entry.Capacity
                });
            }

            return liquidContainerOptions = options.OrderByDescending(o => o.CapacityLitres).ToList();
        }

        int maxCookingServings;

        /// <summary>Servings the best cooking pot in this world does per batch — read off the
        /// pot blocks themselves (BlockCookingContainer.MaxServingSize; vanilla's clay pot is
        /// 6), so a mod adding a bigger pot raises the answer with no work here. The answer
        /// cannot change within a session; worked out once per world.</summary>
        int MaxCookingServings()
        {
            if (maxCookingServings > 0) return maxCookingServings;

            int max = 0;
            try
            {
                foreach (var block in capi.World.Blocks)
                {
                    if (block is BlockCookingContainer pot && pot.MaxServingSize > max)
                        max = pot.MaxServingSize;
                }
            }
            catch { /* fall through to the vanilla default */ }

            return maxCookingServings = max > 0 ? max : 6;
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

            var groups = shortCode != null && byOutput.TryGetValue(shortCode, out var recipes)
                ? BuildGroups(recipes)
                : new List<RecipeVariantGroup>();

            // The other recipe kinds are real choices too — for acids, ferments, spirits
            // and juices they are the only one. Grid groups stay ahead of most kinds as the
            // default — EXCEPT smelting, which goes first: where both exist, the grid entry
            // is invariably a recycler ("chisel a copper anvil back into ingots") while
            // smelting is how anyone actually gets the item (Mark: "anvil chisel will be
            // the least used method").
            groups.InsertRange(0, SmithGroupsFor(shortCode));
            groups.InsertRange(0, SmeltGroupsFor(shortCode));
            groups.InsertRange(0, AlloyGroupsFor(shortCode));
            groups.AddRange(CookingGroupsFor(shortCode));
            groups.AddRange(BarrelGroupsFor(shortCode));
            groups.AddRange(DistillGroupsFor(shortCode));
            groups.AddRange(PressGroupsFor(shortCode));
            groups.AddRange(GrindGroupsFor(shortCode));
            groups.AddRange(CrushGroupsFor(shortCode));
            return groups;
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
            var result = exact.Count > 0 ? exact : all;
            // Construction builds ride along regardless of the page filter: they are keyed
            // to the STACK (its attributes name the build), not to a recipe output page.
            result.AddRange(ConstructionGroupsFor(stack));
            return result;
        }

        // ---- construction sites (vanilla sailboat, Shipwright's boats) -------------------
        //
        // A construction is an ENTITY whose type attributes carry `stages`, each stage
        // holding `requireStacks` of the game's own ConstructionIngredient — the mechanism
        // EntityBoatConstruction reads (decompile-verified 1.22.6), and the convention mods
        // like Shipwright copy verbatim, class and all. Entity types are synced to clients
        // with their attributes, so the whole build is derivable with no mod named.

        class ConstructionDef
        {
            public string EntityCode;
            public string BoatType;
            public string ClassName;
            public List<ConstructionMat> Totals;
        }

        List<ConstructionDef> constructions;

        /// <summary>The material values a placeholder row can take in this world: match the
        /// row's wildcard form against every collectible of its type and keep the single
        /// clean segment where the placeholder sat ("plank-{wood}" against plank-oak gives
        /// "oak"; multi-segment captures are variant noise and are dropped).</summary>
        List<string> MaterialChoices(ConstructionMat mat)
        {
            var choices = new List<string>();
            try
            {
                string wildcardPath = StripPlaceholders(mat.RawPath, out _);
                int star = wildcardPath.IndexOf('*');
                if (star < 0) return choices;
                string prefix = wildcardPath.Substring(0, star);
                string suffix = wildcardPath.Substring(star + 1);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var coll in mat.Ing.Type == EnumItemClass.Block
                         ? capi.World.Blocks.Cast<CollectibleObject>()
                         : capi.World.Items.Cast<CollectibleObject>())
                {
                    string p = coll?.Code?.Path;
                    if (p == null || !p.StartsWith(prefix) || !p.EndsWith(suffix)
                        || p.Length <= prefix.Length + suffix.Length) continue;
                    string captured = p.Substring(prefix.Length, p.Length - prefix.Length - suffix.Length);
                    if (captured.Length == 0 || captured.Contains('-')) continue;
                    if (seen.Add(captured)) choices.Add(captured);
                }
                choices.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return choices;
        }

        /// <summary>"plank-{wood}" → "plank-*", remembering the first placeholder variable —
        /// it is the storeWildCard binding ("build the whole boat of one wood"), and doubles
        /// as the collapsed row's name exactly the way a named wildcard's Name does.</summary>
        /// <summary>"plank-{wood}" with "oak" → "plank-oak".</summary>
        static string ReplacePlaceholders(string path, string value)
        {
            int i;
            while ((i = path.IndexOf('{')) >= 0)
            {
                int j = path.IndexOf('}', i);
                if (j < 0) break;
                path = path.Substring(0, i) + value + path.Substring(j + 1);
            }
            return path;
        }

        static string StripPlaceholders(string path, out string firstName)
        {
            firstName = null;
            int i;
            while ((i = path.IndexOf('{')) >= 0)
            {
                int j = path.IndexOf('}', i);
                if (j < 0) break;
                firstName ??= path.Substring(i + 1, j - i - 1);
                path = path.Substring(0, i) + "*" + path.Substring(j + 1);
            }
            return path;
        }

        void EnsureConstructionIndex()
        {
            if (constructions != null) return;
            constructions = new List<ConstructionDef>();
            var bySig = new Dictionary<string, ConstructionDef>();

            foreach (var et in capi.World?.EntityTypes ?? new List<EntityProperties>())
            {
                try
                {
                    var stagesAttr = et?.Attributes?["stages"];
                    if (stagesAttr == null || !stagesAttr.Exists) continue;
                    // Default domain "game", VERBATIM the game's own read
                    // (EntityBoatConstruction.Initialize, decompiled) — using the entity's
                    // domain instead made Shipwright's unprefixed "firewood" resolve as
                    // shipwright:firewood, which exists nowhere: rows showed raw codes,
                    // counted nothing and linked to no handbook page (found by Mark).
                    // Codes that spell their domain out still keep it.
                    var stages = stagesAttr.AsArray<ConstructionStage>(null, "game");
                    if (stages == null || stages.Length == 0) continue;

                    // Sum the stage demands per ingredient — "what does the whole build
                    // ask for", which is the question a shopping list answers.
                    var totals = new Dictionary<string, (ConstructionIngredient Ing, int Qty)>();
                    foreach (var stage in stages)
                    {
                        foreach (var ing in stage?.RequireStacks ?? Array.Empty<ConstructionIngredient>())
                        {
                            if (ing?.Code == null || ing.Quantity <= 0) continue;
                            string key = $"{ing.Type}|{ing.Code}|{ing.Name}";
                            totals[key] = totals.TryGetValue(key, out var t)
                                ? (t.Ing, t.Qty + ing.Quantity)
                                : (ing, ing.Quantity);
                        }
                    }
                    if (totals.Count == 0) continue;

                    var list = new List<ConstructionMat>();
                    foreach (var (ing, qty) in totals.Values)
                    {
                        string path = StripPlaceholders(ing.Code.Path, out string placeholder);
                        var built = new ConstructionIngredient
                        {
                            Type = ing.Type,
                            Code = new AssetLocation(ing.Code.Domain, path),
                            Quantity = qty,
                            // The author's label when one is written (a lang key like
                            // "shipbuilding-ingredient-logs"), else the placeholder
                            // variable ("wood") — what a named wildcard would carry.
                            Name = ing.Name ?? placeholder,
                            // Carries the uniform-material rule to the requirement builder:
                            // a stage row that binds or consumes the bound wildcard wants
                            // ONE material throughout, so its count is the best single
                            // variant carried, never a mixed-wood sum (Mark).
                            StoreWildCard = placeholder ?? ing.StoreWildCard,
                        };
                        if (path.Contains('*')) built.MatchingType = EnumRecipeMatchType.Wildcard;
                        try { built.Resolve(capi.World, "tallybook construction"); } catch { }
                        list.Add(new ConstructionMat
                        {
                            Ing = built,
                            RawPath = ing.Code.Path,
                            RawDomain = ing.Code.Domain,
                        });
                    }

                    string boattype = et.Attributes["boattype"]?.AsString();
                    // One def per distinct build: thirty wood variants of one construction
                    // entity all describe the same boat. The representative is the
                    // lexicographically FIRST code, not the first encountered — its code is
                    // part of the group's persisted signature, so it must not depend on
                    // registry iteration order between sessions.
                    string sig = (et.Class ?? "") + "|" + (boattype ?? "") + "|"
                        + string.Join(",", list.Select(x => $"{x.Ing.Code}:{x.Ing.Quantity}")
                            .OrderBy(s => s, StringComparer.Ordinal));
                    string entityCode = et.Code.ToShortString();

                    if (bySig.TryGetValue(sig, out var existing))
                    {
                        if (string.CompareOrdinal(entityCode, existing.EntityCode) < 0)
                            existing.EntityCode = entityCode;
                        continue;
                    }

                    var def = new ConstructionDef
                    {
                        EntityCode = entityCode,
                        BoatType = boattype,
                        ClassName = et.Class,
                        Totals = list,
                    };
                    bySig[sig] = def;
                    constructions.Add(def);
                }
                catch { /* one malformed entity must not cost the scan */ }
            }
        }

        /// <summary>
        /// The construction builds this stack can start, as recipe groups. Linking is the
        /// data's own statement, never a name guess: a collectible and a construction that
        /// declare the same `boattype` attribute value are one build (Shipwright's rollers
        /// do this per boat), and vanilla's roller item is paired with
        /// EntityBoatConstruction because ItemRoller spawns exactly that class
        /// (decompile-verified — the pairing is code, so the class IS the data). A mod
        /// reusing either convention links with zero compat work; anything else gets no
        /// button rather than a guessed one.
        /// </summary>
        public List<RecipeVariantGroup> ConstructionGroupsFor(ItemStack stack)
        {
            var groups = new List<RecipeVariantGroup>();
            try
            {
                if (stack?.Collectible == null) return groups;
                EnsureConstructionIndex();
                if (constructions.Count == 0) return groups;

                List<ConstructionDef> linked;
                string boattype = stack.Collectible.Attributes?["boattype"]?.AsString();
                if (!string.IsNullOrEmpty(boattype))
                {
                    linked = constructions.Where(c => c.BoatType == boattype).ToList();
                }
                else if (stack.Collectible is ItemRoller)
                {
                    linked = constructions.Where(c => c.ClassName == "EntityBoatConstruction").ToList();
                }
                else if (stack.Collectible is ItemBoat)
                {
                    // The boat's own handbook page is where a player looks first (Mark
                    // checked "Sailboat", not "Roller"). ItemBoat and EntityBoatConstruction
                    // are the same decompile-verified vanilla pairing as the roller; the
                    // type variant ("sailed") must appear in the construction's code so a
                    // raft — which is a grid recipe, not a build — links to nothing.
                    string typeVariant = stack.Collectible.Code.Path.Split('-').Skip(1).FirstOrDefault();
                    linked = typeVariant == null
                        ? new List<ConstructionDef>()
                        : constructions.Where(c => c.ClassName == "EntityBoatConstruction"
                            && c.EntityCode.Split('-').Contains(typeVariant)).ToList();
                }
                else
                {
                    return groups;
                }

                string shortCode = stack.Collectible.Code?.ToShortString();

                // The starter leads the list: the build needs its rollers as much as its
                // planks, and as an ordinary requirement row it can be expanded to ITS
                // recipe — so the roller craft and the site materials track together in
                // one tree (Mark). Vanilla's five-per-site is ItemRoller's own constant
                // ("Need 5 rolles…", and deconstructing returns GetItem("roller") ×5 —
                // both decompiled); a boat-page pin gets those same five rollers, and an
                // attribute-linked kit places itself, so one of itself.
                CollectibleObject starterColl = stack.Collectible;
                int starterQty = 1;
                if (stack.Collectible is ItemRoller) starterQty = 5;
                else if (stack.Collectible is ItemBoat)
                {
                    starterColl = capi.World.GetItem(new AssetLocation("roller"));
                    starterQty = 5;
                }

                ConstructionMat starter = null;
                if (starterColl?.Code != null)
                {
                    var starterIng = new ConstructionIngredient
                    {
                        Type = starterColl.ItemClass,
                        Code = starterColl.Code.Clone(),
                        Quantity = starterQty,
                    };
                    try { starterIng.Resolve(capi.World, "tallybook construction"); } catch { }
                    starter = new ConstructionMat
                    {
                        Ing = starterIng,
                        RawPath = starterColl.Code.Path,
                        RawDomain = starterColl.Code.Domain,
                    };
                }

                foreach (var def in linked)
                {
                    var totals = new List<ConstructionMat>();
                    if (starter != null) totals.Add(starter);
                    totals.AddRange(def.Totals);

                    var group = new RecipeVariantGroup
                    {
                        OutputCode = shortCode,
                        OutputPageCode = PageCode(stack) ?? shortCode,
                        OutputName = stack.GetName(),
                        OutputQuantity = 1,
                        OutputStack = stack,
                        Pattern = "construct:" + def.EntityCode,
                        Width = 0,
                        Height = 0,
                        LayoutCount = 1,
                        MethodLabel = "brought to the construction site, stage by stage",
                        Construction = totals,
                    };

                    // The material variable and its choices, from the first placeholder
                    // row ("plank-{wood}" → the world's woods): what the selector offers
                    // when the player commits the build to one material.
                    var placeholderMat = totals.FirstOrDefault(m => m.RawPath.Contains('{'));
                    if (placeholderMat != null)
                    {
                        group.BuildMaterialName = placeholderMat.Ing.StoreWildCard;
                        group.BuildMaterialChoices = MaterialChoices(placeholderMat);
                    }
                    groups.Add(group);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] construction lookup failed: {0}", e.Message);
            }
            return groups;
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
            catch
            {
                // Mirror PageCodeForStack's own format, including its lowercase class name —
                // "Block-" (enum casing) can never match a handbook index entry.
                return $"{stack.Class.Name()}-{stack.Collectible.Code.ToShortString()}";
            }
        }

        /// <summary>
        /// The page code to *open the handbook with* for this stack — distinct from
        /// <see cref="PageCode"/>, which is pin identity. The game's own open-handbook-for-stack
        /// flow (ModSystemSurvivalHandbook's hotkey handler, and every itemstack link inside
        /// handbook pages) asks the collectible first: classes like BlockMeal implement
        /// IHandBookPageCodeProvider to name the page that *represents* the stack, which for
        /// them is not the page PageCodeForStack derives — meals map to their recipe page, and
        /// mod classes do the same. Skipping that hop sends such stacks to a page code the
        /// index has never held, which reads as the handbook opening on nothing.
        ///
        /// Identity deliberately stays PageCodeForStack-based: the provider maps many stacks
        /// to one representative page, and keying pins on it would merge variants the rest of
        /// the mod treats as distinct.
        /// </summary>
        public static string HandbookPageCode(ItemStack stack, IWorldAccessor world)
        {
            if (stack?.Collectible?.Code == null) return null;
            try
            {
                string provided = stack.Collectible
                    .GetCollectibleInterface<IHandBookPageCodeProvider>()
                    ?.HandbookPageCodeForStack(world, stack);
                if (provided != null) return provided;
            }
            catch { /* a modded provider throwing must not cost the button */ }
            return PageCode(stack);
        }

        /// <summary>
        /// Page codes the handbook may actually hold for this stack, most specific first —
        /// the last resort before giving up and searching by name.
        ///
        /// The handbook builds one stack page per entry of
        /// <c>collectible.GetHandBookStacks(capi)</c> (decompile-verified:
        /// ModSystemSurvivalHandbook.SetupBehaviorAndGetItemStacks), and that list is not
        /// always made of the stacks players carry. Clutter is the case that found it: its
        /// handbook stack for a globe is <c>{ type: "globe1" }</c> — built as a bare
        /// JsonItemStack in BlockClutter.LoadTypes — while a globe you salvaged carries
        /// <c>{ type: "globe1", collected: true }</c>, because BlockBehaviorReparable stamps
        /// that on the drop. Two attributes against one, so the page code differs, the index
        /// has never held it, and the Handbook button fell through to a name search that
        /// found the page anyway — visibly the long way round (found by Mark).
        ///
        /// Asked of the game's own list rather than by dropping attributes until something
        /// matches: a candidate qualifies when what it carries is a subset of what we carry
        /// (<c>Satisfies</c>, the same direction the game uses everywhere else), and the most
        /// specific qualifying stack comes first — so a collectible whose pages genuinely DO
        /// differ by attribute still lands on the right one rather than its blandest sibling.
        /// Only ever consulted once an exact lookup has already missed, so a well-indexed
        /// stack can never be talked down to a coarser page.
        /// </summary>
        public static IEnumerable<string> RepresentativePageCodes(ItemStack stack, ICoreClientAPI capi)
        {
            if (stack?.Collectible == null || capi == null) return Enumerable.Empty<string>();
            try
            {
                var stacks = stack.Collectible.GetHandBookStacks(capi);
                if (stacks == null) return Enumerable.Empty<string>();
                return stacks
                    .Where(hs => hs != null && hs.Satisfies(stack))
                    .OrderByDescending(hs => hs.Attributes?.Count ?? 0)
                    .Select(PageCode)
                    .Where(c => c != null)
                    .Distinct()
                    .ToList();
            }
            catch { return Enumerable.Empty<string>(); }
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

            // A pinned liquid (sulfuric acid, milk) can only ever be carried inside a
            // container, so its self count must look there — and its numbers must read in
            // litres. TallyService switches both off for errand pins, whose hand-over check
            // inspects bare slot stacks and whose counts come from dialogue in items.
            var liquidProps = ContainableProps(stack);
            if (liquidProps != null)
            {
                req.CountContainerContents = true;
                req.ShowLitres = true;
                req.ItemsPerLitre = liquidProps.ItemsPerLitre;
            }

            req.ExactCodes.Add(stack.Collectible.Code.ToShortString());
            req.PresetSampleStack(stack);
            return req;
        }

        /// <summary>Is this the kind of item that lives in liquid containers? Asked of the
        /// game's own containable props rather than the item's class, so modded liquids that
        /// skip ItemLiquidPortion still qualify.</summary>
        static bool IsContainableLiquid(ItemStack stack) => ContainableProps(stack) != null;

        /// <summary>The stack's containable-liquid props, or null when it is not a liquid.</summary>
        static WaterTightContainableProps ContainableProps(ItemStack stack)
        {
            try
            {
                var props = BlockLiquidContainerBase.GetContainableProps(stack);
                return props != null && props.Containable ? props : null;
            }
            catch { return null; }
        }

        /// <summary>A plain stack for a known vanilla code — how the story tracker pins the
        /// items it names. Null when this world does not have the collectible, which callers
        /// treat as "then don't pin it".</summary>
        public static ItemStack StackFor(IWorldAccessor world, string code, bool isBlock)
        {
            try
            {
                var loc = new AssetLocation(code);
                if (isBlock)
                {
                    var block = world.GetBlock(loc);
                    return block == null ? null : new ItemStack(block);
                }
                var item = world.GetItem(loc);
                return item == null ? null : new ItemStack(item);
            }
            catch { return null; }
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

            // A liquid row's ExactCodes are its *vessels* when it came from a grid recipe;
            // expanding it means asking how to make the liquid, never how to make a bucket.
            var codes = (req.IsLiquid && req.CookingIngredient == null
                ? (IEnumerable<string>)(req.LiquidCode != null ? new[] { req.LiquidCode } : Array.Empty<string>())
                : req.ExactCodes).ToList();

            var recipes = codes
                .Where(code => byOutput.ContainsKey(code))
                .SelectMany(code => byOutput[code]);

            var groups = BuildGroups(recipes, collapseOutputs: true);

            // Every other recipe kind answers expansions too — this is what lets a barrel
            // of brandy walk back through the boiler, the fermenting barrel and the fruit
            // press to the orchard, one deliberate expand at a time.
            foreach (var code in codes.Distinct())
            {
                groups.InsertRange(0, SmithGroupsFor(code));
                groups.InsertRange(0, SmeltGroupsFor(code));
                groups.InsertRange(0, AlloyGroupsFor(code));
                groups.AddRange(CookingGroupsFor(code));
                groups.AddRange(BarrelGroupsFor(code));
                groups.AddRange(DistillGroupsFor(code));
                groups.AddRange(PressGroupsFor(code));
                groups.AddRange(GrindGroupsFor(code));
                groups.AddRange(CrushGroupsFor(code));
            }
            return groups;
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
                        Collapsed = collapseOutputs,
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
            foreach (var (ing, qty) in ConsumedIngredients(recipe)) parts.Add($"I{qty}x{MaterialToken(recipe, ing)}");
            foreach (var (ing, _) in ToolCells(recipe)) parts.Add($"K{MaterialToken(recipe, ing)}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        /// <summary>Variant-blind identity of one ingredient: the author's word for what varies
        /// when there is one, else the matcher itself (a bare wildcard, regex or tag condition
        /// is already variant-blind, so its own key is the right token).
        ///
        /// A liquid-bearing ingredient's identity is the liquid, not the vessel: dough is
        /// authored three times — bucket of water, bowl of water, jug of water — and to a
        /// shopper those are one recipe wanting one litre of water, so they must share a token
        /// (and thereby a group, whose requirement row then accepts every vessel). Litres stay
        /// in the token so a recipe wanting more of the same liquid remains a distinct choice.</summary>
        string MaterialToken(GridRecipe recipe, CraftingRecipeIngredient ing)
        {
            var demand = LiquidDemandFor(recipe, ing);
            if (demand != null)
            {
                return "L|" + demand.Litres.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                     + "|" + demand.Content.Code;
            }
            return string.IsNullOrEmpty(ing.Name) ? MatcherKey(ing) : $"{ing.Type}|${ing.Name}";
        }

        // ---- liquid ingredients ---------------------------------------------------------

        /// <summary>What a liquid-bearing ingredient really wants: which liquid, how much.</summary>
        class LiquidDemand
        {
            /// <summary>The recipe's own requiresContent matcher, resolved. Counting delegates
            /// to its Matches() — the same check BlockLiquidContainerBase.MatchesForCrafting
            /// runs at craft time, so we can never claim liquid the grid would refuse.</summary>
            public JsonItemStack Content;
            public float Litres;
            public float ItemsPerLitre;
            /// <summary>Litres × items-per-litre: the demand in portion items, ≥ 1.</summary>
            public int Items;
        }

        readonly Dictionary<(GridRecipe, CraftingRecipeIngredient), LiquidDemand> liquidDemands
            = new Dictionary<(GridRecipe, CraftingRecipeIngredient), LiquidDemand>();

        readonly Dictionary<string, bool> liquidContainerMatchers = new Dictionary<string, bool>();

        /// <summary>
        /// The liquid an ingredient demands inside its container, or null for ordinary
        /// ingredients. Mirrors BlockLiquidContainerBase.MatchesForCrafting's lookup order
        /// (verified in the 1.22.6 decompile): the ingredient's own recipeAttributes
        /// (requiresContent + requiresLitres — the dough style), else the recipe-level
        /// attributes.liquidContainerProps (the bandage style). The recipe-level form names no
        /// ingredient, and at craft time only liquid-container collectibles ever run the
        /// check — so it is applied here only to ingredients a liquid container can satisfy.
        /// </summary>
        LiquidDemand LiquidDemandFor(GridRecipe recipe, CraftingRecipeIngredient ing)
        {
            if (ing == null) return null;
            // Nearly every ingredient of every recipe passes through here when groups are
            // built; the ones that can possibly carry a liquid are the handful with any
            // attributes at all, so answer "no" for the rest without touching the cache.
            if (ing.RecipeAttributes == null && recipe?.Attributes == null) return null;
            if (liquidDemands.TryGetValue((recipe, ing), out var cached)) return cached;

            LiquidDemand demand = null;
            try
            {
                JsonObject props = null;
                var own = ing.RecipeAttributes;
                if (own?.Exists == true && own["requiresContent"].Exists) props = own;
                else
                {
                    var recipeLevel = recipe?.Attributes?["liquidContainerProps"];
                    if (recipeLevel?.Exists == true && recipeLevel["requiresContent"].Exists
                        && IsLiquidContainerMatcher(ing))
                    {
                        props = recipeLevel;
                    }
                }

                if (props != null)
                {
                    var content = props["requiresContent"].AsObject<JsonItemStack>(null);
                    if (content != null && content.Resolve(capi.World, "[tallybook] liquid ingredient"))
                    {
                        float litres = props["requiresLitres"].AsFloat(1f);
                        float ipl = BlockLiquidContainerBase
                            .GetContainableProps(content.ResolvedItemstack)?.ItemsPerLitre ?? 1f;
                        demand = new LiquidDemand
                        {
                            Content = content,
                            Litres = litres,
                            ItemsPerLitre = ipl,
                            Items = Math.Max(1, (int)Math.Round(litres * ipl))
                        };
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read a liquid ingredient: {0}", e.Message);
            }

            // Negative results cache too — MaterialToken asks for every ingredient of every
            // recipe it groups, and re-parsing the answer "no" would be the whole cost.
            liquidDemands[(recipe, ing)] = demand;
            return demand;
        }

        /// <summary>Could a liquid container satisfy this matcher? Exact ingredients answer
        /// from their resolved stack; wildcards ask the world once and the answer is cached —
        /// only recipes that carry liquidContainerProps ever get here.</summary>
        bool IsLiquidContainerMatcher(CraftingRecipeIngredient ing)
        {
            if (ing.ResolvedItemStack != null)
                return ing.ResolvedItemStack.Collectible is BlockLiquidContainerBase;

            string key = MatcherKey(ing);
            if (liquidContainerMatchers.TryGetValue(key, out var known)) return known;

            bool found = false;
            try
            {
                var candidates = ing.Type == EnumItemClass.Block
                    ? capi.World.Blocks.Cast<CollectibleObject>()
                    : capi.World.Items.Cast<CollectibleObject>();
                foreach (var obj in candidates)
                {
                    if (!(obj is BlockLiquidContainerBase) || obj.Code == null) continue;
                    if (ing.Code != null && ing.Code.Path.Contains('*')
                        && !WildcardUtil.Match(ing.Code, obj.Code)) continue;
                    if (!ing.SatisfiesAsIngredient(new ItemStack(obj), false)) continue;
                    found = true;
                    break;
                }
            }
            catch { /* treated as not a container */ }

            return liquidContainerMatchers[key] = found;
        }

        /// <summary>
        /// Collapse a variant group into one requirement per ingredient row, each accepting
        /// every variant the group's recipes accept.
        /// </summary>
        public List<Requirement> BuildRequirements(RecipeVariantGroup group, bool tools = false)
        {
            if (group?.Construction != null) return BuildConstructionRequirements(group, tools);
            if (group?.Cooking != null) return BuildCookingRequirements(group, tools);
            if (group?.Barrel != null) return BuildBarrelRequirements(group, tools);
            if (group?.Alloy != null) return BuildAlloyRequirements(group, tools);
            if (group?.Smithing != null) return BuildSmithingRequirements(group, tools);
            if (group?.DistillFrom != null || group?.PressFrom != null)
                return BuildAttributeRequirements(group, tools);

            var reqs = new List<Requirement>();
            if (group?.Recipes == null || group.Recipes.Count == 0) return reqs;

            var representative = group.Recipes[0];
            var baseCells = tools ? ToolCells(representative) : ConsumedIngredients(representative);
            foreach (var (ing, qty) in baseCells)
            {
                var req = new Requirement { Quantity = qty, CellQuantity = qty, IsTool = tools };

                // A container-of-liquid ingredient's real demand is the liquid: rescale the
                // row to portion items (the unit the game itself checks litres in), and keep
                // the container matchers — counting accepts the liquid only inside a vessel
                // the recipe accepts.
                var demand = tools ? null : LiquidDemandFor(representative, ing);
                if (demand != null)
                {
                    req.LiquidMatcher = demand.Content;
                    req.LiquidStack = demand.Content.ResolvedItemstack;
                    req.ItemsPerLitre = demand.ItemsPerLitre;
                    req.LitresPerCraft = demand.Litres * qty;
                    req.Quantity = demand.Items * qty;
                }

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
                    // Grid-cell count, not Quantity — a liquid row's Quantity was rescaled to
                    // portion items and would never equal another recipe's cell count again.
                    if (cells[i].Quantity != reqs[i].CellQuantity) continue;
                    // Non-collapsed groups only: same *ingredient*, not just same amount. Cell
                    // order is grid order while the group key's material signature is sorted,
                    // so a variant recipe with the same materials in a permuted grid lands
                    // here misaligned, and merging by index alone puts its resin into the
                    // plank row — after which the plank row happily counts resin (found by
                    // Fable's review). Tokens are variant-blind, so thirty woods still merge.
                    //
                    // Collapsed (expansion) groups must NOT get this gate: their members are
                    // variants of one row, separately-authored ones carry different exact
                    // codes and no name, and refusing those merges would shrink "any clay"
                    // back to whichever clay the representative uses.
                    if (!group.Collapsed
                        && MaterialToken(recipe, cells[i].Ingredient)
                           != MaterialToken(representative, reqs[i].Sample)) continue;
                    AddMatcher(reqs[i], cells[i].Ingredient);
                }
            }

            foreach (var req in reqs)
            {
                ResolveVariants(req);
                if (req.IsLiquid)
                {
                    // The row is the liquid, said as the liquid: "Water (in Bowl)" — the
                    // vessel stays in the name because an accepted container is still part
                    // of the demand. Icon likewise: the report this fixes was a dye recipe
                    // showing a bare bowl.
                    string liquid = req.LiquidStack?.GetName() ?? BuildDisplayName(req);
                    string vessel = ContainerLabel(req);
                    req.DisplayName = vessel == null ? liquid : $"{liquid} (in {vessel})";
                    req.PresetSampleStack(req.LiquidStack);
                }
                else
                {
                    req.DisplayName = BuildDisplayName(req);
                }
            }

            // Remember what this recipe takes, so a choice between recipes can be made on
            // what you would have to go and find rather than on a number.
            if (!tools && group.Materials == null)
            {
                // With quantities: the hunter backpack's recipes differ by *how many* pelts,
                // not by which, so a list of bare names makes four distinct recipes read as
                // four identical ones. Liquid rows state litres — "100 × Water" would be
                // portion items wearing an item count's clothes.
                var summary = string.Join(", ",
                    reqs.Select(r => r.IsLiquid
                        ? $"{r.LitresText(r.Quantity)} L {StripVariants(r.DisplayName)}"
                        : $"{r.Quantity} × {StripVariants(r.DisplayName)}"));
                group.MaterialsBrief = summary;

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
        /// Requirement rows for a construction build: the summed stage demands, matched by
        /// the game's own ingredient machinery (ConstructionIngredient IS a
        /// CraftingRecipeIngredient). No tool rows: everything a stage takes — the
        /// plumb-and-squares included — is in requireStacks and consumed, so it all counts
        /// as materials. An authored label ("shipbuilding-ingredient-logs") resolves
        /// through Lang; a placeholder-bound wildcard reads like any named wildcard
        /// ("Board (any wood)") — one wood in play, since the site binds the first match.
        /// </summary>
        List<Requirement> BuildConstructionRequirements(RecipeVariantGroup group, bool tools)
        {
            var reqs = new List<Requirement>();
            if (tools) return reqs;

            foreach (var mat in group.Construction)
            {
                var ing = mat.Ing;
                bool bound = false;

                // A committed material ("oak") substitutes back into the authored code:
                // "plank-{wood}" becomes the exact plank-oak, and the binding wildcard
                // ("log-placed-*") narrows to log-placed-oak* — the rows then NAME and
                // COUNT that material only, which is what picking an oak boat means
                // (Mark). Falls back to the unbound row if the substitution resolves to
                // nothing in this world.
                if (group.BuildMaterial != null && ing.StoreWildCard != null)
                {
                    string boundPath = mat.RawPath.Contains('{')
                        ? ReplacePlaceholders(mat.RawPath, group.BuildMaterial)
                        : StripPlaceholders(mat.RawPath, out _)
                            .Replace("*", group.BuildMaterial + "*");
                    var boundIng = new ConstructionIngredient
                    {
                        Type = ing.Type,
                        Code = new AssetLocation(mat.RawDomain, boundPath),
                        Quantity = ing.Quantity,
                        Name = ing.Name,
                    };
                    if (boundPath.Contains('*')) boundIng.MatchingType = EnumRecipeMatchType.Wildcard;
                    bool resolved = false;
                    try { resolved = boundIng.Resolve(capi.World, "tallybook construction"); } catch { }
                    if (resolved || boundPath.Contains('*'))
                    {
                        ing = boundIng;
                        bound = true;
                    }
                }

                var req = new Requirement
                {
                    Quantity = ing.Quantity,
                    CellQuantity = ing.Quantity,
                    // Before any matcher work: the flag is part of the requirement's
                    // pooling key, and the key caches on first read.
                    UniformVariants = !bound && mat.Ing.StoreWildCard != null,
                };
                if (req.UniformVariants)
                {
                    // Where the wood sits in this row's codes, for material-wise counting.
                    string wildcardPath = StripPlaceholders(mat.RawPath, out _);
                    int star = wildcardPath.IndexOf('*');
                    if (star >= 0)
                    {
                        req.UniformPrefix = wildcardPath.Substring(0, star);
                        req.UniformSuffix = wildcardPath.Substring(star + 1);
                    }
                }
                AddMatcher(req, ing);
                ResolveVariants(req);
                string langName = ing.Name == null ? null : Lang.GetIfExists(ing.Name);
                // Bound rows prefer the concrete item's own name ("Oak board") over the
                // authored group label.
                req.DisplayName = bound
                    ? BuildDisplayName(req)
                    : langName ?? BuildDisplayName(req);
                // "any wood" would be a lie on an unbound uniform row — the site takes
                // one wood, your choice, throughout.
                if (req.UniformVariants && req.DisplayName != null)
                    req.DisplayName = req.DisplayName.Replace("(any ", "(one ");
                reqs.Add(req);
            }

            // The wood-bound rows are ONE decision: they count the same wood, chosen as
            // whichever single wood carries the whole build furthest. Wired as a shared
            // sibling set so the counting can make that choice jointly.
            var uniformSet = reqs.Where(r => r.UniformVariants).ToList();
            if (uniformSet.Count > 1)
            {
                foreach (var r in uniformSet) r.UniformSet = uniformSet;
            }

            if (group.Materials == null)
            {
                group.MaterialsBrief = string.Join(", ",
                    reqs.Select(r => $"{r.Quantity} × {StripVariants(r.DisplayName)}"));
                group.Materials = group.MaterialsBrief
                    + (group.MethodLabel != null ? $" — {group.MethodLabel}" : "");
            }
            return reqs;
        }

        /// <summary>
        /// Requirement rows for a cooking-pot recipe. Solids come from the ingredient's
        /// resolved valid stacks (counting also delegates to the ingredient's own Matches, the
        /// game's authority on what may go in the pot); a liquid ingredient (portionSizeLitres)
        /// becomes a liquid row counted from any carried container — cooking pours the liquid
        /// in, so no vessel is part of the demand. Quantities use MinQuantity: the cheapest
        /// honest floor, same principle as a group's cheapest grid layout.
        /// </summary>
        List<Requirement> BuildCookingRequirements(RecipeVariantGroup group, bool tools)
        {
            var reqs = new List<Requirement>();
            // The pot and the fire are the mechanic, not data the recipe carries — inventing
            // a pot requirement would mean guessing item codes by name.
            if (tools) return reqs;

            foreach (var cing in group.Cooking.Ingredients ?? Array.Empty<CookingRecipeIngredient>())
            {
                if (cing == null) continue;
                int qty = Math.Max(1, cing.MinQuantity);
                var req = new Requirement
                {
                    Quantity = qty,
                    CellQuantity = qty,
                    CookingIngredient = cing,
                    VariantLabel = cing.TypeName ?? cing.Code
                };

                var samples = new List<ItemStack>();
                foreach (var vs in cing.ValidStacks ?? Array.Empty<CookingRecipeStack>())
                {
                    var s = vs?.ResolvedItemstack;
                    if (s?.Collectible?.Code == null) continue;
                    samples.Add(s);
                    req.ExactCodes.Add(s.Collectible.Code.ToShortString());
                }

                var liquid = cing.PortionSizeLitres > 0 ? samples.FirstOrDefault(IsContainableLiquid) : null;
                if (liquid != null)
                {
                    float ipl = BlockLiquidContainerBase.GetContainableProps(liquid)?.ItemsPerLitre ?? 1f;
                    req.LiquidMatcher = (cing.ValidStacks ?? Array.Empty<CookingRecipeStack>())
                        .FirstOrDefault(vs => vs?.ResolvedItemstack == liquid);
                    req.LiquidStack = liquid;
                    req.AnyVessel = true;
                    req.ItemsPerLitre = ipl;
                    req.LitresPerCraft = cing.PortionSizeLitres * qty;
                    req.Quantity = Math.Max(1, (int)Math.Round(req.LitresPerCraft * ipl));
                    req.DisplayName = liquid.GetName();
                    req.PresetSampleStack(liquid);
                }
                else
                {
                    req.MatchedVariants = req.ExactCodes.Count;
                    if (samples.Count > 0) req.PresetSampleStacks(samples);
                    req.DisplayName = BuildDisplayName(req);
                }
                reqs.Add(req);
            }

            if (group.Materials == null)
            {
                var summary = string.Join(", ",
                    reqs.Select(r => r.IsLiquid
                        ? $"{r.LitresText(r.Quantity)} L {StripVariants(r.DisplayName)}"
                        : $"{r.Quantity} × {StripVariants(r.DisplayName)}"));
                group.MaterialsBrief = summary;

                // Say what one pot can do: for a liquid output that is a hard per-load cap
                // ("up to 6 L per pot"), which is exactly the number a player planning a big
                // cook needs to see.
                string perLoad = " — cooked in a pot";
                var outputProps = ContainableProps(group.OutputStack);
                if (outputProps != null && group.ServingsPerBatch > 0)
                {
                    float ipl = outputProps.ItemsPerLitre <= 0 ? 1f : outputProps.ItemsPerLitre;
                    float litres = group.ServingsPerBatch * group.OutputQuantity / ipl;
                    perLoad += ", up to " + litres.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                             + " L per pot";
                }

                group.Materials = summary.Length > 0 ? summary + perLoad : perLoad.Substring(3);
            }
            return reqs;
        }

        /// <summary>
        /// Requirement rows for a sealed-barrel recipe. Liquid ingredients (litres on the
        /// ingredient) become any-vessel liquid rows — the barrel is filled by pouring —
        /// matched by the recipe's own ingredient (a CraftingRecipeIngredient). Solids count
        /// normally; kept ingredients (consume:false) land in the tools list, as with grid
        /// recipes. One craft = the recipe's base quantities; seal-count math lives on the
        /// group (BatchLitres / LitresPerCraft).
        /// </summary>
        List<Requirement> BuildBarrelRequirements(RecipeVariantGroup group, bool tools)
        {
            var reqs = new List<Requirement>();
            foreach (var ing in group.Barrel.Ingredients ?? Array.Empty<BarrelRecipeIngredient>())
            {
                if (ing == null || IsKept(ing) != tools) continue;

                var req = new Requirement { IsTool = tools };
                if (ing.Litres > 0)
                {
                    var liquid = ing.ResolvedItemStack;
                    float ipl = liquid == null ? 1f : (ContainableProps(liquid)?.ItemsPerLitre ?? 1f);
                    req.LiquidStack = liquid;
                    req.LiquidContentMatcher = ing;
                    req.AnyVessel = true;
                    req.ItemsPerLitre = ipl;
                    req.LitresPerCraft = ing.Litres;
                    req.Quantity = Math.Max(1, (int)Math.Round(ing.Litres * ipl));
                    req.CellQuantity = req.Quantity;
                    AddMatcher(req, ing);
                    req.DisplayName = liquid?.GetName() ?? IngredientName(ing);
                    if (liquid != null) req.PresetSampleStack(liquid);
                }
                else
                {
                    req.Quantity = Math.Max(1, ing.Quantity);
                    req.CellQuantity = req.Quantity;
                    AddMatcher(req, ing);
                    ResolveVariants(req);
                    req.DisplayName = BuildDisplayName(req);
                }
                reqs.Add(req);
            }

            if (!tools && group.Materials == null)
            {
                var summary = string.Join(", ",
                    reqs.Select(r => r.IsLiquid
                        ? $"{r.LitresText(r.Quantity)} L {StripVariants(r.DisplayName)}"
                        : $"{r.Quantity} × {StripVariants(r.DisplayName)}"));
                group.MaterialsBrief = summary;
                int days = (int)Math.Round(group.SealHours / 24.0);
                string how = days > 0
                    ? $" — sealed in a barrel, ~{days} day{(days == 1 ? "" : "s")}"
                    : " — mixed in a barrel";
                group.Materials = summary.Length > 0 ? summary + how : how.Substring(3);
            }
            return reqs;
        }

        /// <summary>
        /// Requirement rows for a crucible alloy: one row per METAL, counted in the game's
        /// own metal units (an ingot-equivalent is 100) and accepting only crucible-fitting
        /// forms — the recipe JSON names ingots as the unit the ratios are written against,
        /// while the crucible takes nuggets and bits (Mark). One craft = one output, at the
        /// midpoint of each metal's ratio window (60/25/15 units per bismuth bronze ingot);
        /// row names carry the real window.
        /// </summary>
        List<Requirement> BuildAlloyRequirements(RecipeVariantGroup group, bool tools)
        {
            const int UnitsPerBase = 100;
            var reqs = new List<Requirement>();
            if (tools) return reqs;

            int craftSize = Math.Max(1, group.OutputQuantity);
            foreach (var ing in group.Alloy.Ingredients)
            {
                var baseStack = ing?.ResolvedItemstack;
                var baseCode = baseStack?.Collectible?.Code?.ToShortString();
                if (baseCode == null) continue;

                float mid = (ing.MinRatio + ing.MaxRatio) / 2f;
                var req = new Requirement
                {
                    Quantity = Math.Max(1, (int)Math.Round(mid * craftSize * UnitsPerBase)),
                    UnitsPerItem = new Dictionary<string, int>()
                };
                req.CellQuantity = req.Quantity;

                // Only forms the crucible actually takes count: everything that smelts
                // INTO this metal — nuggets, bits, modded equivalents — each weighed by
                // its share (20 nuggets to the ingot makes a nugget 5 units). The ingot
                // itself is deliberately NOT counted: a crucible refuses whole ingots
                // (Mark) — the player chisels them into bits first, at which point the
                // bits count. Same honesty rule as liquids in unaccepted vessels.
                var samples = new List<ItemStack>();
                if (smeltByOutput != null && smeltByOutput.TryGetValue(baseCode, out var sources))
                {
                    foreach (var (input, output, ratio) in sources)
                    {
                        var code = input?.Collectible?.Code?.ToShortString();
                        if (code == null || ratio <= 0) continue;
                        int per = (int)Math.Round(UnitsPerBase * Math.Max(1, output.StackSize) / ratio);
                        if (per <= 0) continue;
                        req.UnitsPerItem[code] = per;
                        req.ExactCodes.Add(code);
                        if (samples.Count < 8) samples.Add(input);
                    }
                }
                if (req.UnitsPerItem.Count == 0)
                {
                    // A mod alloy whose metal has no meltable forms we can see: the base
                    // item is the only countable thing left — degraded but not blind.
                    req.UnitsPerItem[baseCode] = UnitsPerBase;
                    req.ExactCodes.Add(baseCode);
                    samples.Add(baseStack);
                }

                req.MatchedVariants = req.ExactCodes.Count;
                req.PresetSampleStacks(samples);
                req.DisplayName = $"{MetalName(baseStack)} — bits, nuggets or any meltable "
                    + $"({Percent(ing.MinRatio)}–{Percent(ing.MaxRatio)}%)";
                reqs.Add(req);
            }

            if (group.Materials == null)
            {
                group.MaterialsBrief = string.Join(", ",
                    reqs.Select(r => $"{r.Quantity} units {StripVariants(r.DisplayName)}"));
                group.Materials = group.MaterialsBrief + " — alloyed in a crucible";
            }
            return reqs;
        }

        static string Percent(float ratio) => Math.Round(ratio * 100).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The metal's bare name via the game's own material-* lang convention
        /// ("ingot-bismuthbronze" → "Bismuth bronze") — the same names the handbook's
        /// "Alloyed from" line uses. Falls back to the item's display name when a modded
        /// metal skips the convention.</summary>
        static string MetalName(ItemStack baseStack)
        {
            string path = baseStack?.Collectible?.Code?.Path;
            int dash = path?.IndexOf('-') ?? -1;
            if (dash > 0)
            {
                string key = "material-" + path.Substring(dash + 1);
                string name = Lang.Get(key);
                if (name != key) return name;
            }
            return baseStack?.GetName() ?? "?";
        }

        /// <summary>The longest run of words all sample names START with: "Copper ingot" +
        /// "Copper nugget" → "Copper". The head-side twin of SharedNameTail, for families
        /// whose names vary at the end.</summary>
        static string SharedNameHead(List<ItemStack> samples)
        {
            if (samples == null || samples.Count < 2) return null;
            string[] head = null;
            foreach (var stack in samples)
            {
                var words = stack?.GetName()?.Split(' ');
                if (words == null || words.Length == 0) return null;
                if (head == null) { head = words; continue; }

                int shared = 0;
                while (shared < head.Length && shared < words.Length
                       && string.Equals(head[shared], words[shared], StringComparison.OrdinalIgnoreCase)) shared++;
                if (shared == 0) return null;
                if (shared < head.Length) head = head.Take(shared).ToArray();
            }
            return head == null ? null : string.Join(" ", head);
        }

        /// <summary>
        /// Requirement rows for the synthesized kinds: distillation is one any-vessel liquid
        /// row of 1/ratio litres per litre distilled; pressing is one item per craft.
        /// </summary>
        List<Requirement> BuildAttributeRequirements(RecipeVariantGroup group, bool tools)
        {
            var reqs = new List<Requirement>();
            if (tools) return reqs;

            if (group.DistillFrom != null)
            {
                float ipl = ContainableProps(group.DistillFrom)?.ItemsPerLitre ?? 1f;
                float litresPerCraft = 1f / group.DistillRatio;
                var req = new Requirement
                {
                    LiquidStack = group.DistillFrom,
                    AnyVessel = true,
                    ItemsPerLitre = ipl,
                    LitresPerCraft = litresPerCraft,
                    Quantity = Math.Max(1, (int)Math.Round(litresPerCraft * ipl)),
                    DisplayName = group.DistillFrom.GetName()
                };
                req.CellQuantity = req.Quantity;
                req.ExactCodes.Add(group.DistillFrom.Collectible.Code.ToShortString());
                req.PresetSampleStack(group.DistillFrom);
                reqs.Add(req);

                if (group.Materials == null)
                {
                    group.MaterialsBrief = $"{req.LitresText(req.Quantity)} L {StripVariants(req.DisplayName)} per litre";
                    group.Materials = group.MaterialsBrief + " — distilled in a boiler";
                }
            }
            else if (group.PressFrom != null)
            {
                int qty = Math.Max(1, (int)Math.Round(group.InputsPerCraft));
                var req = new Requirement { Quantity = qty, CellQuantity = qty };
                req.ExactCodes.Add(group.PressFrom.Collectible.Code.ToShortString());
                req.MatchedVariants = 1;
                req.PresetSampleStack(group.PressFrom);
                req.DisplayName = group.PressFrom.GetName();
                reqs.Add(req);

                if (group.Materials == null)
                {
                    group.MaterialsBrief = $"{qty} × {req.DisplayName}";
                    group.Materials = group.MaterialsBrief
                        + (group.MethodLabel != null ? $" — {group.MethodLabel}" : "");
                }
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
            else
            {
                // Prefix-style families vary the front of the name instead: nine metals of
                // "… shears" all satisfy a tool-shears tag, and calling the row "Copper
                // shears" tells an iron-shears owner they lack the tool. The words every
                // sampled variant shares are the set's name; sharing none, the first
                // member's name is still the best available.
                string shared = SharedNameTail(samples);
                if (!string.IsNullOrEmpty(shared))
                    name = char.ToUpper(shared[0]) + shared.Substring(1);
            }

            string what = req.VariantLabel;
            return string.IsNullOrEmpty(what)
                ? $"{name} (any, {variants} variants)"
                : $"{name} (any {what}, {variants} variants)";
        }

        /// <summary>The vessels a liquid row accepts, for its name: "Bucket", "Bucket / Bowl",
        /// "Bucket / Bowl / …". Reads the container samples, so it must run before the row's
        /// icon is repointed at the liquid.</summary>
        string ContainerLabel(Requirement req)
        {
            var names = new List<string>();
            void AddName(ItemStack s)
            {
                var n = s?.GetName();
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }

            foreach (var s in req.SampleStacks(capi.World)) AddName(s);
            // A merged row (bucket exact + bowl/jug wildcards) short-circuits ResolveVariants
            // on its exact codes, leaving the wildcard vessels sampleless — and a label that
            // hides an accepted vessel tells the player their bowl won't do.
            foreach (var m in req.OtherMatchers)
            {
                if (m.ResolvedItemStack == null) AddName(FirstMatchSample(m));
            }

            if (names.Count == 0) return null;
            if (names.Count <= 2) return string.Join(" / ", names);
            return $"{names[0]} / {names[1]} / …";
        }

        readonly Dictionary<string, ItemStack> matcherSamples = new Dictionary<string, ItemStack>();

        /// <summary>First collectible in the world this matcher accepts, cached per matcher —
        /// the answer cannot change within a session.</summary>
        ItemStack FirstMatchSample(CraftingRecipeIngredient ing)
        {
            string key = MatcherKey(ing);
            if (matcherSamples.TryGetValue(key, out var known)) return known;

            ItemStack sample = null;
            try
            {
                var candidates = ing.Type == EnumItemClass.Block
                    ? capi.World.Blocks.Cast<CollectibleObject>()
                    : capi.World.Items.Cast<CollectibleObject>();
                foreach (var obj in candidates)
                {
                    if (obj?.Code == null) continue;
                    if (ing.Code != null && ing.Code.Path.Contains('*')
                        && !WildcardUtil.Match(ing.Code, obj.Code)) continue;
                    var stack = new ItemStack(obj);
                    if (!ing.SatisfiesAsIngredient(stack, false)) continue;
                    sample = stack;
                    break;
                }
            }
            catch { /* no sample is just a shorter label */ }

            return matcherSamples[key] = sample;
        }

        /// <summary>"Board (any wood, 12 variants)" → "Board": a materials summary wants the
        /// thing, not its bookkeeping. Only OUR "(any …)" suffix is bookkeeping, though —
        /// a parenthetical in the item's real name is often the whole identity. Vanilla's
        /// three shears recipes for linen (normal stitches) each convert a different stitch
        /// type, and chopping at the first "(" made "Linen (Square stitches)" and friends
        /// all read "Linen": the chooser offered three seemingly identical recipes (found
        /// by Mark, 0.3.7).</summary>
        static string StripVariants(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int paren = name.IndexOf(" (any");
            return paren > 0 ? name.Substring(0, paren) : name;
        }

        /// <summary>The longest run of words all sample names end with: "Copper shears" +
        /// "Iron shears" → "shears". Null when there are fewer than two samples or the
        /// names share no tail.</summary>
        static string SharedNameTail(List<ItemStack> samples)
        {
            if (samples == null || samples.Count < 2) return null;
            string[] tail = null;
            foreach (var stack in samples)
            {
                var words = stack?.GetName()?.Split(' ');
                if (words == null || words.Length == 0) return null;
                if (tail == null) { tail = words; continue; }

                int shared = 0;
                while (shared < tail.Length && shared < words.Length
                       && string.Equals(tail[tail.Length - 1 - shared],
                                        words[words.Length - 1 - shared],
                                        StringComparison.OrdinalIgnoreCase)) shared++;
                if (shared == 0) return null;
                if (shared < tail.Length) tail = tail.Skip(tail.Length - shared).ToArray();
            }
            return string.Join(" ", tail);
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
