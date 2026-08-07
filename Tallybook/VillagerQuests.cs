using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
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

        /// <summary>What the NPC actually said when they asked — kept so the errand can be
        /// re-read later, when "ten of what, and why?" has faded.</summary>
        public List<string> Briefing = new List<string>();

        public string Summary => string.Join(", ", Requirements.Select(r => $"{r.Quantity} x {r.Name}"));
    }

    /// <summary>One quest chain — the "beataquest" behind beataqueststarted / …completed /
    /// …rewarded — and how far into the story it sits.</summary>
    public class QuestChain
    {
        public string Key;              // "beataquest"
        public string DisplayName;      // "Beata"
        public HashSet<string> Requires = new HashSet<string>();
        public int Depth;               // how many chains must come first
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
        class DlgComp
        {
            public string type;
            public string code;
            public string owner;
            public string jumpTo;
            public Dictionary<string, string> setVariables;
            public DlgText[] text;
        }
        class DlgText { public string value; public string jumpTo; public DlgCond condition; public DlgCond[] conditions; }
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

            // Requirements sharing a gate set are *alternatives*, not a shopping list: Better
            // Ruins' salt trader accepts an andesite, basalt or peridotite quern for one
            // errand, each as its own answer line. Adding them all would demand three querns
            // for a task that wants one.
            var byGate = new Dictionary<string, List<QuestRequirement>>();

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
                    if (gates.Count == 0 || !gates.All(g => GateMet(g, npc))) continue;

                    string gateKey = string.Join("&", gates
                        .Select(g => $"{g.variable}={g.isValue}/{g.isNotValue}")
                        .OrderBy(s => s, StringComparer.Ordinal));

                    foreach (var w in wanted)
                    {
                        var req = ToRequirement(w);
                        if (req == null) continue;
                        string key = $"{req.Stack.Collectible.Code}|{req.Quantity}";
                        if (!seen.Add(key)) continue;

                        if (!byGate.TryGetValue(gateKey, out var group))
                        {
                            group = new List<QuestRequirement>();
                            byGate[gateKey] = group;
                        }
                        group.Add(req);
                    }

                    foreach (var said in Briefing(file, gates))
                    {
                        if (!offer.Briefing.Contains(said)) offer.Briefing.Add(said);
                    }
                }
            }

            foreach (var group in byGate.Values)
            {
                offer.Requirements.Add(group[0]);
                if (group.Count < 2) continue;

                // Say what else would do, since the row can only name one of them.
                string note = "Any of these will do: " + string.Join(", ", group.Select(r => r.Name));
                if (!offer.Briefing.Contains(note)) offer.Briefing.Add(note);
            }

            return offer.Requirements.Count > 0 ? offer : null;
        }

        /// <summary>
        /// What the NPC said when they set this errand, recovered from the dialogue graph.
        ///
        /// The accepting step is the one that *sets* the gate variable we are gated on — that
        /// is what "accepted" means here. From it: the choice that leads to it, and the speech
        /// that leads to the choice, which is the actual briefing ("Can you bring me some
        /// small raw hides? Say ten of them?"). Only the NPC's own lines are kept; the
        /// player's answers are not news to the player.
        ///
        /// Read from the dialogue file, so no part of the live conversation is touched.
        /// </summary>
        List<string> Briefing(DlgFile file, List<DlgCond> gates)
        {
            var lines = new List<string>();

            foreach (var gate in gates)
            {
                if (gate.variable == null || gate.isValue == null) continue;

                var accept = file.components.FirstOrDefault(c =>
                    c?.setVariables != null
                    && c.setVariables.TryGetValue(gate.variable, out var set)
                    && string.Equals(set, gate.isValue, StringComparison.OrdinalIgnoreCase));
                if (accept?.code == null) continue;

                var choice = file.components.FirstOrDefault(c =>
                    c?.text != null && c.text.Any(t => t?.jumpTo == accept.code));

                var briefing = choice?.code == null ? null : file.components.FirstOrDefault(c =>
                    c?.jumpTo == choice.code && !IsPlayer(c));

                AddSpeech(lines, briefing);
                AddSpeech(lines, accept);
            }
            return lines;
        }

        static bool IsPlayer(DlgComp comp) => comp?.owner == "player";

        /// <summary>
        /// Every village errand you are currently on, found without talking to anyone.
        ///
        /// Village quest state lives on the *player* and is synced to this client, so the
        /// dialogue files plus those variables are enough to work out what is outstanding —
        /// even for a quest accepted long before this mod was installed.
        ///
        /// Trader tasks are deliberately excluded: their state lives on the NPC
        /// (`entity.requestbronze`), which only exists while that trader is loaded, so they
        /// cannot be recovered from here and are picked up in conversation instead. Any
        /// requirement gated on entity scope is therefore skipped rather than guessed at.
        ///
        /// The errand is attributed to the NPC whose dialogue *asks for the goods*, which is
        /// not always the one who set the quest — Wall's daisy is handed to Gerhardt, Beata's
        /// bread to Kat. That is the one you need to walk to, so that is the one named.
        /// </summary>
        public List<QuestOffer> LiveVillageQuests()
        {
            var offers = new List<QuestOffer>();

            List<AssetLocation> files;
            try { files = capi.Assets.GetLocations("config/dialogue/"); }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not list dialogue files: {0}", e.Message);
                return offers;
            }
            if (files == null) return offers;

            foreach (var loc in files)
            {
                if (!loc.Path.EndsWith(".json")) continue;

                DlgFile file;
                try { file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>(); }
                catch { continue; }
                if (file?.components == null) continue;

                var offer = new QuestOffer { NpcName = NpcNameFrom(loc) };
                var seen = new HashSet<string>();

                foreach (var comp in file.components)
                {
                    if (comp?.text == null) continue;
                    foreach (var line in comp.text)
                    {
                        var conds = AllConditions(line);
                        var wanted = conds.Where(IsInventoryCondition).ToList();
                        if (wanted.Count == 0) continue;

                        var gates = conds.Where(c => !IsInventoryCondition(c)).ToList();
                        if (gates.Count == 0) continue;                                  // a price
                        if (gates.Any(g => g.variable?.StartsWith("player.") != true)) continue;
                        if (!gates.All(g => GateMet(g, null))) continue;

                        foreach (var w in wanted)
                        {
                            var req = ToRequirement(w);
                            if (req == null) continue;
                            string key = $"{req.Stack.Collectible.Code}|{req.Quantity}";
                            if (seen.Add(key)) offer.Requirements.Add(req);
                        }

                        foreach (var said in Briefing(file, gates))
                        {
                            if (!offer.Briefing.Contains(said)) offer.Briefing.Add(said);
                        }
                    }
                }

                if (offer.Requirements.Count > 0) offers.Add(offer);
            }
            return offers;
        }

        /// <summary>
        /// What was said when a quest chain was set, found from the chain alone — the history
        /// records a finished quest long after its pins are gone, so it cannot ask them.
        ///
        /// Works by handing Briefing a gate for the chain's own "started" variable: the step
        /// that *sets* it is the accepting one, and the speech leading to that choice is the
        /// pitch. Same walk as when the quest is live, from the other end.
        /// </summary>
        public List<string> BriefingForChain(string chainKey)
        {
            var gate = new DlgCond { variable = "player." + chainKey + "started", isValue = "true" };

            try
            {
                foreach (var loc in capi.Assets.GetLocations("config/dialogue/") ?? new List<AssetLocation>())
                {
                    if (!loc.Path.EndsWith(".json")) continue;

                    var file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>();
                    if (file?.components == null) continue;

                    var lines = Briefing(file, new List<DlgCond> { gate });
                    if (lines.Count > 0) return lines;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not recover quest text: {0}", e.Message);
            }
            return new List<string>();
        }

        /// <summary>Read a player-scope quest flag. Public so the history can ask too.</summary>
        public string PlayerVariable(string name)
        {
            try
            {
                var vars = capi.ModLoader.GetModSystem<VariablesModSystem>();
                return vars?.GetPlayerVariable(capi.World.Player.PlayerUID, name);
            }
            catch { return null; }
        }

        Dictionary<string, QuestChain> chains;

        /// <summary>
        /// Every quest chain the loaded content defines, with a prerequisite depth.
        ///
        /// Chains are found from the variables dialogue *sets* — anything matching
        /// `<name>quest{started,completed,rewarded}`. Prerequisites come from the gates on the
        /// answers that open a chain: if opening Beata's quest requires a variable another
        /// chain sets, that chain comes first. Depth is how deep that goes, which is what lets
        /// undated history be ordered by story rather than alphabetically.
        ///
        /// Built once per session — content cannot change under us mid-world.
        /// </summary>
        public Dictionary<string, QuestChain> QuestChains()
        {
            if (chains != null) return chains;
            chains = new Dictionary<string, QuestChain>();

            var setterOf = new Dictionary<string, string>();     // variable -> chain that sets it
            var files = new List<DlgFile>();

            try
            {
                foreach (var loc in capi.Assets.GetLocations("config/dialogue/") ?? new List<AssetLocation>())
                {
                    if (!loc.Path.EndsWith(".json")) continue;
                    var file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>();
                    if (file?.components == null) continue;
                    files.Add(file);

                    foreach (var comp in file.components)
                    {
                        if (comp?.setVariables == null) continue;
                        foreach (var set in comp.setVariables.Keys)
                        {
                            string key = ChainKeyOf(set);
                            if (key == null) continue;

                            setterOf[set] = key;
                            if (!chains.ContainsKey(key))
                            {
                                chains[key] = new QuestChain { Key = key, DisplayName = NameOfChain(key) };
                            }
                        }
                    }
                }

                // What must happen before each chain opens.
                foreach (var file in files)
                {
                    foreach (var comp in file.components)
                    {
                        if (comp?.setVariables == null || comp.text == null) continue;
                        if (!comp.setVariables.Keys.Any(k => k.EndsWith("queststarted"))) continue;

                        string opening = ChainKeyOf(comp.setVariables.Keys.First(k => k.EndsWith("queststarted")));
                        if (opening == null || !chains.TryGetValue(opening, out var chain)) continue;

                        foreach (var line in comp.text)
                        {
                            foreach (var cond in AllConditions(line))
                            {
                                if (cond.variable == null || cond.isValue == null) continue;
                                if (!setterOf.TryGetValue(cond.variable, out string from)) continue;
                                if (from != opening) chain.Requires.Add(from);
                            }
                        }
                    }
                }

                foreach (var chain in chains.Values) chain.Depth = DepthOf(chain, new HashSet<string>());
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not map quest chains: {0}", e.Message);
            }
            return chains;
        }

        int DepthOf(QuestChain chain, HashSet<string> visiting)
        {
            // The guard matters: content is free to describe a cycle, and a story that
            // requires itself must not take the client with it.
            if (!visiting.Add(chain.Key)) return 0;

            int deepest = 0;
            foreach (var need in chain.Requires)
            {
                if (!chains.TryGetValue(need, out var prior)) continue;
                deepest = Math.Max(deepest, DepthOf(prior, visiting) + 1);
            }
            visiting.Remove(chain.Key);
            return deepest;
        }

        /// <summary>"player.beataqueststarted" → "beataquest"; null when it is not quest state.</summary>
        static string ChainKeyOf(string variable)
        {
            if (variable == null) return null;
            string name = variable.StartsWith("player.") ? variable.Substring("player.".Length) : variable;

            foreach (var suffix in new[] { "started", "completed", "rewarded" })
            {
                if (name.EndsWith("quest" + suffix)) return name.Substring(0, name.Length - suffix.Length);
            }
            return null;
        }

        static string NameOfChain(string key)
        {
            string name = key.EndsWith("quest") ? key.Substring(0, key.Length - "quest".Length) : key;
            return name.Length == 0 ? key : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>"config/dialogue/kat.json" → "Kat". Vanilla names these files after the
        /// villager, and the real display name replaces this the first time you speak to
        /// them.</summary>
        static string NpcNameFrom(AssetLocation loc)
        {
            string name = loc.Path;
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".json")) name = name.Substring(0, name.Length - 5);

            return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// The briefing for an errand already on the list, looked up by who gave it — so
        /// errands tracked before this feature existed can be filled in without asking the
        /// player to go and accept them again.
        ///
        /// The gates are deliberately *not* required to be satisfied here: the quest is
        /// already underway and may be nearly done. We are recovering words that were said,
        /// not deciding whether to offer anything, so there is nothing to spoil.
        /// </summary>
        public List<string> BriefingFor(string giverName, string itemCode, int quantity)
        {
            var file = LoadDialogueByName(giverName);
            if (file?.components == null) return null;

            foreach (var comp in file.components)
            {
                if (comp?.text == null) continue;
                foreach (var line in comp.text)
                {
                    var conds = AllConditions(line);
                    var wanted = conds.Where(IsInventoryCondition).ToList();
                    if (wanted.Count == 0) continue;

                    var gates = conds.Where(c => !IsInventoryCondition(c)).ToList();
                    if (gates.Count == 0) continue;      // a price, not an errand

                    foreach (var w in wanted)
                    {
                        var req = ToRequirement(w);
                        if (req?.Stack?.Collectible?.Code?.ToShortString() != itemCode) continue;
                        if (req.Quantity != quantity) continue;

                        var lines = Briefing(file, gates);
                        if (lines.Count > 0) return lines;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// A villager's dialogue file found from their name — vanilla names the files after
        /// them ("Gerhardt" → config/dialogue/gerhardt.json). A guess, but a cheap one: when
        /// it misses, nothing is filled in and the next conversation with them does the job
        /// properly.
        /// </summary>
        DlgFile LoadDialogueByName(string giverName)
        {
            if (string.IsNullOrWhiteSpace(giverName)) return null;

            string name = new string(giverName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (name.Length == 0) return null;

            try { return capi.Assets.TryGet(new AssetLocation("game", $"config/dialogue/{name}.json"))?.ToObject<DlgFile>(); }
            catch { return null; }
        }

        void AddSpeech(List<string> into, DlgComp comp)
        {
            if (comp?.text == null || IsPlayer(comp)) return;

            foreach (var line in comp.text)
            {
                if (string.IsNullOrEmpty(line?.value)) continue;

                string text = Lang.Get(line.value);
                // Lang.Get hands back the key when there is no translation; a raw key on
                // screen is worse than saying nothing.
                if (string.IsNullOrEmpty(text) || text == line.value) continue;
                if (!into.Contains(text)) into.Add(text);
            }
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
        /// Is this non-inventory condition currently true right now?
        ///
        /// Two scopes matter. **Player** variables carry village quest state
        /// ("gerhardtqueststarted"). **Entity** variables carry state belonging to the NPC in
        /// front of you, which is how traders hold a task: the treasure hunter sets
        /// `entity.requestbronze` when he asks for a pickaxe and `entity.bronzereceived` when
        /// you hand it over. Refusing to read entity scope — as this did at first — meant
        /// every trader errand was silently invisible, since its gate could never be met.
        ///
        /// Anything else stays unmet: an unrecognised gate must never leak a task that was
        /// not offered.
        /// </summary>
        bool GateMet(DlgCond cond, Entity npc)
        {
            if (cond.variable == null) return false;

            string actual;
            try
            {
                var vars = capi.ModLoader.GetModSystem<VariablesModSystem>();
                if (vars == null) return false;

                if (cond.variable.StartsWith("player."))
                {
                    actual = vars.GetPlayerVariable(
                        capi.World.Player.PlayerUID, cond.variable.Substring("player.".Length)) ?? "";
                }
                else if (cond.variable.StartsWith("entity.") && npc != null)
                {
                    actual = vars.GetVariable(EnumActivityVariableScope.Entity,
                        cond.variable.Substring("entity.".Length), npc) ?? "";
                }
                else return false;
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
