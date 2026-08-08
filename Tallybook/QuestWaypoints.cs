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

        /// <summary>
        /// The marker's title, guaranteed to be something the map can actually draw.
        ///
        /// A waypoint with a blank title crashes the game outright, and not where you would
        /// look for it: hovering it makes vanilla's world map build hover text of zero width
        /// and hand that to Cairo, which refuses a surface with no area and takes the client
        /// with it (found by Mark, 0.3.4). The name comes from the entity, an entity can be
        /// nameless, and the placing guard only ever checked for null — so an unnamed quest
        /// giver planted a live landmine on the player's map.
        ///
        /// Newlines go too: the waypoint command takes the title as rest-of-line, so an
        /// embedded break would silently truncate it back to nothing.
        /// </summary>
        static string SafeTitle(Pin pin)
        {
            string title = (pin?.QuestGiver ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (title.Length > 0) return title;

            title = (pin?.DisplayName ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return title.Length > 0 ? $"Errand: {title}" : "Errand";
        }

        void Place(Pin pin)
        {
            // Recorded before sending: if the command fails we would rather have no marker
            // than try again on the next change, and again, and again.
            pin.WaypointPlaced = true;

            // InvariantCulture is load-bearing: on a locale that writes 131,5 the comma splits
            // one argument into two and every argument after it shifts.
            string C(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

            // The command wants SPAWN-RELATIVE X/Z — the numbers the coordinate HUD shows —
            // while entity positions are absolute world coordinates, map middle around half a
            // million. Sending absolute into a relative-taking command offset every marker by
            // the whole spawn position: a cluster of blue x's nowhere near the villagers,
            // which the resolver then captured back as their "positions" (found by Mark, by
            // marking the real villagers by hand and comparing). Y stays as-is; only X/Z are
            // spawn-relative in the game's convention.
            var spawn = capi.World?.DefaultSpawnPosition?.XYZ;
            double sx = spawn?.X ?? 0, sz = spawn?.Z ?? 0;

            capi.SendChatMessage(
                $"/waypoint addati {config.QuestWaypointIcon} " +
                $"{C(pin.QuestX - sx)} {C(pin.QuestY)} {C(pin.QuestZ - sz)} " +
                $"{(config.QuestWaypointPinned ? "true" : "false")} " +
                $"{config.QuestWaypointColor} {SafeTitle(pin)}",
                null);
        }

        void Clear(Pin pin)
        {
            pin.WaypointPlaced = false;

            int index = FindIndex(SafeTitle(pin), pin.QuestX, pin.QuestY, pin.QuestZ);
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
        /// Where a map that came with an errand points, read off the waypoint that reading it
        /// created — vanilla's own record of the place. Matched on the distinctive words of
        /// the map's name ("Devastation" out of "Map to the devastation"), which is loose by
        /// nature: a near miss falls back to the giver, which is where the errand ends anyway.
        ///
        /// Safe to use only because QuestMaps is now tied to the errand by a shared quest
        /// variable in the dialogue (QuestScanner.MapsForGates). This same lookup over
        /// *per-file* maps sent every errand to the Devastation. Read live, never persisted,
        /// acts on nothing — a failed read costs a button target, not a marker.
        /// </summary>
        public Vec3d WaypointForMaps(List<string> mapNames)
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
        /// A waypoint whose title names this person — "Tobias' cave" for Tobias — or null.
        ///
        /// This is how a giver you have never stood next to can still get a Map button:
        /// reading "Map to Tobias' cave" drops vanilla's waypoint at his cave, and the title
        /// carrying his name is the game's own statement that the place is his. Matching a
        /// waypoint to a *person* by name is sound where matching one to an *errand* was
        /// invented — a name in the title is a claim about whose place it is, which is
        /// exactly the question the Map button answers.
        ///
        /// Read live, never persisted: if the player deletes the waypoint the answer honestly
        /// becomes "unknown" again, and nothing here acts outward, so a failed read costs a
        /// button rather than planting anything.
        /// </summary>
        public Vec3d WaypointNamed(string person)
        {
            if (string.IsNullOrWhiteSpace(person)) return null;

            try
            {
                var list = OwnWaypoints();
                if (list == null) return null;

                foreach (var wp in list)
                {
                    if (wp?.Title == null || wp.Position == null) continue;
                    // Never our own quest markers. They are titled exactly the giver's name
                    // with our icon, and they are placed FROM the position being resolved —
                    // capturing one back is a feedback loop, and when a marker had been
                    // misplaced, the loop laundered the bad position into "knowledge"
                    // (found by Mark). A player's own "Tobias' cave" is not exact-titled,
                    // so the genuine use survives.
                    if (IsOurs(wp, person)) continue;
                    if (wp.Title.IndexOf(person, StringComparison.OrdinalIgnoreCase) >= 0)
                        return wp.Position;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read waypoints: {0}", e.Message);
            }
            return null;
        }

        bool IsOurs(Waypoint wp, string title)
            => string.Equals(wp.Title?.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase)
               && string.Equals(wp.Icon, config.QuestWaypointIcon, StringComparison.OrdinalIgnoreCase);

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
        /// Fill in anything the map can tell us about quest pins that do not know it yet:
        /// a giver's position from a waypoint naming them, an errand's destination from the
        /// waypoint its own map created. Captured into persisted pin fields the moment a
        /// read succeeds, because the read itself is known to fail intermittently — the
        /// fifty-marker incident was this very list coming back empty. Once captured, a
        /// later failed read costs nothing: the dialog draws from the pin, never from here.
        ///
        /// Runs on the tick, and is cheap by construction: it asks the map only when some
        /// pin is actually missing something, which after the first success is never.
        /// </summary>
        public bool ResolveQuestPlaces()
        {
            bool changed = false;
            try
            {
                foreach (var pin in store.Pins)
                {
                    if (pin.QuestGiver == null || !pin.Active) continue;

                    if (pin.QuestX == 0 && pin.QuestY == 0 && pin.QuestZ == 0)
                    {
                        var named = WaypointNamed(pin.QuestGiver);
                        if (named != null)
                        {
                            pin.QuestX = named.X; pin.QuestY = named.Y; pin.QuestZ = named.Z;
                            changed = true;
                        }
                    }

                    if (!pin.HasSite && pin.QuestMaps?.Count > 0)
                    {
                        var site = WaypointForMaps(pin.QuestMaps);
                        if (site != null)
                        {
                            pin.SiteX = site.X; pin.SiteY = site.Y; pin.SiteZ = site.Z;
                            changed = true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] quest place resolution failed: {0}", e.Message);
            }
            return changed;
        }

        /// <summary>What the client can currently read off its own waypoint list — the ground
        /// truth for "why is there no Map button". The read fails intermittently, which is
        /// exactly why this exists as a command: run it twice and compare.</summary>
        public List<string> ReadableWaypoints()
        {
            var lines = new List<string>();
            var list = OwnWaypoints();
            if (list == null) return lines;

            // Spawn-relative, because that is what the player's coordinate HUD shows — this
            // list exists to be compared against it, and absolute world coordinates read as
            // gibberish next to it.
            var spawn = capi.World?.DefaultSpawnPosition?.XYZ;
            double sx = spawn?.X ?? 0, sz = spawn?.Z ?? 0;

            for (int i = 0; i < list.Count; i++)
            {
                var wp = list[i];
                if (wp == null) continue;
                string title = string.IsNullOrWhiteSpace(wp.Title) ? "(untitled)" : wp.Title;
                lines.Add(wp.Position == null
                    ? $"{i}: {title} (no position)"
                    : $"{i}: {title} at {(int)(wp.Position.X - sx)}, {(int)(wp.Position.Z - sz)}");
            }
            return lines;
        }

        /// <summary>
        /// Forget every learned quest place and start over: our markers removed, positions
        /// and destinations zeroed. The way out of poisoned knowledge — positions captured
        /// while placement was broken are wrong *in the save*, and nothing re-asks about a
        /// position it believes it has. Walking past the NPCs and the tick resolver relearn
        /// everything from scratch, through the fixed paths.
        /// </summary>
        public int Relearn()
        {
            int markers = RemoveAllQuestMarkers();

            foreach (var pin in store.Pins)
            {
                if (pin.QuestGiver == null) continue;
                pin.QuestX = pin.QuestY = pin.QuestZ = 0;
                pin.SiteX = pin.SiteY = pin.SiteZ = 0;
                // Which maps belong to this errand is also learned data — and it has been
                // wrong before (a mis-tied map is exactly what sends Map to the wrong ruin).
                // The catalogue re-derives it immediately after.
                pin.QuestMaps = new List<string>();
                pin.WaypointPlaced = false;
            }
            store.NpcPlaces.Clear();
            store.Save();
            return markers;
        }

        /// <summary>
        /// Every waypoint on the map with a blank title, as "index at x, z" lines.
        ///
        /// These crash the client when hovered on the world map — vanilla builds hover text of
        /// zero width and Cairo will not make a surface with no area — so finding them is
        /// worth a command even though any mod, or the player, can have made one. Reported
        /// rather than removed on sight: they are the player's waypoints, not ours, and only
        /// their positions are worth anything once the title is gone.
        /// </summary>
        public List<string> BlankTitledWaypoints()
        {
            var found = new List<string>();
            var list = OwnWaypoints();
            if (list == null) return found;

            for (int i = 0; i < list.Count; i++)
            {
                var wp = list[i];
                if (wp == null || !string.IsNullOrWhiteSpace(wp.Title)) continue;
                found.Add(wp.Position == null
                    ? $"{i}: (no position)"
                    : $"{i}: {(int)wp.Position.X}, {(int)wp.Position.Z}");
            }
            return found;
        }

        /// <summary>Delete them, highest index first so removals cannot shift each other.</summary>
        public int RemoveBlankTitledWaypoints()
        {
            var list = OwnWaypoints();
            if (list == null) return 0;

            var indices = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && string.IsNullOrWhiteSpace(list[i].Title)) indices.Add(i);
            }

            foreach (int i in Enumerable.Reverse(indices))
            {
                capi.SendChatMessage($"/waypoint remove {i}", null);
            }
            return indices.Count;
        }

        /// <summary>
        /// Remove every marker we can identify as one of ours — the way out of a map full of
        /// duplicates. Highest index first, so removing one never shifts the index of another
        /// still to be removed.
        /// </summary>
        public int RemoveAllQuestMarkers()
        {
            var givers = new HashSet<string>(
                store.Pins.Where(p => p.QuestGiver != null).Select(SafeTitle));
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

            // Marked as PLACED, deliberately, even though the markers are now gone. Sync
            // places wherever an active errand says it has no marker, and it runs on every
            // recount — flags cleared here were re-planting every marker within seconds of
            // the player asking for them gone (found by Fable's review). "I removed it on
            // purpose" and "it exists" want the same thing from Sync: leave it alone.
            // Re-checking a pin later still gets a marker (uncheck clears the flag), and
            // `.tallybook markers` remains the explicit way to bring them all back.
            foreach (var pin in store.Pins)
            {
                if (pin.QuestGiver != null && pin.Active) pin.WaypointPlaced = true;
            }
            return indices.Count;
        }
    }
}
