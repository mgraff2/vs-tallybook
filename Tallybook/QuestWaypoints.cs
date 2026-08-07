using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// Keeps the map markers for tracked errands in step with the list: a marker exists for
    /// exactly those quest givers whose errand is pinned and checked, and disappears the
    /// moment it is unchecked or unpinned. Re-check or re-accept and it comes back.
    ///
    /// Written as a reconcile rather than as add/remove calls scattered through the places
    /// that change pins. There are five ways a marker can become wrong — pin, unpin, check,
    /// uncheck, re-accept — and hanging a side effect off each is how one gets missed and a
    /// marker outlives its errand. Instead the desired set is computed from the pins and the
    /// map is nudged toward it; every path through the code lands here.
    ///
    /// A client-only mod cannot write another player's waypoints directly
    /// (WaypointMapLayer.AddWaypoint needs an IServerPlayer), so both directions go through
    /// the vanilla chat commands. Removal indexes into the player's own waypoint list, which
    /// the client mirrors — so we only ever remove an entry we can still see and identify as
    /// one of ours, and never by a remembered index.
    /// </summary>
    public class QuestWaypoints
    {
        const int SettleMs = 4000;     // how long a sent command is given to take effect

        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly PinStore store;

        /// <summary>Keys we have just sent a command for. The map does not update until the
        /// server answers, and without this the next tick would send it all over again.</summary>
        readonly Dictionary<string, long> inFlight = new Dictionary<string, long>();

        long tickId;

        public QuestWaypoints(ICoreClientAPI capi, TallybookConfig config, PinStore store)
        {
            this.capi = capi;
            this.config = config;
            this.store = store;
            tickId = capi.Event.RegisterGameTickListener(_ => Sync(), 2000);
        }

        public void Dispose()
        {
            if (tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickId);
                tickId = 0;
            }
        }

        public void Sync()
        {
            try
            {
                if (!config.QuestWaypoints) return;

                var layer = capi.ModLoader.GetModSystem<WorldMapManager>()?
                    .MapLayers?.OfType<WaypointMapLayer>().FirstOrDefault();
                if (layer?.ownWaypoints == null) return;

                // Every errand we have ever marked, and the subset that should be marked now.
                var managed = new Dictionary<string, Pin>();
                var wanted = new HashSet<string>();
                foreach (var pin in store.Pins)
                {
                    if (pin.QuestGiver == null || !HasPosition(pin)) continue;
                    string key = KeyFor(pin.QuestGiver, pin.QuestX, pin.QuestY, pin.QuestZ);
                    managed[key] = pin;
                    if (pin.Active) wanted.Add(key);
                }
                if (managed.Count == 0) return;

                var present = new Dictionary<string, int>();
                for (int i = 0; i < layer.ownWaypoints.Count; i++)
                {
                    var wp = layer.ownWaypoints[i];
                    if (wp?.Position == null || wp.Title == null) continue;
                    string key = KeyFor(wp.Title, wp.Position.X, wp.Position.Y, wp.Position.Z);
                    if (managed.ContainsKey(key)) present[key] = i;      // last index wins
                }

                foreach (var key in wanted)
                {
                    if (present.ContainsKey(key) || IsInFlight(key)) continue;
                    Add(managed[key], key);
                }

                foreach (var entry in present)
                {
                    if (wanted.Contains(entry.Key) || IsInFlight(entry.Key)) continue;
                    Remove(entry.Value, entry.Key);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest waypoint sync failed: {0}", e.Message);
            }
        }

        static bool HasPosition(Pin pin) => pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0;

        /// <summary>Whole-block precision: the marker was placed from the same coordinates, and
        /// the map rounds them anyway.</summary>
        static string KeyFor(string title, double x, double y, double z)
            => $"{title}|{(int)x},{(int)y},{(int)z}";

        bool IsInFlight(string key)
        {
            if (!inFlight.TryGetValue(key, out long at)) return false;
            if (capi.World.ElapsedMilliseconds - at < SettleMs) return true;
            inFlight.Remove(key);
            return false;
        }

        void Add(Pin pin, string key)
        {
            // InvariantCulture is load-bearing: on a locale that writes 131,5 the comma splits
            // one argument into two and every argument after it shifts.
            string C(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

            inFlight[key] = capi.World.ElapsedMilliseconds;
            capi.SendChatMessage(
                $"/waypoint addati {config.QuestWaypointIcon} " +
                $"{C(pin.QuestX)} {C(pin.QuestY)} {C(pin.QuestZ)} " +
                $"{(config.QuestWaypointPinned ? "true" : "false")} " +
                $"{config.QuestWaypointColor} {pin.QuestGiver}",
                null);
        }

        void Remove(int index, string key)
        {
            inFlight[key] = capi.World.ElapsedMilliseconds;
            capi.SendChatMessage($"/waypoint remove {index}", null);
        }
    }
}
