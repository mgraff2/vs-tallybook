using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;

namespace Tallybook
{
    /// <summary>
    /// Notices when you take on a villager's errand and adds it to the list by itself — no
    /// button, no extra click: accept the quest the way the game intends and the items are
    /// already being tallied by the time the conversation closes.
    ///
    /// This deliberately replaced a Harmony patch that injected a link into the conversation.
    /// Nothing here touches the dialogue UI at all, which matters more than the saved code:
    /// villager conversation is story content, and a mod that can break a conversation can
    /// cost a player progress they cannot get back. Watching state the server already sends
    /// us carries none of that risk.
    ///
    /// How it knows: a fetch request is only *live* once its quest gates are true (started,
    /// not yet completed). So there is no need to detect the moment of acceptance — polling
    /// while a conversation is open is enough, because accepting is exactly what flips the
    /// gate, and the next poll sees a live request that was not live before.
    ///
    /// What it will not do is fight the player *during* a conversation. The gate stays true
    /// for as long as the quest is open, so without a guard, unpinning an errand mid-chat
    /// would see it reappear half a second later. That guard lasts exactly one conversation:
    /// walk away, come back, and the errand is offered again — re-accepting should re-add,
    /// and a permanent "you already declined this" memory would make an unpin unrecoverable
    /// (Mark). To set an errand aside for good, uncheck it instead: auto-tracking never
    /// re-activates a parked pin.
    /// </summary>
    public class QuestWatcher
    {
        const int IntervalMs = 500;

        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly TallyService svc;
        readonly QuestScanner scanner;
        readonly Action<QuestOffer> onAccepted;

        /// <summary>Set once the history exists. While conversing is the one moment
        /// entity-scope completion state (vanilla traders keep it on the NPC) can be read, so
        /// the watcher hands the NPC over for a completion sweep each poll.</summary>
        public QuestHistory History;

        /// <summary>Fired per poll with the NPC being talked to — conversation is the only
        /// moment their position is recorded (deliberately: no walk-past radar, Mark).</summary>
        public Action<Entity> OnConversing;

        long tickId;

        // Scoped to the conversation in progress, and dropped the moment it ends.
        readonly HashSet<string> offeredThisChat = new HashSet<string>();
        Entity talkingTo;

        public QuestWatcher(ICoreClientAPI capi, TallybookConfig config, TallyService svc,
                            QuestScanner scanner, Action<QuestOffer> onAccepted)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
            this.scanner = scanner;
            this.onAccepted = onAccepted;
            tickId = capi.Event.RegisterGameTickListener(OnTick, IntervalMs);
        }

        public void Dispose()
        {
            if (tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickId);
                tickId = 0;
            }
        }

        void OnTick(float dt)
        {
            try
            {
                if (!config.AutoTrackQuests) return;

                // Only while actually in conversation. Quests are accepted in dialogue, so
                // this is both the only moment worth checking and a natural bound on the work.
                var npc = scanner.FindTalkingNpc();
                if (npc == null || npc != talkingTo)
                {
                    talkingTo = npc;
                    offeredThisChat.Clear();
                    if (npc == null) return;
                }

                // While standing with them, their entity-scope state is readable — the only
                // moment a vanilla trader's held completion can be noticed at all — and for
                // a trader, the only moment their position is recorded.
                History?.CheckErrandCompletion(npc);
                OnConversing?.Invoke(npc);

                var offer = scanner.Scan(npc);
                if (offer == null) return;

                // Talking to them again is also the chance to fill in words we never captured
                // — an errand tracked before we kept them, or one whose dialogue file the
                // name-based lookup could not find.
                //
                // Text that predates the named-transcript format is replaced too, not just
                // missing text. The login backfill can only re-derive what it can find a
                // dialogue file for, which is villagers named after their file — traders are
                // exactly the ones it misses, so standing in front of one is the only chance
                // their old unattributed paragraphs ever get their speaker back.
                //
                // Per requirement, matched by item: an offer-wide rewrite stitched every
                // thread this NPC carries onto every one of their pins (found by Mark — the
                // gear pin retelling the pickaxe story).
                foreach (var req in offer.Requirements)
                {
                    if (req.Briefing.Count == 0) continue;
                    string reqCode = req.Stack?.Collectible?.Code?.ToShortString();
                    foreach (var pin in svc.Store.Pins)
                    {
                        if (pin.QuestGiver == offer.NpcName && pin.Code == reqCode
                            && !QuestScanner.IsTranscript(pin.QuestText))
                        {
                            pin.QuestText = req.Briefing.ToList();
                        }
                    }
                }

                var fresh = new QuestOffer { Npc = offer.Npc, NpcName = offer.NpcName, Pos = offer.Pos };
                foreach (var req in offer.Requirements)
                {
                    if (offeredThisChat.Add(OfferKey(offer.NpcName, req))) fresh.Requirements.Add(req);
                }
                if (fresh.Requirements.Count == 0) return;

                onAccepted(fresh);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest auto-tracking disabled after error: {0}", e.Message);
                config.AutoTrackQuests = false;
            }
        }

        /// <summary>Identity of a request already offered in this conversation. Includes the
        /// quantity so a villager asking for more of the same thing later still registers.</summary>
        public static string OfferKey(string giver, QuestRequirement req)
            => $"{giver}|{req.Stack?.Collectible?.Code}|{req.Quantity}";
    }
}
