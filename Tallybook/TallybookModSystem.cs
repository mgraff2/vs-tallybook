using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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

            svc = new TallyService(api);
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
                });

            api.Event.PlayerJoin += OnPlayerJoin;
            api.Event.LeaveWorld += OnLeaveWorld;

            // The "came from the list" hint lives only as long as the handbook stays open;
            // once it closes, a later press of H is a fresh visit with nowhere to go back to.
            // Costs nothing while the flag is clear, which is nearly always.
            handbookWatchId = api.Event.RegisterGameTickListener(_ =>
            {
                if (!HandbookPin.CameFromList) return;
                var hb = capi.Gui.LoadedGuis?.OfType<GuiDialogHandbook>().FirstOrDefault();
                if (hb == null || !hb.IsOpened()) HandbookPin.CameFromList = false;
            }, 1000);
        }

        void EnsureGui()
        {
            if (dialog != null) return;
            dialog = new GuiDialogTallybook(capi, config, svc);
            hud = new HudTallybook(capi, config, svc);
            handbookReturn = new HandbookReturnButton(capi, OnOpenListRequested);
            questGlow = new QuestReadyGlow(capi, config, svc);
            questWatcher = new QuestWatcher(capi, config, svc, quests, OnQuestTracked);
            questWaypoints = new QuestWaypoints(capi, config, svc.Store);

            // Any change to the list can make a marker wrong — checking, unchecking,
            // unpinning — so reconcile off the one event they all funnel through.
            svc.OnCountsChanged += questWaypoints.Sync;
        }

        bool OnDialogHotkey(KeyCombination comb)
        {
            if (capi.World?.Player == null) return false;
            EnsureGui();
            if (dialog.IsOpened()) dialog.TryClose();
            else dialog.TryOpen();
            return true;
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
            SubscribeToCarriedInventories();
            svc.Store.Load(svc.Resolve);
            svc.RecountAll();
            hud.Refresh();
        }

        void OnLeaveWorld()
        {
            svc.Store.Save();
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
                svc.Resolve(pin);
                added++;
            }

            svc.Store.Changed();
            questWaypoints?.Sync();      // mark them now rather than on the next slow tick
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
            if (questWaypoints != null && svc != null) svc.OnCountsChanged -= questWaypoints.Sync;
            questWaypoints?.Dispose();
            questWaypoints = null;
            base.Dispose();
        }
    }
}
