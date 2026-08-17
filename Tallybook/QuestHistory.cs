using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;

namespace Tallybook
{
    /// <summary>
    /// A record of the quests you have finished, including ones finished long before this mod
    /// was installed.
    ///
    /// Quest progress lives in player variables — `beataqueststarted`, `…completed`,
    /// `…rewarded` — which are synced to this client, so what is *done* is knowable at any
    /// time. What is not knowable is *when*: a quest already finished the first time we look
    /// has no date and never will, and inventing one would be worse than saying so. Those are
    /// kept, marked undated, and shown last.
    ///
    /// Order for the undated ones comes from prerequisites rather than guesswork. A quest's
    /// opening is gated on variables other quests set, so the dialogue files describe a
    /// partial order — the archives really must come before what it unlocks — and counting how
    /// many quests must precede each one puts them in a sensible sequence.
    /// </summary>
    public class QuestHistory
    {
        readonly ICoreClientAPI capi;
        readonly PinStore store;
        readonly QuestScanner scanner;

        public QuestHistory(ICoreClientAPI capi, PinStore store, QuestScanner scanner)
        {
            this.capi = capi;
            this.store = store;
            this.scanner = scanner;
        }

        /// <summary>
        /// Bring the record up to date. Safe to call repeatedly: a quest already recorded is
        /// left alone, so nothing is ever double-counted or re-dated. Returns whether
        /// anything moved — a stage change is invisible to the store's own change event
        /// (variables flip server-side, not in our data), so the caller owns the redraw.
        /// </summary>
        public bool Update()
        {
            try
            {
                var chains = scanner.QuestChains();
                if (chains.Count == 0) return false;

                bool changed = false;
                bool firstEverLook = store.ChainStates.Count == 0;

                foreach (var chain in chains.Values)
                {
                    // Normalised before comparing: "" is what gets *stored* for a null stage,
                    // so comparing the raw null against it read as a change on every tick —
                    // one transiently unreadable variable meant rewriting the save file every
                    // second until it read back (found by Fable's review).
                    string stage = StageOf(chain) ?? "";
                    store.ChainStates.TryGetValue(chain.Key, out string previous);

                    if (stage != previous)
                    {
                        store.ChainStates[chain.Key] = stage;
                        changed = true;
                    }
                    if (stage != "completed" && stage != "rewarded") continue;

                    // Handed in but the reward is still uncollected is *not* finished — it is
                    // the "go and collect" state, which InProgress reports. Recording it here
                    // as well put the same quest in two sections at once, and since both rows
                    // carried the same identity, opening one opened the other.
                    bool rewardStep = scanner.StoryVariables().ContainsKey(chain.Key + "rewarded");
                    bool awaiting = stage == "completed" && rewardStep;

                    var existing = store.QuestHistory.FirstOrDefault(r => r.Chain == chain.Key);

                    if (awaiting)
                    {
                        // Clears the duplicate an earlier build may already have written; the
                        // record comes back for good once the reward is collected.
                        if (existing != null) { store.QuestHistory.Remove(existing); changed = true; }
                        continue;
                    }
                    if (existing != null)
                    {
                        // A quest can finish twice over: handed in, then rewarded later.
                        if (existing.Stage != "rewarded" && stage == "rewarded")
                        {
                            existing.Stage = stage;
                            changed = true;
                        }
                        // Records written before the archive kept the words, or before it kept
                        // them as a transcript. Copied — BriefingForChain memoizes, and a
                        // record must never share a list with the cache (the hand-in append
                        // below would write into it).
                        if (!QuestScanner.IsTranscript(existing.Text))
                        {
                            var said = scanner.BriefingForChain(chain.Key);
                            if (said.Count > 0) { existing.Text = said.ToList(); changed = true; }
                        }
                        // The rest of the conversation, appended as it happens — records
                        // written before it was recovered carry only the briefing, and "what
                        // did she say when I brought the goods" is the half worth rereading
                        // (Mark). The reward exchange joins only at stage rewarded: before
                        // the visit those words have not been spoken. Line-level containment
                        // keeps every append idempotent.
                        var extra = scanner.HandInForChain(chain.Key).ToList();
                        if (stage == "rewarded") extra.AddRange(scanner.RewardTranscriptForChain(chain.Key));

                        var have = existing.Text ?? new List<string>();
                        var missing = extra.Where(l => !have.Contains(l)).ToList();
                        if (missing.Count > 0)
                        {
                            existing.Text = have.Concat(missing).ToList();
                            changed = true;
                        }
                        continue;
                    }

                    // Dated only when we saw it move. Finished before we ever looked, or
                    // already done on our first ever pass, means we genuinely do not know.
                    bool watched = !firstEverLook && previous != null && previous != stage;

                    // The whole conversation as far as it has happened: the ask, the
                    // hand-over, and — only once the reward is actually collected — the
                    // payout. Copied out of the memo cache before anything is appended.
                    var text = scanner.BriefingForChain(chain.Key).ToList();
                    var rest = scanner.HandInForChain(chain.Key).ToList();
                    if (stage == "rewarded") rest.AddRange(scanner.RewardTranscriptForChain(chain.Key));
                    foreach (var said in rest)
                    {
                        if (!text.Contains(said)) text.Add(said);
                    }

                    store.QuestHistory.Add(new QuestRecord
                    {
                        Chain = chain.Key,
                        Name = chain.DisplayName,
                        Stage = stage,
                        Day = watched ? capi.World.Calendar?.TotalDays : null,
                        Depth = chain.Depth,
                        Text = text
                    });
                    changed = true;
                }

                if (UpdateMilestones()) changed = true;
                if (RepairErrandRecords()) changed = true;
                if (changed) store.Save();
                return changed;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest history update failed: {0}", e.Message);
                return false;
            }
        }

