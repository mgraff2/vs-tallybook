using System;
using System.Collections.Generic;
using System.Linq;

namespace Tallybook
{
    /// <summary>
    /// One ingredient row in a pin's tree: a requirement, live have/needed numbers, and —
    /// when the player has deliberately expanded it — a chosen recipe with child rows.
    ///
    /// Expansion is always a player action (spec §2, §2a). Nothing in this file ever expands
    /// a node on its own; automatic recursion is permanently rejected because recipe cycles,
    /// per-level recipe-choice explosions and silent wrong guesses each make the list lie.
    /// </summary>
    public class TallyNode
    {
        public Requirement Req;

        /// <summary>Live numbers, recomputed by TallyTree.Recompute.</summary>
        public int Needed;
        public int Have;

        public bool Satisfied => Have >= Needed;

        /// <summary>Non-null only while the player has this node expanded.</summary>
        public RecipeVariantGroup Choice;
        public List<RecipeVariantGroup> Choices;
        public List<TallyNode> Children = new List<TallyNode>();

        /// <summary>Tools the chosen expansion recipe uses without consuming (saw for boards).</summary>
        public List<Requirement> Tools = new List<Requirement>();

        public bool Expanded => Choice != null;

        /// <summary>"Ready to craft": every direct child satisfied. Direct rows only — having a
        /// spile's ingredients does not mark the spile itself as had (spec §2a).</summary>
        public bool ReadyToCraft => Expanded && Children.Count > 0 && Children.All(ch => ch.Satisfied);
    }

    /// <summary>Persisted shape of an expanded node (spec §2a: expansion state and recipe
    /// choices save with the list).</summary>
    public class SavedExpansion
    {
        public string ReqKey;
        public string ChoiceSignature;
        public List<SavedExpansion> Children = new List<SavedExpansion>();
    }

    /// <summary>
    /// The deficit math and tree bookkeeping for one pin (spec §2a). Kept free of GUI and API
    /// types so the arithmetic is readable in one place.
    /// </summary>
    public static class TallyTree
    {
        /// <summary>Whole crafts needed to produce <paramref name="wanted"/> outputs.</summary>
        public static int CraftsFor(int wanted, int outputQuantity)
        {
            if (wanted <= 0) return 0;
            if (outputQuantity < 1) outputQuantity = 1;
            return (wanted + outputQuantity - 1) / outputQuantity;
        }

        /// <summary>How many times the pot must be filled and cooked to cover a deficit —
        /// each pot load cooks up to ServingsPerBatch servings at once. 0 when the group is
        /// not a cooking recipe or nothing is missing.</summary>
        public static int PotLoads(int deficitItems, RecipeVariantGroup group)
        {
            if (group?.Cooking == null || group.ServingsPerBatch < 1) return 0;
            int servings = CraftsFor(Math.Max(0, deficitItems), group.OutputQuantity);
            return (servings + group.ServingsPerBatch - 1) / group.ServingsPerBatch;
        }

        /// <summary>How many barrel seals cover a deficit — a seal processes as many crafts
        /// as fit the barrel by litres (the craft's largest liquid amount against the
        /// biggest barrel's capacity). 0 for non-barrel groups or nothing missing.</summary>
        public static int BarrelSeals(int deficitItems, RecipeVariantGroup group)
        {
            if (group?.Barrel == null || group.BatchLitres <= 0 || group.LitresPerCraft <= 0) return 0;
            int crafts = CraftsFor(Math.Max(0, deficitItems), group.OutputQuantity);
            if (crafts <= 0) return 0;
            int craftsPerSeal = Math.Max(1, (int)(group.BatchLitres / group.LitresPerCraft));
            return (crafts + craftsPerSeal - 1) / craftsPerSeal;
        }

        /// <summary>The batching suffix for a row, whatever kind its recipe is: "2 pot
        /// loads", "3 barrel seals (~7 days each)", or null when there is nothing to say.</summary>
        public static string BatchText(int deficitItems, RecipeVariantGroup group)
        {
            int loads = PotLoads(deficitItems, group);
            if (loads > 0) return $"{loads} pot load{(loads == 1 ? "" : "s")}";

            int seals = BarrelSeals(deficitItems, group);
            if (seals > 0)
            {
                int days = (int)Math.Round(group.SealHours / 24.0);
                string each = days > 0 ? $" (~{days} day{(days == 1 ? "" : "s")} each)" : "";
                return $"{seals} barrel seal{(seals == 1 ? "" : "s")}{each}";
            }
            return null;
        }

        /// <summary>
        /// Recompute the whole tree under a pin from live inventory counts.
        ///
        /// Root rows scale with the pin count (factor propagation, spec §2a: one factor at the
        /// root, no per-node multipliers). Child rows scale with their parent's DEFICIT:
        ///   craftsNeeded = ceil(max(0, parentNeeded − parentHave) / recipeOutputQuantity)
        /// so crafting a spile visibly shrinks what its children demand. A satisfied parent
        /// drives its children to needed = 0 — done, not hidden.
        /// </summary>
        public static void Recompute(Pin pin, InventorySnapshot inv)
        {
            // The pin's own have/needed, whether or not anything crafts it. An item with no
            // recipe is still worth tracking — "collect 64 low-quality soil" is a real goal,
            // and answering it is the same question the rest of the mod answers.
            if (pin.SelfNode != null)
            {
                // CountInItems, not Count: a liquid pin's Count is litres, the math is items.
                pin.SelfNode.Needed = pin.CountInItems;
                pin.SelfNode.Have = inv.Count(pin.SelfNode.Req);
            }

            if (pin.Group == null) return;

            // Deficit at the root, exactly as at every level below it (spec §2a): already
            // carrying two of the four you pinned means gathering ingredients for two.
            int deficit = Math.Max(0, pin.CountInItems - pin.Have);
            int rootCrafts = CraftsFor(deficit, pin.Group.OutputQuantity);
            foreach (var node in pin.RootNodes)
            {
                node.Needed = node.Req.Quantity * rootCrafts;
                RecomputeNode(node, inv);
            }
            foreach (var tool in pin.Tools) tool.Present = inv.Count(tool) > 0;
        }

