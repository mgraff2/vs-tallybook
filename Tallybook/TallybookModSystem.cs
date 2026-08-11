using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// Client-side crafting shopping list. See tallybook-mod-spec.md.
    ///
    /// Client-only by construction: the ShouldLoad gate below and "side": "Client" in
    /// modinfo.json are both load-bearing. A dedicated server still unpacks the zip and
    /// loads this assembly, but must never see a single line of Tallybook output — that
    /// silence is pinned as a regression invariant in tools/compat-test.ps1.
    ///
    /// Surfaces: pin from any handbook page (HandbookPin), manage the list in the L dialog
    /// (GuiDialogTallybook), watch the merged gather totals on the K HUD (HudTallybook).
    /// </summary>
    public class TallybookModSystem : ModSystem
    {
        ICoreClientAPI capi;
        TallybookConfig config;
        TallyService svc;
        QuestScanner quests;
        QuestReadyGlow questGlow;
        QuestWatcher questWatcher;
        QuestWaypoints questWaypoints;
        QuestHistory questHistory;
        StoryProgress story;
        SiteQuests siteQuests;
        GuiDialogTallybook dialog;
        HudTallybook hud;
        HandbookReturnButton handbookReturn;

        readonly HashSet<IInventory> subscribed = new HashSet<IInventory>();
        bool recountQueued;
        long handbookWatchId;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        /// <summary>The per-build stamp from the csproj — what ".tallybook version" prints.
        /// Two builds can share a mod version mid-iteration, and a stale staged zip looks
        /// exactly like a fix not working (bit Mark on 0.3.10 and again on 0.3.11).</summary>
        static string BuildStamp =>
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "unknown";

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            capi.Logger.Notification("[tallybook] {0}, {1}", Mod?.Info?.Version, BuildStamp);

            try
            {
                config = capi.LoadModConfig<TallybookConfig>("tallybook.json") ?? new TallybookConfig();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] Bad config file, using defaults: {0}", e.Message);
                config = new TallybookConfig();
            }
            config.Clamp();
            capi.StoreModConfig(config, "tallybook.json");

            svc = new TallyService(api, config);
            quests = new QuestScanner(api);
            HandbookPin.Apply(api, OnPinRequested, OnOpenListRequested);

            // Defaults L and K per spec §9; both rebindable in Settings → Controls.
            capi.Input.RegisterHotKey("tallybook", "Tallybook (shopping list)", GlKeys.L, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("tallybook", OnDialogHotkey);
            capi.Input.RegisterHotKey("tallybookhud", "Tallybook HUD toggle", GlKeys.K, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler("tallybookhud", OnHudHotkey);

            // Discoverability fallback; the dialog is the product. Client command, so ".tallybook".
            api.ChatCommands.Create("tallybook")
                .WithDescription("Open the Tallybook shopping list")
                .HandleWith(_ =>
                {
                    OnDialogHotkey(null);
                    return TextCommandResult.Success("");
                })
                .BeginSubCommand("clearmarkers")
                    .WithDescription("Remove every quest map marker Tallybook has placed")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        int removed = questWaypoints?.RemoveAllQuestMarkers() ?? 0;
                        return TextCommandResult.Success(removed > 0
                            ? $"Removing {removed} quest marker(s)."
                            : "No Tallybook quest markers found on your map.");
                    })
                .EndSubCommand()
                .BeginSubCommand("markers")
                    .WithDescription("Put a map marker back on every tracked quest giver")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        int placed = questWaypoints?.ReplaceAllQuestMarkers() ?? 0;
                        return TextCommandResult.Success(placed > 0
                            ? $"Placing {placed} quest marker(s)."
                            : "No tracked errands with a known location.");
                    })
                .EndSubCommand()
                .BeginSubCommand("quests")
                    .WithDescription("Tie out every fetch errand in this world's dialogue: item, giver, maps, status")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        foreach (var line in QuestTieOut()) capi.ShowChatMessage(line);
                        return TextCommandResult.Success("");
                    })
                .EndSubCommand()
                .BeginSubCommand("npcs")
                    .WithDescription("List conversable NPCs nearby with their exact names — ground truth for quest-giver matching")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        var me = capi.World?.Player?.Entity;
                        if (me?.Pos == null) return TextCommandResult.Error("No position available.");

                        int seen = 0;
                        foreach (var e in capi.World.LoadedEntities.Values)
                        {
                            if (e == null || e == me) continue;
                            if (e.GetBehavior<EntityBehaviorConversable>() == null) continue;
                            if (e.Pos == null) continue;
                            double d = Math.Sqrt(e.Pos.XYZ.SquareDistanceTo(me.Pos.XYZ));
                            if (d > 60) continue;
                            capi.ShowChatMessage($"  '{e.GetName()}' ({(int)d}m)  [{e.Code?.ToShortString()}]");
                            seen++;
                        }
                        return TextCommandResult.Success(seen > 0
                            ? $"{seen} conversable NPC(s) within 60 blocks. Quest givers match on these names."
                            : "No conversable NPCs within 60 blocks.");
                    })
                .EndSubCommand()
                .BeginSubCommand("here")
                    .WithDescription("Set a quest giver's location to where you are standing: .tallybook here Agnieszka")
                    .WithArgs(api.ChatCommands.Parsers.All("giver"))
                    .HandleWith(args =>
                    {
                        EnsureGui();
                        string who = (args[0] as string)?.Trim();
                        if (string.IsNullOrEmpty(who))
                            return TextCommandResult.Error("Say who: .tallybook here Agnieszka");

                        var me = capi.World?.Player?.Entity?.Pos;
                        if (me == null) return TextCommandResult.Error("No position available.");

                        // The player asserting "they live here" outranks anything learned —
                        // it is the one source that cannot be a stale capture.
                        var matched = svc.Store.Pins.Where(p =>
                            p.QuestGiver != null
                            && p.QuestGiver.IndexOf(who, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (matched.Count == 0)
                            return TextCommandResult.Error($"No tracked errand has a giver matching '{who}'.");

                        foreach (var pin in matched)
                        {
                            pin.QuestX = me.X; pin.QuestY = me.Y; pin.QuestZ = me.Z;
                        }
                        svc.Store.NpcPlaces[matched[0].QuestGiver] = string.Format(
                            CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}", me.X, me.Y, me.Z);
                        svc.Store.Save();
                        svc.RecountAll();

                        return TextCommandResult.Success(
                            $"{matched[0].QuestGiver} placed here. Map on their errand(s) now comes to this spot.");
                    })
                .EndSubCommand()
                .BeginSubCommand("relearn")
                    .WithDescription("Forget all learned quest-giver positions and markers, then relearn them fresh")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        int removed = questWaypoints?.Relearn() ?? 0;
                        quests.InvalidateCatalogue();
                        BackfillQuestText();     // re-derive maps and text from the files now
                        svc.RecountAll();
                        return TextCommandResult.Success(
                            $"Forgot all quest places and removed {removed} marker(s). "
                            + "Walk past your quest givers (or open the map once, for places "
                            + "your own waypoints name) and everything relearns.");
                    })
                .EndSubCommand()
                .BeginSubCommand("waypoints")
                    .WithDescription("List the waypoints this client can currently read — ground truth for a missing Map button")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        var lines = questWaypoints?.ReadableWaypoints() ?? new List<string>();
                        if (lines.Count == 0)
                            return TextCommandResult.Success(
                                "The waypoint list reads back empty right now. This read fails "
                                + "intermittently — captured positions on quest pins survive it.");
                        foreach (var line in lines) capi.ShowChatMessage("  " + line);
                        return TextCommandResult.Success($"{lines.Count} waypoint(s) readable.");
                    })
                .EndSubCommand()
                .BeginSubCommand("glow")
                    .WithDescription("Diagnose the quest-ready glow: option state, who should glow, who is nearby — and fire a test burst overhead")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        if (questGlow == null) return TextCommandResult.Error("Tallybook is not ready yet.");

                        foreach (var line in questGlow.Diagnose()) capi.ShowChatMessage("  " + line);
                        questGlow.TestBurst();
                        return TextCommandResult.Success(
                            "Test burst fired over your own head — if nothing sparkled just now, "
                            + "the particle itself is the problem; say so.");
                    })
                .EndSubCommand()
                .BeginSubCommand("blankmarkers")
                    .WithDescription("Find map waypoints with no title — hovering one crashes the world map")
                    .WithArgs(api.ChatCommands.Parsers.OptionalWord("remove"))
                    .HandleWith(args =>
                    {
                        EnsureGui();
                        if (questWaypoints == null) return TextCommandResult.Error("Tallybook is not ready yet.");

                        bool remove = string.Equals(args[0] as string, "remove", StringComparison.OrdinalIgnoreCase);
                        var blank = questWaypoints.BlankTitledWaypoints();
                        if (blank.Count == 0)
                            return TextCommandResult.Success("No untitled waypoints on your map.");

                        if (!remove)
                        {
                            foreach (var line in blank) capi.ShowChatMessage("  " + line);
                            return TextCommandResult.Success(
                                $"{blank.Count} untitled waypoint(s) — these crash the world map when hovered. "
                                + "Their positions are above; '.tallybook blankmarkers remove' deletes them.");
                        }

                        int gone = questWaypoints.RemoveBlankTitledWaypoints();
                        return TextCommandResult.Success($"Removing {gone} untitled waypoint(s).");
                    })
                .EndSubCommand()
                .BeginSubCommand("recipes")
                    .WithDescription("List items this world can craft more than one way (slow; reads every recipe)")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        foreach (var line in svc.Probe.MultiRecipeReport())
                            capi.ShowChatMessage(line);
                        return TextCommandResult.Success("");
                    })
                .EndSubCommand()
                .BeginSubCommand("pages")
                    .WithDescription("Diagnose Handbook buttons: each pin's page code and whether the handbook index knows it")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        foreach (var line in HandbookPageReport())
                            capi.ShowChatMessage(line);
                        return TextCommandResult.Success("");
                    })
                .EndSubCommand()
                .BeginSubCommand("story")
                    .WithDescription("Where you are in the story — revealed steps only, no spoilers")
                    .HandleWith(_ =>
                    {
                        EnsureGui();
                        foreach (var line in story.Report()) capi.ShowChatMessage(line);
                        return TextCommandResult.Success("");
                    })
                .EndSubCommand()
                .BeginSubCommand("version")
                    .WithDescription("Which Tallybook build is actually running — checks a restaged zip really loaded")
                    .HandleWith(_ => TextCommandResult.Success(
                        $"Tallybook {Mod?.Info?.Version}, {BuildStamp}. "
                        + "If you just restaged the zip and this stamp looks old, the game is "
                        + "still running the previous build — a full game restart is required."))
                .EndSubCommand()
                .BeginSubCommand("sites")
                    .WithDescription("Map-artifact side quests: what the catalogue derived, scan state, progress. 'track <name>' re-adds a dismissed one")
                    .WithArgs(api.ChatCommands.Parsers.OptionalAll("action"))
                    .HandleWith(args =>
                    {
                        EnsureGui();
                        string action = (args[0] as string)?.Trim();
                        if (!string.IsNullOrEmpty(action)
                            && action.StartsWith("track ", StringComparison.OrdinalIgnoreCase))
                        {
                            return TextCommandResult.Success(
                                siteQuests.Retrack(action.Substring("track ".Length)));
                        }
                        foreach (var line in siteQuests.Report()) capi.ShowChatMessage("  " + line);
                        return TextCommandResult.Success("");
                    })
                .EndSubCommand();

            api.Event.PlayerJoin += OnPlayerJoin;
            api.Event.LeaveWorld += OnLeaveWorld;

            // The "came from the list" hint lives only as long as the handbook stays open;
            // once it closes, a later press of H is a fresh visit with nowhere to go back to.
            // Costs nothing while the flag is clear, which is nearly always.
            handbookWatchId = api.Event.RegisterGameTickListener(_ =>
            {
                // Guarded like every other tick handler in the mod, for the same reason: an
                // exception escaping a game callback is fatal, and this one walks matchers
                // and name getters over arbitrary modded content. It was the only tick in the
                // codebase without the guard (found by Fable's review) — one modded
                // collectible throwing from SatisfiesAsIngredient would have taken the client
                // down once per second.
                try
                {
                    if (HandbookPin.CameFromList)
                    {
                        // FindDialog, not a raw OfType over LoadedGuis: the Command Handbook
                        // shares the base class, and reading whichever registered first could
                        // clear the flag while the player is still in the survival handbook.
                        var hb = HandbookPin.FindDialog(capi);
                        if (hb == null || !hb.IsOpened()) HandbookPin.CameFromList = false;
                    }

                    // Animal bags are the one source we cannot subscribe to — their contents
                    // live inside an itemstack, so moving something in there raises no slot
                    // event we can hear, and the animal wandering in or out of range changes
                    // the answer with no event at all. Recounting on this tick is the only way
                    // those numbers move; it is opt-in and only runs while one of your animals
                    // is actually nearby, so the event-driven rule still holds everywhere else.
                    if (config.IncludeMountBags && svc.Probe.HasOwnedAnimalNearby(config.MountBagRange))
                    {
                        svc.RecountAll();
                    }

                    // Capture what the map can tell us about quest pins into the pins
                    // themselves, so the Map button never depends on a live waypoint read —
                    // which is known to come back empty at random. A successful capture is
                    // worth a save (it survives the session) and a recount (the button
                    // appears now, not at the next unrelated change).
                    if (questWaypoints?.ResolveQuestPlaces() == true)
                    {
                        svc.Store.Save();
                        svc.RecountAll();
                    }

                    // Cheap while nothing changes, and this is the only way finishing a quest
                    // is ever noticed — completing one raises no event we can hear. A stage
                    // moving is also invisible to the store's change event (variables flip
                    // server-side, not in our data), so a move earns an explicit recount —
                    // that is what clears a "collect your reward" row the moment it is paid.
                    bool questsMoved = questHistory?.Update() == true;
                    if (questHistory?.CheckErrandCompletion() == true) questsMoved = true;
                    if (questsMoved) svc.RecountAll();

                    // Map-artifact side quests: adopt newly read locator maps, notice
                    // arrivals, count recovered writings. Gated on Ready like the waypoint
                    // resolver — adopting into a store that has not loaded yet would be
                    // writing over the player's file with an empty one.
                    if (questWaypoints?.Ready == true && siteQuests?.Tick() == true)
                    {
                        svc.Store.Save();
                        svc.RecountAll();
                    }

                    // Same reason: a story step completing raises no event. A handful of
                    // variable reads when nothing moved.
                    story?.Poll();
                }
                catch (Exception e)
                {
                    capi.Logger.Warning("[tallybook] tick update failed: {0}", e.Message);
                }
            }, 1000);
        }

        void EnsureGui()
        {
            if (dialog != null) return;
            questHistory = new QuestHistory(capi, svc.Store, quests);
            questWaypoints = new QuestWaypoints(capi, config, svc.Store);
            story = new StoryProgress(capi, svc, quests, questWaypoints);
            siteQuests = new SiteQuests(capi, svc, questWaypoints);
            // The story block redraws with the same surfaces as every count, so its state
            // rides the shared change signature — and so does the set of quests awaiting a
            // reward, or the "collect your reward" row could never appear or clear. Site
            // quests ride it for the same reason: arrival and a found writing change no
            // store data the pin signature can see.
            svc.ExtraSignature = () => story.UiSignature() + "|rw:"
                + string.Join(",", questHistory.AwaitingRewards().Select(a => a.Chain))
                + "|sq:" + siteQuests.Signature();
            dialog = new GuiDialogTallybook(capi, config, svc, questHistory, questWaypoints,
                                            story, siteQuests, SetHudVisible, () => hud?.Refresh());
            hud = new HudTallybook(capi, config, svc) { Sites = siteQuests };
            handbookReturn = new HandbookReturnButton(capi, OnOpenListRequested);
            questGlow = new QuestReadyGlow(capi, config, svc, questHistory);
            questWatcher = new QuestWatcher(capi, config, svc, quests, OnQuestTracked);
            questWatcher.History = questHistory;
            questWatcher.OnConversing = npc =>
            {
                RecordNpcPlace(npc);
                // Standing with an NPC is the only moment their entity-scope story state is
                // readable at all — record what is true before the conversation is over.
                story.ObserveConversation(npc);
            };
            // Checking and unchecking come through the recount; unpinning has to be caught as
            // it happens, while the pin can still tell us it had a marker.
            svc.OnCountsChanged += questWaypoints.Sync;
            svc.Store.OnPinRemoved += questWaypoints.OnPinRemoved;
        }

        bool OnDialogHotkey(KeyCombination comb)
        {
            if (capi.World?.Player == null) return false;
            EnsureGui();
            if (dialog.IsOpened()) dialog.TryClose();
            else dialog.TryOpen();
            return true;
        }

        /// <summary>One place that turns the HUD on or off, so the hotkey and the Options
        /// switch cannot disagree about what is showing.</summary>
        void SetHudVisible(bool visible)
        {
            EnsureGui();
            hud.UserVisible = visible;
            hud.Refresh();

            config.HudVisible = visible;
            capi.StoreModConfig(config, "tallybook.json");
        }

        bool OnHudHotkey(KeyCombination comb)
        {
            if (capi.World?.Player == null) return false;
            EnsureGui();
            hud.UserVisible = !hud.UserVisible;
            hud.Refresh();

            // The toggle is a preference, not a moment — persist it so a player who keeps the
            // HUD off is not greeted by it every relog.
            config.HudVisible = hud.UserVisible;
            capi.StoreModConfig(config, "tallybook.json");

            // Say which way it went. Hiding it is silent by nature, and a preference that
            // survives relogs is one you can forget you set — "the HUD is broken" is the
            // reasonable conclusion a week later.
            capi.ShowChatMessage(hud.UserVisible
                ? "Tallybook HUD shown."
                : "Tallybook HUD hidden — press K to show it again.");
            return true;
        }

        void OnPinRequested(ItemStack stack)
        {
            EnsureGui();
            var pin = svc.Store.Add(stack);
            if (pin == null) return;

            // A fresh pin never auto-expands — not even with a single recipe, and not even
            // with a remembered choice. This rule tightened three times (Mark, 0.3.9):
            // first for many-path liquids ("it should wait for me to expand it"), then for
            // stale remembered picks (a test-time click resurrected "Distilled Mead"), and
            // finally for everything ("Sulfuric acid auto expands to its components, I
            // thought we weren't doing that anymore?"). The pin counts; Expand is the
            // player's act, and a remembered pick merely preselects there.
            if (pin.SelfNode == null) pin.GatherOnly = true;

            svc.Resolve(pin);
            svc.RecountAll();
            string note = pin.Groups.Count > 1
                ? $"{pin.Groups.Count} ways to make it — Expand in the list to choose."
                : pin.Groups.Count == 1
                    ? "Expand in the list to see the recipe."
                    : "no crafting recipe known, kept as a reminder.";
            capi.ShowChatMessage($"Tallybook: pinned {pin.DisplayName} x{pin.Count} — {note}");
        }

        /// <summary>
        /// Swap the handbook for the shopping list. Deferred by a tick: this runs from inside
        /// the handbook's own click handling, and closing a dialog out from under the event
        /// loop that is still walking it is how composers get disposed mid-iteration.
        /// </summary>
        void OnOpenListRequested()
        {
            // Frame queue rather than RegisterCallback: this link lives inside the handbook,
            // which pauses singleplayer while open — a delayed callback registered then would
            // not fire until unpause, leaving the click apparently dead. Main-thread tasks
            // run every frame, paused or not; closing the handbook here is also what unpauses.
            capi.Event.EnqueueMainThreadTask(() =>
            {
                EnsureGui();

                var handbook = capi.Gui.LoadedGuis?.OfType<GuiDialogHandbook>().FirstOrDefault();
                if (handbook != null && handbook.IsOpened()) handbook.TryClose();

                HandbookPin.CameFromList = false;    // the round trip is finished
                if (!dialog.IsOpened()) dialog.TryOpen();
            }, "tallybook-openlist");
        }

        void OnPlayerJoin(IClientPlayer player)
        {
            if (player?.PlayerUID != capi.World?.Player?.PlayerUID) return;

            EnsureGui();
            // Recipes are pushed by the server on join, so any index built against a previous
            // world is stale.
            svc.Probe.InvalidateIndex();
            quests.InvalidateCatalogue();     // asset sets differ between servers
            story.InvalidateWorld();          // and story content with them
            siteQuests.InvalidateWorld();     // locator items and lore likewise
            SubscribeToCarriedInventories();
            svc.Store.Load(svc.Resolve);
            BackfillQuestText();
            DedupeQuestPins();
            AdoptVillageQuests();
            questHistory.Update();
            story.Poll();
            svc.RecountAll();
            hud.Refresh();

            // Only now: markers placed while the list was still loading would be sent into a
            // world that is not yet listening, and a saved errand must not re-mark itself just
            // because it was reloaded.
            questWaypoints.Ready = true;
        }

        /// <summary>
        /// Fill in what the villager said for errands that predate us keeping it. Runs once
        /// per world load and only touches pins with nothing recorded, so an errand whose text
        /// we already have — or whose giver we cannot find a dialogue file for — is left alone.
        /// </summary>
        void BackfillQuestText()
        {
            bool filled = false;
            foreach (var pin in svc.Store.Pins)
            {
                if (pin.QuestGiver == null) continue;

                // The dialogue files describe every errand in full and do not care whether we
                // were watching when this one was accepted — so they, not what happened to be
                // captured at the time, are what an incomplete pin is repaired from. Matched
                // on what was asked for rather than only on the giver's name, because the name
                // is recorded from a live entity and is the part most likely to be missing.
                var def = quests.DefFor(pin.QuestGiver, pin.Code, pin.Count);

                if (!QuestScanner.IsTranscript(pin.QuestText))
                {
                    var said = quests.BriefingFor(pin.QuestGiver, pin.Code, pin.Count);
                    if ((said == null || said.Count == 0) && def != null) said = def.Briefing;
                    if (said != null && said.Count > 0) { pin.QuestText = said; filled = true; }
                }

                // The def's maps are tied to this errand by a shared gate variable — the
                // per-file version of this fill sent Agnieszka's ingots to the Devastation,
                // because her file also hands out an unrelated map. A def with no tied maps
                // honestly contributes none.
                if ((pin.QuestMaps == null || pin.QuestMaps.Count == 0) && def?.Maps.Count > 0)
                {
                    pin.QuestMaps = def.Maps.ToList();
                    filled = true;
                }

                // And a location, if we have ever stood next to them. Was only ever applied to
                // freshly recovered errands, so a pin that lost its position kept no way back.
                if (pin.QuestX == 0 && pin.QuestY == 0 && pin.QuestZ == 0)
                {
                    ApplyKnownPlace(pin);
                    if (pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0) filled = true;
                }
            }
            if (filled) svc.Store.Save();
        }

        /// <summary>
        /// Merge the same player-scope errand tracked under two names. The login scan names
        /// givers after their dialogue file ("Luxuries"); a live conversation names the
        /// entity ("Trader"); both paths ran and the player owed two iron pickaxes for one
        /// errand (found by Mark). Player-scope only — the quest belongs to the player, so
        /// one pin is the truth; entity-scope errands stay per-NPC, since two traders can
        /// each genuinely want a quern. The file-derived name is kept (it is the stable one,
        /// the login scan will use it forever), and anything only the removed twin knew —
        /// position, text, maps, an active checkmark — migrates first.
        /// </summary>
        void DedupeQuestPins()
        {
            bool changed = false;
            foreach (var group in svc.Store.Pins
                .Where(p => p.QuestGiver != null)
                .GroupBy(p => (p.Code, p.Count))
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList())
            {
                var def = group
                    .Select(p => quests.DefFor(p.QuestGiver, p.Code, p.Count))
                    .FirstOrDefault(d => d != null);
                if (def == null || !quests.DefIsPlayerScoped(def)) continue;

                var keeper = group.FirstOrDefault(p =>
                        string.Equals(p.QuestGiver, def.NpcName, StringComparison.OrdinalIgnoreCase))
                    ?? group.FirstOrDefault(p => p.QuestX != 0 || p.QuestZ != 0)
                    ?? group[0];

                foreach (var twin in group)
                {
                    if (twin == keeper) continue;
                    if (keeper.QuestX == 0 && keeper.QuestY == 0 && keeper.QuestZ == 0)
                    {
                        keeper.QuestX = twin.QuestX; keeper.QuestY = twin.QuestY; keeper.QuestZ = twin.QuestZ;
                    }
                    if ((keeper.QuestText == null || keeper.QuestText.Count == 0) && twin.QuestText?.Count > 0)
                        keeper.QuestText = twin.QuestText;
                    if ((keeper.QuestMaps == null || keeper.QuestMaps.Count == 0) && twin.QuestMaps?.Count > 0)
                        keeper.QuestMaps = twin.QuestMaps;
                    if (twin.Active) keeper.Active = true;

                    svc.Store.Remove(twin);
                    changed = true;
                }
            }
            if (changed) svc.Store.Save();
        }

        /// <summary>
        /// Pick up village errands you are already on, without needing to go and talk to
        /// anyone. Their state lives on the player and is synced here, so the dialogue files
        /// plus those variables are enough — including for quests accepted long before this
        /// mod existed.
        ///
        /// Each errand is offered **once ever**: this runs at every login, and an errand you
        /// unpinned coming back each time you logged in would be its own kind of broken.
        /// Talking to the NPC still re-adds it, because that is something you chose to do.
        /// </summary>
        void AdoptVillageQuests()
        {
            int adopted = 0;

            foreach (var offer in quests.LiveVillageQuests())
            {
                foreach (var req in offer.Requirements)
                {
                    string key = QuestWatcher.OfferKey(offer.NpcName, req);
                    if (!svc.Store.OfferedQuests.Add(key)) continue;

                    // Already tracked under another name: this scan attributes errands to
                    // the dialogue FILE ("Luxuries") while a live conversation records the
                    // entity's name ("Trader"), and everything here is player-scope, so the
                    // same errand under both names is one quest twice — doubling the demand
                    // (found by Mark, two iron pickaxe rows). Matched on the item alone,
                    // not item+count: a count captured wrong by an older build must not
                    // spawn a "corrected" twin beside itself. The offer key stays consumed.
                    string reqCode = req.Stack?.Collectible?.Code?.ToShortString();
                    if (svc.Store.Pins.Any(p => p.QuestGiver != null && p.Code == reqCode)) continue;

                    var pin = svc.Store.Add(req.Stack, req.Quantity, setCount: true, activate: false,
                                            questGiver: offer.NpcName);
                    if (pin == null) continue;

                    if (req.Briefing.Count > 0 && (pin.QuestText == null || pin.QuestText.Count == 0))
                        pin.QuestText = req.Briefing.ToList();

                    ApplyKnownPlace(pin);
                    svc.Resolve(pin);
                    adopted++;
                }
            }

            if (adopted == 0) return;

            svc.Store.Save();
            capi.ShowChatMessage(
                $"Tallybook: picked up {adopted} errand(s) you were already on. Press L to see them.");
        }

        /// <summary>
        /// One line per pin: the page code its Handbook button will ask for, and whether the
        /// live handbook index actually holds that page. Failures print the code so a report
        /// can say exactly which lookup missed — "the button goes to the root" is this, seen
        /// without instrumentation. The index is a protected dictionary read by reflection;
        /// if that ever breaks, page codes still print and the index column says so.
        /// </summary>
        List<string> HandbookPageReport()
        {
            var lines = new List<string>();
            var handbook = HandbookPin.FindDialog(capi);
            if (handbook == null)
            {
                lines.Add("Handbook dialog not found — nothing to check against.");
                return lines;
            }

            Dictionary<string, int> index = null;
            try
            {
                index = AccessTools.Field(typeof(GuiDialogHandbook), "pageNumberByPageCode")
                    ?.GetValue(handbook) as Dictionary<string, int>;
            }
            catch { /* diagnostic only; report the codes without the index column */ }

            lines.Add(index == null
                ? $"{handbook.GetType().Name}: page index not readable; listing page codes only."
                : $"{handbook.GetType().Name}: index holds {index.Count} page(s).");

            foreach (var pin in svc.Store.Pins)
            {
                if (pin.Stack == null)
                {
                    lines.Add($"· {pin.Code}: unresolved — this world does not know the item (yet).");
                    continue;
                }

                string page = RecipeProbe.HandbookPageCode(pin.Stack, capi.World);
                string identity = RecipeProbe.PageCode(pin.Stack);
                string suffix = page == identity ? "" : $" (pin identity: {identity})";
                string known = index == null ? ""
                    : index.ContainsKey(page) ? " — in index"
                    : " — NOT IN INDEX";
                lines.Add($"· {pin.DisplayName}: {page}{known}{suffix}");
            }

            if (svc.Store.Pins.Count == 0) lines.Add("No pins to check.");
            return lines;
        }

        /// <summary>
        /// Every fetch errand this world's dialogue describes, against what we know about it:
        /// who wants it, how many, which maps lead there, whether its gates are open for this
        /// player, whether it is tracked, and whether we know where to walk.
        ///
        /// A diagnostic, printed on request — deliberately not a screen. The catalogue includes
        /// quests this player has never been offered, and putting that in the interface would
        /// spoil content the game is withholding; asking for it by name is a different act from
        /// being shown it. "live" is player-scope gates only, so a trader errand held on the
        /// NPC reads as not live rather than as a claim we cannot support.
        /// </summary>
        List<string> QuestTieOut()
        {
            var defs = quests.QuestCatalogue();
            var lines = new List<string>
            {
                $"{defs.Count} fetch errand(s) described by this world's dialogue files:"
            };

            foreach (var def in defs.OrderBy(d => d.NpcName).ThenBy(d => d.ItemCode))
            {
                var pin = svc.Store.Pins.FirstOrDefault(
                    p => p.QuestGiver != null && p.Code == def.ItemCode && p.Count == def.Quantity);

                string state = pin == null ? "not tracked"
                    : !pin.Active ? "parked"
                    : pin.Complete ? $"READY ({pin.Have}/{pin.Count})"
                    : $"{pin.Have}/{pin.Count}";

                string where = pin == null ? ""
                    : pin.QuestX != 0 || pin.QuestZ != 0
                        ? $", giver at {(int)pin.QuestX},{(int)pin.QuestZ}"
                        : ", giver location unknown";

                string maps = def.Maps.Count > 0 ? $", maps: {string.Join(", ", def.Maps)}" : "";

                lines.Add($"{def.NpcName}: {def.Quantity} x {def.ItemCode} — {state}"
                          + $" [{(quests.DefIsLive(def) ? "live" : "not live")}]{where}{maps}");
            }
            return lines;
        }

        /// <summary>Give a recovered errand a location if we have ever seen that NPC. Without
        /// one it still tallies; it just cannot be marked on the map yet.</summary>
        void ApplyKnownPlace(Pin pin)
        {
            if (pin.QuestGiver == null) return;

            // Case-insensitive on miss: the giver's name has two sources — the dialogue
            // *filename* for errands recovered at login, the live entity for everything
            // else — and they are only coincidentally identical. An exact-only match makes
            // the Map button quietly depend on that coincidence.
            if (!svc.Store.NpcPlaces.TryGetValue(pin.QuestGiver, out string place))
            {
                place = svc.Store.NpcPlaces.FirstOrDefault(
                    kv => string.Equals(kv.Key, pin.QuestGiver, StringComparison.OrdinalIgnoreCase)).Value;
                if (place == null) return;
            }

            var parts = place.Split(',');
            if (parts.Length != 3) return;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) return;

            pin.QuestX = x;
            pin.QuestY = y;
            pin.QuestZ = z;
        }

        /// <summary>
        /// Remember where conversable NPCs are as you pass them. This is what lets an errand
        /// recovered at load ever get a map marker — at load we know who wants what, but not
        /// where they live unless we have been there.
        /// </summary>
        /// <summary>
        /// Remember where an NPC is because you are TALKING to them — the only automatic
        /// position source, for villagers and traders alike (Mark: "I don't want to save
        /// them unless we talk to them"). An on-sight villager recorder was built and
        /// removed twice over; do not bring it back. Positions are knowledge you earn in
        /// person — or assert with `.tallybook here`, or lend via a waypoint naming them.
        ///
        /// The chain on first contact: position lands on every position-less pin from this
        /// giver → save → recount → the signature (which carries QuestX) changes →
        /// OnCountsChanged → QuestWaypoints.Sync places the blue X. Talking to them IS the
        /// backfill, marker included.
        ///
        /// The directory entry updates freely (it rides along with the next save); a pin
        /// *gaining* a position is worth an immediate save and redraw, since it changes what
        /// the list can do.
        /// </summary>
        void RecordNpcPlace(Entity npc)
        {
            string name = npc?.GetName();
            var pos = npc?.Pos?.XYZ;
            if (string.IsNullOrEmpty(name) || pos == null) return;

            svc.Store.NpcPlaces[name] = string.Format(
                CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}", pos.X, pos.Y, pos.Z);

            bool learned = false;
            foreach (var pin in svc.Store.Pins)
            {
                if (!string.Equals(pin.QuestGiver, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0) continue;

                pin.QuestX = pos.X; pin.QuestY = pos.Y; pin.QuestZ = pos.Z;
                learned = true;
            }
            if (learned)
            {
                svc.Store.Save();
                svc.RecountAll();
            }
        }


        void OnLeaveWorld()
        {
            if (questWaypoints != null) questWaypoints.Ready = false;
            svc.Store.Save();       // carries the NPC directory with it
            foreach (var inv in subscribed) inv.SlotModified -= OnSlotModified;
            subscribed.Clear();
            svc.Probe.InvalidateIndex();
        }

        void SubscribeToCarriedInventories()
        {
            foreach (var inv in svc.Probe.CarriedInventories())
            {
                if (subscribed.Add(inv)) inv.SlotModified += OnSlotModified;
            }
        }

        void OnSlotModified(int slotId)
        {
            if (recountQueued) return;

            // Coalesce to one recount on the next frame. Moving a stack fires SlotModified for
            // the source and destination slots separately, and mid-move the counts are briefly
            // wrong — recounting per event would both waste work and flash a number that was
            // never true. Deferring also avoids mutating event subscriptions inside a handler.
            //
            // The frame queue, not RegisterCallback: in paused singleplayer the inventory
            // stays interactive — the handbook pauses the game and slot clicks still land —
            // and RegisterCallback while paused is an engine warning that developer mode
            // escalates to a deliberate crash (seen in the wild via a ModDB report). Delayed
            // callbacks also only tick while unpaused, whereas main-thread tasks run every
            // frame regardless, so this keeps counts live while the player rearranges bags
            // with the handbook open.
            recountQueued = true;
            capi.Event.EnqueueMainThreadTask(() =>
            {
                recountQueued = false;
                // Backpack slots can appear after login (equipping a bag adds an inventory),
                // so re-scan rather than assuming the login-time set is final.
                SubscribeToCarriedInventories();
                svc.RecountAll();
            }, "tallybook-recount");
        }

        /// <summary>
        /// Take on a villager's fetch request: mark where they are, then pin what they want.
        /// The pins are ordinary pins — the direct-gather tracking already counts them — so a
        /// quest item behaves exactly like any other goal, and the errand is finished when the
        /// row goes green.
        /// </summary>
        void OnQuestTracked(QuestOffer offer)
        {
            if (offer == null) return;
            EnsureGui();

            int added = 0;
            foreach (var req in offer.Requirements)
            {
                // The same player-scope errand may already be pinned under its dialogue
                // file's name (the login scan says "Luxuries" where the live entity says
                // "Trader"). That pin IS this errand — enrich it with what only standing
                // here can teach (the NPC's position, a properly threaded briefing) and add
                // nothing (found by Mark, two iron pickaxe rows).
                string reqCode = req.Stack?.Collectible?.Code?.ToShortString();
                var twin = svc.Store.Pins.FirstOrDefault(p => p.QuestGiver != null
                    && p.QuestGiver != offer.NpcName
                    && p.Code == reqCode && p.Count == req.Quantity);
                if (twin != null
                    && quests.DefIsPlayerScoped(quests.DefFor(twin.QuestGiver, twin.Code, twin.Count)))
                {
                    if (twin.QuestX == 0 && twin.QuestY == 0 && twin.QuestZ == 0 && offer.Pos != null)
                    {
                        twin.QuestX = offer.Pos.X; twin.QuestY = offer.Pos.Y; twin.QuestZ = offer.Pos.Z;
                    }
                    if (req.Briefing.Count > 0 && !QuestScanner.IsTranscript(twin.QuestText))
                        twin.QuestText = req.Briefing.ToList();
                    continue;
                }

                var pin = svc.Store.Add(req.Stack, req.Quantity, setCount: true, activate: false,
                                        questGiver: offer.NpcName);
                if (pin == null) continue;

                if (offer.Pos != null)
                {
                    pin.QuestX = offer.Pos.X;
                    pin.QuestY = offer.Pos.Y;
                    pin.QuestZ = offer.Pos.Z;
                }
                if (req.Briefing.Count > 0) pin.QuestText = req.Briefing.ToList();
                if (offer.Maps.Count > 0) pin.QuestMaps = offer.Maps.ToList();
                svc.Resolve(pin);
                added++;
            }

            svc.Store.Changed();
            questWaypoints?.Sync();      // place the marker as part of accepting
            if (added == 0) return;

            // Say so out loud: this happened without the player asking, so silently editing
            // their list would be the mod acting behind their back.
            capi.ShowChatMessage(
                $"Tallybook: tracking {offer.Summary} for {offer.NpcName}, and marked them on your map. Press L for your list.");
        }

        public override void Dispose()
        {
            if (handbookWatchId != 0 && capi != null)
            {
                capi.Event.UnregisterGameTickListener(handbookWatchId);
                handbookWatchId = 0;
            }
            HandbookPin.Remove();
            if (capi != null) OnLeaveWorld();
            dialog?.Dispose();
            dialog = null;
            hud?.Dispose();
            hud = null;
            handbookReturn?.Dispose();
            handbookReturn = null;
            questGlow?.Dispose();
            questGlow = null;
            questWatcher?.Dispose();
            questWatcher = null;
            if (questWaypoints != null && svc != null)
            {
                svc.OnCountsChanged -= questWaypoints.Sync;
                svc.Store.OnPinRemoved -= questWaypoints.OnPinRemoved;
            }
            questWaypoints = null;
            base.Dispose();
        }
    }
}
