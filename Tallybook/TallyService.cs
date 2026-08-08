using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Tallybook
{
    /// <summary>
    /// Glue between the recipe probe, the pin store and the tree math. The dialog and HUD talk
    /// to this; neither touches the registry or the save file directly.
    /// </summary>
    public class TallyService
    {
        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        public readonly RecipeProbe Probe;
        public readonly PinStore Store;

        /// <summary>Raised after every recount whose numbers changed anything visible.</summary>
        public event Action OnCountsChanged;

        public TallyService(ICoreClientAPI capi, TallybookConfig config)
        {
            this.capi = capi;
            this.config = config;
            Probe = new RecipeProbe(capi);
            Store = new PinStore(capi);

            // Every store mutation flows through one recount, so surfaces subscribe to
            // OnCountsChanged alone and can never observe a structural change with stale
            // numbers attached. The change signature below covers structure as well as counts,
            // so a recount after any mutation is guaranteed to fire it.
            Store.OnChanged += RecountAll;
        }

        // ---- resolve -----------------------------------------------------------------

        /// <summary>
        /// Re-resolve a pin's itemstack, recipe choices and tree. False when the item itself no
        /// longer exists in this world's content. Recipes are re-resolved every time rather
        /// than persisted: a recipe mod added or removed between sessions must change the
        /// answer, not restore a stale one (spec §11).
        /// </summary>
        public bool Resolve(Pin pin)
        {
            if (pin.Stack == null)
            {
                var loc = new AssetLocation(pin.Code);
                var block = pin.IsBlock ? capi.World.GetBlock(loc) : null;
                var item = pin.IsBlock ? null : capi.World.GetItem(loc);

                if (block != null) pin.Stack = new ItemStack(block);
                else if (item != null) pin.Stack = new ItemStack(item);
                else return false;

                // Attributes are part of what was pinned, not decoration — without them a
                // saved 8-plank bookshelf would resolve back to whichever variant shares its
                // code, which is exactly the lie the page-code identity exists to prevent.
                if (pin.Attributes != null
                    && TreeAttribute.FromJson(pin.Attributes) is ITreeAttribute attrs)
                {
                    pin.Stack.Attributes = attrs;
                }
            }

            // Rebuilt from the resolved stack every load: it is derived data, and the stack is
            // the only thing that knows which variant this pin means.
            pin.SelfNode = new TallyNode { Req = Probe.RequirementForStack(pin.Stack) };

            // An errand is a fetch: go get eight iron ingots and hand them over. Showing how
            // to *craft* the item turns that into something else entirely — for iron ingots
            // the only grid recipe is "chisel an iron anvil back into ingots", which reads
            // like a demand for an anvil you do not have and, worse, like a preview of a
            // quest stage the villager has not offered yet (Mark). Quest pins are counted,
            // not decomposed.
            if (pin.QuestGiver != null)
            {
                pin.Groups = new List<RecipeVariantGroup>();
                SetGroup(pin, null, rememberPref: false);
                return true;
            }

            pin.Groups = Probe.FindGroupsFor(pin.Stack);

            // Groups are still resolved so the row can offer to break it down; it just is not
            // broken down until asked.
            if (pin.GatherOnly)
            {
                SetGroup(pin, null, rememberPref: false);
                return true;
            }

            var chosen = pin.Groups.FirstOrDefault(g => g.Signature == pin.RecipeSignature)
                         ?? PreferredGroup(pin.Key, pin.Groups)
                         ?? pin.Groups.FirstOrDefault();
            SetGroup(pin, chosen, rememberPref: false);

            TallyTree.RestoreExpansions(pin.RootNodes, pin.Expansions,
                FindExpansionChoices, BuildRows, BuildTools);
            return true;
        }

        RecipeVariantGroup PreferredGroup(string key, List<RecipeVariantGroup> groups)
        {
            return Store.RecipePrefs.TryGetValue(key, out var sig)
                ? groups.FirstOrDefault(g => g.Signature == sig)
                : null;
        }

        void SetGroup(Pin pin, RecipeVariantGroup group, bool rememberPref)
        {
            pin.Group = group;
            pin.RootNodes = group == null
                ? new List<TallyNode>()
                : BuildRows(group).Select(r => new TallyNode { Req = r }).ToList();
            pin.Tools = group == null ? new List<Requirement>() : BuildTools(group);

            if (rememberPref && group != null)
            {
                // Choosing a recipe records it as this item's per-world default (spec §2a).
                // Keyed by page identity so two variants of one code keep separate defaults.
                Store.RecipePrefs[pin.Key] = group.Signature;
            }
        }

        List<Requirement> BuildRows(RecipeVariantGroup g) => Probe.BuildRequirements(g);
        List<Requirement> BuildTools(RecipeVariantGroup g) => Probe.BuildRequirements(g, tools: true);
        List<RecipeVariantGroup> FindExpansionChoices(Requirement req) => Probe.FindExpansionGroups(req);

        // ---- recipe choice -----------------------------------------------------------

        /// <summary>
        /// Unfold a pin into its recipe, or fold it back to plain counting. Only pins added
        /// from the handbook start unfolded: pinning there is an act of reading a recipe, so
        /// wanting to see it is a fair assumption. Anything arriving another way is a thing
        /// to go and get until the player says otherwise.
        /// </summary>
        public void ToggleGatherOnly(Pin pin)
        {
            if (pin.Groups.Count == 0) return;

            pin.Expansions = new List<SavedExpansion>();
            pin.GatherOnly = !pin.GatherOnly;
            if (pin.GatherOnly) SetGroup(pin, null, rememberPref: false);
            else
            {
                var chosen = pin.Groups.FirstOrDefault(g => g.Signature == pin.RecipeSignature)
                             ?? PreferredGroup(pin.Key, pin.Groups)
                             ?? pin.Groups[0];
                SetGroup(pin, chosen, rememberPref: false);
            }
            Store.Changed();
        }

        /// <summary>Use this recipe for a pin, and show its ingredients.</summary>
        public void ChoosePinRecipe(Pin pin, RecipeVariantGroup group)
        {
            if (group == null) return;

            pin.Expansions = new List<SavedExpansion>();
            pin.GatherOnly = false;
            SetGroup(pin, group, rememberPref: true);
            Store.Changed();
        }

        /// <summary>Unfold an ingredient row using this recipe.</summary>
        public void ChooseNodeRecipe(Pin pin, TallyNode node, RecipeVariantGroup group)
        {
            if (group == null) return;

            TallyTree.Expand(node, group, BuildRows, BuildTools);
            if (group.OutputCode != null) Store.RecipePrefs[group.OutputCode] = group.Signature;
            Store.Changed();
        }

        /// <summary>Cycle a pin to its next recipe choice. Expansion state is discarded — the
        /// old tree described a different recipe's ingredients.</summary>
        public void CyclePinRecipe(Pin pin)
        {
            if (pin.Groups.Count < 2 || pin.GatherOnly) return;
            int idx = pin.Groups.IndexOf(pin.Group);
            var next = pin.Groups[(idx + 1) % pin.Groups.Count];
            pin.Expansions = new List<SavedExpansion>();
            SetGroup(pin, next, rememberPref: true);
            Store.Changed();
        }

        public void CycleNodeRecipe(Pin pin, TallyNode node)
        {
            if (node.Choices == null || node.Choices.Count < 2) return;
            int idx = node.Choices.IndexOf(node.Choice);
            var next = node.Choices[(idx + 1) % node.Choices.Count];
            TallyTree.Expand(node, next, BuildRows, BuildTools);
            if (next.OutputCode != null)
            {
                // A deliberate choice here is a preference for future expansions too (spec §2a).
                Store.RecipePrefs[next.OutputCode] = next.Signature;
            }
            Store.Changed();
        }

        // ---- expansion (spec §2a) ----------------------------------------------------

        /// <summary>Cheap "does an expansion recipe exist" check, so the dialog can hide the
        /// Expand affordance on rows that could never unfold (spec §2a: the affordance sits on
        /// craftable rows). The cycle guard still runs at click time.</summary>
        public bool HasExpansion(TallyNode node)
        {
            node.Choices ??= FindExpansionChoices(node.Req);
            return node.Choices.Count > 0;
        }

        public bool CanExpand(Pin pin, TallyNode node, out string reason)
        {
            reason = null;
            if (node.Expanded) { reason = "already expanded"; return false; }

            node.Choices ??= FindExpansionChoices(node.Req);
            if (node.Choices.Count == 0) { reason = "no recipe known"; return false; }

            var ancestors = AncestorCodes(pin, node);
            if (TallyTree.WouldCycle(node.Req, ancestors))
            {
                // The guard that makes degenerate recipe loops safe without traversal logic.
                reason = "recipe loop — this item is already an ancestor in this branch";
                return false;
            }
            return true;
        }

        public void ExpandNode(Pin pin, TallyNode node)
        {
            if (!CanExpand(pin, node, out _)) return;

            var choice = node.Choices.FirstOrDefault(c =>
                             c.OutputCode != null
                             && Store.RecipePrefs.TryGetValue(c.OutputCode, out var sig)
                             && sig == c.Signature)
                         ?? node.Choices[0];
            TallyTree.Expand(node, choice, BuildRows, BuildTools);
            Store.Changed();
        }

        public void CollapseNode(TallyNode node)
        {
            TallyTree.Collapse(node);
            Store.Changed();
        }

        /// <summary>The pin's own code plus every requirement code on the path down to (and
        /// including) this node's parent chain — the guard set for cycle detection.</summary>
        HashSet<string> AncestorCodes(Pin pin, TallyNode target)
        {
            var codes = new HashSet<string> { pin.Code };
            FindPath(pin.RootNodes, target, codes);
            return codes;
        }

        static bool FindPath(List<TallyNode> nodes, TallyNode target, HashSet<string> codes)
        {
            foreach (var node in nodes)
            {
                if (node == target) return true;
                if (!node.Expanded) continue;

                var added = new List<string>();
                foreach (var c in node.Req.ExactCodes)
                {
                    if (codes.Add(c)) added.Add(c);
                }
                if (FindPath(node.Children, target, codes)) return true;
                foreach (var c in added) codes.Remove(c);
            }
            return false;
        }

        // ---- counting ----------------------------------------------------------------

        /// <summary>One inventory pass, all trees recomputed. Fires OnCountsChanged when any
        /// visible number moved so open surfaces know to redraw.</summary>
        public void RecountAll()
        {
            // Anything the world could not identify at load gets another chance here, so a
            // registry that simply was not ready yet costs a tick rather than the pin.
            Store.RetryUnresolved(Resolve);

            var snapshot = new InventorySnapshot(
                Probe.CarriedInventories(),
                config.IncludeMountBags ? Probe.OwnedAnimalBagStacks(config.MountBagRange) : null);
            foreach (var pin in Store.Pins) TallyTree.Recompute(pin, snapshot);

            // Change detection by signature of every visible number. Simpler and safer than
            // per-node dirty flags — a missed dirty bit shows stale numbers, and this can't.
            string sig = Signature();
            if (sig == lastSignature) return;
            lastSignature = sig;

            // Each surface is told separately so one that throws cannot silence the rest. The
            // HUD once vanished entirely because a later subscriber threw during world load
            // and took the whole notification — and with it the redraw — down with it.
            foreach (var handler in OnCountsChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action)handler)(); }
                catch (Exception e)
                {
                    capi.Logger.Warning("[tallybook] a surface failed to update: {0}", e);
                }
            }
        }

        string lastSignature = "";

        /// <summary>
        /// Every visible fact in one string: counts AND structure (recipe choice, expansion
        /// shape). Structure is included so cycling a recipe whose numbers happen to match
        /// still registers as a change and redraws the choice label.
        /// </summary>
        string Signature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var pin in Store.Pins)
            {
                sb.Append(pin.Key).Append(':').Append(pin.Count)
                  .Append('/').Append(pin.Have)
                  .Append(pin.Active ? 'A' : 'a')
                  .Append(pin.Craftable ? '+' : '-')
                  // Where the Map button can point is a visible fact: learning a giver's
                  // position or a map destination must redraw the row it puts a button on.
                  .Append((int)pin.QuestX).Append(',').Append((int)pin.QuestZ)
                  .Append('~').Append((int)pin.SiteX).Append(',').Append((int)pin.SiteZ)
                  .Append(pin.Group?.Signature).Append(';');
                foreach (var node in Walk(pin))
                {
                    sb.Append('|').Append(node.Req.Key).Append('=')
                      .Append(node.Have).Append('/').Append(node.Needed)
                      .Append('@').Append(node.Choice?.Signature);
                    // Expanded craft-steps carry tools of their own; leaving them out let a
                    // node tool arrive in inventory without any surface redrawing.
                    foreach (var t in node.Tools) sb.Append(t.Present ? 'y' : 'n');
                }
                foreach (var t in pin.Tools) sb.Append(t.Present ? 'y' : 'n');
            }
            return sb.ToString();
        }

        public IEnumerable<TallyNode> Walk(Pin pin)
        {
            foreach (var node in pin.RootNodes)
            {
                foreach (var n in WalkNode(node)) yield return n;
            }
        }

        static IEnumerable<TallyNode> WalkNode(TallyNode node)
        {
            yield return node;
            foreach (var child in node.Children)
            {
                foreach (var n in WalkNode(child)) yield return n;
            }
        }

        // ---- HUD data (spec §5) --------------------------------------------------------

        public class HudRow
        {
            public string Name;
            public int Have;
            public int Needed;

            /// <summary>The first-seen requirement behind this merged row — the icon source.
            /// Rows merge by requirement key, so any of the merged requirements accepts the
            /// same variant set; first-seen is as representative as any.</summary>
            public Requirement Req;
        }

        /// <summary>
        /// Merged gather totals across all pins, leaves only (spec §2a / §5): one
        /// "Boards 12/48" line even when three pinned items want boards. "Have" is the shared
        /// carried pool so it merges by max, while "needed" sums.
        /// </summary>
        public List<HudRow> MergedLeafTotals()
        {
            var rows = new Dictionary<string, HudRow>();
            foreach (var pin in Store.Pins)
            {
                if (!pin.Active) continue;
                foreach (var leaf in TallyTree.Leaves(pin))
                {
                    if (rows.TryGetValue(leaf.Req.Key, out var row))
                    {
                        row.Needed += leaf.Needed;
                        row.Have = Math.Max(row.Have, leaf.Have);
                    }
                    else
                    {
                        rows[leaf.Req.Key] = new HudRow
                        {
                            Name = leaf.Req.DisplayName,
                            Have = leaf.Have,
                            Needed = leaf.Needed,
                            Req = leaf.Req
                        };
                    }
                }
            }
            return rows.Values.OrderBy(r => r.Have >= r.Needed ? 1 : 0).ThenBy(r => r.Name).ToList();
        }

        /// <summary>
        /// Gather rows for one pin alone — what this thing still needs, rather than what
        /// everything needs. Still merged *within* the pin, so an item wanted by two branches
        /// of the same build is one row.
        /// </summary>
        public List<HudRow> LeafTotalsFor(Pin pin)
        {
            var rows = new Dictionary<string, HudRow>();
            foreach (var leaf in TallyTree.Leaves(pin))
            {
                if (rows.TryGetValue(leaf.Req.Key, out var row))
                {
                    row.Needed += leaf.Needed;
                    row.Have = Math.Max(row.Have, leaf.Have);
                }
                else
                {
                    rows[leaf.Req.Key] = new HudRow
                    {
                        Name = leaf.Req.DisplayName,
                        Have = leaf.Have,
                        Needed = leaf.Needed,
                        Req = leaf.Req
                    };
                }
            }
            return rows.Values.OrderBy(r => r.Have >= r.Needed ? 1 : 0).ThenBy(r => r.Name).ToList();
        }

        /// <summary>
        /// Names of quest givers every one of whose tracked requests is now satisfied — the
        /// people you can go and hand something to. All-or-nothing per giver: an errand that
        /// wants two things is not ready when you have only one of them, and flagging it as
        /// ready would send the player on a wasted walk.
        /// </summary>
        public HashSet<string> ReadyQuestGivers()
        {
            var progress = new Dictionary<string, bool>();
            foreach (var pin in Store.Pins)
            {
                if (!pin.Active || pin.QuestGiver == null) continue;
                bool wasReady = progress.TryGetValue(pin.QuestGiver, out var r) ? r : true;
                progress[pin.QuestGiver] = wasReady && pin.Complete;
            }
            return new HashSet<string>(progress.Where(kv => kv.Value).Select(kv => kv.Key));
        }

        /// <summary>
        /// Every tool any pin's recipes call for — pin-level and expanded craft-steps alike —
        /// merged by requirement key. Tools are presence checks, never counted (spec §4), so
        /// one saw row covers every recipe that wants a saw. Missing tools sort first.
        /// </summary>
        public List<Requirement> MergedTools()
        {
            var tools = new Dictionary<string, Requirement>();
            foreach (var pin in Store.Pins)
            {
                if (!pin.Active) continue;
                foreach (var t in pin.Tools) tools.TryAdd(t.Key, t);
                foreach (var node in Walk(pin))
                {
                    foreach (var t in node.Tools) tools.TryAdd(t.Key, t);
                }
            }
            return tools.Values.OrderBy(t => t.Present ? 1 : 0).ThenBy(t => t.DisplayName).ToList();
        }
    }
}
