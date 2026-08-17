using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>One objective of a VS Quest quest: a set of accepted codes and how many.</summary>
    public class VsQuestObjective
    {
        /// <summary>As authored. Matched against a collectible's <c>Code.Path</c> only —
        /// domain-blind, with a trailing "*" meaning prefix — because that is exactly what
        /// the framework's own matcher does.</summary>
        public List<string> ValidCodes = new List<string>();
        public int Demand;

        // ---- resolved against this world, once ----------------------------------------
        internal HashSet<string> Codes;
        internal List<ItemStack> Samples;

        public bool Wildcarded => ValidCodes.Any(c => c != null && c.EndsWith("*", StringComparison.Ordinal));
    }

    /// <summary>
    /// One quest exactly as the framework's own <c>config/quests</c> files describe it. Static
    /// content, so it is readable whether or not this client was watching when the quest was
    /// accepted — the same reason the vanilla dialogue catalogue exists.
    /// </summary>
    public class VsQuestDef
    {
        public string Id;
        public int Cooldown;
        public bool PerPlayer;
        public string Predecessor;

        public List<VsQuestObjective> Gather = new List<VsQuestObjective>();
        public List<VsQuestObjective> Kill = new List<VsQuestObjective>();
        public List<VsQuestObjective> BlockPlace = new List<VsQuestObjective>();
        public List<VsQuestObjective> BlockBreak = new List<VsQuestObjective>();

        /// <summary>How many objectives are evaluated by the framework's own action registry.
        /// We cannot judge those, so a quest carrying any never claims to be ready.</summary>
        public int ActionObjectives;

        /// <summary>What the hand-over gives back, as readable lines for the archive.</summary>
        public List<string> Rewards = new List<string>();

        /// <summary>Objectives that are not gathering, in the order the framework reports their
        /// trackers (kill, then block-place, then block-break) — the order
        /// <c>ActiveQuest.trackerProgress()</c> concatenates them in.</summary>
        public IEnumerable<VsQuestObjective> Trackers => Kill.Concat(BlockPlace).Concat(BlockBreak);

        public int TrackerCount => Kill.Count + BlockPlace.Count + BlockBreak.Count;

        /// <summary>The framework's own lang convention. Falls back to the raw id: a quest
        /// from a server-side-only pack has no lang file on this client, and an honest id
        /// beats an invented name.</summary>
        public string Title => VsQuests.Translated(Id + "-title") ?? Id;

        public List<string> Description => VsQuests.RichTextLines(VsQuests.Translated(Id + "-desc"));
    }

    /// <summary>One quest the player is on, as the framework's own quest dialog reported it.</summary>
    public class VsQuestActive
    {
        public string QuestId;
        public long GiverId;

        /// <summary>Kill / block-place / block-break counters, concatenated in that order —
        /// the shape <c>ActiveQuest.trackerProgress()</c> hands out.</summary>
        public List<int> Trackers = new List<int>();
    }

    /// <summary>
    /// Reads the VS Quest framework (G3rste's <c>vsquest</c>) the way this mod reads everything
    /// else: from the content files and from state the server already syncs. Nothing here names
    /// a quest, a giver or a content pack — the framework's data is the contract, so a quest
    /// pack works with no per-pack support, exactly as a recipe mod does.
    ///
    /// Three sources, in descending authority:
    ///
    ///   1. <b>The catalogue</b> — <c>config/quests</c> assets, loaded on both sides by the
    ///      framework itself and therefore readable here. Objectives, demands, rewards.
    ///   2. <b>The quest dialog</b> — the only place the player's *active* quests and their
    ///      kill/block counters ever reach a client (the framework keeps them in savegame data
    ///      and pushes them in one packet when the dialog opens). Read by reflection while it
    ///      is open; never patched, never spoken to over its network channel — the only two
    ///      messages that channel accepts from a client both *write*, and this mod does not
    ///      write to anyone's world.
    ///   3. <b>The giver's WatchedAttributes</b> — <c>lastaccepted-…</c> and
    ///      <c>playercompleted-…</c>, which sync with the entity and are what makes a quest
    ///      recoverable on a machine that has never seen this world.
    /// </summary>
    public class VsQuests
    {
        readonly ICoreClientAPI capi;

        public VsQuests(ICoreClientAPI capi) { this.capi = capi; }

        // ---- catalogue ------------------------------------------------------------------

        Dictionary<string, VsQuestDef> catalogue;

        /// <summary>Recipes and assets arrive with the world, so the catalogue is per world —
        /// invalidated on join and leave exactly like the recipe index.</summary>
        public void Invalidate()
        {
            catalogue = null;
            foreach (var seen in seenGivers.Values) seen.Clear();
            seenGivers.Clear();
        }

        /// <summary>Is there anything here at all? Nameless on purpose: "a world with VS Quest
        /// content" is a fact about what the assets contain, not about a mod id.</summary>
        public bool Enabled => Catalogue().Count > 0;

        public Dictionary<string, VsQuestDef> Catalogue()
        {
            if (catalogue != null) return catalogue;

            var defs = new Dictionary<string, VsQuestDef>(StringComparer.Ordinal);
            try
            {
                // The framework reads the path prefix "config/quests" per mod. Enumerating
                // locations and filtering here rather than leaning on GetMany's own matching
                // covers BOTH layouts deterministically: the post-2.0 `config/quests/*.json`
                // and the older single `config/quests.json` its own example still ships.
                foreach (var loc in capi.Assets?.GetLocations("config/quests") ?? new List<AssetLocation>())
                {
                    if (loc?.Path == null || !loc.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { ParseInto(capi.Assets.TryGet(loc), defs); }
                    catch (Exception e)
                    {
                        capi.Logger.Warning("[tallybook] could not read quest file {0}: {1}", loc, e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] VS Quest catalogue unreadable: {0}", e.Message);
            }
            return catalogue = defs;
        }

        void ParseInto(IAsset asset, Dictionary<string, VsQuestDef> defs)
        {
            string text = asset?.ToText();
            if (string.IsNullOrWhiteSpace(text)) return;

            var token = JToken.Parse(text);
            // Authored as an array; a lone object is accepted rather than dropped, since the
            // cost of tolerating it is nothing and the cost of missing a pack's quests is a
            // silently empty list.
            IEnumerable<JToken> entries = token is JArray arr
                ? arr.Children()
                : (IEnumerable<JToken>)new[] { token };
            foreach (var entry in entries)
            {
                var json = entry.ToObject<QuestJson>();
                if (string.IsNullOrEmpty(json?.id)) continue;

                var def = new VsQuestDef
                {
                    Id = json.id,
                    Cooldown = json.cooldown,
                    PerPlayer = json.perPlayer,
                    Predecessor = json.predecessor,
                    ActionObjectives = json.actionObjectives?.Count ?? 0
                };
                Fill(def.Gather, json.gatherObjectives);
                Fill(def.Kill, json.killObjectives);
                Fill(def.BlockPlace, json.blockPlaceObjectives);
                Fill(def.BlockBreak, json.blockBreakObjectives);

                foreach (var reward in json.itemRewards ?? new List<RewardJson>())
                {
                    string name = NameOfCode(reward?.itemCode);
                    if (name != null) def.Rewards.Add($"{Math.Max(1, reward.amount)} x {name}");
                }
                int random = json.randomItemRewards?.selectAmount ?? 0;
                int pool = json.randomItemRewards?.items?.Count ?? 0;
                if (random > 0 && pool > 0)
                    def.Rewards.Add($"{random} of {pool} random reward(s)");

                // First definition of an id wins, matching the framework's own dictionary add.
                if (!defs.ContainsKey(def.Id)) defs[def.Id] = def;
            }
        }

        /// <summary>
        /// Every authored objective is kept, including one we can make nothing of. Dropping
        /// them would be worse than useless: the framework creates one counter per objective
        /// **as authored**, and the counters arrive as a flat list to be read back against
        /// this one — so a skipped entry silently shifts every count after it onto the wrong
        /// objective. The demand is kept raw for the same reason: it is what the framework
        /// compares against, and clamping it here would make us stricter than the quest is.
        /// </summary>
        static void Fill(List<VsQuestObjective> into, List<ObjJson> from)
        {
            foreach (var o in from ?? new List<ObjJson>())
            {
                if (o == null) continue;
                into.Add(new VsQuestObjective
                {
                    ValidCodes = (o.validCodes ?? new List<string>())
                        .Where(c => !string.IsNullOrEmpty(c)).ToList(),
                    Demand = o.demand
                });
            }
        }

        public VsQuestDef Def(string questId)
            => questId != null && Catalogue().TryGetValue(questId, out var def) ? def : null;

        // ---- objective -> requirement ---------------------------------------------------

        /// <summary>
        /// Every code in this world the objective accepts. Matching is the framework's own:
        /// <c>Collectible.Code.Path</c> equality, or prefix when the authored code ends in "*"
        /// — the domain is not part of it, which is why this asks the world rather than trying
        /// to build an AssetLocation from what was written.
        /// </summary>
        public bool ResolveObjective(VsQuestObjective obj)
        {
            if (obj == null) return false;
            if (obj.Codes != null) return obj.Codes.Count > 0;

            var codes = new HashSet<string>();
            var samples = new List<ItemStack>();

            void Consider(CollectibleObject c)
            {
                var path = c?.Code?.Path;
                if (path == null) return;
                if (!MatchesAny(obj.ValidCodes, path)) return;

                codes.Add(c.Code.ToShortString());
                if (samples.Count < 30)
                {
                    try { samples.Add(new ItemStack(c)); } catch { /* unstackable oddity: skip the icon */ }
                }
            }

            try
            {
                foreach (var item in capi.World.Items) Consider(item);
                foreach (var block in capi.World.Blocks) Consider(block);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not resolve a VS Quest objective: {0}", e.Message);
            }

            obj.Codes = codes;
            obj.Samples = samples;
            return codes.Count > 0;
        }

        public static bool MatchesAny(List<string> validCodes, string path)
        {
            foreach (var candidate in validCodes ?? new List<string>())
            {
                if (candidate == null) continue;
                if (candidate.EndsWith("*", StringComparison.Ordinal))
                {
                    if (path.StartsWith(candidate.Substring(0, candidate.Length - 1), StringComparison.Ordinal))
                        return true;
                }
                else if (candidate == path) return true;
            }
            return false;
        }

        /// <summary>A representative stack for the pin — what it is called and what it draws.</summary>
        public ItemStack SampleStack(VsQuestObjective obj)
            => ResolveObjective(obj) ? obj.Samples.FirstOrDefault() : null;

        /// <summary>
        /// The counting row for a gather objective: every accepted code, summed. Deliberately
        /// NOT a page-exact self requirement — the framework sums whole stacks across every
        /// non-creative inventory by bare code, so a page-exact count would report nothing
        /// while the player carries a variant the quest happily accepts.
        /// </summary>
        public Requirement RequirementFor(VsQuestObjective obj)
        {
            if (!ResolveObjective(obj)) return null;

            var req = new Requirement { Quantity = Math.Max(1, obj.Demand) };
            foreach (var code in obj.Codes) req.ExactCodes.Add(code);
            req.MatchedVariants = obj.Codes.Count;
            if (obj.Samples.Count > 0) req.PresetSampleStacks(obj.Samples);

            string name = obj.Samples.FirstOrDefault()?.GetName() ?? obj.ValidCodes.FirstOrDefault();
            req.DisplayName = obj.Codes.Count > 1 ? $"{name} (any of {obj.Codes.Count})" : name;
            return req;
        }

        /// <summary>"Any of these will do: …" — the same note a vanilla errand with alternative
        /// hand-ins gets, so a row naming one quern does not read as a demand for that one.</summary>
        public string AlternativesNote(VsQuestObjective obj)
        {
            if (!ResolveObjective(obj) || obj.Codes.Count < 2) return null;

            var names = obj.Samples.Take(6).Select(s => s.GetName()).Where(n => n != null).ToList();
            if (names.Count == 0) return null;
            string more = obj.Codes.Count > names.Count ? $", and {obj.Codes.Count - names.Count} more" : "";
            return $"Any of these will do: {string.Join(", ", names)}{more}.";
        }

        // ---- the quest dialog (the only place active quests reach a client) ---------------

        /// <summary>The framework's quest window, matched by type name. Both dialog lists are
        /// checked: a dialog's presence in either is not something to bet a feature on (the
        /// handbook is missing from LoadedGuis until its first open).</summary>
        public GuiDialog FindQuestDialog()
        {
            try
            {
                foreach (var list in new[] { capi.Gui?.OpenedGuis, capi.Gui?.LoadedGuis })
                {
                    if (list == null) continue;
                    foreach (var dlg in list)
                    {
                        if (dlg?.GetType().FullName != QuestGuiTypeName) continue;
                        if (dlg.IsOpened()) return dlg;
                    }
                }
            }
            catch { /* a failed read is "no dialog", never an error at the caller */ }
            return null;
        }

        /// <summary>The one place this integration names the framework. Everything else is
        /// derived from its data; a GUI class has no data-side identity to derive from.</summary>
        public const string QuestGuiTypeName = "VsQuest.QuestSelectGui";

        /// <summary>
        /// What the open quest dialog is showing: the giver it belongs to and every quest the
        /// player has on with them, counters included. Null when no dialog is open or its shape
        /// has changed under us — a framework update must cost the integration, never the game.
        /// </summary>
        public (long GiverId, List<VsQuestActive> Active)? ReadQuestDialog()
        {
            var dlg = FindQuestDialog();
            if (dlg == null) return null;

            try
            {
                var type = dlg.GetType();
                var giverField = AccessTools.Field(type, "questGiverId");
                if (giverField == null) return null;
                long giverId = Convert.ToInt64(giverField.GetValue(dlg));

                // A MISSING field and an EMPTY list must not look alike. Empty is a real
                // answer — "you have no quests with this giver" — and callers treat it as
                // proof that anything they track for that giver is finished. A field that is
                // no longer there is not proof of anything, so it reads as no dialog at all.
                var activeField = AccessTools.Field(type, "activeQuests");
                if (activeField == null) return null;

                var list = new List<VsQuestActive>();
                if (activeField.GetValue(dlg) is System.Collections.IEnumerable actives)
                {
                    foreach (var active in actives)
                    {
                        var read = ReadActiveQuest(active, giverId);
                        if (read != null) list.Add(read);
                    }
                }
                return (giverId, list);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read the VS Quest dialog: {0}", e.Message);
                return null;
            }
        }

        static VsQuestActive ReadActiveQuest(object active, long fallbackGiverId)
        {
            if (active == null) return null;
            var type = active.GetType();

            string questId = AccessTools.Property(type, "questId")?.GetValue(active) as string;
            if (questId == null) return null;

            long giverId = fallbackGiverId;
            var own = AccessTools.Property(type, "questGiverId")?.GetValue(active);
            if (own != null) giverId = Convert.ToInt64(own);

            var result = new VsQuestActive { QuestId = questId, GiverId = giverId };

            // Kill, then block-place, then block-break: the framework's own concatenation
            // order, and the order the quest definition's objectives are read back against.
            foreach (var name in new[] { "killTrackers", "blockPlaceTrackers", "blockBreakTrackers" })
            {
                if (!(AccessTools.Property(type, name)?.GetValue(active) is System.Collections.IEnumerable trackers))
                    continue;
                foreach (var tracker in trackers)
                {
                    var count = tracker == null ? null
                        : AccessTools.Property(tracker.GetType(), "count")?.GetValue(tracker);
                    result.Trackers.Add(count == null ? 0 : Convert.ToInt32(count));
                }
            }
            return result;
        }

        // ---- the giver entity (the only login-time source) ------------------------------

        /// <summary>Does this entity hand out quests? The behavior name is the framework's own
        /// registration, so this is data rather than a mod check.</summary>
        public static bool IsQuestGiver(Entity e)
        {
            try { return e?.GetBehavior("questgiver") != null; }
            catch { return false; }
        }

        /// <summary>Every loaded quest giver. LoadedEntities, never GetEntitiesAround — the
        /// partition query has returned empty here while entities stood in plain sight, and a
        /// feature that silently never fires looks exactly like a broken one.</summary>
        public IEnumerable<Entity> LoadedGivers()
        {
            var entities = capi.World?.LoadedEntities;
            if (entities == null) yield break;

            foreach (var e in entities.Values)
            {
                if (e != null && e.Alive && IsQuestGiver(e)) yield return e;
            }
        }

        /// <summary>
        /// What this giver's synced attributes say about THIS player: for each quest, the day
        /// it was last accepted and whether it has been completed.
        ///
        /// Read by walking the attribute tree for our own uid rather than testing the catalogue
        /// per entity — the keys *are* the list of quests this giver has given this player, and
        /// the uid suffix is itself the proof the quest is perPlayer. A quest written without
        /// one is shared by everybody on the server, so an acceptance under it cannot be
        /// attributed to us and is deliberately not read at all.
        /// </summary>
        public Dictionary<string, (double AcceptedDay, bool Completed)> GiverState(Entity giver)
        {
            var state = new Dictionary<string, (double, bool)>(StringComparer.Ordinal);
            try
            {
                string uid = capi.World?.Player?.PlayerUID;
                if (giver?.WatchedAttributes == null || string.IsNullOrEmpty(uid)) return state;

                var completed = new HashSet<string>(
                    giver.WatchedAttributes.GetStringArray("playercompleted-" + uid) ?? new string[0],
                    StringComparer.Ordinal);

                string prefix = "lastaccepted-", suffix = "-" + uid;
                foreach (var pair in giver.WatchedAttributes)
                {
                    string key = pair.Key;
                    if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)
                        || !key.EndsWith(suffix, StringComparison.Ordinal)) continue;

                    string questId = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);
                    if (questId.Length == 0) continue;

                    state[questId] = (giver.WatchedAttributes.GetDouble(key, 0),
                                      completed.Contains(questId));
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read a quest giver's state: {0}", e.Message);
            }
            return state;
        }

        /// <summary>Givers already read this session, so the diagnostic can say whether a
        /// silent giver was ever looked at. Session-only — it proves nothing worth saving.</summary>
        readonly Dictionary<long, HashSet<string>> seenGivers = new Dictionary<long, HashSet<string>>();

        public void NoteSeen(long giverId, IEnumerable<string> questIds)
        {
            if (!seenGivers.TryGetValue(giverId, out var set))
                seenGivers[giverId] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in questIds) set.Add(id);
        }

        public IReadOnlyDictionary<long, HashSet<string>> SeenGivers => seenGivers;

        // ---- helpers ---------------------------------------------------------------------

        /// <summary>
        /// A translation, or null when there is none. Belt and braces on purpose: the API's own
        /// documentation disagrees with itself about whether a miss returns null or the key
        /// back, and a quest from a pack whose lang file this client does not have is exactly
        /// the case that hits it. Either way the caller gets null and falls back to the id,
        /// which is honest, rather than to a raw lang key, which reads like a bug.
        /// </summary>
        public static string Translated(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string value = Lang.GetIfExists(key);
            return string.IsNullOrWhiteSpace(value) || value == key ? null : value;
        }

        /// <summary>Quest text is the framework's rich text: "&lt;br&gt;" for lines, tags for
        /// emphasis. The list draws plain strings, so flatten rather than print markup.</summary>
        public static List<string> RichTextLines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return lines;

            foreach (var part in Regex.Split(text, "<br\\s*/?>", RegexOptions.IgnoreCase))
            {
                string line = Regex.Replace(part, "<[^>]+>", "").Trim();
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }

        string NameOfCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            try
            {
                var loc = new AssetLocation(code);
                var item = capi.World.GetItem(loc);
                if (item != null) return new ItemStack(item).GetName();
                var block = capi.World.GetBlock(loc);
                if (block != null) return new ItemStack(block).GetName();
            }
            catch { /* an unknown reward code names itself below */ }
            return code;
        }

        // ---- the framework's own JSON shape (only the parts we read) ---------------------
        // Populated by the deserializer, never by our code, hence the disable.
#pragma warning disable 0649
        class QuestJson
        {
            public string id;
            public int cooldown;
            public bool perPlayer;
            public string predecessor;
            public List<ObjJson> gatherObjectives;
            public List<ObjJson> killObjectives;
            public List<ObjJson> blockPlaceObjectives;
            public List<ObjJson> blockBreakObjectives;
            public List<ActionJson> actionObjectives;
            public List<RewardJson> itemRewards;
            public RandomRewardJson randomItemRewards;
        }
        class ObjJson { public List<string> validCodes; public int demand; }
        class ActionJson { public string id; public string[] args; }
        class RewardJson { public string itemCode; public int amount; }
        class RandomRewardJson { public int selectAmount; public List<RandomItemJson> items; }
        class RandomItemJson { public string itemCode; public int minAmount; public int maxAmount; }
#pragma warning restore 0649
    }
}
