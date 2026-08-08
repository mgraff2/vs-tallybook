using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

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
        /// left alone, so nothing is ever double-counted or re-dated.
        /// </summary>
        public void Update()
        {
            try
            {
                var chains = scanner.QuestChains();
                if (chains.Count == 0) return;

                bool changed = false;
                bool firstEverLook = store.ChainStates.Count == 0;

                foreach (var chain in chains.Values)
                {
                    string stage = StageOf(chain);
                    store.ChainStates.TryGetValue(chain.Key, out string previous);

                    if (stage != previous)
                    {
                        store.ChainStates[chain.Key] = stage ?? "";
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
                        // them as a transcript.
                        if (!QuestScanner.IsTranscript(existing.Text))
                        {
                            var said = scanner.BriefingForChain(chain.Key);
                            if (said.Count > 0) { existing.Text = said; changed = true; }
                        }
                        continue;
                    }

                    // Dated only when we saw it move. Finished before we ever looked, or
                    // already done on our first ever pass, means we genuinely do not know.
                    bool watched = !firstEverLook && previous != null && previous != stage;

                    store.QuestHistory.Add(new QuestRecord
                    {
                        Chain = chain.Key,
                        Name = chain.DisplayName,
                        Stage = stage,
                        Day = watched ? capi.World.Calendar?.TotalDays : null,
                        Depth = chain.Depth,
                        Text = scanner.BriefingForChain(chain.Key)
                    });
                    changed = true;
                }

                if (UpdateMilestones()) changed = true;
                if (changed) store.Save();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest history update failed: {0}", e.Message);
            }
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

                    open.Add(new QuestRecord
                    {
                        Chain = chain.Key,
                        Name = chain.DisplayName,
                        Stage = stage == "completed" ? "awaiting" : "open",
                        Depth = chain.Depth,
                        Text = scanner.BriefingForChain(chain.Key)
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