        static void RecomputeNode(TallyNode node, InventorySnapshot inv)
        {
            node.Have = inv.Count(node.Req);
            if (!node.Expanded) return;

            int deficit = Math.Max(0, node.Needed - node.Have);
            int crafts = CraftsFor(deficit, node.Choice.OutputQuantity);
            foreach (var child in node.Children)
            {
                child.Needed = child.Req.Quantity * crafts;
                RecomputeNode(child, inv);
            }
            foreach (var tool in node.Tools) tool.Present = inv.Count(tool) > 0;
        }

        /// <summary>
        /// The cycle guard (spec §2a): a node may not expand into a recipe whose output is
        /// already an ancestor in its own branch. <paramref name="ancestorCodes"/> carries the
        /// pin's own code plus every expanded ancestor's accepted codes.
        /// </summary>
        public static bool WouldCycle(Requirement req, HashSet<string> ancestorCodes)
            => req.ExactCodes.Overlaps(ancestorCodes)
               // A liquid row's ExactCodes are its vessels; the thing an expansion would
               // craft is the liquid, so that is what must not already be an ancestor.
               || (req.LiquidCode != null && ancestorCodes.Contains(req.LiquidCode));

        /// <summary>
        /// Leaf rows only — the HUD's gather list (spec §2a): expanding a node moves it from
        /// "gather this" to "craft this from the things below", so expanded intermediates stay
        /// out of the merged totals. Rows already driven to needed 0 are skipped; "0 more to
        /// gather" is not a shopping item.
        /// </summary>
        public static IEnumerable<TallyNode> Leaves(Pin pin)
        {
            // A pin with no recipe contributes no gather rows: its own have/needed rides on
            // the pin header, which every surface already draws. Emitting it here too would
            // print the identical line twice in the HUD for the common one-pin case.
            foreach (var node in pin.RootNodes)
            {
                foreach (var leaf in LeavesOf(node)) yield return leaf;
            }
        }

        static IEnumerable<TallyNode> LeavesOf(TallyNode node)
        {
            if (!node.Expanded)
            {
                if (node.Needed > 0) yield return node;
                yield break;
            }
            foreach (var child in node.Children)
            {
                foreach (var leaf in LeavesOf(child)) yield return leaf;
            }
        }

        // ---- persistence of expansion state -------------------------------------------

        public static List<SavedExpansion> SaveExpansions(IEnumerable<TallyNode> nodes)
        {
            var list = new List<SavedExpansion>();
            foreach (var node in nodes)
            {
                if (!node.Expanded) continue;
                list.Add(new SavedExpansion
                {
                    ReqKey = node.Req.Key,
                    ChoiceSignature = node.Choice.Signature,
                    Children = SaveExpansions(node.Children)
                });
            }
            return list;
        }

        /// <summary>
        /// Re-apply saved expansion state after recipes were re-resolved. Nodes whose
        /// requirement or chosen recipe no longer exists are silently left collapsed — the
        /// world's content changed, and restoring a ghost would lie (spec §11).
        /// </summary>
        /// <summary>Attach a chosen recipe to a node: children from its ingredient rows, tools
        /// alongside. The single path for both player expansion and restore-from-save.</summary>
        public static void Expand(
            TallyNode node, RecipeVariantGroup choice,
            System.Func<RecipeVariantGroup, List<Requirement>> buildRows,
            System.Func<RecipeVariantGroup, List<Requirement>> buildTools)
        {
            node.Choice = choice;
            node.Children = buildRows(choice).Select(r => new TallyNode { Req = r }).ToList();
            node.Tools = buildTools(choice);
        }

        public static void Collapse(TallyNode node)
        {
            // State kept deliberately minimal: children are cheap to rebuild, and holding a
            // stale subtree risks showing it. Choices stay so re-expanding reuses the lookup.
            node.Choice = null;
            node.Children = new List<TallyNode>();
            node.Tools = new List<Requirement>();
        }

        public static void RestoreExpansions(
            List<TallyNode> nodes, List<SavedExpansion> saved,
            System.Func<Requirement, List<RecipeVariantGroup>> findChoices,
            System.Func<RecipeVariantGroup, List<Requirement>> buildRows,
            System.Func<RecipeVariantGroup, List<Requirement>> buildTools)
        {
            if (saved == null) return;

            foreach (var s in saved)
            {
                var node = nodes.FirstOrDefault(n => n.Req.Key == s.ReqKey);
                if (node == null || node.Expanded) continue;

                node.Choices = findChoices(node.Req);
                var choice = node.Choices.FirstOrDefault(c => c.Signature == s.ChoiceSignature)
                             ?? node.Choices.FirstOrDefault();
                if (choice == null) continue;

                Expand(node, choice, buildRows, buildTools);
                RestoreExpansions(node.Children, s.Children, findChoices, buildRows, buildTools);
            }
        }
    }
}
