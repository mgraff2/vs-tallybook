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

    /// <summary>
    /// One fetch errand exactly as the dialogue files describe it — who asks, what for, how
    /// many, what maps come with it, what gates it, and what was said. Static content, so it
    /// is available whether or not this player has ever been offered the quest, and whether or
    /// not anything about it was captured at the time.
    /// </summary>
    public class QuestDef
    {
        public string NpcName;
        public string ItemCode;
        public int Quantity;
        public List<string> Maps = new List<string>();
        internal List<QuestScanner.DlgCond> Gates = new List<QuestScanner.DlgCond>();
        public List<string> Briefing = new List<string>();

        /// <summary>What was said when the goods changed hands — the player's turn-in line
        /// and the NPC's response, recovered from the dialogue graph like the briefing is.
        /// The half of the conversation a finished quest's record was missing (Mark: "I want
        /// to see what she said after I brought her the goods").</summary>
        public List<string> HandIn = new List<string>();

        /// <summary>
        /// Variables the hand-over sets — what "this errand is finished" looks like in the
        /// player's own state. Collected by walking forward from the turn-in line into the
        /// components it leads to (Agnieszka's `workaccept` flows into `workcomplete`, which
        /// sets `agnieszkaquestcompleted`). Any one of them reading true means the goods were
        /// handed over, whether or not this mod was watching at the time.
        /// </summary>
        internal List<QuestScanner.DlgCond> Done = new List<QuestScanner.DlgCond>();
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

        /// <summary>Maps this NPC hands out — "Map to the Devastation" and the like. A quest
        /// that comes with a map is a quest with a place attached, and saying which one is the
        /// difference between "fetch a lens" and "fetch a lens from the Devastation".</summary>
        public List<string> Maps = new List<string>();

        public string Summary => string.Join(", ", Requirements.Select(r => $"{r.Quantity} x {r.Name}"));
    }

    /// <summary>A story flag dialogue can set, and what has to happen first.</summary>
    public class StoryVar
    {
        public string Name;
        public HashSet<string> Requires = new HashSet<string>();
        public int Depth;
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

            /// <summary>What the step hands over — including the locator maps that quests
            /// come with, which is how an errand knows there is a place attached to it.</summary>
            public StackSpec triggerdata;
        }
        class DlgText { public string value; public string jumpTo; public DlgCond condition; public DlgCond[] conditions; }
        // Internal rather than private: QuestDef carries a quest's gates so they can be
        // evaluated later, when the answer can have changed. The rest of the dialogue shape
        // stays private — it is a parsing detail and nothing outside needs it.
        internal class DlgCond { public string variable; public string isValue; public string isNotValue; }
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
                // Never blank: the name ends up as a map marker's title, and a blank title
                // crashes the client the moment that marker is hovered.
                NpcName = NonBlank(npc.GetName(), "Villager"),
                Pos = npc.Pos?.XYZ
            };

            var seen = new HashSet<string>();

            // Requirements sharing a gate set are *alternatives*, not a shopping list: Better
            // Ruins' salt trader accepts an andesite, basalt or peridotite quern for one
            // errand, each as its own answer line. Adding them all would demand three querns
            // for a task that wants one.
            //
            // Grouped per *answer line*, then per gate set. The distinction carries the whole
            // AND/OR logic: conditions on one line must all hold for that answer, so two
            // inventory conditions on the same line are a shopping list — both required. Two
            // *lines* sharing a gate set are the same errand written once per acceptable
            // item — alternatives. Flattening them into one pool read "3 logs AND 5 hides"
            // as "logs, or hides if you prefer" (found by Fable's review).
            var byGate = new Dictionary<string, List<List<QuestRequirement>>>();
            var allGates = new List<DlgCond>();

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

                    var thisLine = new List<QuestRequirement>();
                    foreach (var w in wanted)
                    {
                        var req = ToRequirement(w);
                        if (req == null) continue;
                        string key = $"{req.Stack.Collectible.Code}|{req.Quantity}";
                        if (!seen.Add(key)) continue;
                        thisLine.Add(req);
                    }
                    if (thisLine.Count > 0)
                    {
                        if (!byGate.TryGetValue(gateKey, out var lines))
                        {
                            lines = new List<List<QuestRequirement>>();
                            byGate[gateKey] = lines;
                        }
                        lines.Add(thisLine);
                        allGates.AddRange(gates);
                    }

                    foreach (var said in Briefing(file, gates))
                    {
                        if (!offer.Briefing.Contains(said)) offer.Briefing.Add(said);
                    }
                }
            }

            // Only maps tied to these errands by a shared gate variable — never everything
            // the file hands out.
            offer.Maps.AddRange(MapsForGates(file, allGates));

            foreach (var lines in byGate.Values)
            {
                // Everything the first line wants — a line is a conjunction, so a two-item
                // line is two requirements, not a choice.
                offer.Requirements.AddRange(lines[0]);
                if (lines.Count < 2) continue;

                // The other lines are the alternatives. The note names every acceptable
                // option, tracked one included — "any of these" should mean these.
                string note = AlternativesNote + string.Join(", ",
                    lines.SelectMany(l => l).Select(r => r.Name).Distinct());
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
            string me = capi.World?.Player?.PlayerName ?? "You";

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

                // Read as the conversation it was. Every villager line answers something you
                // said, and the dialogue graph knows which: the answer that jumps to a step is
                // what prompted it.
                if (briefing?.code != null) AddPlayerLine(lines, file, briefing.code, me);
                AddSpeech(lines, briefing);

                if (choice != null) AddPlayerLine(lines, file, accept.code, me);
                AddSpeech(lines, accept);
            }
            return lines;
        }

        /// <summary>
        /// Map handouts that belong to THIS errand: components handing a locator map, where an
        /// answer line leading into the handout is gated on one of the errand's own gate
        /// variables.
        ///
        /// The shared variable is the game's own statement that they are one quest. Tobias'
        /// "what am I supposed to do again?" line hands over the Devastation map and is gated
        /// on `player.gavelens` — the same variable gating the lens turn-in. Gerhardt's and
        /// Agnieszka's handout threads are gated on their own letter/map variables, shared
        /// with no errand, so nothing attaches to their fetch quests (all verified against
        /// the 1.22.6 dialogue files).
        ///
        /// This replaces attaching every map in the *file* to every errand in it, which sent
        /// Agnieszka's iron ingots to the Devastation — a file spans unrelated threads, and
        /// only a shared gate proves two of them are the same quest. Unconditioned entry
        /// lines attach nothing: fail toward no map, never toward a walk across the world.
        ///
        /// Names come from the item itself — "locatormap-devastationarea" is not something to
        /// show anybody, and the game already has a proper name for it.
        /// </summary>
        /// <summary>
        /// What handing the goods over *sets* — the completion signature of a turn-in line.
        /// Walks forward from the line's jumpTo target through the component chain (explicit
        /// jumpTo, else file-order fallthrough, the same convention the dialogue runner uses),
        /// a few steps only, collecting every setVariables along the way. Agnieszka's take
        /// step flows into `workcomplete`, which sets `agnieszkaquestcompleted=true`; Tobias'
        /// `lens` sets `gavelens=true` directly. Bounded so an errand can never claim a
        /// variable set half a conversation away.
        /// </summary>
        /// <summary>
        /// The hand-over as a transcript: the player's turn-in words, then the NPC's
        /// response, walked forward from the turn-in line exactly as <see cref="DoneSetters"/>
        /// walks it for variables. Read from the dialogue file, so no part of the live
        /// conversation is touched — available whether or not this mod was listening when the
        /// goods were handed over.
        ///
        /// A player component with a single onward line is walked *through* without quoting
        /// it — that is the "Continue" punctuation the files use mid-speech (Gerhardt's
        /// reward is split across two components with one between them) — while a player
        /// component offering real choices means control came back to the player, which is
        /// where the exchange ends.
        /// </summary>
        List<string> HandInTranscript(DlgFile file, DlgText turnInLine)
        {
            var lines = new List<string>();
            string me = capi.World?.Player?.PlayerName ?? "You";

            if (!string.IsNullOrEmpty(turnInLine?.value))
            {
                string text = Lang.Get(turnInLine.value);
                if (!string.IsNullOrEmpty(text) && text != turnInLine.value)
                {
                    lines.Add($"{me}: \"{OneLine(text)}\"");
                }
            }

            string cur = turnInLine?.jumpTo;
            for (int hop = 0; hop < 6 && cur != null; hop++)
            {
                int idx = Array.FindIndex(file.components, c => c?.code == cur);
                if (idx < 0) break;
                var comp = file.components[idx];

                if (IsPlayer(comp))
                {
                    var only = comp.text != null && comp.text.Length == 1 ? comp.text[0] : null;
                    if (only?.jumpTo == null) break;
                    cur = only.jumpTo;
                    continue;
                }

                AddSpeech(lines, comp);
                cur = comp.jumpTo
                      ?? (idx + 1 < file.components.Length ? file.components[idx + 1]?.code : null);
            }
            return lines;
        }

        List<DlgCond> DoneSetters(DlgFile file, DlgText turnInLine)
        {
            var result = new List<DlgCond>();
            string cur = turnInLine?.jumpTo;

            for (int hop = 0; hop < 4 && cur != null; hop++)
            {
                int idx = Array.FindIndex(file.components, c => c?.code == cur);
                if (idx < 0) break;
                var comp = file.components[idx];

                if (comp.setVariables != null)
                {
                    foreach (var kv in comp.setVariables)
                    {
                        result.Add(new DlgCond { variable = kv.Key, isValue = kv.Value });
                    }
                }

                cur = comp.jumpTo
                      ?? (idx + 1 < file.components.Length ? file.components[idx + 1]?.code : null);
            }
            return result;
        }

        List<string> MapsForGates(DlgFile file, IEnumerable<DlgCond> gates)
        {
            var names = new List<string>();

            // Real state variables only, matched with their expected value. Inventory
            // conditions are excluded even though they ride in the gate list: "not carrying
            // the map" appears on the turn-in line AND on every map handout line as the same
            // pseudo-variable `player.inventory`, which made *any* map handout look tied to
            // *any* errand — Better Ruins' pickaxe fetch got the Sunrift Experiment map
            // attached that way and sent the player to a ruin instead of back to the trader
            // (found by Mark). Value-compatibility guards the other direction: a handout
            // gated on quest-DONE must not attach to the errand gated on quest-OPEN, or the
            // reward map masquerades as the errand's destination.
            var gateWants = gates
                .Where(g => g?.variable != null && g.variable != "player.inventory")
                .GroupBy(g => g.variable)
                .ToDictionary(g => g.Key, g => g.First());
            if (gateWants.Count == 0) return names;

            bool Compatible(DlgCond c)
                => c?.variable != null
                   && gateWants.TryGetValue(c.variable, out var want)
                   && c.isValue == want.isValue && c.isNotValue == want.isNotValue;

            foreach (var comp in file.components)
            {
                string code = comp?.triggerdata?.code;
                if (code == null || !code.Contains("locatormap")) continue;

                bool tied = file.components.Any(other =>
                    other?.text != null && other.text.Any(line =>
                        line?.jumpTo == comp.code && AllConditions(line).Any(Compatible)));
                if (!tied) continue;

                var item = capi.World.GetItem(new AssetLocation(code));
                string name = item == null ? null : new ItemStack(item).GetName();
                if (string.IsNullOrEmpty(name)) continue;

                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }

        /// <summary>The player's answer that leads to a given step, quoted under their name.</summary>
        void AddPlayerLine(List<string> into, DlgFile file, string targetCode, string me)
        {
            foreach (var comp in file.components)
            {
                if (comp?.text == null || !IsPlayer(comp)) continue;

                foreach (var line in comp.text)
                {
                    if (line?.jumpTo != targetCode || string.IsNullOrEmpty(line.value)) continue;

                    string text = Lang.Get(line.value);
                    if (string.IsNullOrEmpty(text) || text == line.value) continue;

                    string said = $"{me}: \"{OneLine(text)}\"";
                    if (!into.Contains(said)) into.Add(said);
                    return;
                }
            }
        }

        /// <summary>
        /// Flatten a spoken line to one line. Dialogue text carries its own line breaks for
        /// the conversation window, and a static text element honours them — so a single
        /// "row" quietly rendered as three and painted over the two beneath it.
        /// </summary>
        static string OneLine(string text)
        {
            var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
            return flat;
        }

        static bool IsPlayer(DlgComp comp) => comp?.owner == "player";

        /// <summary>Text, or a stand-in when there is none. An entity can be nameless, and a
        /// nameless quest giver used to travel all the way to a blank map marker.</summary>
        static string NonBlank(string text, string fallback)
            => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

        /// <summary>"gerhardt" → "Gerhardt". The owner field is the speaker.</summary>
        static string SpeakerOf(DlgComp comp)
        {
            string owner = comp?.owner;
            if (string.IsNullOrEmpty(owner)) return "They";
            return char.ToUpperInvariant(owner[0]) + owner.Substring(1);
        }

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

                        // At least one gate must be a *positive* flag that something happened.
                        // Negative gates ("gavelens is not true") are satisfied by default on a
                        // brand-new world, so a scan that accepted them would hand a player who
                        // has never left home a list of every errand in the game. In
                        // conversation that risk does not exist — you are stood in front of the
                        // person — which is why only this path is fussy.
                        if (!gates.Any(g => g.isValue != null)) continue;

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
            // Memoized, and the empty answer is memoized too — that is the load-bearing half.
            // This walks and deserializes every dialogue file, its callers run per dialog
            // recompose and on a 1s tick, and a chain whose text cannot be recovered (a modded
            // chain with no Lang entries) otherwise re-parses the whole set every second
            // forever, precisely because it found nothing to store (found by Fable's review).
            // The files cannot change within a session, so a miss today is a miss tonight.
            if (briefingsByChain.TryGetValue(chainKey, out var cached)) return cached;

            var gate = new DlgCond { variable = "player." + chainKey + "started", isValue = "true" };
            var result = new List<string>();
            try
            {
                foreach (var loc in capi.Assets.GetLocations("config/dialogue/") ?? new List<AssetLocation>())
                {
                    if (!loc.Path.EndsWith(".json")) continue;

                    var file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>();
                    if (file?.components == null) continue;

                    var lines = Briefing(file, new List<DlgCond> { gate });
                    if (lines.Count > 0) { result = lines; break; }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not recover quest text: {0}", e.Message);
            }
            return briefingsByChain[chainKey] = result;
        }

        readonly Dictionary<string, List<string>> briefingsByChain = new Dictionary<string, List<string>>();

        /// <summary>
        /// What was said at a chain quest's hand-over, found from the chain alone. The
        /// catalogue's errand defs already carry the transcript; the one whose turn-in sets
        /// this chain's `…questcompleted` is this chain's hand-over. Callers must treat the
        /// result as read-only — it is the catalogue's own list.
        /// </summary>
        public List<string> HandInForChain(string chainKey)
        {
            string done = "player." + chainKey + "completed";
            foreach (var def in QuestCatalogue())
            {
                if (def.HandIn.Count == 0) continue;
                if (def.Done.Any(d => string.Equals(d.variable, done, StringComparison.OrdinalIgnoreCase)))
                {
                    return def.HandIn;
                }
            }
            return new List<string>();
        }

        /// <summary>
        /// Was this text captured before extracts were written as a transcript? Plain
        /// paragraphs from an older build are re-derived rather than left looking different
        /// from everything captured since.
        /// </summary>
        /// Attribution is asked of the set, not of every line: a capture can legitimately carry
        /// notes that are ours rather than anyone's speech ("Any of these will do: …"), and
        /// demanding a speaker on those marked every modern capture as stale, so it was
        /// re-derived at each login and rewritten on top of itself.
        /// <summary>Our own note among the captured lines — nobody says this, so it must never
        /// be dressed up as speech.</summary>
        public const string AlternativesNote = "Any of these will do: ";

        /// <summary>
        /// A captured line as `Name: "…"`, giving it back its speaker if it lost one.
        ///
        /// Errands captured before quotes carried speakers are plain paragraphs, and only the
        /// NPC's own words were kept then — so the giver is the honest attribution, not a
        /// guess. It is done here, at the point of drawing, because the repair that runs at
        /// login can only re-derive text whose dialogue file it can find by the giver's name:
        /// true of every villager, false of every trader (found by Mark — Gerhardt and
        /// Agnieszka read correctly while the trader Luxuries did not).
        /// </summary>
        public static string Attributed(string line, string giver)
        {
            if (string.IsNullOrWhiteSpace(line)) return line;
            if (line.Contains(": \"")) return line;                       // already a transcript
            if (line.StartsWith(AlternativesNote, StringComparison.Ordinal)) return line;

            string who = string.IsNullOrWhiteSpace(giver) ? "They" : giver.Trim();
            return $"{who}: \"{line.Trim()}\"";
        }

        public static bool IsTranscript(List<string> text)
            => text != null && text.Count > 0
               && text.Any(line => line.Contains(": \""))
               && text.All(line => line.IndexOf('\n') < 0 && line.IndexOf('\r') < 0);

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

        /// <summary>Read an entity-scope quest flag off the NPC in front of you — the only
        /// moment such state is readable at all. Public for the story tracker, which records
        /// what it sees here so the fact outlives the conversation.</summary>
        public string EntityVariable(string name, Entity npc)
        {
            if (npc == null) return null;
            try
            {
                var vars = capi.ModLoader.GetModSystem<VariablesModSystem>();
                return vars?.GetVariable(EnumActivityVariableScope.Entity, name, npc)?.ToString();
            }
            catch { return null; }
        }

        Dictionary<string, StoryVar> storyVars;

        /// <summary>
        /// Every player-scope story flag the loaded content can set, with what must happen
        /// first.
        ///
        /// Quest chains are only a slice of the story: buying the elk, reading the note,
        /// hearing about the archives and meeting each villager are all recorded the same way
        /// — a variable set by a line of dialogue — and none of them is called `…quest…`. An
        /// archive that only knows about quest chains is empty for a player who has done
        /// plenty (Mark).
        ///
        /// Prerequisites are the conditions on the answer that *leads to* the step which sets
        /// the flag, so the graph reflects what actually has to precede what.
        /// </summary>
        public Dictionary<string, StoryVar> StoryVariables()
        {
            if (storyVars != null) return storyVars;
            storyVars = new Dictionary<string, StoryVar>();

            try
            {
                var files = new List<DlgFile>();
                foreach (var loc in capi.Assets.GetLocations("config/dialogue/") ?? new List<AssetLocation>())
                {
                    if (!loc.Path.EndsWith(".json")) continue;
                    var file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>();
                    if (file?.components != null) files.Add(file);
                }

                // What can be set at all.
                foreach (var file in files)
                {
                    foreach (var comp in file.components)
                    {
                        foreach (var name in PlayerVarsSetBy(comp))
                        {
                            if (!storyVars.ContainsKey(name)) storyVars[name] = new StoryVar { Name = name };
                        }
                    }
                }

                // What has to come first.
                foreach (var file in files)
                {
                    foreach (var setter in file.components)
                    {
                        var names = PlayerVarsSetBy(setter).ToList();
                        if (names.Count == 0 || setter.code == null) continue;

                        foreach (var other in file.components)
                        {
                            if (other?.text == null) continue;
                            foreach (var line in other.text)
                            {
                                if (line?.jumpTo != setter.code) continue;

                                foreach (var cond in AllConditions(line))
                                {
                                    if (cond.variable == null || cond.isValue == null) continue;
                                    if (!cond.variable.StartsWith("player.")) continue;

                                    string need = cond.variable.Substring("player.".Length);
                                    if (!storyVars.ContainsKey(need)) continue;

                                    foreach (var name in names)
                                    {
                                        if (need != name) storyVars[name].Requires.Add(need);
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var v in storyVars.Values) v.Depth = DepthOfVar(v, new HashSet<string>());
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not map story variables: {0}", e.Message);
            }
            return storyVars;
        }

        static IEnumerable<string> PlayerVarsSetBy(DlgComp comp)
        {
            if (comp?.setVariables == null) yield break;
            foreach (var key in comp.setVariables.Keys)
            {
                if (key != null && key.StartsWith("player.")) yield return key.Substring("player.".Length);
            }
        }

        int DepthOfVar(StoryVar v, HashSet<string> visiting)
        {
            if (!visiting.Add(v.Name)) return 0;      // content may describe a cycle

            int deepest = 0;
            foreach (var need in v.Requires)
            {
                if (!storyVars.TryGetValue(need, out var prior)) continue;
                deepest = Math.Max(deepest, DepthOfVar(prior, visiting) + 1);
            }
            visiting.Remove(v.Name);
            return deepest;
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
        /// Every fetch errand the dialogue files describe, whether or not this player has been
        /// offered it. Built once per session — the files cannot change under us.
        ///
        /// This is a *reference* catalogue, and the distinction matters: it is what lets an
        /// errand the player already has be filled in completely — which maps lead to it, who
        /// actually takes the goods, what they said — without depending on us having been
        /// watching when it was accepted, or on state that a lost save took with it.
        ///
        /// It must never be used to *offer* anything. Half of what is in here has not been
        /// offered to this player, and showing it would spoil content the game is deliberately
        /// withholding. Deciding what is live is <see cref="LiveVillageQuests"/>'s job, and it
        /// checks gates properly; this one deliberately does not filter by them.
        /// </summary>
        public List<QuestDef> QuestCatalogue()
        {
            if (catalogue != null) return catalogue;
            catalogue = new List<QuestDef>();

            List<AssetLocation> files;
            try { files = capi.Assets.GetLocations("config/dialogue/"); }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not list dialogue files: {0}", e.Message);
                return catalogue;
            }
            if (files == null) return catalogue;

            foreach (var loc in files)
            {
                if (!loc.Path.EndsWith(".json")) continue;

                DlgFile file;
                try { file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>(); }
                catch { continue; }
                if (file?.components == null) continue;

                string npc = NpcNameFrom(loc);

                foreach (var comp in file.components)
                {
                    if (comp?.text == null) continue;
                    foreach (var line in comp.text)
                    {
                        var conds = AllConditions(line);
                        var wanted = conds.Where(IsInventoryCondition).ToList();
                        if (wanted.Count == 0) continue;

                        // Still required: a bare inventory condition with no gate is a *price*,
                        // not an errand — the game writes "costs one gear" exactly as it writes
                        // "bring me ten hides". That rule is about what the condition means, so
                        // it holds here too, unlike gate *satisfaction*, which does not.
                        var gates = conds.Where(c => !IsInventoryCondition(c)).ToList();
                        if (gates.Count == 0) continue;

                        foreach (var w in wanted)
                        {
                            var req = ToRequirement(w);
                            string code = req?.Stack?.Collectible?.Code?.ToShortString();
                            if (code == null) continue;

                            catalogue.Add(new QuestDef
                            {
                                NpcName = npc,
                                ItemCode = code,
                                Quantity = req.Quantity,
                                // This errand's maps, tied by shared gate variable — never
                                // everything the file hands out.
                                Maps = MapsForGates(file, gates),
                                Gates = gates,
                                Briefing = Briefing(file, gates),
                                HandIn = HandInTranscript(file, line),
                                Done = DoneSetters(file, line)
                            });
                        }
                    }
                }
            }
            return catalogue;
        }

        List<QuestDef> catalogue;

        /// <summary>Drop everything derived from the asset set; it differs between servers.</summary>
        public void InvalidateCatalogue()
        {
            catalogue = null;
            briefingsByChain.Clear();
            rewardGiverByChain.Clear();
            rewardTextByChain.Clear();
        }

        /// <summary>
        /// Who pays out a chain's reward — the NPC whose dialogue sets `<chain>rewarded`.
        /// That is the person to walk to once a quest reads "done — go and collect", and it
        /// is not always the one who took the goods: Kat takes Beata's bread, Beata pays for
        /// it. Null (memoized, like an empty briefing) when no file sets the variable.
        /// </summary>
        public string RewardGiverForChain(string chainKey)
        {
            if (rewardGiverByChain.TryGetValue(chainKey, out string cached)) return cached;

            string wanted = "player." + chainKey + "rewarded";
            string giver = null;
            try
            {
                foreach (var loc in capi.Assets.GetLocations("config/dialogue/") ?? new List<AssetLocation>())
                {
                    if (!loc.Path.EndsWith(".json")) continue;

                    var file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>();
                    if (file?.components == null) continue;

                    bool sets = file.components.Any(c => c?.setVariables != null
                        && c.setVariables.Keys.Any(k =>
                            string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase)));
                    if (sets) { giver = NpcNameFrom(loc); break; }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not find reward giver for {0}: {1}", chainKey, e.Message);
            }
            return rewardGiverByChain[chainKey] = giver;
        }

        readonly Dictionary<string, string> rewardGiverByChain = new Dictionary<string, string>();

        /// <summary>
        /// What was said when the reward was collected — the third act of a chain quest's
        /// conversation, after the briefing and the hand-in. Found from the chain alone: a
        /// player-owned line whose forward walk sets `<chain>rewarded` is the collecting
        /// step (Beata's "I delivered your bread" answer leads into "So glad…" and the flag),
        /// and the same walk that turns a turn-in line into a transcript turns this one.
        /// Empty (memoized) when the chain has no reward step. Only ever appended to records
        /// already at stage rewarded — before that the words have not been spoken, and
        /// showing them would spoil the visit.
        /// </summary>
        public List<string> RewardTranscriptForChain(string chainKey)
        {
            if (rewardTextByChain.TryGetValue(chainKey, out var cached)) return cached;

            string wanted = "player." + chainKey + "rewarded";
            var result = new List<string>();
            try
            {
                foreach (var loc in capi.Assets.GetLocations("config/dialogue/") ?? new List<AssetLocation>())
                {
                    if (!loc.Path.EndsWith(".json")) continue;

                    var file = capi.Assets.TryGet(loc)?.ToObject<DlgFile>();
                    if (file?.components == null) continue;

                    foreach (var comp in file.components)
                    {
                        if (comp?.text == null || !IsPlayer(comp)) continue;
                        foreach (var line in comp.text)
                        {
                            if (line?.jumpTo == null) continue;
                            if (!DoneSetters(file, line).Any(d =>
                                string.Equals(d.variable, wanted, StringComparison.OrdinalIgnoreCase))) continue;

                            var said = HandInTranscript(file, line);
                            if (said.Count > 0) result = said;
                            break;
                        }
                        if (result.Count > 0) break;
                    }
                    if (result.Count > 0) break;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not recover reward text for {0}: {1}", chainKey, e.Message);
            }
            return rewardTextByChain[chainKey] = result;
        }

        readonly Dictionary<string, List<string>> rewardTextByChain = new Dictionary<string, List<string>>();

        /// <summary>
        /// The catalogue entry behind a pin. The giver's name is the strongest signal but the
        /// weakest link — it is recorded from a live entity, so it can be blank, renamed, or
        /// lost — hence the fall back to what was asked for and how much, which is what makes
        /// an errand identifiable at all.
        ///
        /// There is deliberately no item-code-only fallback. The quantity passed in is the
        /// pin's Count, which the player can edit, and once it drifts a bare-code match picks
        /// whichever quest for that item the catalogue lists first — attaching the wrong
        /// conversation to the errand and, because the result reads as a finished transcript,
        /// blocking every future repair (found by Fable's review). No match beats a confident
        /// wrong one.
        /// </summary>
        public QuestDef DefFor(string giver, string itemCode, int quantity)
        {
            var all = QuestCatalogue();
            return all.FirstOrDefault(d => d.NpcName == giver && d.ItemCode == itemCode && d.Quantity == quantity)
                ?? all.FirstOrDefault(d => d.ItemCode == itemCode && d.Quantity == quantity)
                ?? all.FirstOrDefault(d => d.NpcName == giver && d.ItemCode == itemCode);
        }

        /// <summary>Are this errand's gates satisfied for the player right now? Entity-scope
        /// gates live on an NPC that may not be loaded, so they read as unmet — fail toward
        /// "no", never toward a claim we cannot support.</summary>
        public bool DefIsLive(QuestDef def)
            => def?.Gates != null && def.Gates.Count > 0 && def.Gates.All(g => GateMet(g, null));

        /// <summary>Has this errand's hand-over already happened? Any completion variable
        /// reading its set value is a yes — the state outlives the conversation, so this is
        /// answerable long after, and for player-scope variables without any NPC nearby.
        /// Entity-scope completion needs the NPC (pass them while conversing); absent that it
        /// reads as not-done, failing toward "still open" rather than archiving a live errand.</summary>
        internal bool DefIsDone(QuestDef def, Entity npc = null)
            => def?.Done != null && def.Done.Count > 0 && def.Done.Any(d => GateMet(d, npc));

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

            string speaker = SpeakerOf(comp);

            foreach (var line in comp.text)
            {
                if (string.IsNullOrEmpty(line?.value)) continue;

                string text = Lang.Get(line.value);
                // Lang.Get hands back the key when there is no translation; a raw key on
                // screen is worse than saying nothing.
                if (string.IsNullOrEmpty(text) || text == line.value) continue;

                string said = $"{speaker}: \"{OneLine(text)}\"";
                if (!into.Contains(said)) into.Add(said);
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