        /// <summary>
        /// Chains handed in but not yet paid out, with who to see. This is the "done — go
        /// and collect" state made actionable: the Side quests tab lists it and the glow
        /// marks the payer, because a quest that still owes the player a walk is not
        /// finished business. Giver can be null when no dialogue file claims the reward
        /// variable — the row still shows, it just cannot point anywhere.
        /// </summary>
        public List<(string Chain, string Name, string Giver)> AwaitingRewards()
        {
            var result = new List<(string, string, string)>();
            try
            {
                var vars = scanner.StoryVariables();
                foreach (var chain in scanner.QuestChains().Values)
                {
                    // "completed" from StageOf already means not rewarded — the rewarded
                    // flag would have won. No reward step defined means simply finished.
                    if (StageOf(chain) != "completed") continue;
                    if (!vars.ContainsKey(chain.Key + "rewarded")) continue;

                    result.Add((chain.Key, chain.DisplayName, scanner.RewardGiverForChain(chain.Key)));
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not list awaiting rewards: {0}", e.Message);
            }
            return result;
        }

        /// <summary>
        /// Notice tracked errands whose hand-over has already happened, archive them, and
        /// take their pins off the list.
        ///
        /// "Done" is read from the player's own state: handing goods over sets variables
        /// (`agnieszkaquestcompleted`, `gavelens`, Better Ruins' `gaveironpickaxe`), the
        /// catalogue knows which ones belong to each errand, and player-scope variables are
        /// synced — so completion is knowable at login, not just mid-conversation. Without
        /// this, a handed-in errand looked *less* finished afterwards: the goods left the
        /// inventory, so 8/8 fell back to 0/8 on a quest that was over.
        ///
        /// The pin is removed, not merely unchecked: a finished errand sitting greyed-out on
        /// the Side quests tab read as clutter the player had to sweep up themselves (Mark),
        /// and the History tab is where it now lives. Removal happens exactly once, when the
        /// completion is first noticed (the ChainStates guard): re-pinning afterwards is a
        /// player choice this must not fight every second. Errands whose completion variable
        /// is the `…questcompleted` of a vanilla chain get no record of their own — the chain
        /// pass above already records those properly, with stages.
        ///
        /// Entity-scope completion (vanilla traders keep quest state on the NPC) is only
        /// readable while standing with them — pass the NPC from the conversation watcher;
        /// with none, those errands read as still open rather than getting archived on a
        /// guess.
        /// </summary>
        public bool CheckErrandCompletion(Entity npc = null)
        {
            try
            {
                bool changed = false;
                foreach (var pin in store.Pins.ToList())
                {
                    if (pin.QuestGiver == null) continue;
                    // Another framework's errand is finished by that framework's own record,
                    // never by a dialogue variable. Without this, a villager and a quest giver
                    // who happen to share a name and want the same item would archive each
                    // other's errands — the identities are unrelated and must not be matched
                    // by name and code alone.
                    if (pin.VsQuestId != null) continue;

                    // The once-guard is ChainStates, not the record: chain-owned errands add
                    // no record of their own, and guarding on the record would redo those
                    // every second.
                    string key = $"errand:{pin.QuestGiver}:{pin.Code}";
                    if (store.ChainStates.TryGetValue(key, out string already))
                    {
                        // A pin over a recorded-done errand goes, checked or not. The old
                        // "a checked pin is the player's deliberate re-add" exception turned
                        // out to protect a bug, not a choice: the watcher re-offered every
                        // finished barter (its gates never close), minting checked pins
                        // nobody asked for that this guard then refused to touch — an
                        // immortal "√ 221/1" (found by Mark). The watcher no longer offers
                        // done errands, so nothing legitimate can sit here any more.
                        if (already == "done" && store.Remove(pin)) changed = true;
                        continue;
                    }

                    // The name-and-count lookup first; failing that, the NPC being talked to
                    // identifies the errand by the file they speak from — the recovery for
                    // pins whose captured giver ("Trader" vs the file's "Luxuries") or
                    // quantity drifted, which otherwise never archive at all (found via
                    // Mark's Forlorn Hope map purchase sitting complete forever).
                    var def = scanner.DefFor(pin.QuestGiver, pin.Code, pin.Count)
                              ?? scanner.DefForConversation(npc, pin.QuestGiver, pin.Code);
                    if (def == null || !scanner.DefIsDone(def, npc)) continue;

                    store.ChainStates[key] = "done";

                    bool chainOwned = def.Done.Any(d =>
                        d.variable != null && d.variable.EndsWith("questcompleted", StringComparison.OrdinalIgnoreCase));
                    if (!chainOwned)
                    {
                        // The archived words: what was said when the errand was set, then what
                        // was said when the goods changed hands — both recovered from the
                        // dialogue files, so neither depends on us having been listening.
                        var text = pin.QuestText?.ToList() ?? new List<string>();
                        foreach (var said in def.HandIn)
                        {
                            if (!text.Contains(said)) text.Add(said);
                        }
                        // The items are the subset, the quest is the thing: the summary line
                        // and what the hand-over gave back ("Received: Map to the Forlorn
                        // Hope Tower"), or the archive reads as gears vanishing for nothing
                        // (Mark).
                        string handed = HandedLine(def, pin.QuestGiver);
                        if (handed != null && !text.Contains(handed)) text.Insert(0, handed);
                        foreach (var got in def.Receives)
                        {
                            string line = $"Received: {got}.";
                            if (!text.Contains(line)) text.Add(line);
                        }

                        store.QuestHistory.Add(new QuestRecord
                        {
                            Chain = key,
                            // Named for what it was about when the data says — the map's
                            // destination ("Forlorn Hope Tower") — with the paid goods as
                            // the fallback identity it always had.
                            Name = def.Title ?? $"{pin.DisplayName} for {pin.QuestGiver}",
                            Stage = "completed",
                            Day = capi.World.Calendar?.TotalDays,
                            Text = text
                        });
                    }

                    // Off the list — the record above is where a finished errand lives now.
                    // Remove fires the marker cleanup and the recount through the normal
                    // paths, exactly as the Unpin button would.
                    store.Remove(pin);
                    changed = true;
                }
                if (changed) store.Save();
                return changed;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] errand completion check failed: {0}", e.Message);
                return false;
            }
        }

