using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Tallybook
{
    /// <summary>
    /// Persisted spawn-tracking state (SaveFile.Spawn). Positions are absolute world
    /// coordinates; converting to the spawn-relative numbers players see happens at the
    /// display and command seams, same as everywhere else.
    /// </summary>
    public class SpawnState
    {
        /// <summary>The player's temporal-gear returning point, all-zero when none known.</summary>
        public double TempX, TempY, TempZ;

        /// <summary>temporalGearRespawnUses the moment the point appeared. -2: the point was
        /// already set before Tallybook was watching, so the budget is unknowable. -1: the
        /// server grants unlimited uses.</summary>
        public int UsesAtSet = -2;

        /// <summary>The synced death counter the moment the point appeared — spent uses are
        /// deaths since then.</summary>
        public int DeathsAtSet;

        public bool TempWaypointPlaced;
        public double TempWpX, TempWpY, TempWpZ;

        public bool HomeWaypointPlaced;
        public double HomeWpX, HomeWpY, HomeWpZ;

        /// <summary>"x,z" of a returning point our arithmetic says has expired. The server
        /// clears an expired point without re-sending player data, so the client's synced
        /// copy stays stale until relog — without this latch the stale packet would be
        /// re-adopted as a fresh point every second, replanting the marker we just removed.</summary>
        public string ExpiredLatch;

        /// <summary>Whether the tracker has ever looked at this world. A returning point
        /// that already exists at the very first look predates the watching, so its budget
        /// is unknowable — only a point that appears or moves WHILE we watch gets stamped
        /// with the config value the gear just wrote into it.</summary>
        public bool Observed;

        /// <summary>Markers owed a removal, as "title|x|y|z". The waypoint list is known to
        /// read back empty at random, and a removal attempted against a failed read removes
        /// nothing — which left the old marker standing when a returning point moved. Each
        /// entry is retried until a SUCCESSFUL read either removes the marker or proves it
        /// already gone; retrying cannot spam, because a remove command is only ever sent
        /// at a marker actually found.</summary>
        public List<string> PendingRemovals = new List<string>();

        /// <summary>Deaths in this world, monotonic and persisted: raised to the synced
        /// counter whenever that is credible (&gt;0), raised by each own death observed
        /// live, never lowered. Monotonic because the synced field CANNOT be trusted
        /// downward — the game has a packet variant that never fills it, so a zero can
        /// arrive at any time and means nothing (found by Mark: deaths read 0, and the
        /// synced spawn read the world corner, off the same sparse packet).</summary>
        public int DeathsSeen;
    }

    /// <summary>
    /// Watches the player's own spawn, all from data the server actually syncs
    /// (decompile-verified 1.22.6):
    ///
    /// - The player's spawn point rides Packet_PlayerData (Spawnx/y/z) into the public
    ///   ClientPlayer.SpawnPosition property — the personal (temporal gear / admin) point
    ///   when one is set, else the world default spawn's centre. It refreshes the moment a
    ///   point is SET (SetSpawnPosition → SendOwnPlayerData) — but NOT when one is consumed
    ///   or expires: the respawn path decrements server-side and sends only a chat line. So
    ///   "is my point still there" is answered by arithmetic, not by the packet:
    /// - Uses left = temporalGearRespawnUses at set-time minus deaths since set-time.
    ///   IWorldPlayerData.Deaths is synced (server-persisted per world — deaths since the
    ///   player's first day), and the gear stamps RemainingUses with that config value, so
    ///   this mirrors the server's own bookkeeping exactly unless an admin intervenes.
    ///   The stamps only exist from the moment we saw the point appear — a point that
    ///   predates the mod shows "unknown" rather than a guess.
    ///
    /// Map markers follow the errand-marker contract to the letter: placed and removed on
    /// transitions only, remembered in persisted flags, never reconciled against a live map
    /// read, and gated on the same "never touch my waypoints" option as everything else.
    /// </summary>
    public class SpawnTracker
    {
        public const string HomeTitle = "World spawn";
        public const string TempTitle = "Returning point";

        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly PinStore store;
        readonly QuestWaypoints waypoints;

        PropertyInfo spawnPosProp;

        /// <summary>Same contract as QuestWaypoints.Ready: nothing is read or sent before
        /// the store has loaded — outward actions taken against a half-loaded world spam.</summary>
        public bool Ready { get; set; }

        public SpawnTracker(ICoreClientAPI capi, TallybookConfig config, PinStore store,
                            QuestWaypoints waypoints)
        {
            this.capi = capi;
            this.config = config;
            this.store = store;
            this.waypoints = waypoints;

            // The synced death counter cannot be watched passively: it is written only by
            // FULL player-data packets, the death broadcast never touches it, and a sparse
            // packet variant can zero it at any moment (see SpawnState.DeathsSeen). Own
            // deaths are counted from the event as they happen instead.
            capi.Event.PlayerDeath += OnPlayerDeath;
        }

        void OnPlayerDeath(IClientPlayer byPlayer)
        {
            try
            {
                if (byPlayer?.PlayerUID != null
                    && byPlayer.PlayerUID == capi.World?.Player?.PlayerUID)
                {
                    State.DeathsSeen++;
                    deathsDirty = true;   // persisted by the next tick's save
                }
            }
            catch { }
        }

        bool deathsDirty;

        public SpawnState State => store.Spawn;

        public bool HasTemp => State.TempX != 0 || State.TempY != 0 || State.TempZ != 0;

        /// <summary>Total deaths in this world, as reliably as a client can know it: the
        /// persisted monotonic counter. The synced value ratchets it up when credible; a
        /// death observed live bumps it immediately; a clobbered zero changes nothing.
        /// The two sources cannot double-count — the ratchet is a max, so a synced total
        /// that already contains an observed death does not add to it.</summary>
        public int Deaths
        {
            get
            {
                int raw = capi.World?.Player?.WorldData?.Deaths ?? 0;
                if (raw > State.DeathsSeen) { State.DeathsSeen = raw; deathsDirty = true; }
                return State.DeathsSeen;
            }
        }

        /// <summary>Spawns left at the returning point: null = unknown (point predates the
        /// mod), int.MaxValue = unlimited, else the estimate. Never negative.</summary>
        public int? UsesLeft()
        {
            if (!HasTemp) return null;
            var st = State;
            if (st.UsesAtSet == -2) return null;
            if (st.UsesAtSet < 0) return int.MaxValue;
            return Math.Max(0, st.UsesAtSet - Math.Max(0, Deaths - st.DeathsAtSet));
        }

        /// <summary>The synced spawn point — reflection because the property lives on the
        /// concrete NoObf ClientPlayer, not on IPlayer. A failed read is "don't know" and
        /// changes nothing, per the standing rule.</summary>
        BlockPos SyncedSpawn()
        {
            try
            {
                var player = capi.World?.Player;
                if (player == null) return null;
                spawnPosProp ??= player.GetType().GetProperty("SpawnPosition");
                return spawnPosProp?.GetValue(player) as BlockPos;
            }
            catch { return null; }
        }

        /// <summary>One pass on the 1s tick. Returns true when anything user-visible moved —
        /// the caller recounts, which is what redraws the tab and the HUD.</summary>
        public bool Update()
        {
            try
            {
                if (!Ready) return false;
                var st = State;
                bool changed = ProcessPendingRemovals(st);

                var def = capi.World?.DefaultSpawnPosition?.XYZ;
                if (def == null) { if (changed) store.Save(); return changed; }
                var synced = SyncedSpawn();
                int deaths = Deaths;

                if (deathsDirty) { deathsDirty = false; changed = true; }

                bool firstLook = !st.Observed;
                st.Observed = true;
                if (firstLook)
                {
                    changed = true;   // Observed itself is worth persisting
                    // Stamps recorded before this flag existed came from a build that
                    // wrongly gave pre-existing points a fresh budget — same unknowability,
                    // same answer.
                    if (HasTemp) st.UsesAtSet = -2;
                }

                // A synced spawn is CREDIBLE only when it is present and not the world
                // corner: the game has a sparse packet variant that never fills the spawn
                // fields, so BlockPos(0,0,0) arrives meaning nothing (found by Mark —
                // `.tallybook spawn` read "-512372, 0, -513987", the corner seen from the
                // map middle). A non-credible read proves NOTHING: classification, adoption
                // and clearing all wait for a real one, and the tracked point stands.
                bool credible = synced != null && !(synced.X == 0 && synced.Z == 0);
                if (credible)
                {
                    // The packet carries the personal point when set, else the default centre.
                    bool syncedIsTemp = synced.X != (int)def.X || synced.Z != (int)def.Z;
                    string latchKey = synced.X.ToString(CultureInfo.InvariantCulture) + ","
                        + synced.Z.ToString(CultureInfo.InvariantCulture);
                    if (syncedIsTemp && st.ExpiredLatch == latchKey) syncedIsTemp = false;

                    if (syncedIsTemp)
                    {
                        bool moved = (int)st.TempX != synced.X || (int)st.TempZ != synced.Z;
                        if (!HasTemp || moved)
                        {
                            // New point, or the old one moved: the stamps restart here. The
                            // budget is only knowable for a point that appeared WHILE we were
                            // watching — the config value the gear wrote into it a second
                            // ago. A point already standing at our first-ever look predates
                            // the watching, and its budget honestly reads "not known".
                            changed |= RemoveTempMarker(st);
                            st.TempX = synced.X; st.TempY = synced.Y; st.TempZ = synced.Z;
                            st.UsesAtSet = firstLook
                                ? -2
                                : capi.World.Config?.GetAsInt("temporalGearRespawnUses", -1) ?? -1;
                            st.DeathsAtSet = deaths;
                            st.ExpiredLatch = null;
                            changed = true;
                        }
                    }
                    else if (HasTemp)
                    {
                        // A credible read says default: expiry seen after a relog, or an
                        // admin cleared it. The point is gone and the marker goes with it.
                        changed |= ClearTemp(st);
                    }
                }

                // Expiry arithmetic runs on the TRACKED point whatever the packet said —
                // the packet does not refresh on use, so counting deaths against the budget
                // is the only way an expiry is ever noticed before the next login.
                if (HasTemp)
                {
                    int? left = UsesLeft();
                    if (left.HasValue && left.Value == 0)
                    {
                        // The server already knows; it just never told this client.
                        // Latch first — see ExpiredLatch.
                        st.ExpiredLatch = ((int)st.TempX).ToString(CultureInfo.InvariantCulture)
                            + "," + ((int)st.TempZ).ToString(CultureInfo.InvariantCulture);
                        changed |= ClearTemp(st);
                    }
                }

                // State tracking above runs even with the tab switched off — a returning
                // point set and part-spent while nobody was looking must not read as
                // fresh when the tab comes back. Only the markers are gated: on the tab's
                // own option, and on the "Tallybook never touches your waypoints" master.
                bool markers = config.ShowPlayerTab && config.QuestWaypoints;
                if (markers)
                {
                    if (!st.HomeWaypointPlaced)
                    {
                        waypoints.PlaceMarker(config.SpawnWaypointIcon, config.SpawnWaypointColor,
                            config.QuestWaypointPinned, def.X, def.Y, def.Z, HomeTitle);
                        st.HomeWpX = def.X; st.HomeWpY = def.Y; st.HomeWpZ = def.Z;
                        st.HomeWaypointPlaced = true;
                        changed = true;
                    }
                    else if ((int)st.HomeWpX != (int)def.X || (int)st.HomeWpZ != (int)def.Z)
                    {
                        // The world spawn itself moved (admin /setspawn) — follow it.
                        QueueRemoval(st, HomeTitle, st.HomeWpX, st.HomeWpY, st.HomeWpZ);
                        waypoints.PlaceMarker(config.SpawnWaypointIcon, config.SpawnWaypointColor,
                            config.QuestWaypointPinned, def.X, def.Y, def.Z, HomeTitle);
                        st.HomeWpX = def.X; st.HomeWpY = def.Y; st.HomeWpZ = def.Z;
                        changed = true;
                    }

                    if (HasTemp && !st.TempWaypointPlaced)
                    {
                        waypoints.PlaceMarker(config.SpawnWaypointIcon, config.SpawnWaypointColor,
                            config.QuestWaypointPinned, st.TempX, st.TempY, st.TempZ, TempTitle);
                        st.TempWpX = st.TempX; st.TempWpY = st.TempY; st.TempWpZ = st.TempZ;
                        st.TempWaypointPlaced = true;
                        changed = true;
                    }
                }
                else
                {
                    changed |= RemoveHomeMarker(st);
                    changed |= RemoveTempMarker(st);
                }

                if (changed) store.Save();
                return changed;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] spawn tracking failed: {0}", e.Message);
                return false;
            }
        }

        bool ClearTemp(SpawnState st)
        {
            bool changed = RemoveTempMarker(st);
            if (HasTemp) changed = true;
            st.TempX = st.TempY = st.TempZ = 0;
            st.UsesAtSet = -2;
            st.DeathsAtSet = 0;
            return changed;
        }

        bool RemoveTempMarker(SpawnState st)
        {
            if (!st.TempWaypointPlaced) return false;
            st.TempWaypointPlaced = false;
            QueueRemoval(st, TempTitle, st.TempWpX, st.TempWpY, st.TempWpZ);
            return true;
        }

        bool RemoveHomeMarker(SpawnState st)
        {
            if (!st.HomeWaypointPlaced) return false;
            st.HomeWaypointPlaced = false;
            QueueRemoval(st, HomeTitle, st.HomeWpX, st.HomeWpY, st.HomeWpZ);
            return true;
        }

        static void QueueRemoval(SpawnState st, string title, double x, double y, double z)
        {
            string entry = string.Create(CultureInfo.InvariantCulture, $"{title}|{x:0.#}|{y:0.#}|{z:0.#}");
            if (!st.PendingRemovals.Contains(entry)) st.PendingRemovals.Add(entry);
        }

        /// <summary>Work the removal queue: each entry stays until a successful map read
        /// settles it — either the marker was found and a remove sent, or a good read
        /// proved it absent. See SpawnState.PendingRemovals for why this cannot spam.</summary>
        bool ProcessPendingRemovals(SpawnState st)
        {
            if (st.PendingRemovals == null || st.PendingRemovals.Count == 0) return false;
            bool changed = false;
            for (int i = st.PendingRemovals.Count - 1; i >= 0; i--)
            {
                var parts = st.PendingRemovals[i].Split('|');
                if (parts.Length != 4
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                {
                    st.PendingRemovals.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (waypoints.TryRemoveMarker(parts[0], x, y, z))
                {
                    st.PendingRemovals.RemoveAt(i);
                    changed = true;
                }
            }
            return changed;
        }


        /// <summary>Every layer, side by side, for `.tallybook spawn` — when a surface
        /// disagrees with the world, this says which layer diverged: the packet, the
        /// tracking, the arithmetic, or the markers. Coordinates print spawn-relative,
        /// the same numbers the coordinate HUD shows (the standing rule).</summary>
        public string Diagnose()
        {
            var sb = new System.Text.StringBuilder();
            var def = capi.World?.DefaultSpawnPosition?.XYZ;
            var synced = SyncedSpawn();
            var st = State;
            double sx = def?.X ?? 0, sz = def?.Z ?? 0;
            string Rel(double x, double y, double z) => string.Create(CultureInfo.InvariantCulture,
                $"{(int)(x - sx)}, {(int)y}, {(int)(z - sz)}");

            sb.AppendLine(def == null
                ? "Default spawn: unknown"
                : $"Default spawn: {Rel(def.X, def.Y, def.Z)}");
            if (synced == null)
            {
                sb.AppendLine(spawnPosProp == null
                    ? "Synced spawn: UNREADABLE — SpawnPosition not found on the player "
                      + "object (a game update may have moved it)"
                    : "Synced spawn: empty — no player data received yet");
            }
            else if (synced.X == 0 && synced.Z == 0)
            {
                sb.AppendLine("Synced spawn: (0,0) — the world corner, i.e. a packet that "
                    + "never filled the field. Ignored; the tracked point stands.");
            }
            else
            {
                bool temp = def != null && ((int)synced.X != (int)def.X || (int)synced.Z != (int)def.Z);
                sb.AppendLine($"Synced spawn: {Rel(synced.X, synced.Y, synced.Z)} — "
                    + (temp ? "a personal point" : "the world default")
                    + " (refreshes on set and at login, not on use)");
            }
            sb.AppendLine(HasTemp
                ? $"Tracked returning point: {Rel(st.TempX, st.TempY, st.TempZ)} — budget "
                    + (st.UsesAtSet == -2 ? "not known" : st.UsesAtSet < 0 ? "unlimited" : st.UsesAtSet.ToString())
                    + $", deaths when set {st.DeathsAtSet}, left {(UsesLeft() is int l ? l == int.MaxValue ? "unlimited" : l.ToString() : "?")}"
                : "Tracked returning point: none");
            int raw = capi.World?.Player?.WorldData?.Deaths ?? 0;
            sb.AppendLine($"Deaths: synced field {raw} (a 0 here can be a sparse packet, "
                + $"not a fact), recorded {Deaths}");
            sb.AppendLine($"Config: temporalGearRespawnUses {capi.World?.Config?.GetAsInt("temporalGearRespawnUses", -1)}, "
                + $"playerlives {capi.World?.Config?.GetAsInt("playerlives", -1)}");
            sb.AppendLine($"Markers: home {(st.HomeWaypointPlaced ? "placed" : "none")}, "
                + $"returning {(st.TempWaypointPlaced ? "placed" : "none")}, "
                + $"pending removals {st.PendingRemovals?.Count ?? 0}");
            if (st.ExpiredLatch != null)
                sb.AppendLine($"Expired latch held at absolute x,z {st.ExpiredLatch}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Everything the Player tab draws from this tracker, flattened for the
        /// shared change signature — a point appearing, expiring, or a death must redraw.</summary>
        public string Signature()
        {
            var st = State;
            return string.Create(CultureInfo.InvariantCulture,
                $"{(int)st.TempX},{(int)st.TempZ},{UsesLeft() ?? -9},{Deaths},{st.TempWaypointPlaced},{st.HomeWaypointPlaced}");
        }
    }
}
