using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        GuiDialogTallybook dialog;
        HudTallybook hud;
        HandbookReturnButton handbookReturn;

        readonly HashSet<IInventory> subscribed = new HashSet<IInventory>();
        bool recountQueued;
        long handbookWatchId;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

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
                        var hb = capi.Gui.LoadedGuis?.OfType<GuiDialogHandbook>().FirstOrDefault();
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

                    RecordNearbyNpcs();

                    // Cheap while nothing changes, and this is the only way finishing a quest
                    // is ever noticed — completing one raises no event we can hear.
                    questHistory?.Update();
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
            dialog = new GuiDialogTallybook(capi, config, svc, questHistory, questWaypoints,
                                            SetHudVisible, () => hud?.Refresh());
            hud = new HudTallybook(capi, config, svc);
            handbookReturn = new HandbookReturnButton(capi, OnOpenListRequested);
            questGlow = new QuestReadyGlow(capi, config, svc);
            questWatcher = new QuestWatcher(capi, config, svc, quests, OnQuestTracked);
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

            svc.Resolve(pin);
            svc.RecountAll();
            capi.ShowChatMessage(pin.HasRecipe
                ? $"Tallybook: pinned {pin.DisplayName} x{pin.Count} — press L to manage your list."
                : $"Tallybook: pinned {pin.DisplayName} x{pin.Count} — no crafting recipe known, kept as a reminder.");
        }

        /// <summary>
        /// Swap the handbook for the shopping list. Deferred by a tick: this runs from inside
        /// the handbook's own click handling, and closing a dialog out from under the event
        /// loop that is still walking it is how composers get disposed mid-iteration.
        /// </summary>
        void OnOpenListRequested()
        {
            capi.Event.RegisterCallback(_ =>
            {
                EnsureGui();

                var handbook = capi.Gui.LoadedGuis?.OfType<GuiDialogHandbook>().FirstOrDefault();
                if (handbook != null && handbook.IsOpened()) handbook.TryClose();

                HandbookPin.CameFromList = false;    // the round trip is finished
                if (!dialog.IsOpened()) dialog.TryOpen();
            }, 0);
        }

        void OnPlayerJoin(IClientPlayer player)
        {
            if (player?.PlayerUID != capi.World?.Player?.PlayerUID) return;

            EnsureGui();
            // Recipes are pushed by the server on join, so any index built against a previous
            // world is stale.
            svc.Probe.InvalidateIndex();
            quests.InvalidateCatalogue();     // asset sets differ between servers
            SubscribeToCarriedInventories();
            svc.Store.Load(svc.Resolve);
            BackfillQuestText();
            AdoptVillageQuests();
            questHistory.Update();
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

                    var pin = svc.Store.Add(req.Stack, req.Quantity, setCount: true, activate: false,
                                            questGiver: offer.NpcName);
                    if (pin == null) continue;

                    if (offer.Briefing.Count > 0 && (pin.QuestText == null || pin.QuestText.Count == 0))
                        pin.QuestText = offer.Briefing.ToList();

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
        bool placesLearned;

        void RecordNearbyNpcs()
        {
            var me = capi.World?.Player?.Entity;
            if (me?.Pos == null) return;

            var found = capi.World.GetEntitiesAround(me.Pos.XYZ, 24, 24,
                e => e?.GetBehavior<EntityBehaviorConversable>() != null);
            if (found == null || found.Length == 0) return;

            bool changed = false;
            foreach (var npc in found)
            {
                string name = npc.GetName();
                var pos = npc.Pos?.XYZ;
                if (string.IsNullOrEmpty(name) || pos == null) continue;

                string place = string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}", pos.X, pos.Y, pos.Z);
                if (svc.Store.NpcPlaces.TryGetValue(name, out string had) && had == place) continue;

                svc.Store.NpcPlaces[name] = place;
                changed = true;

                // Give the errand its location the moment we learn it. An errand whose giver
                // we have never stood next to has no position, and without one there is no
                // Map button and no marker — so walking past them should be enough, without
                // having to open a conversation. Only fills a blank: a position recorded when
                // the errand was taken on is the better one.
                foreach (var pin in svc.Store.Pins)
                {
                    if (!string.Equals(pin.QuestGiver, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0) continue;

                    ApplyKnownPlace(pin);
                    placesLearned = true;
                }
            }

            // The directory itself rides along with the next save — writing the file every
            // time a villager takes a step would be absurd. A pin gaining a location is
            // different: that changes what the list can do, so it is worth a write and a
            // redraw.
            if (placesLearned)
            {
                placesLearned = false;
                svc.Store.Save();
                svc.RecountAll();
            }
            _ = changed;
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

            // Coalesce to one recount on the next tick. Moving a stack fires SlotModified for
            // the source and destination slots separately, and mid-move the counts are briefly
            // wrong — recounting per event would both waste work and flash a number that was
            // never true. Deferring also avoids mutating event subscriptions inside a handler.
            recountQueued = true;
            capi.Event.RegisterCallback(_ =>
            {
                recountQueued = false;
                // Backpack slots can appear after login (equipping a bag adds an inventory),
                // so re-scan rather than assuming the login-time set is final.
                SubscribeToCarriedInventories();
                svc.RecountAll();
            }, 0);
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
                var pin = svc.Store.Add(req.Stack, req.Quantity, setCount: true, activate: false,
                                        questGiver: offer.NpcName);
                if (pin == null) continue;

                if (offer.Pos != null)
                {
                    pin.QuestX = offer.Pos.X;
                    pin.QuestY = offer.Pos.Y;
                    pin.QuestZ = offer.Pos.Z;
                }
                if (offer.Briefing.Count > 0) pin.QuestText = offer.Briefing.ToList();
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