        /// <summary>"Handed in: 10 x Rusty gear to Trader." — the errand's items as the
        /// sub-line of its record. Null when the item cannot be named in this world.</summary>
        string HandedLine(QuestDef def, string giver)
        {
            try
            {
                var loc = new Vintagestory.API.Common.AssetLocation(def.ItemCode);
                var item = capi.World.GetItem(loc);
                var block = item == null ? capi.World.GetBlock(loc) : null;
                string name = item != null ? new Vintagestory.API.Common.ItemStack(item).GetName()
                    : block != null ? new Vintagestory.API.Common.ItemStack(block).GetName()
                    : null;
                if (name == null) return null;

                int qty = Math.Max(1, def.Quantity);
                return $"Handed in: {qty} x {name} to {giver}.";
            }
            catch { return null; }
        }

        /// <summary>
        /// Bring errand records written by earlier builds up to the current shape: named for
        /// what the quest was about (the bought map's destination) with the handed-in items
        /// as sub-lines. Runs on the tick, so every guard is an equality check and every
        /// append is idempotent — a record already in shape costs a few comparisons. Matching
        /// is by the chain key's own giver and item; an item with several catalogue entries
        /// only matches through its giver, never by guess.
        /// </summary>
        bool RepairErrandRecords()
        {
            bool changed = false;
            foreach (var rec in store.QuestHistory)
            {
                if (rec?.Chain == null || !rec.Chain.StartsWith("errand:")) continue;

                // errand:{giver}:{code} — the code itself may carry a domain colon.
                var parts = rec.Chain.Split(new[] { ':' }, 3);
                if (parts.Length < 3) continue;
                string giver = parts[1], code = parts[2];

                var candidates = scanner.QuestCatalogue().Where(d => d.ItemCode == code).ToList();
                var def = candidates.FirstOrDefault(d =>
                        string.Equals(d.NpcName, giver, StringComparison.OrdinalIgnoreCase))
                    ?? (candidates.Count == 1 ? candidates[0] : null);

                // Ambiguous by name (the giver was recorded from a live entity — "Trader" —
                // while defs carry file names), but the record already holds the words that
                // were said, and those identify the errand: the def whose recovered briefing
                // and hand-in lines are the ones archived here. Zero overlap stays unmatched
                // — a guess renames someone's history (this is why the Forlorn Hope record
                // did not regroup on the first pass: several errands want rusty gears, and
                // the unique-item rule refused them all).
                if (def == null && rec.Text != null && rec.Text.Count > 0)
                {
                    def = candidates
                        .Select(d => (Def: d,
                            Overlap: d.HandIn.Concat(d.Briefing).Count(l => rec.Text.Contains(l))))
                        .Where(x => x.Overlap > 0)
                        .OrderByDescending(x => x.Overlap)
                        .Select(x => x.Def)
                        .FirstOrDefault();
                }
                if (def == null) continue;

                if (def.Title != null && rec.Name != def.Title)
                {
                    rec.Name = def.Title;
                    changed = true;
                }

                var text = rec.Text ?? (rec.Text = new List<string>());
                string handed = HandedLine(def, giver);
                if (handed != null && !text.Contains(handed)) { text.Insert(0, handed); changed = true; }
                foreach (var got in def.Receives)
                {
                    string line = $"Received: {got}.";
                    if (!text.Contains(line)) { text.Add(line); changed = true; }
                }
            }
            return changed;
        }

