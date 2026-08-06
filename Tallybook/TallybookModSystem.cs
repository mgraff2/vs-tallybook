using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

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
    /// Pinning happens from the handbook (see HandbookPin), which is where the player already
    /// is when they decide they want something. The chat command reports the list; it is not
    /// the way things get onto it.
    ///
    /// Still missing from the spec: the HUD overlay (§5), the management dialog (§6), and the
    /// manual expansion tree (§2a).
    /// </summary>
    public class TallybookModSystem : ModSystem
    {
        ICoreClientAPI capi;
        RecipeProbe probe;
        PinStore store;

        readonly Dictionary<string, int> lastCounts = new Dictionary<string, int>();
        readonly HashSet<IInventory> subscribed = new HashSet<IInventory>();

        // Picking up one stack modifies several slots, and each modification raises its own
        // event. Without coalescing, a single pickup triggers a full recount per slot touched.
        bool recountQueued;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            probe = new RecipeProbe(api);
            store = new PinStore(api);
            store.OnChanged += OnPinsChanged;

            HandbookPin.Apply(api, OnPinRequested);

            // Registered on capi.ChatCommands, so this is a CLIENT command and players invoke
            // it as ".tallybook". A leading "/" is routed to the server, which has never heard
            // of us (and must not — see the server-side silence invariant) and answers "No
            // such command exists".
            api.ChatCommands.Create("tallybook")
                .WithDescription("Show your Tallybook shopping list")
                .WithExamples(".tallybook", ".tallybook clear")
                .WithArgs(api.ChatCommands.Parsers.OptionalAll("subcommand"))
                .HandleWith(OnCommand);

            api.Event.PlayerJoin += OnPlayerJoin;
            api.Event.LeaveWorld += OnLeaveWorld;
        }

        void OnPlayerJoin(IClientPlayer player)
        {
            if (player?.PlayerUID != capi.World?.Player?.PlayerUID) return;

            // Recipes are pushed by the server on join, so any index built against a previous
            // world is stale.
            probe.InvalidateIndex();
            SubscribeToCarriedInventories();
            store.Load(Resolve);
        }

        void OnLeaveWorld()
        {
            store.Save();
            Unsubscribe();
        }

        // ---- pinning -----------------------------------------------------------------

        void OnPinRequested(ItemStack stack)
        {
            var pin = store.Add(stack);
            if (pin == null) return;

            capi.ShowChatMessage(pin.HasRecipe
                ? $"Tallybook: pinned {pin.DisplayName} x{pin.Count}. Type .tallybook to see your list."
                : $"Tallybook: pinned {pin.DisplayName} x{pin.Count} — no crafting recipe known, kept as a reminder.");
        }

        /// <summary>
        /// Re-resolve a pin's itemstack and recipe. Returns false when the item itself no longer
        /// exists in this world's content.
        /// </summary>
        bool Resolve(Pin pin)
        {
            if (pin.Stack == null)
            {
                var loc = new AssetLocation(pin.Code);
                var block = pin.IsBlock ? capi.World.GetBlock(loc) : null;
                var item = pin.IsBlock ? null : capi.World.GetItem(loc);

                if (block != null) pin.Stack = new ItemStack(block);
                else if (item != null) pin.Stack = new ItemStack(item);
                else return false;
            }

            // Recipes are re-resolved every time rather than persisted: a recipe mod added or
            // removed between sessions must change the answer, not restore a stale one
            // (spec §11).
            pin.Group = probe.FindGroupFor(pin.Stack);
            pin.Requirements = pin.Group == null
                ? new List<Requirement>()
                : probe.BuildRequirements(pin.Group);
            pin.Tools = pin.Group == null
                ? new List<Requirement>()
                : probe.BuildRequirements(pin.Group, tools: true);
            return true;
        }

        void OnPinsChanged()
        {
            foreach (var pin in store.Pins) Resolve(pin);
            SubscribeToCarriedInventories();
            SeedBaseline();
        }

        // ---- command -----------------------------------------------------------------

        TextCommandResult OnCommand(TextCommandCallingArgs args)
        {
            // Read the parsed value directly, never gated on args.ArgCount: parsers consume
            // the raw arguments while parsing, so by the time a handler runs ArgCount reads 0
            // even though args[0] holds the value.
            var sub = (args.Parsers.Count > 0 ? args[0] as string : null)?.Trim();

            if (sub == "clear")
            {
                store.Clear();
                return TextCommandResult.Success("Tallybook: list cleared.");
            }

            if (!string.IsNullOrEmpty(sub) && sub.StartsWith("unpin "))
            {
                string name = sub.Substring(6).Trim();
                var match = store.Pins.FirstOrDefault(p =>
                    p.Code.Contains(name) || p.DisplayName.ToLowerInvariant().Contains(name.ToLowerInvariant()));
                if (match == null) return TextCommandResult.Success($"Tallybook: nothing pinned matching '{name}'.");
                store.Remove(match.Code);
                return TextCommandResult.Success($"Tallybook: unpinned {match.DisplayName}.");
            }

            if (store.Pins.Count == 0)
            {
                string how = HandbookPin.Active
                    ? "Open the handbook, find something, and click \"Add to Tallybook\"."
                    : "The handbook button could not be installed — see client-main.log.";
                return TextCommandResult.Success($"Tallybook: your list is empty. {how}");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Tallybook — {store.Pins.Count} pinned:");
            foreach (var pin in store.Pins) sb.Append(DescribePin(pin));
            sb.Append("(.tallybook unpin <name> to remove, .tallybook clear to empty)");
            return TextCommandResult.Success(sb.ToString());
        }

        string DescribePin(Pin pin)
        {
            var sb = new StringBuilder();
            string link = probe.HandbookLink(pin.Stack);
            string handbook = link == null ? "" : $" <a href=\"{link}\">[handbook]</a>";

            sb.AppendLine($"  {pin.DisplayName} x{pin.Count}{handbook}");

            if (!pin.HasRecipe)
            {
                sb.AppendLine("    (no crafting recipe known)");
                return sb.ToString();
            }

            if (pin.Group.LayoutCount > 1)
            {
                sb.AppendLine($"    (cheapest of {pin.Group.LayoutCount} grid layouts — see handbook for the others)");
            }

            foreach (var req in pin.Requirements)
            {
                int needed = RecipeProbe.NeededFor(req, pin.Count, pin.Group.OutputQuantity);
                int have = probe.CountCarried(req);
                sb.AppendLine($"    {StatusMark(have, needed)} {req.DisplayName}  {have}/{needed}");
            }

            foreach (var tool in pin.Tools)
            {
                bool present = probe.CountCarried(tool) > 0;
                sb.AppendLine($"    {(present ? "[x]" : "[ ]")} requires: {tool.DisplayName} (not consumed)");
            }
            return sb.ToString();
        }

        static string StatusMark(int have, int needed)
        {
            if (have >= needed) return "[x]";   // satisfied
            if (have > 0) return "[~]";         // partial
            return "[ ]";                       // none
        }

        // ---- live inventory tracking -------------------------------------------------

        /// <summary>
        /// Record current counts without reporting them, so the next inventory change reports
        /// only what actually moved rather than replaying the whole list.
        /// </summary>
        void SeedBaseline()
        {
            lastCounts.Clear();
            foreach (var pin in store.Pins)
            {
                foreach (var req in pin.Requirements) lastCounts[CountKey(pin, req)] = probe.CountCarried(req);
            }
        }

        static string CountKey(Pin pin, Requirement req) => $"{pin.Code}|{req.DisplayName}";

        void SubscribeToCarriedInventories()
        {
            foreach (var inv in probe.CarriedInventories())
            {
                if (subscribed.Add(inv)) inv.SlotModified += OnSlotModified;
            }
        }

        void Unsubscribe()
        {
            foreach (var inv in subscribed) inv.SlotModified -= OnSlotModified;
            subscribed.Clear();
            probe?.InvalidateIndex();
            lastCounts.Clear();
        }

        void OnSlotModified(int slotId)
        {
            if (store.Pins.Count == 0 || recountQueued) return;

            // Coalesce to one recount on the next tick. Moving a stack fires SlotModified for
            // the source and destination slots separately, and mid-move the counts are briefly
            // wrong — recounting per event would both waste work and flash a number that was
            // never true. Deferring also avoids mutating event subscriptions inside a handler.
            recountQueued = true;
            capi.Event.RegisterCallback(_ =>
            {
                recountQueued = false;
                Recount();
            }, 0);
        }

        void Recount()
        {
            // Backpack slots can appear after login (equipping a bag adds an inventory), so
            // re-scan rather than assuming the login-time set is final.
            SubscribeToCarriedInventories();

            // Re-counting per requirement walks every carried slot again, so this is
            // O(requirements x slots) per change. Acceptable for a chat list; the HUD's merged
            // totals (spec §5) will need a single inventory pass building a code->count map.
            foreach (var pin in store.Pins)
            {
                foreach (var req in pin.Requirements)
                {
                    string key = CountKey(pin, req);
                    int needed = RecipeProbe.NeededFor(req, pin.Count, pin.Group.OutputQuantity);
                    int have = probe.CountCarried(req);

                    bool known = lastCounts.TryGetValue(key, out int previous);
                    if (known && previous == have) continue;
                    lastCounts[key] = have;

                    // An ingredient never counted before is a baseline being recorded, not a
                    // change the player caused.
                    if (!known) continue;

                    capi.ShowChatMessage(
                        $"Tallybook: {req.DisplayName} {have}/{needed} {StatusMark(have, needed)} " +
                        $"(for {pin.DisplayName})");
                }
            }
        }

        public override void Dispose()
        {
            HandbookPin.Remove();
            if (capi != null) Unsubscribe();
            base.Dispose();
        }
    }
}
