using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// Map markers for tracked errands: one appears when an errand is pinned and checked, and
    /// goes when it is unchecked or unpinned.
    ///
    /// Driven by transitions, never by a timer, and remembered on the pin itself
    /// (<see cref="Pin.WaypointPlaced"/>). An earlier version reconciled instead — it asked
    /// the map which markers existed and added any that were missing, on a tick. When that
    /// read came back empty, as it does, "missing" was always true and it planted a fresh
    /// marker every few seconds until the player had fifty of them. A design that repeats an
    /// outward action on a schedule can spam; one that acts only when a flag flips cannot,
    /// even if every read of the map fails. That is the whole reason for the flag, and it is
    /// why nothing here runs on a tick.
    ///
    /// A client-only mod cannot write another player's waypoints directly
    /// (WaypointMapLayer.AddWaypoint needs an IServerPlayer), so both directions go through
    /// the vanilla chat commands.
    /// </summary>
    public class QuestWaypoints
    {
        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly PinStore store;

        public QuestWaypoints(ICoreClientAPI capi, TallybookConfig config, PinStore store)
        {
            this.capi = capi;
            this.config = config;
            this.store = store;
        }

        /// <summary>Bring markers in line with the list. Cheap and idempotent: every pin
        /// already knows whether its marker exists, so a call with nothing to do does nothing
        /// and sends no commands.</summary>
        /// <summary>Set once the world is up. Nothing is sent before then: markers would be
        /// placed while the list is still loading, and a command sent mid-join is a command
        /// sent into the dark.</summary>
        public bool Ready { get; set; }

        public void Sync()
        {
            // Never throws: this runs from the same notification the HUD and dialog listen to,
            // and a throw here used to stop them redrawing.
            try
            {
                if (!Ready || !config.QuestWaypoints) return;

                foreach (var pin in store.Pins.ToList())
                {
                    if (pin.QuestGiver == null || !HasPosition(pin)) continue;

                    if (pin.Active && !pin.WaypointPlaced) Place(pin);
                    else if (!pin.Active && pin.WaypointPlaced) Clear(pin);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest marker update failed: {0}", e.Message);
            }
        }

        /// <summary>A pin is leaving the list — take its marker with it.</summary>
        public void OnPinRemoved(Pin pin)
        {
            try
            {
                if (!Ready || pin?.QuestGiver == null || !pin.WaypointPlaced) return;
                Clear(pin);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest marker removal failed: {0}", e.Message);
            }
        }

        static bool HasPosition(Pin pin) => pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0;

        void Place(Pin pin)
        {
            // Recorded before sending: if the command fails we would rather have no marker
            // than try again on the next change, and again, and again.
            pin.WaypointPlaced = true;

            // InvariantCulture is load-bearing: on a locale that writes 131,5 the comma splits
            // one argument into two and every argument after it shifts.
            string C(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

            capi.SendChatMessage(
                $"/waypoint addati {config.QuestWaypointIcon} " +
                $"{C(pin.QuestX)} {C(pin.QuestY)} {C(pin.QuestZ)} " +
                $"{(config.QuestWaypointPinned ? "true" : "false")} " +
                $"{config.QuestWaypointColor} {pin.QuestGiver}",
                null);
        }

        void Clear(Pin pin)
        {
            pin.WaypointPlaced = false;

            int index = FindIndex(pin.QuestGiver, pin.QuestX, pin.QuestY, pin.QuestZ);
            if (index < 0) return;      // already gone, or we cannot see the list — leave it be

            capi.SendChatMessage($"/waypoint remove {index}", null);
        }

        /// <summary>
        /// Index of our marker in the player's own waypoints, or -1. Matched on title and
        /// whole-block position at the moment of removal — never a remembered index, which
        /// shifts as other waypoints come and go and would delete someone else's marker.
        /// </summary>
        int FindIndex(string title, double x, double y, double z)
        {
            try
            {
                var list = OwnWaypoints();
                if (list == null) return -1;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var wp = list[i];
                    if (wp?.Position == null || wp.Title != title) continue;
                    if ((int)wp.Position.X == (int)x && (int)wp.Position.Z == (int)z) return i;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read waypoints: {0}", e.Message);
            }
            return -1;
        }

        List<Waypoint> OwnWaypoints()
        {
            var layer = capi.ModLoader.GetModSystem<WorldMapManager>()?
                .MapLayers?.OfType<WaypointMapLayer>().FirstOrDefault();
            if (layer == null) return null;

            // ownWaypoints is the list the remove command indexes into; on a client that has
            // not been sent it, fall back to the full list rather than guessing.
            if (layer.ownWaypoints != null && layer.ownWaypoints.Count > 0) return layer.ownWaypoints;
            return layer.Waypoints;
        }

        /// <summary>
        /// Where the map that came with an errand points, once you have read it.
        ///
        /// Reading a locator map is what puts its destination on your map, so the waypoint
        /// existing *is* the signal that you know where to go — no separate bookkeeping, and
        /// nothing claimed before you have actually read it. Matched on the distinctive words
        /// of the map's name ("Devastation"), which is loose by nature: a near miss shows the
        /// giver instead, which is where the errand ends anyway.
        /// </summary>
        public Vec3d FindReadMapSite(List<string> mapNames)
        {
            if (mapNames == null || mapNames.Count == 0) return null;

            try
            {
                var list = OwnWaypoints();
                if (list == null) return null;

                var words = mapNames.SelectMany(Distinctive).ToList();
                if (words.Count == 0) return null;

                foreach (var wp in list)
                {
                    if (wp?.Title == null || wp.Position == null) continue;
                    if (words.Any(w => wp.Title.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                        return wp.Position;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not look up a quest map site: {0}", e.Message);
            }
            return null;
        }

        /// <summary>The words in a map's name worth matching on — "Map to the Devastation"
        /// carries exactly one.</summary>
        static IEnumerable<string> Distinctive(string mapName)
        {
            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "map", "maps", "to", "the", "of", "a", "an", "and", "location", "locator"
            };

            foreach (var word in (mapName ?? "").Split(' ', ',', '\'', '"'))
            {
                string w = word.Trim();
                if (w.Length >= 5 && !ignore.Contains(w)) yield return w;
            }
        }

        /// <summary>
        /// Put a marker back on every active errand, whether or not we believe one is already
        /// there. For when the markers are gone but the pins still say otherwise — deleting
        /// them by hand leaves that belief stale, and nothing else would ever re-place them.
        ///
        /// Deliberately a command rather than a check at load: verifying against the map means
        /// trusting a read that has come back empty before, and acting on *that* is what
        /// produced fifty duplicates. Asked for once, by hand, it can only ever place one per
        /// errand.
        /// </summary>
        public int ReplaceAllQuestMarkers()
        {
            int placed = 0;
            foreach (var pin in store.Pins.ToList())
            {
                if (pin.QuestGiver == null || !pin.Active || !HasPosition(pin)) continue;

                pin.WaypointPlaced = false;
                Place(pin);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Remove every marker we can identify as one of ours — the way out of a map full of
        /// duplicates. Highest index first, so removing one never shifts the index of another
        /// still to be removed.
        /// </summary>
        public int RemoveAllQuestMarkers()
        {
            var givers = new HashSet<string>(
                store.Pins.Where(p => p.QuestGiver != null).Select(p => p.QuestGiver));
            if (givers.Count == 0) return 0;

            var list = OwnWaypoints();
            if (list == null) return 0;

            var indices = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                var wp = list[i];
                if (wp?.Title == null || !givers.Contains(wp.Title)) continue;
                if (!string.Equals(wp.Icon, config.QuestWaypointIcon, StringComparison.OrdinalIgnoreCase)) continue;
                indices.Add(i);
            }

            foreach (int i in Enumerable.Reverse(indices))
            {
                capi.SendChatMessage($"/waypoint remove {i}", null);
            }

            foreach (var pin in store.Pins) pin.WaypointPlaced = false;
            return indices.Count;
        }
    }
}
