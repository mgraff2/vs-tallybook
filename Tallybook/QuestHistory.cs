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

                    var existing = store.QuestHistory.FirstOrDefault(r => r.Chain == chain.Key);
                    if (existing != null)
                    {
                        // A quest can finish twice over: handed in, then rewarded later.
                        if (existing.Stage != "rewarded" && stage == "rewarded")
                        {
                            existing.Stage = stage;
                            changed = true;
                        }
                        // Records written before the archive kept the words.
                        if (existing.Text == null || existing.Text.Count == 0)
                        {
                            existing.Text = scanner.BriefingForChain(chain.Key);
                            if (existing.Text.Count > 0) changed = true;
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

                if (changed) store.Save();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest history update failed: {0}", e.Message);
            }
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
