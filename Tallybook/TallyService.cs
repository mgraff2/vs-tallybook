using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Tallybook
{
    /// <summary>
    /// Glue between the recipe probe, the pin store and the tree math. The dialog and HUD talk
    /// to this; neither touches the registry or the save file directly.
    /// </summary>
    public class TallyService
    {
        readonly ICoreClientAPI capi;
        public readonly RecipeProbe Probe;
        public readonly PinStore Store;

        /// <summary>Raised after every recount whose numbers changed anything visible.</summary>
        public event Action OnCountsChanged;

        public TallyService(ICoreClientAPI capi)
        {
            this.capi = capi;
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
            }

            pin.Groups = Probe.FindGroupsFor(pin.Code);
            var chosen = pin.Groups.FirstOrDefault(g => g.Signature == pin.RecipeSignature)
                         ?? PreferredGroup(pin.Code, pin.Groups)
                         ?? pin.Groups.FirstOrDefault();
            SetGroup(pin, chosen, rememberPref: false);

            TallyTree.RestoreExpansions(pin.RootNodes, pin.Expansions,
                FindExpansionChoices, BuildRows, BuildTools);
            return true;
        }

        RecipeVariantGroup PreferredGroup(string code, List<RecipeVariantGroup> groups)
        {
            return Store.RecipePrefs.TryGetValue(code, out var sig)
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
                Store.RecipePrefs[pin.Code] = group.Signature;
            }
        }

        List<Requirement> BuildRows(RecipeVariantGroup g) => Probe.BuildRequirements(g);
        List<Requirement> BuildTools(RecipeVariantGroup g) => Probe.BuildRequirements(g, tools: true);
        List<RecipeVariantGroup> FindExpansionChoices(Requirement req) => Probe.FindExpansionGroups(req);

        // ---- recipe choice -----------------------------------------------------------

        /// <summary>Cycle a pin to its next recipe choice. Expansion state is discarded — the
        /// old tree described a different recipe's ingredients.</summary>
        public void CyclePinRecipe(Pin pin)
        {
            if (pin.Groups.Count < 2) return;
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
            var snapshot = new InventorySnapshot(Probe.CarriedInventories());
            foreach (var pin in Store.Pins) TallyTree.Recompute(pin, snapshot);

            // Change detection by signature of every visible number. Simpler and safer than
            // per-node dirty flags — a missed dirty bit shows stale numbers, and this can't.
            string sig = Signature();
            if (sig == lastSignature) return;
            lastSignature = sig;
            OnCountsChanged?.Invoke();
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
                sb.Append(pin.Code).Append(':').Append(pin.Count)
                  .Append(pin.Craftable ? '+' : '-')
                  .Append(pin.Group?.Signature).Append(';');
                foreach (var node in Walk(pin))
                {
                    sb.Append('|').Append(node.Req.Key).Append('=')
                      .Append(node.Have).Append('/').Append(node.Needed)
                      .Append('@').Append(node.Choice?.Signature);
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
                            Needed = leaf.Needed
                        };
                    }
                }
            }
            return rows.Values.OrderBy(r => r.Have >= r.Needed ? 1 : 0).ThenBy(r => r.Name).ToList();
        }
    }
}
