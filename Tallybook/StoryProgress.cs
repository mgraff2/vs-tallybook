using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>
    /// Steps the player through the vanilla story, one step at a time, without ever running
    /// ahead of the game. The chain itself — trader map → treasure hunter → archives →
    /// lazaret → village → devastation → Tobias — is authored from the file-verified order in
    /// docs/story-progression-1.22.md; nothing here is guessed from prose.
    ///
    /// The spoiler rule is structural, not editorial: every step carries two gates.
    /// *Reveal* means the game has already told the player this much (a variable their own
    /// play set, an item in their hands, a waypoint their own map-reading created), and a
    /// step is never shown before its reveal gate AND its predecessor's completion. *Done*
    /// means the step provably happened. Only the step between those two states is ever
    /// displayed, so the surface can not become a table of contents for unplayed content —
    /// the exact failure the quest catalogue rules already guard against.
    ///
    /// Progress is persisted monotonically (SaveFile.StoryStates) and only ever moves
    /// forward. That matters because half the signals are transient: locator maps get handed
    /// over or lost, the lens leaves the inventory at the hand-over, waypoint reads fail
    /// intermittently (the fifty-marker incident). A signal observed once is a fact recorded
    /// forever; a signal that cannot currently be read is "don't know", never "undone".
    /// A player who installs mid-story is caught up by downward closure: any later step's
    /// completion marks every earlier step done, silently.
    /// </summary>
    public class StoryProgress
    {
        public class Step
        {
            public string Key;
            public string Title;
            public string Text;

            /// <summary>Extra hint shown under the text once its own signal fires — the
            /// "you heard part of the telling, hear the rest" nudges. Never reveals anything
            /// the gating signal doesn't prove the player already met.</summary>
            public Func<string> Detail;

            /// <summary>The game has told the player this much. Null = revealed as soon as
            /// the previous step completes (used where completing the predecessor IS the
            /// telling — e.g. the map in your hand names the destination).</summary>
            public Func<bool> Reveal;

            public Func<bool> Done;

            /// <summary>Items worth counting for this step, auto-pinned once at reveal.</summary>
            public StepPin[] Pins = Array.Empty<StepPin>();
        }

        public class StepPin
        {
            public string Code;
            public bool IsBlock;
            public int Quantity;

            /// <summary>Park the pin when THIS step completes — usually the owning step, but
            /// the lens is handed over one step later than it is obtained, and parking it
            /// while it still reads 1/1 in the bag would be premature.</summary>
            public string ParkOnStep;
        }

        readonly ICoreClientAPI capi;
        readonly TallyService svc;
        readonly QuestScanner scanner;
        readonly QuestWaypoints waypoints;

        readonly List<Step> steps;
        bool? enabled;
        bool announcedThisSession;

        public StoryProgress(ICoreClientAPI capi, TallyService svc, QuestScanner scanner,
                             QuestWaypoints waypoints)
        {
            this.capi = capi;
            this.svc = svc;
            this.scanner = scanner;
            this.waypoints = waypoints;
            steps = BuildSteps();
        }

        // ---- the chain (docs/story-progression-1.22.md) --------------------------------

        /// <summary>
        /// Waypoint titles (as lang keys) the story steps below already watch. The site-quest
        /// adopter skips these: the walk to the Devastation belongs to the story block, and a
        /// second row saying "visit the Devastation" would be the same fact rendered twice.
        /// Authored from the same file-verified list as the steps — keep the two in step.
        /// </summary>
        public static readonly string[] StoryLocationLangKeys =
        {
            "location-treasurehunter", "location-resonancearchives", "location-lazaret",
            "location-village", "location-cavetobias", "location-devastationarea",
        };

        List<Step> BuildSteps()
        {
            return new List<Step>
            {
                new Step
                {
                    Key = "trader",
                    Title = "Rumours worth a few gears",
                    Text = "Wandering traders know this region better than anyone. One of them "
                         + "can point you toward a treasure hunter — for a small price in "
                         + "rusty gears.",
                    // The trader's own menu shows this option to anyone who talks to one, so
                    // showing it from the start reveals nothing the first conversation
                    // wouldn't. This is the step that gets a fresh player started.
                    Reveal = () => true,
                    Done = () => Var("hasmet") || Item("locatormap-treasurehunter")
                              || Waypoint("location-treasurehunter"),
                    Pins = new[] { new StepPin { Code = "gear-rusty", Quantity = 4, ParkOnStep = "trader" } }
                },
                new Step
                {
                    Key = "treasurehunter",
                    Title = "The treasure hunter's price",
                    Text = "Find the treasure hunter's camp and hear what they know. They "
                         + "trade in dangerous places, and knowledge like that is not paid "
                         + "for in gears.",
                    // Only once the request has actually been voiced — meeting the hunter
                    // without asking about the ruin has not earned the price yet.
                    Detail = () => Obs("requestbronze")
                        ? "They want a tin-bronze pickaxe in exchange for a map to a special ruin."
                        : null,
                    Done = () => Item("locatormap-resonancearchive") || Obs("bronzereceived")
                              || Waypoint("location-resonancearchives") || Var("heardalchemist"),
                },
                new Step
                {
                    Key = "archives",
                    Title = "Down into the archive",
                    Text = "The map leads to a ruin buried deep underground. \"Good luck down "
                         + "there, you're gonna need it.\" Something below is still worth "
                         + "listening to.",
                    Done = () => Var("heardalchemistlocations"),
                    Detail = () => Var("heardalchemist") && !Var("heardalchemistlocations")
                        ? "You have heard part of the telling. Stay for all of it — the places "
                        + "it names matter."
                        : null,
                },
                new Step
                {
                    Key = "lazaretletter",
                    Title = "Names on a map",
                    Text = "The voice in the archive named places. The treasure hunter has "
                         + "spent years in this region — ask them about the names you heard.",
                    Done = () => Item("letter-lazaret") || Obs("heardlazaret")
                              || Waypoint("location-lazaret"),
                },
                new Step
                {
                    Key = "lazaret",
                    Title = "The long road to the Lazaret",
                    Text = "The letter carries directions to the Lazaret — a long journey. "
                         + "\"Make sure you're prepared!\" Search the place when you arrive; "
                         + "not everyone who went there left.",
                    Detail = () =>
                    {
                        if (Var("inspectskeleton") && !Var("foundmissive"))
                            return "The skeleton you found points to more — look for the severed arm.";
                        if (Obs("offeredelk") && !Var("boughtelk"))
                            return "The treasure hunter offered to sell you a tamed elk for the "
                                 + "trip — bring a saddle, a bridle, and a heavy purse of gears.";
                        return null;
                    },
                    Done = () => Var("foundmissive") || Item("letter-faded")
                              || Waypoint("location-village"),
                },
                new Step
                {
                    Key = "village",
                    Title = "A letter looking for its reader",
                    Text = "The faded letter you found is itself directions — to a village. "
                         + "Follow it there, and show the letter to the people who live "
                         + "there. Someone will know the hand it was written in.",
                    Detail = () => Var("heardoftobias") && !Var("sawletteragnieszka")
                        ? "The villagers speak of a Saint Tobias. Someone here receives "
                        + "letters from outside — find them."
                        : null,
                    Done = () => Var("sawletteragnieszka"),
                },
                new Step
                {
                    Key = "cavemap",
                    Title = "The old man in the hills",
                    Text = "Agnieszka knows who wrote your letter. Ask her about the old man "
                         + "— and ask her for the map to his cave.",
                    Done = () => Var("readnote") || Var("gavemapagnieszka")
                              || Item("locatormap-cavetobias") || Waypoint("location-cavetobias"),
                },
                new Step
                {
                    Key = "lens",
                    Title = "The tower in the Devastation",
                    Text = "The note with Agnieszka's map is blunt: \"Before you come to meet "
                         + "me, you must make your way to the Devastation. There is a tower "
                         + "there. You must climb it and take The Lens that rests upon its "
                         + "peak.\" Gerhardt has ranged that far — he can give you a map.",
                    Detail = () => Var("gavemapgerhardt") || Item("locatormap-devastationarea")
                                || Waypoint("location-devastationarea")
                        ? "You have the map to the Devastation. \"Be prepared. Watch the "
                        + "skies. Expect death.\""
                        : null,
                    Done = () => Item("jonaslens-north") || Var("gavelens"),
                    Pins = new[] { new StepPin { Code = "jonaslens-north", IsBlock = true, Quantity = 1, ParkOnStep = "givelens" } }
                },
                new Step
                {
                    Key = "givelens",
                    Title = "Carry The Lens to its keeper",
                    Text = "You hold The Lens. Take it to the cave marked on Agnieszka's map "
                         + "— the old man has been waiting a long time for it.",
                    Done = () => Var("gavelens"),
                },
                new Step
                {
                    Key = "efforts",
                    Title = "What Tobias has been building",
                    Text = "Tobias has more than answers — ask him about his efforts. What he "
                         + "has been working on in that cave is meant for your hands.",
                    Done = () => Var("receivedtranslocator"),
                },
            };
        }

        // ---- signals -------------------------------------------------------------------

        bool Var(string name)
            => string.Equals(scanner.PlayerVariable(name), "true", StringComparison.OrdinalIgnoreCase);

        bool Item(string shortCode)
        {
            try
            {
                foreach (var inv in svc.Probe.CarriedInventories())
                {
                    foreach (var slot in inv)
                    {
                        if (slot?.Itemstack?.Collectible?.Code?.ToShortString() == shortCode)
                            return true;
                    }
                }
            }
            catch { /* an inventory mid-change reads as "not seen", never as a crash */ }
            return false;
        }

        /// <summary>True only on a successful read that finds the title; a failed or empty
        /// read is "don't know" and moves nothing (the waypoint list is KNOWN to read back
        /// empty at random — nothing may act on its absence).</summary>
        bool Waypoint(string langKey)
        {
            string title = Lang.Get(langKey);
            if (string.IsNullOrEmpty(title) || title == langKey) return false;
            return waypoints?.HasWaypointTitled(title) == true;
        }

        bool Obs(string name) => States.TryGetValue("obs:" + name, out var v) && v == "true";

        Dictionary<string, string> States => svc.Store.StoryStates;

        /// <summary>
        /// Entity-scope quest state is only readable while standing with the NPC, so the
        /// conversation poll hands every conversation partner through here and anything true
        /// is recorded for good. The treasure hunter keeps half the early story on himself.
        /// </summary>
        public void ObserveConversation(Entity npc)
        {
            if (npc == null || !Enabled) return;

            string code = npc.Code?.Path ?? "";
            if (!code.Contains("treasurehunter")) return;

            bool changed = false;
            foreach (var name in new[] { "requestbronze", "bronzereceived", "heardlazaret", "offeredelk" })
            {
                if (Obs(name)) continue;
                if (!string.Equals(scanner.EntityVariable(name, npc), "true", StringComparison.OrdinalIgnoreCase)) continue;
                States["obs:" + name] = "true";
                changed = true;
            }
            if (changed) svc.Store.Save();
        }

        // ---- evaluation ----------------------------------------------------------------

        /// <summary>Story content can be absent (a server that strips the story assets); with
        /// no files there is no story to step through and every surface stays silent.</summary>
        public bool Enabled
        {
            get
            {
                if (enabled != null) return enabled.Value;
                try
                {
                    var locs = capi.Assets.GetLocations("config/dialogue/");
                    enabled = locs != null
                        && locs.Any(l => l.Path.EndsWith("tobias.json"))
                        && locs.Any(l => l.Path.EndsWith("treasurehunter.json"));
                }
                catch { enabled = false; }
                return enabled.Value;
            }
        }

        /// <summary>Worlds change per session; the asset answer does not change within one.</summary>
        public void InvalidateWorld()
        {
            enabled = null;
            announcedThisSession = false;
        }

        bool IsDone(Step s) => States.TryGetValue("done:" + s.Key, out var v) && v == "true";
        bool IsRevealed(Step s) => IsDone(s) || (States.TryGetValue("seen:" + s.Key, out var v) && v == "true");

        /// <summary>The step the player is on: first not done. Null before anything is
        /// revealed and after everything is finished.</summary>
        public Step Current
        {
            get
            {
                if (!Enabled) return null;
                var step = steps.FirstOrDefault(s => !IsDone(s));
                return step != null && IsRevealed(step) ? step : null;
            }
        }

        public bool AllDone => Enabled && steps.All(IsDone);
        public bool AnyRevealed => Enabled && steps.Any(IsRevealed);

        /// <summary>
        /// One pass: record every done signal that fires, close the chain downward (a later
        /// step done proves every earlier one), then reveal the current step if the game has
        /// earned it. Cheap when nothing changes — a handful of variable reads.
        /// </summary>
        public void Poll()
        {
            if (!Enabled) return;

            try
            {
                bool changed = false;

                // Done signals, highest first, then downward closure.
                int highestDone = -1;
                for (int i = steps.Count - 1; i >= 0; i--)
                {
                    if (IsDone(steps[i])) { highestDone = Math.Max(highestDone, i); continue; }
                    bool done;
                    try { done = steps[i].Done?.Invoke() == true; }
                    catch { done = false; }
                    if (done) highestDone = Math.Max(highestDone, i);
                }
                for (int i = 0; i <= highestDone; i++)
                {
                    if (IsDone(steps[i])) continue;
                    States["done:" + steps[i].Key] = "true";
                    changed = true;
                }

                // Park the pins whose parking step has completed. Goods get consumed at the
                // hand-over, and a parked pin cannot fall back to 0/N on a finished step.
                foreach (var step in steps)
                {
                    foreach (var sp in step.Pins)
                    {
                        string parkKey = "parked:" + step.Key + ":" + sp.Code;
                        if (States.ContainsKey(parkKey)) continue;
                        var parkStep = steps.FirstOrDefault(s => s.Key == sp.ParkOnStep);
                        if (parkStep == null || !IsDone(parkStep)) continue;

                        // Not matched on GatherOnly — the player may have expanded the pin —
                        // and never parked when the count outgrew what the step asked for:
                        // a bigger goal is the player's own, and our step ending is no
                        // reason to switch their pin off.
                        var pin = svc.Store.Pins.FirstOrDefault(
                            p => p.QuestGiver == null && p.Code == sp.Code && p.Count <= sp.Quantity);
                        if (pin != null && pin.Active) svc.Store.SetActive(pin, false);
                        States[parkKey] = "true";
                        changed = true;
                    }
                }

                // Reveal the current step, if the game has told the player this much.
                var current = steps.FirstOrDefault(s => !IsDone(s));
                if (current != null && !IsRevealed(current))
                {
                    int idx = steps.IndexOf(current);
                    bool predecessorDone = idx == 0 || IsDone(steps[idx - 1]);
                    bool revealSignal;
                    try { revealSignal = current.Reveal?.Invoke() ?? true; }
                    catch { revealSignal = false; }

                    if (predecessorDone && revealSignal)
                    {
                        States["seen:" + current.Key] = "true";
                        AddStepPins(current);
                        changed = true;

                        // Out loud, because it happened without the player asking — but once
                        // per session at most, so a noisy login can't stack notices.
                        if (!announcedThisSession)
                        {
                            announcedThisSession = true;
                            string verb = steps.Any(IsDone) ? "continues" : "begins";
                            capi.ShowChatMessage(
                                $"Tallybook: the story {verb} — {current.Title}. Press L, Side quests tab.");
                        }
                    }
                }

                if (changed)
                {
                    svc.Store.Save();
                    svc.RecountAll();
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] story progress check failed: {0}", e.Message);
            }
        }

        void AddStepPins(Step step)
        {
            foreach (var sp in step.Pins)
            {
                // Once ever, like adopted errands: unpinning is a decision, and an auto-pin
                // that returns every login would override it.
                string guard = "pinned:" + step.Key + ":" + sp.Code;
                if (States.ContainsKey(guard)) continue;
                States[guard] = "true";

                var stack = RecipeProbe.StackFor(capi.World, sp.Code, sp.IsBlock);
                if (stack == null) continue;

                var pin = svc.Store.Add(stack, sp.Quantity, setCount: true);
                if (pin == null) continue;
                pin.GatherOnly = true;   // the step is a fetch, never a crafting plan
                svc.Resolve(pin);
            }
        }

        /// <summary>Everything the story block draws, in one string, so the shared signature
        /// notices a step change the way it notices a count change.</summary>
        public string UiSignature()
        {
            if (!Enabled) return "";
            var current = Current;
            return $"{steps.Count(IsDone)}|{current?.Key}|{current?.Detail?.Invoke()}|{AllDone}";
        }

        /// <summary>Diagnostic for `.tallybook story`. Spoiler-safe by the same rule as the
        /// display: steps the game has not revealed appear only as a count.</summary>
        public List<string> Report()
        {
            var lines = new List<string>();
            if (!Enabled)
            {
                lines.Add("This world has no story content (no story dialogue files).");
                return lines;
            }

            int hidden = 0;
            foreach (var step in steps)
            {
                if (!IsRevealed(step)) { hidden++; continue; }
                string mark = IsDone(step) ? "√" : "▶";
                lines.Add($"{mark} {step.Title}");
                if (!IsDone(step))
                {
                    lines.Add($"   {step.Text}");
                    string detail = null;
                    try { detail = step.Detail?.Invoke(); } catch { }
                    if (detail != null) lines.Add($"   {detail}");
                }
            }
            if (lines.Count == 0) lines.Add("The story has not found you yet.");
            if (hidden > 0) lines.Add($"… and the road goes on from there ({hidden} step(s) still ahead).");
            return lines;
        }
    }
}