        /// <summary>
        /// Everything else the story remembers: the elk you bought, the note you read, the
        /// archives you heard of, each villager you met. None of these is a quest chain, and
        /// an archive that ignored them would be empty for a player who has done plenty.
        /// </summary>
        bool UpdateMilestones()
        {
            var vars = scanner.StoryVariables();
            if (vars.Count == 0) return false;

            bool changed = false;
            bool firstEverLook = store.ChainStates.Count == 0;

            foreach (var v in vars.Values)
            {
                // Quest chains are recorded properly elsewhere, with their text and stages.
                if (v.Name.Contains("quest")) continue;

                string key = "var:" + v.Name;
                bool set = string.Equals(scanner.PlayerVariable(v.Name), "true", StringComparison.OrdinalIgnoreCase);

                store.ChainStates.TryGetValue(key, out string previous);
                if (set.ToString() != previous)
                {
                    store.ChainStates[key] = set.ToString();
                    changed = true;
                }
                if (!set) continue;
                if (store.QuestHistory.Any(r => r.Chain == key)) continue;

                bool watched = !firstEverLook && previous == bool.FalseString;

                store.QuestHistory.Add(new QuestRecord
                {
                    Chain = key,
                    Name = Humanise(v.Name),
                    Stage = "milestone",
                    Day = watched ? capi.World.Calendar?.TotalDays : null,
                    Depth = v.Depth
                });
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// "hasmetgerhardt" → "Met Gerhardt"; "boughtelk" → "Bought elk". The variables are
        /// written for the game, not for reading, so the common verb prefixes get unpacked
        /// and anything unrecognised is left alone rather than mangled.
        /// </summary>
        static string Humanise(string name)
        {
            var prefixes = new (string Prefix, string Reads)[]
            {
                ("hasmet", "Met "), ("heardof", "Heard of "), ("heard", "Heard about "),
                ("received", "Received "), ("bought", "Bought "), ("found", "Found "),
                ("gave", "Gave "), ("read", "Read "), ("saw", "Saw "), ("inspect", "Inspected "),
                ("triedbuy", "Tried to buy from "), ("triedsell", "Tried to sell to "),
            };

            foreach (var (prefix, reads) in prefixes)
            {
                if (!name.StartsWith(prefix) || name.Length <= prefix.Length) continue;
                string rest = name.Substring(prefix.Length);
                return reads + char.ToUpperInvariant(rest[0]) + rest.Substring(1);
            }
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Quests you are on but have not finished. Computed fresh rather than stored — this
        /// is live state, and a saved copy could only ever be out of date.
        ///
        /// Only chains you have actually *started* appear. Listing every quest you have yet
        /// to be offered would turn an archive of what you have done into a table of contents
        /// for what you have not, which is content the game is deliberately withholding.
        /// </summary>
        public List<QuestRecord> InProgress()
        {
            var open = new List<QuestRecord>();
            try
            {
                var vars = scanner.StoryVariables();

                foreach (var chain in scanner.QuestChains().Values)
                {
                    string stage = StageOf(chain);
                    if (stage == null || stage == "rewarded") continue;

                    // Handed in with no reward step defined is simply finished.
                    bool rewardStep = vars.ContainsKey(chain.Key + "rewarded");
                    if (stage == "completed" && !rewardStep) continue;

                    // Awaiting a reward means the goods already changed hands, so that
                    // exchange belongs in the readable text too. Concat copies — the memo
                    // cache must never be handed out in a list that grew.
                    var text = scanner.BriefingForChain(chain.Key);
                    if (stage == "completed")
                    {
                        var handIn = scanner.HandInForChain(chain.Key);
                        if (handIn.Count > 0)
                        {
                            text = text.Concat(handIn.Where(l => !text.Contains(l))).ToList();
                        }
                    }

                    open.Add(new QuestRecord
                    {
                        Chain = chain.Key,
                        Name = chain.DisplayName,
                        Stage = stage == "completed" ? "awaiting" : "open",
                        Depth = chain.Depth,
                        Text = text
                    });
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not list open quests: {0}", e.Message);
            }

            return open.OrderBy(r => r.Depth).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Finished quests: dated ones oldest first, then everything undated in
        /// prerequisite order — earliest in the story at the top.</summary>
        public List<QuestRecord> Records()
            => store.QuestHistory
                .OrderBy(r => r.Day.HasValue ? 0 : 1)
                .ThenBy(r => r.Day ?? 0)
                .ThenBy(r => r.Depth)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        string StageOf(QuestChain chain)
        {
            if (Flag(chain.Key + "rewarded")) return "rewarded";
            if (Flag(chain.Key + "completed")) return "completed";
            if (Flag(chain.Key + "started")) return "started";
            return null;
        }

        bool Flag(string variable)
        {
            var value = scanner.PlayerVariable(variable);
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
