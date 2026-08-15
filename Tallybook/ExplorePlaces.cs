using System;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;

namespace Tallybook
{
    /// <summary>
    /// Saved places (the Explore tab): spots the player wants to come back to — a mine
    /// half-dug, a ruin spotted at dusk — each with a name, a one-line "what it is", longer
    /// notes, and a map marker. Everything is the player's own writing; the only thing
    /// captured from the world is where they were standing when they saved it.
    ///
    /// Markers follow the errand-marker contract to the letter: placed and removed on
    /// transitions, remembered in persisted flags (SavedPlace.WaypointPlaced), removals
    /// queued and retried until a successful map read settles them (the waypoint list is
    /// known to read back empty at random), and never reconciled against a live read. The
    /// master "quest map markers" option gates them the same way it gates everything else —
    /// off means Tallybook never touches your waypoints, and switching it flips the flags
    /// so nothing is placed or removed twice.
    /// </summary>
    public class ExplorePlaces
    {
        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly PinStore store;
        readonly QuestWaypoints waypoints;

        public ExplorePlaces(ICoreClientAPI capi, TallybookConfig config, PinStore store,
                             QuestWaypoints waypoints)
        {
            this.capi = capi;
            this.config = config;
            this.store = store;
            this.waypoints = waypoints;
        }

        /// <summary>Save the spot the player is standing on. The name is required — an
        /// unnamed place cannot be told apart on the map or the list — the note is not.</summary>
        public SavedPlace SaveHere(string name, string note)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            var me = capi.World?.Player?.Entity?.Pos;
            if (me == null) return null;

            var place = new SavedPlace
            {
                Name = name,
                Note = note?.Trim() is { Length: > 0 } n ? n : null,
                X = me.X, Y = me.Y, Z = me.Z,
                Day = capi.World.Calendar?.TotalDays,
                // Checked from birth, like an adopted errand: you saved it because you
                // want it in view. Unchecking parks it.
                ShowOnHud = true,
            };
            store.Places.Add(place);

            if (config.QuestWaypoints)
            {
                waypoints.PlaceMarker(config.PlaceWaypointIcon, config.PlaceWaypointColor,
                    config.QuestWaypointPinned, place.X, place.Y, place.Z, place.Name);
                place.WaypointPlaced = true;
            }
            store.Save();
            return place;
        }

        /// <summary>
        /// Write the editor window's result onto a place. A rename replaces the marker —
        /// its title IS the name, so the old marker goes onto the removal queue and a new
        /// one is planted, same transition discipline as everywhere else. A blanked name
        /// keeps the old one: a nameless place cannot be told apart anywhere.
        /// </summary>
        public void Apply(SavedPlace place, string name, string note, string notesText)
        {
            if (place == null) return;
            name = name?.Trim();
            note = note?.Trim();

            if (!string.IsNullOrEmpty(name) && name != place.Name)
            {
                if (place.WaypointPlaced)
                {
                    QueueRemoval(place);
                    place.WaypointPlaced = false;
                }
                place.Name = name;
                if (config.QuestWaypoints)
                {
                    waypoints.PlaceMarker(config.PlaceWaypointIcon, config.PlaceWaypointColor,
                        config.QuestWaypointPinned, place.X, place.Y, place.Z, place.Name);
                    place.WaypointPlaced = true;
                }
            }

            place.Note = string.IsNullOrEmpty(note) ? null : note;
            place.NotesText = string.IsNullOrWhiteSpace(notesText) ? null : notesText;
            store.Save();
        }

        /// <summary>Forget a place. Its marker goes onto the retry queue — sent now if the
        /// map answers, later if it does not — and the notes go with the place, which is
        /// why the button asks twice.</summary>
        public void Remove(SavedPlace place)
        {
            if (place == null) return;
            if (place.WaypointPlaced) QueueRemoval(place);
            store.Places.Remove(place);
            store.Save();
        }

        void QueueRemoval(SavedPlace place)
        {
            string entry = string.Create(CultureInfo.InvariantCulture,
                $"{place.Name}|{place.X:0.#}|{place.Y:0.#}|{place.Z:0.#}");
            if (!store.PlaceRemovals.Contains(entry)) store.PlaceRemovals.Add(entry);
        }

        /// <summary>
        /// The 1s tick: work the removal queue, and follow the master waypoint option's
        /// transitions — flag-driven both ways, so a flip acts exactly once per place and a
        /// failed read can never cause a second placement. Returns true when anything
        /// user-visible moved; the caller saves and recounts.
        /// </summary>
        public bool Tick()
        {
            bool changed = false;
            try
            {
                for (int i = store.PlaceRemovals.Count - 1; i >= 0; i--)
                {
                    var parts = store.PlaceRemovals[i].Split('|');
                    if (parts.Length != 4
                        || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                        || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                        || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)
                        || waypoints.TryRemoveMarker(parts[0], x, y, z))
                    {
                        store.PlaceRemovals.RemoveAt(i);
                        changed = true;
                    }
                }

                foreach (var place in store.Places)
                {
                    if (config.QuestWaypoints && !place.WaypointPlaced)
                    {
                        waypoints.PlaceMarker(config.PlaceWaypointIcon, config.PlaceWaypointColor,
                            config.QuestWaypointPinned, place.X, place.Y, place.Z, place.Name);
                        place.WaypointPlaced = true;
                        changed = true;
                    }
                    else if (!config.QuestWaypoints && place.WaypointPlaced)
                    {
                        QueueRemoval(place);
                        place.WaypointPlaced = false;
                        changed = true;
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] place markers failed: {0}", e.Message);
            }
            return changed;
        }

        /// <summary>Everything the surfaces draw from places, for the shared change
        /// signature: identity, HUD membership, note counts. Distances stay out — walking
        /// must not redraw the dialog every step, same rule as the Player tab.</summary>
        public string Signature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var place in store.Places)
            {
                sb.Append(place.Key).Append(place.ShowOnHud ? '+' : '-')
                  .Append(place.NotesExpanded ? 'o' : 'c')
                  .Append(place.NotesText?.Length ?? 0).Append(',');
            }
            return sb.ToString();
        }
    }
}
