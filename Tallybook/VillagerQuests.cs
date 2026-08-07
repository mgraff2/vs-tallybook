using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>One thing an NPC wants brought to them, resolved to a real stack.</summary>
    public class QuestRequirement
    {
        public ItemStack Stack;
        public int Quantity;
        public string Name => Stack?.GetName() ?? "?";
    }

    /// <summary>A trackable fetch request from the NPC currently being talked to.</summary>
    public class QuestOffer
    {
        public Entity Npc;
        public string NpcName;
        public Vec3d Pos;
        public List<QuestRequirement> Requirements = new List<QuestRequirement>();

        public string Summary => string.Join(", ", Requirements.Select(r => $"{r.Quantity} x {r.Name}"));
    }

    /// <summary>
    /// Reads what the villager in front of you is asking for.
    ///
    /// There is no quest system in the game to query — quests *are* dialogue. A fetch request
    /// is expressed as a condition on the answer that hands the goods over:
    ///
    ///   { variable: "player.inventory", isValue: "{type:'item', code:'hide-raw-small', stacksize:10}" }
    ///
    /// which is machine-readable, so nothing here parses prose. The other conditions on the
    /// same answer are the quest's gates ("gerhardtqueststarted" true, "…completed" not true);
    /// they are evaluated against the player variables the server syncs to the client, and an
    /// answer whose gates are unmet is ignored. That is deliberate: the dialogue file also
    /// describes quests this player has never been offered, and surfacing those would spoil
    /// content the game is deliberately withholding. A gate we cannot evaluate counts as
    /// unmet, so the failure direction is "no button", never "spoiler".
    /// </summary>
    public class QuestScanner
    {
        readonly ICoreClientAPI capi;

        public QuestScanner(ICoreClientAPI capi) { this.capi = capi; }

        // ---- dialogue file shape (only the parts we read) ------------------------------
        // Populated by the deserializer, never by our code, hence the disable.
#pragma warning disable 0649
        class DlgFile { public DlgComp[] components; }
        class DlgComp { public string type; public string code; public DlgText[] text; }
        class DlgText { public DlgCond condition; public DlgCond[] conditions; }
        class DlgCond { public string variable; public string isValue; public string isNotValue; }
        class StackSpec { public string type; public string code; public int stacksize = 1; }
#pragma warning restore 0649

        /// <summary>The NPC whose conversation window is open, or null.</summary>
        public Entity FindTalkingNpc()
        {
            var entities = capi.World?.LoadedEntities;
            if (entities == null) return null;

            foreach (var e in entities.Values)
            {
                var bh = e?.GetBehavior<EntityBehaviorConversable>();
                if (bh?.Dialog != null && bh.Dialog.IsOpened()) return e;
            }
            return null;
        }

        /// <summary>What that NPC currently wants, or null when there is nothing to track.</summary>
        public QuestOffer Scan(Entity npc)
        {
            if (npc == null) return null;

            var file = LoadDialogue(npc);
            if (file?.components == null) return null;

            var offer = new QuestOffer
            {
                Npc = npc,
                NpcName = npc.GetName(),
                Pos = npc.Pos?.XYZ
            };

            var seen = new HashSet<string>();
            foreach (var comp in file.components)
            {
                if (comp?.text == null) continue;
                foreach (var line in comp.text)
                {
                    var conds = AllConditions(line);
                    if (conds.Count == 0) continue;

                    var wanted = conds.Where(IsInventoryCondition).ToList();
                    if (wanted.Count == 0) continue;

                    // An inventory condition on its own is not an errand. The game uses the
                    // same condition for prices and for "do you have the letter" — Tad's heal
                    // costs one gear, checked exactly this way — and treating those as quests
                    // put things on the list that were never accepted and could never be
                    // completed. A real fetch quest is tied to quest state, so require at
                    // least one other player-state gate, all of them currently satisfied.
                    // (Note: All() over an empty set is true, which is precisely how the
                    // bare-condition case slipped through before.)
                    var gates = conds.Where(c => !IsInventoryCondition(c)).ToList();
                    if (gates.Count == 0 || !gates.All(GateMet)) continue;

                    foreach (var w in wanted)
                    {
                        var req = ToRequirement(w);
                        if (req == null) continue;
                        string key = $"{req.Stack.Collectible.Code}|{req.Quantity}";
                        if (seen.Add(key)) offer.Requirements.Add(req);
                    }
                }
            }

            return offer.Requirements.Count > 0 ? offer : null;
        }

        static List<DlgCond> AllConditions(DlgText line)
        {
            var list = new List<DlgCond>();
            if (line.condition != null) list.Add(line.condition);
            if (line.conditions != null) list.AddRange(line.conditions.Where(c => c != null));
            return list;
        }

        // An inverted inventory condition means "you must NOT be carrying this" — a state
        // check, not a shopping list. Only the positive form is a fetch request.
        static bool IsInventoryCondition(DlgCond c)
            => c.variable == "player.inventory" && c.isValue != null;

        QuestRequirement ToRequirement(DlgCond cond)
        {
            StackSpec spec;
            try { spec = JsonConvert.DeserializeObject<StackSpec>(cond.isValue); }
            catch { return null; }
            if (spec?.code == null) return null;

            var loc = new AssetLocation(spec.code);
            ItemStack stack = null;
            if (spec.type == "block")
            {
                var block = capi.World.GetBlock(loc);
                if (block != null) stack = new ItemStack(block);
            }
            else
            {
                var item = capi.World.GetItem(loc);
                if (item != null) stack = new ItemStack(item);
                else
                {
                    // Some requests name a block without saying so.
                    var block = capi.World.GetBlock(loc);
                    if (block != null) stack = new ItemStack(block);
                }
            }
            if (stack == null) return null;

            return new QuestRequirement { Stack = stack, Quantity = Math.Max(1, spec.stacksize) };
        }

        /// <summary>
        /// Is this non-inventory condition currently true for this player? Only player-scope
        /// variables are understood; anything else (entity or global scope) is treated as
        /// unmet so an unrecognised gate can never leak a quest that was not offered.
        /// </summary>
        bool GateMet(DlgCond cond)
        {
            if (cond.variable == null) return false;
            if (!cond.variable.StartsWith("player.")) return false;

            string name = cond.variable.Substring("player.".Length);
            string actual;
            try
            {
                var vars = capi.ModLoader.GetModSystem<VariablesModSystem>();
                actual = vars?.GetPlayerVariable(capi.World.Player.PlayerUID, name) ?? "";
            }
            catch { return false; }

            if (cond.isValue != null) return string.Equals(actual, cond.isValue, StringComparison.OrdinalIgnoreCase);
            if (cond.isNotValue != null) return !string.Equals(actual, cond.isNotValue, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        DlgFile LoadDialogue(Entity npc)
        {
            try
            {
                var bh = npc.GetBehavior<EntityBehaviorConversable>();
                if (bh == null) return null;

                // The behavior already resolved which dialogue file this individual uses
                // (villagers map entity code -> file via dialogueByType), so read its choice
                // rather than re-deriving it.
                var loc = AccessTools.Field(typeof(EntityBehaviorConversable), "dialogueLoc")
                    ?.GetValue(bh) as AssetLocation;
                if (loc == null) return null;

                var withExt = loc.Clone();
                if (!withExt.Path.EndsWith(".json")) withExt.Path += ".json";

                return capi.Assets.TryGet(withExt)?.ToObject<DlgFile>();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read villager dialogue: {0}", e.Message);
                return null;
            }
        }
    }
}
