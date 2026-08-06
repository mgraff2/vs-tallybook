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
    /// Adds an "Add to Tallybook" link to the bottom of every item's handbook page.
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

        public static bool Active { get; private set; }

        public static void Apply(ICoreClientAPI api, Action<ItemStack> pinAction)
        {
            capi = api;
            onPin = pinAction;

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
        }

        public static void Postfix(ItemSlot inSlot, ICoreClientAPI capi, ref RichTextComponentBase[] __result)
        {
            try
            {
                var stack = inSlot?.Itemstack;
                if (stack?.Collectible == null || onPin == null) return;

                var components = __result?.ToList() ?? new List<RichTextComponentBase>();
                components.Add(new ClearFloatTextComponent(capi, 12));

                // Capture a clone: the slot this page was built from is reused, and holding the
                // live reference would pin whatever the slot contains later, not what was shown.
                var pinned = stack.Clone();
                components.Add(new LinkTextComponent(
                    capi, "→ Add to Tallybook", CairoFont.WhiteSmallText(), _ => onPin(pinned)));

                __result = components.ToArray();
            }
            catch (Exception e)
            {
                capi?.Logger.Warning("[tallybook] handbook pin link failed: {0}", e.Message);
            }
        }
    }
}
