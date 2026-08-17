using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Tallybook
{
    /// <summary>
    /// One VS Quest errand being tracked, and the evidence behind it. The pins are the
    /// shopping list; this holds what a pin cannot know — which quest and which giver it
    /// belongs to, and what the framework's own kill/block counters read the last time it
    /// told us anything.
    /// </summary>
    public class VsQuestTrack
    {
        public string QuestId;

        /// <summary>The giver's entity id — the framework's own identity, exact where a name
        /// is not. Zero for an errand the player asserted by hand before meeting anyone.</summary>
        public long GiverId;
        public string GiverName;

        /// <summary>The giver's `lastaccepted` value when this was adopted. Archived alongside
        /// the "done" mark so a later day can be recognised as the quest being taken on AGAIN
        /// — which is the only way a repeatable quest ever gets back onto the list. It is not
        /// what decides completion; see SeenIncomplete for that.</summary>
        public double AcceptedDay;
        public bool AcceptedDayKnown;

        /// <summary>The giver's own record has been seen saying this quest is OPEN. Load-bearing
        /// for repeatable quests: the completed mark is a set that only grows, so its value
        /// alone cannot separate "finished" from "finished once and taken again". Only the
        /// transition open → completed is a hand-in, and this is what makes that observable.</summary>
        public bool SeenIncomplete;

        /// <summary>Kill / block counters as of the last time the quest dialog was open — the
        /// only place they ever reach a client. Unknown is a real state and is not the same as
        /// zero: unknown blocks the ready glow, zero merely fails to satisfy it.</summary>
        public bool TrackersKnown;
        public bool TrackersMet;
        public string TrackerNote;

        /// <summary>In-game day this was picked up, for the archive.</summary>
        public double? Day;

        /// <summary>Ever confirmed by the framework's own quest dialog, as opposed to inferred
        /// from the giver's attributes. Reported by the diagnostic, so "why does it say that"
        /// has an answer that names its source.</summary>
        public bool SeenInDialog;

        public string Key => $"{QuestId}@{GiverId}";
    }

    /// <summary>
    /// Tracks quests from the VS Quest framework as ordinary errands: pins, HUD rows, map
    /// markers, history, and the ready glow, all reused.
    ///
    /// Two read paths, and the difference between them is the whole design:
    ///
    /// - <b>The quest dialog is authoritative.</b> It carries the player's active quests for
    ///   that giver, counters included, and its list being complete is what makes "no longer
    ///   listed" mean "finished". Polled while open — never patched, and never answered over
    ///   the framework's network channel, whose only two client messages both *write*.
    ///
    /// - <b>Proximity restores, and only restores.</b> A loaded giver's WatchedAttributes say
    ///   which quests this player accepted from it and which are done, which is the only way a
    ///   machine that has never seen this world can recover an errand already in flight. This
    ///   is a deliberate carve-out from "no passive radar" and the reasoning is narrow: for
    ///   vanilla villagers the state is player-scope and arrives at login anyway, so a scan
    ///   buys nothing; here there is no login path at all. Adoption needs an acceptance
    ///   recorded for *this* player, so it can only ever restore a quest already taken — a new
    ///   one still requires talking to the giver, and the catalogue is never used to offer.
    /// </summary>
    public class VsQuestWatcher
    {
        const int IntervalMs = 500;

        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly TallyService svc;
        readonly VsQuests vs;

        PinStore Store => svc.Store;

        /// <summary>Nothing is read or written before the world's pin file has loaded: every
        /// path here can add a pin, and adding one to a store that has not loaded yet is how
        /// an empty list gets saved over a real one.</summary>
        public bool Ready { get; set; }

        long tickId;
        int phase;

        /// <summary>The giver whose dialog was open on the previous poll. When it closes, that
        /// giver is still loaded and its attributes are the freshest statement of what just
        /// happened — which is where a hand-in is noticed.</summary>
        long lastDialogGiver;

        /// <summary>Quests already offered while THIS quest window has been open. Same guard,
        /// and the same reason, as the villager watcher's: the framework keeps listing a quest
        /// for as long as it is active, so without this, unpinning an errand while the window
        /// is open would see it back half a second later. Scoped to one window and never
        /// persisted — walk away, come back, and opening the window re-adds it, because that
        /// is something the player chose to do.</summary>
        readonly HashSet<string> offeredThisWindow = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Ever managed to read the dialog this session — a diagnostic answer to
        /// "why are the kill counts unknown".</summary>
        public bool DialogEverRead { get; private set; }

        /// <summary>Active quests this client has no definition for: a pack whose assets are
        /// server-side only. Session-only, and reported rather than guessed around.</summary>
        readonly HashSet<string> undefined = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Errands picked up this tick, announced once the store has settled. Nothing
        /// here was asked for by the player, so quietly editing their list would be the mod
        /// working behind their back — the same reason the villager watcher speaks up.</summary>
        readonly List<(string Title, string Giver, bool Restored)> announce
            = new List<(string, string, bool)>();

        public VsQuestWatcher(ICoreClientAPI capi, TallybookConfig config, TallyService svc, VsQuests vs)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
            this.vs = vs;
            tickId = capi.Event.RegisterGameTickListener(OnTick, IntervalMs);
        }

        /// <summary>Session state that belongs to one world: which givers have been read, what
        /// the dialog has shown, which quests this client has no definition for. Per-world
        /// separation is the design — a different server has different quest packs, and
        /// carrying "we never saw a definition for that" across would report the wrong world.</summary>
        public void NewWorld()
        {
            undefined.Clear();
            offeredThisWindow.Clear();
            lastDialogGiver = 0;
            DialogEverRead = false;
        }

        public void Dispose()
        {
            if (tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickId);
                tickId = 0;
            }
        }

        // ---- the poll --------------------------------------------------------------------

        void OnTick(float dt)
        {
            try
            {
                if (!Ready || !config.TrackVsQuests || !vs.Enabled) return;

                bool changed = ReadDialog();

                // The giver scan is the slower half — one attribute-tree walk per loaded
                // giver — and nothing it can see changes twice a second.
                if (++phase % 2 == 0) changed |= ScanGivers();

                if (changed)
                {
                    Store.Save();
                    svc.RecountAll();
                }
                Announce();
            }
            catch (Exception e)
            {
                // Same contract as the villager watcher: an integration that has started
                // throwing stops, rather than throwing once a tick forever.
                capi.Logger.Warning("[tallybook] VS Quest tracking disabled after error: {0}", e);
                config.TrackVsQuests = false;
            }
        }

        // ---- the quest dialog ------------------------------------------------------------

        bool ReadDialog()
        {
            var read = vs.ReadQuestDialog();
            if (read == null)
            {
                if (lastDialogGiver == 0) return false;

                // It just closed. Whatever the player did in there — accepted, handed in —
                // the giver standing in front of them now records it.
                long giver = lastDialogGiver;
                lastDialogGiver = 0;
                offeredThisWindow.Clear();
                return ProcessGiver(capi.World?.GetEntityById(giver));
            }

            DialogEverRead = true;
            bool changed = false;
            if (lastDialogGiver != read.Value.GiverId)
            {
                offeredThisWindow.Clear();
                // Read the giver's own record the moment the window opens, before the player
                // can do anything in it. That is what makes a hand-in visible as a transition
                // even when the quest is accepted and handed in inside a single window: without
                // it, a quest whose "open" state was never observed can only be closed out by
                // opening the window a second time.
                changed = ProcessGiver(capi.World?.GetEntityById(read.Value.GiverId));
            }
            lastDialogGiver = read.Value.GiverId;
            return ApplyDialog(read.Value.GiverId, read.Value.Active) || changed;
        }

        bool ApplyDialog(long giverId, List<VsQuestActive> active)
        {
            var giver = capi.World?.GetEntityById(giverId);
            string name = GiverName(giver);
            var pos = giver?.Pos?.XYZ;
            bool changed = false;

            vs.NoteSeen(giverId, active.Select(a => a.QuestId));

            foreach (var quest in active)
            {
                var def = vs.Def(quest.QuestId);
                if (def == null) { undefined.Add(quest.QuestId); continue; }

                // Adopt once per window; after that only the live half is refreshed. Calling
                // Adopt every poll would put back a pin the player had just unpinned with the
                // window still open.
                var track = offeredThisWindow.Add(def.Id)
                    ? Adopt(def, giverId, name, pos, restored: false, speak: true, changed: ref changed)
                    : Find(def.Id, giverId);
                if (track == null) continue;

                if (!track.SeenInDialog) { track.SeenInDialog = true; changed = true; }
                changed |= ApplyTrackers(def, track, quest);
                changed |= WriteText(def, track);
            }

            // The dialog's list is every quest this player has on with this giver, so anything
            // of theirs we track and it does not name is finished. This is the authoritative
            // completion path — it depends on no attribute sync at all.
            var activeIds = new HashSet<string>(active.Select(a => a.QuestId), StringComparer.Ordinal);
            foreach (var track in Store.VsQuests.ToList())
            {
                if (track.GiverId != giverId || activeIds.Contains(track.QuestId)) continue;
                Complete(track);
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Fold the framework's own counters into what we know. A counter already at its demand
        /// stays satisfied — counts only rise, and completing removes the quest — so this is
        /// safe to remember. One short of it is knowledge with a date on it, and the row says
        /// so rather than implying it is live.
        /// </summary>
        bool ApplyTrackers(VsQuestDef def, VsQuestTrack track, VsQuestActive quest)
        {
            if (def.TrackerCount == 0)
            {
                if (track.TrackersKnown && track.TrackersMet) return false;
                track.TrackersKnown = true;
                track.TrackersMet = true;
                track.TrackerNote = null;
                return true;
            }

            // A shape we do not recognise is not something to interpret: leave the counters
            // unknown, which blocks the glow and says "as of never" on the row.
            if (quest.Trackers.Count < def.TrackerCount) return false;

            bool met = true;
            var parts = new List<string>();
            int i = 0;
            foreach (var objective in def.Trackers)
            {
                int have = quest.Trackers[i++];
                if (have < objective.Demand) met = false;
                parts.Add($"{Math.Min(have, objective.Demand)}/{objective.Demand} {ObjectiveLabel(objective)}");
            }

            string note = string.Join(", ", parts);
            if (track.TrackersKnown && track.TrackersMet == met && track.TrackerNote == note) return false;

            track.TrackersKnown = true;
            track.TrackersMet = met;
            track.TrackerNote = note;
            return true;
        }

        // ---- the giver's own attributes ---------------------------------------------------

        bool ScanGivers()
        {
            bool changed = false;
            foreach (var giver in vs.LoadedGivers()) changed |= ProcessGiver(giver);
            return changed;
        }

        /// <summary>
        /// What one giver's synced state says about this player, applied. Adoption needs an
        /// acceptance recorded under our own uid — so this can restore an errand already taken
        /// and can never announce one that has not been offered.
        /// </summary>
        bool ProcessGiver(Entity giver)
        {
            if (giver == null) return false;

            var state = vs.GiverState(giver);
            if (state.Count == 0) return false;

            long giverId = giver.EntityId;
            string name = GiverName(giver);
            var pos = giver.Pos?.XYZ;
            bool changed = false;
            vs.NoteSeen(giverId, state.Keys);

            foreach (var pair in state)
            {
                string questId = pair.Key;
                double acceptedDay = pair.Value.AcceptedDay;
                bool completed = pair.Value.Completed;

                var track = Find(questId, giverId);
                if (track != null)
                {
                    if (!track.AcceptedDayKnown)
                    {
                        track.AcceptedDay = acceptedDay;
                        track.AcceptedDayKnown = true;
                        changed = true;
                    }

                    if (!completed)
                    {
                        // The giver's record says this quest is open. Remembering that is what
                        // makes the completed mark mean anything later: it is a set that only
                        // ever grows, so its VALUE says nothing, and only the TRANSITION from
                        // open to completed is the hand-in.
                        if (!track.SeenIncomplete) { track.SeenIncomplete = true; changed = true; }
                        changed |= LearnPlace(track, pos);
                        continue;
                    }

                    // Completed, but was already completed the first time we looked — which is
                    // what a repeatable quest taken again looks like, since finishing it once
                    // marks it forever. That tells us nothing about the run in flight, so this
                    // one can only be ended by the quest window's own list.
                    if (!track.SeenIncomplete) continue;

                    Complete(track);
                    changed = true;
                    continue;
                }

                // Completed, and not tracked. Usually that is simply history — but a repeatable
                // quest taken again is ALSO completed, and the difference is the day: every
                // acceptance raises lastaccepted, so a day later than the one we archived means
                // a new run and the errand belongs back on the list. With nothing archived to
                // compare against we cannot tell, and under-reporting is the safe direction:
                // the quest window will adopt it correctly if it really is live.
                if (completed && !RetakenSinceArchived(questId, giverId, acceptedDay)) continue;

                var def = vs.Def(questId);
                if (def == null) { undefined.Add(questId); continue; }
                if (def.Gather.Count == 0) continue;

                // Once ever per giver+quest, exactly like a village errand recovered at login:
                // this runs whenever the giver is loaded, and an unpinned errand returning
                // every time the player walks past would be its own bug. The key is consumed
                // only once there is something to adopt, so a catalogue that was not ready yet
                // costs a pass rather than the errand.
                if (!Store.OfferedQuests.Add("vsquest:" + questId + "@" + giverId)) continue;

                var adopted = Adopt(def, giverId, name, pos, restored: true, speak: true, changed: ref changed);
                if (adopted != null)
                {
                    if (!adopted.AcceptedDayKnown)
                    {
                        adopted.AcceptedDay = acceptedDay;
                        adopted.AcceptedDayKnown = true;
                    }
                    // Adopted while the giver says it is open — so the hand-in will be visible
                    // as a transition. A retaken repeat run does not get this, and is ended by
                    // the quest window instead.
                    if (!completed) adopted.SeenIncomplete = true;
                }
                changed = true;
            }
            return changed;
        }

        // ---- adoption --------------------------------------------------------------------

        VsQuestTrack Find(string questId, long giverId)
            => Store.VsQuests.FirstOrDefault(t => t.QuestId == questId && t.GiverId == giverId);

        void Announce()
        {
            foreach (var (title, giver, restored) in announce)
            {
                capi.ShowChatMessage(restored
                    ? $"Tallybook: picked up \"{title}\" — an errand you were already on for {giver}. Press L for your list."
                    : $"Tallybook: tracking \"{title}\" for {giver}. Press L for your list.");
            }
            announce.Clear();
        }

        /// <summary>
        /// Make (or find) the track and its pins. One pin per gather objective: a quest asking
        /// for two different things is two rows, and merging them would make either one's
        /// progress read as the other's.
        /// </summary>
        VsQuestTrack Adopt(VsQuestDef def, long giverId, string name, Vec3d pos,
                           bool restored, bool speak, ref bool changed)
        {
            if (def.Gather.Count == 0) return null;

            var track = Find(def.Id, giverId);
            if (track == null)
            {
                // An errand the player asserted by hand before meeting anyone gets its giver
                // now. Rebuilt rather than edited: the pin's key carries the giver, and a key
                // that changes under a live pin is how two rows for one errand happen.
                var unbound = Store.VsQuests.FirstOrDefault(t => t.QuestId == def.Id && t.GiverId == 0);
                if (unbound != null)
                {
                    RemovePins(unbound);
                    Store.VsQuests.Remove(unbound);
                }

                track = new VsQuestTrack
                {
                    QuestId = def.Id,
                    GiverId = giverId,
                    GiverName = name,
                    Day = capi.World?.Calendar?.TotalDays
                };
                Store.VsQuests.Add(track);
                if (speak) announce.Add((def.Title, name, restored));
                changed = true;
            }
            // The recorded name is NOT refreshed if the giver is renamed later. The pin's key
            // carries the giver, so writing a new name here would make the next Store.Add mint
            // a second row for the same errand beside the first. The entity id is the identity
            // that matters; the name is a label, and the label the pins already wear is the
            // one that keeps them one errand.

            for (int i = 0; i < def.Gather.Count; i++)
            {
                var objective = def.Gather[i];
                var stack = vs.SampleStack(objective);
                if (stack == null) continue;    // nothing in this world satisfies it; say nothing

                // activate:false is the adopted-errand contract — a pin the player parked is a
                // decision, and re-checking it every time they walk past would override them.
                // A brand-new pin is created checked, as always.
                var pin = Store.Add(stack, objective.Demand, setCount: true, activate: false,
                                    questGiver: track.GiverName, vsQuestId: def.Id, vsObjective: i);
                if (pin == null) continue;

                if (pin.VsQuestGiverId != giverId) { pin.VsQuestGiverId = giverId; changed = true; }
                if (pos != null && pin.QuestX == 0 && pin.QuestY == 0 && pin.QuestZ == 0)
                {
                    pin.QuestX = pos.X; pin.QuestY = pos.Y; pin.QuestZ = pos.Z;
                    changed = true;
                }
                svc.Resolve(pin);
            }

            changed |= WriteText(def, track);
            return track;
        }

        /// <summary>Position learned later — a track adopted from a dialog whose giver was not
        /// resolvable, or a hand-asserted one — reaches the pins that put a Map button on it.</summary>
        bool LearnPlace(VsQuestTrack track, Vec3d pos)
        {
            if (pos == null) return false;

            bool changed = false;
            foreach (var pin in PinsOf(track))
            {
                if (pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0) continue;
                pin.QuestX = pos.X; pin.QuestY = pos.Y; pin.QuestZ = pos.Z;
                changed = true;
            }
            // Deliberately not renaming — see Adopt: the pins are keyed on the giver's name,
            // and rewriting it here would split one errand into two rows.
            return changed;
        }

        /// <summary>
        /// The errand's readable half, rebuilt from the catalogue every time so the counter
        /// line stays current. All of it is derived — the framework's own title, description
        /// and reward list — so it survives a save that never captured a word of it.
        /// </summary>
        bool WriteText(VsQuestDef def, VsQuestTrack track)
        {
            bool changed = false;
            for (int i = 0; i < def.Gather.Count; i++)
            {
                var pin = PinFor(track, i);
                if (pin == null) continue;

                var text = new List<string> { def.Title };
                text.AddRange(def.Description);

                string alternatives = vs.AlternativesNote(def.Gather[i]);
                if (alternatives != null) text.Add(alternatives);

                if (track.TrackerNote != null)
                {
                    text.Add(track.TrackersKnown
                        ? $"Also: {track.TrackerNote} — as of your last talk with them."
                        : $"Also: {track.TrackerNote}.");
                }
                else if (def.TrackerCount > 0 && !track.TrackersKnown)
                {
                    text.Add("This quest also counts kills or blocks; open it with the giver "
                             + "to see how far along those are.");
                }
                if (def.ActionObjectives > 0)
                    text.Add("This quest has a condition Tallybook cannot check — the giver's own window is the judge.");
                if (def.Rewards.Count > 0)
                    text.Add("Reward: " + string.Join(", ", def.Rewards) + ".");

                if (pin.QuestText == null || !pin.QuestText.SequenceEqual(text))
                {
                    pin.QuestText = text;
                    changed = true;
                }
            }
            return changed;
        }

        // ---- completion -------------------------------------------------------------------

        /// <summary>
        /// Archive and clear. The record goes under a key of its own so the vanilla history
        /// sweep never touches it, and the pins go with it — a finished errand lives on the
        /// History tab, not greyed out on the list for the player to sweep up.
        /// </summary>
        void Complete(VsQuestTrack track)
        {
            var def = vs.Def(track.QuestId);
            string key = $"vsquest:{track.GiverId}:{track.QuestId}";
            bool firstTime = !Store.ChainStates.ContainsKey(key);

            // The day it was accepted travels with the "done" mark, because that is what makes
            // a repeatable quest work: taken again, the giver's own lastaccepted reads LATER
            // than this, which is the only way to tell a new run from the one just archived.
            Store.ChainStates[key] = "done@" + (track.AcceptedDayKnown
                ? track.AcceptedDay.ToString("0.####", CultureInfo.InvariantCulture)
                : "?");

            // And the once-ever adoption guard is released with it. That guard exists to stop
            // an *unpinned* errand coming back every time the player walks past; a quest that
            // has been finished and taken again is a different errand, not that one returning.
            Store.OfferedQuests.Remove("vsquest:" + track.QuestId + "@" + track.GiverId);

            if (firstTime)
            {
                var text = new List<string>();
                if (def != null)
                {
                    text.AddRange(def.Description);
                    for (int i = 0; i < def.Gather.Count; i++)
                    {
                        var pin = PinFor(track, i);
                        string name = pin?.Stack?.GetName()
                                      ?? vs.SampleStack(def.Gather[i])?.GetName();
                        if (name != null)
                            text.Add($"Handed in: {def.Gather[i].Demand} x {name} to {track.GiverName}.");
                    }
                    foreach (var reward in def.Rewards) text.Add($"Received: {reward}.");
                }

                Store.QuestHistory.Add(new QuestRecord
                {
                    Chain = key,
                    Name = def?.Title ?? track.QuestId,
                    Stage = "completed",
                    Day = capi.World?.Calendar?.TotalDays,
                    Text = text
                });
            }

            RemovePins(track);
            Store.VsQuests.Remove(track);
        }

        /// <summary>
        /// Has this giver's completed quest been taken on again since we archived it? The
        /// archived mark carries the day it had been accepted; the giver's own record now says
        /// a later one only if it has been accepted since. An archive with no day ("?" — the
        /// quest was never seen accepted) answers no: a guess in this direction would put a
        /// finished quest back on the list every time the player walked past its giver.
        /// </summary>
        bool RetakenSinceArchived(string questId, long giverId, double acceptedDay)
        {
            if (!Store.ChainStates.TryGetValue($"vsquest:{giverId}:{questId}", out string mark)
                || mark == null) return false;

            int at = mark.IndexOf('@');
            if (at < 0) return false;

            return double.TryParse(mark.Substring(at + 1), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out double archived)
                   && acceptedDay > archived + 0.0001;
        }

        void RemovePins(VsQuestTrack track)
        {
            foreach (var pin in PinsOf(track).ToList()) Store.Remove(pin);
        }

        List<Pin> PinsOf(VsQuestTrack track)
            => Store.Pins.Where(p => p.VsQuestId == track.QuestId && p.VsQuestGiverId == track.GiverId).ToList();

        Pin PinFor(VsQuestTrack track, int objective)
            => Store.Pins.FirstOrDefault(p => p.VsQuestId == track.QuestId
                                              && p.VsQuestGiverId == track.GiverId
                                              && p.VsQuestObjective == objective);

        // ---- the ready glow ----------------------------------------------------------------

        /// <summary>
        /// Givers who are ready for you, by entity id. Every gather objective carried AND every
        /// other objective verifiably satisfied — a counter last seen short is *unknown*, not
        /// unmet, and an action objective cannot be judged here at all. Both block the glow:
        /// under-glowing costs a player nothing, since the framework's own Complete button is
        /// right there, while over-glowing sends them across a village for a refusal.
        /// </summary>
        public HashSet<long> ReadyGiverIds()
        {
            var ids = new HashSet<long>();
            try
            {
                foreach (var track in Store.VsQuests)
                {
                    if (track.GiverId == 0) continue;

                    var def = vs.Def(track.QuestId);
                    if (def == null || def.ActionObjectives > 0) continue;
                    if (def.TrackerCount > 0 && !(track.TrackersKnown && track.TrackersMet)) continue;

                    var pins = PinsOf(track);
                    if (pins.Count == 0 || pins.Any(p => !p.Active || !p.Complete)) continue;

                    ids.Add(track.GiverId);
                }
            }
            catch { /* cosmetic: a failed read is "nobody is ready" */ }
            return ids;
        }

        // ---- the manual assertion ------------------------------------------------------------

        /// <summary>
        /// "I am on this one" — for the case proximity cannot serve: a quest accepted on
        /// another machine, with the giver a continent away. The player asserts it, the
        /// catalogue supplies the rest, and the first real read of that giver binds it
        /// properly. Same spirit as `.tallybook here`, and the same reason it is a command
        /// rather than a list: offering from the catalogue would spoil quests the game is
        /// withholding.
        /// </summary>
        public string Track(string questId)
        {
            if (string.IsNullOrWhiteSpace(questId))
                return "Say which quest: .tallybook vsquest track <quest id>";
            // Same rule as everything else that can add a pin: nothing before the world's list
            // has loaded, or the load that follows throws the addition away.
            if (!Ready) return "Tallybook has not finished loading this world's list yet.";

            questId = questId.Trim();
            var def = vs.Def(questId);
            if (def == null)
                return $"No quest with the id \"{questId}\" in this world's quest files.";
            if (def.Gather.Count == 0)
                return $"\"{def.Title}\" asks for nothing to be gathered, so there is nothing to put on the list.";
            if (Store.VsQuests.Any(t => t.QuestId == questId))
                return $"\"{def.Title}\" is already on your list.";

            bool changed = false;
            var track = Adopt(def, 0, "Unknown giver", null,
                              restored: false, speak: false, changed: ref changed);
            if (track == null) return "That quest could not be read.";

            Store.Save();
            svc.RecountAll();
            return $"Tracking \"{def.Title}\". Its giver fills in the first time you are near them.";
        }

        // ---- diagnostics ----------------------------------------------------------------------

        /// <summary>Every layer side by side, the way `.tallybook spawn` reports the spawn
        /// tracker: what the files hold, what is tracked and on what evidence, and what each
        /// nearby giver's own attributes say. Run this before theorising about which layer is
        /// lying.</summary>
        public List<string> Report()
        {
            var lines = new List<string>();
            try
            {
                var catalogue = vs.Catalogue();
                lines.Add($"catalogue: {catalogue.Count} quest(s) in this world's quest files"
                          + (vs.Enabled ? "" : " — no VS Quest content here, so nothing is tracked"));
                lines.Add("option: " + (config.TrackVsQuests ? "on" : "OFF (TrackVsQuests in the config file)")
                          + (Ready ? "" : " — not ready yet (the list has not loaded)"));
                lines.Add("quest dialog: " + (DialogEverRead
                    ? "read at least once this session"
                    : "never read this session — kill and block counters stay unknown until it is"));

                if (Store.VsQuests.Count == 0) lines.Add("tracked: none");
                foreach (var track in Store.VsQuests)
                {
                    var def = vs.Def(track.QuestId);
                    lines.Add($"tracked: \"{def?.Title ?? track.QuestId}\" from {track.GiverName}"
                              + (track.GiverId == 0 ? " (giver not yet met)" : $" (#{track.GiverId})")
                              + $" — {(track.SeenInDialog ? "confirmed in the quest window" : "restored from the giver's own state")}"
                              + (track.AcceptedDayKnown
                                  ? $", accepted day {track.AcceptedDay.ToString("0.#", CultureInfo.InvariantCulture)}"
                                  : ", accepted day unknown"));

                    foreach (var pin in PinsOf(track))
                        lines.Add($"    {pin.DisplayName}: {pin.Have}/{pin.Count}{(pin.Active ? "" : " (parked)")}");

                    if (!track.SeenIncomplete)
                        lines.Add("    the giver already had this marked completed when we "
                                  + "first looked (a repeat run) — only the quest window can end it");
                    if (def != null && def.TrackerCount > 0)
                        lines.Add("    other objectives: " + (track.TrackersKnown
                            ? track.TrackerNote + (track.TrackersMet ? " — met" : " — not met, as of your last talk")
                            : "not known yet"));
                    if (def != null && def.ActionObjectives > 0)
                        lines.Add("    has a condition only the framework can judge — never claims ready");
                }

                foreach (var id in undefined)
                    lines.Add($"active but undefined here: {id} — its quest pack is not installed on this client");

                var eye = capi.World?.Player?.Entity?.Pos?.XYZ;
                int givers = 0;
                foreach (var giver in vs.LoadedGivers())
                {
                    givers++;
                    var state = vs.GiverState(giver);
                    double away = eye == null ? 0 : Math.Sqrt(giver.Pos.XYZ.SquareDistanceTo(eye));
                    lines.Add($"nearby giver: \"{GiverName(giver)}\" (#{giver.EntityId}) {away:0}m — "
                              + (state.Count == 0
                                  ? "nothing recorded for you"
                                  : string.Join(", ", state.Select(kv =>
                                      $"{kv.Key} {(kv.Value.Completed ? "completed" : "accepted")}"))));
                }
                if (givers == 0) lines.Add("nearby giver: none loaded");
            }
            catch (Exception e)
            {
                lines.Add("report failed: " + e.Message);
            }
            return lines;
        }

        // ---- helpers ------------------------------------------------------------------------

        /// <summary>Never blank: this name ends up as a map marker's title, and an untitled
        /// waypoint crashes the client the moment it is hovered.</summary>
        string GiverName(Entity giver)
        {
            string name = null;
            try { name = giver?.GetName(); } catch { }
            return string.IsNullOrWhiteSpace(name) ? "Quest giver" : name.Trim();
        }

        /// <summary>What a kill or block objective is called. Creature codes are entity codes,
        /// not item codes, so the game's own creature lang key is tried first; a code we cannot
        /// name prints as itself rather than as a guess.</summary>
        string ObjectiveLabel(VsQuestObjective objective)
        {
            string code = objective.ValidCodes.FirstOrDefault() ?? "?";
            string bare = code.EndsWith("*", StringComparison.Ordinal)
                ? code.Substring(0, code.Length - 1).TrimEnd('-')
                : code;

            string creature = VsQuests.Translated("item-creature-" + bare);
            if (creature != null) return creature;

            try
            {
                var loc = new AssetLocation(bare);
                var block = capi.World.GetBlock(loc);
                if (block != null) return new ItemStack(block).GetName();
                var item = capi.World.GetItem(loc);
                if (item != null) return new ItemStack(item).GetName();
            }
            catch { /* names itself below */ }
            return bare;
        }
    }
}
