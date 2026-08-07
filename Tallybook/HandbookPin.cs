using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// Adds "Add to Tallybook" and "Go to Tallybook" links to the bottom of every item's
    /// handbook page — pin what you are looking at, or go straight to the list you have been
    /// filling without hunting for a hotkey.
    ///
    /// The handbook is the right entry point: the player is already looking at the thing they
    /// want, so pinning should be a click there rather than typing an item code at a command.
    /// It also hands us a real ItemStack, which is exactly the input the pin list wants — no
    /// name matching, no guessing which item was meant.
    ///
    /// Done with Harmony because the page content is produced by
    /// CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo and there is no registration
    /// hook to append to it. The patch is a postfix that only appends to the returned array —
    /// it reads nothing, changes nothing the game produced, and if it ever fails to apply the
    /// handbook keeps working exactly as before (see Apply's catch).
    ///
    /// Client-only: patching happens in StartClientSide, which the ShouldLoad gate keeps off
    /// dedicated servers entirely.
    /// </summary>
    public static class HandbookPin
    {
        const string HarmonyId = "tallybook.handbook";

        static Harmony harmony;
        static ICoreClientAPI capi;
        static Action<ItemStack> onPin;
        static Action onOpen;

        public static bool Active { get; private set; }

        /// <summary>
        /// True while the player is in the handbook because they clicked Book on a Tallybook
        /// row. The link then reads as a return rather than a departure, and leads — coming
        /// back is the likely next move when you opened a page to check one thing. Cleared as
        /// soon as the handbook closes, so pressing H later never claims a journey you did
        /// not make.
        /// </summary>
        public static bool CameFromList { get; set; }

        /// <summary>
        /// The handbook dialog, whether or not the player has opened it yet this session.
        ///
        /// Before its first use it is absent from <c>Gui.LoadedGuis</c> — that list holds
        /// dialogs registered with the GUI manager, and the handbook is not wired in until it
        /// is first opened. The survival handbook mod system has built the instance long
        /// before that, so read it from there when the registered list comes up empty.
        /// (Without this, Book on a list row failed with "handbook is not available" until
        /// the player had pressed H once — found by Mark.)
        /// </summary>
        public static GuiDialogHandbook FindDialog(ICoreClientAPI api)
        {
            var registered = api?.Gui?.LoadedGuis?.OfType<GuiDialogHandbook>().FirstOrDefault();
            if (registered != null) return registered;

            try
            {
                var sys = api?.ModLoader?.GetModSystem<ModSystemSurvivalHandbook>();
                if (sys == null) return null;
                return AccessTools.Field(typeof(ModSystemSurvivalHandbook), "dialog")
                    ?.GetValue(sys) as GuiDialogHandbook;
            }
            catch (Exception e)
            {
                api?.Logger.Warning("[tallybook] could not locate the handbook: {0}", e.Message);
                return null;
            }
        }

        /// <summary>
        /// Open the handbook the way pressing H does, so whatever setup the game performs on
        /// first use happens exactly once and by its own hand rather than ours.
        /// </summary>
        public static void OpenLikeThePlayerWould(ICoreClientAPI api)
        {
            foreach (var code in new[] { "handbook", "survivalhandbook", "guihandbook" })
            {
                var hk = api.Input.GetHotKeyByCode(code);
                if (hk?.Handler == null) continue;
                hk.Handler(hk.CurrentMapping);
                return;
            }
            FindDialog(api)?.TryOpen();
        }

        public static void Apply(ICoreClientAPI api, Action<ItemStack> pinAction, Action openAction)
        {
            capi = api;
            onPin = pinAction;
            onOpen = openAction;

            try
            {
                var target = AccessTools.Method(
                    typeof(CollectibleBehaviorHandbookTextAndExtraInfo),
                    nameof(CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo));

                if (target == null)
                {
                    api.Logger.Warning("[tallybook] handbook method not found; pin button unavailable.");
                    return;
                }

                harmony = new Harmony(HarmonyId);
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(HandbookPin), nameof(Postfix)));
                Active = true;
            }
            catch (Exception e)
            {
                // A failed patch must never take the handbook down with it. Log and carry on
                // without the button rather than leaving the player unable to read recipes.
                api.Logger.Warning("[tallybook] could not add handbook pin button: {0}", e.Message);
                Active = false;
            }
        }

        public static void Remove()
        {
            try { harmony?.UnpatchAll(HarmonyId); }
            catch { /* shutting down; nothing useful to do about it */ }
            harmony = null;
            Active = false;
            capi = null;
            onPin = null;
            onOpen = null;
        }

        public static void Postfix(ItemSlot inSlot, ICoreClientAPI capi, ref RichTextComponentBase[] __result)
        {
            try
            {
                var stack = inSlot?.Itemstack;
                if (stack?.Collectible == null || onPin == null) return;

                var components = __result?.ToList() ?? new List<RichTextComponentBase>();
                components.Add(new ClearFloatTextComponent(capi, 12));

                var font = CairoFont.WhiteSmallText();

                // Capture a clone: the slot this page was built from is reused, and holding the
                // live reference would pin whatever the slot contains later, not what was shown.
                var pinned = stack.Clone();
                components.Add(new LinkTextComponent(
                    capi, "→ Add to Tallybook", font, _ => onPin(pinned)));

                if (onOpen != null)
                {
                    // Own line: two links flowing together read as one sentence. Returning
                    // from the list is handled by the button under the handbook's own Back,
                    // so this stays the neutral "take me there".
                    components.Add(new ClearFloatTextComponent(capi, 6));
                    components.Add(new LinkTextComponent(
                        capi, "→ Go to Tallybook", font, _ => onOpen()));
                }

                __result = components.ToArray();
            }
            catch (Exception e)
            {
                capi?.Logger.Warning("[tallybook] handbook pin link failed: {0}", e.Message);
            }
        }
    }
}
