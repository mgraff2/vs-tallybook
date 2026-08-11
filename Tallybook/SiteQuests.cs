using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// One map-artifact destination adopted as a side quest: "Visit the Abandoned Mine",
    /// or "The Sunrift Experiment — 5/17 writings". Created from a waypoint the player's own
    /// map-reading planted, so nothing here is a spoiler: the game already told them this
    /// place exists, in ink.
    /// </summary>
    public class SiteQuest
    {
        /// <summary>Title plus rounded position. Two "Abandoned Mine" maps point at two
        /// different mines (mine1/mine2 share a waypoint title), and each is its own walk.</summary>
        public string Key;

        public string Title;

        /// <summary>The waypoint's position at adoption — absolute world coordinates, same
        /// as entity positions. Captured once, because the live waypoint read this came from
        /// fails intermittently and the player may later delete the marker; the quest still
        /// knows the way.</summary>
        public double X, Y, Z;

        /// <summary>The locator-map site codes whose waypoint title this is ("mine1","mine2").
        /// What the lore scan keys on.</summary>
        public List<string> SiteCodes = new List<string>();

        public bool Visited;

        /// <summary>Lore codes proven found — journal entries or a carried writing. Monotonic
        /// and persisted: the writing is consumed when read and the journal read can fail,
        /// and a fact observed once must never un-happen (same rule as story progress).</summary>
        public List<string> LoreFound = new List<string>();

        /// <summary>Set aside by the player. Kept, not deleted — the offered-once guard means
        /// a dismissed site cannot come back by itself, so this is the way back.</summary>
        public bool Dismissed;

        /// <summary>Unchecked site quests stay on the tab but leave the HUD and stop
        /// announcing themselves — the same parking contract every pin has. Facts still
        /// latch while parked (arrival, a found writing): knowledge is not a preference.</summary>
        public bool Active = true;

        /// <summary>Is the found-writings list open under the row? Remembered like an
        /// errand's conversation toggle.</summary>
        public bool TextExpanded;

        [JsonIgnore] public string GroupKey => SiteLoreScan.GroupKey(SiteCodes ?? new List<string>());
    }

    /// <summary>A destination locator maps in this world can mark: the waypoint title they
    /// stamp, the site codes behind it, and a sample map stack for the row icon.</summary>
    public class LocatorSite
    {
        public string Title;
        public List<string> SiteCodes = new List<string>();
        public string Icon = "x";
        public ItemStack SampleStack;
    }

    /// <summary>
    /// Map artifacts as side quests. A locator map (vanilla treasure maps, Better Ruins'
    /// ruin maps — anything using the game's locatorProps convention) plants a titled
    /// waypoint when read; this adopts that waypoint as a trackable quest: a visit step
    /// proven by standing there, and — where the lore scan shows writings that exist only
    /// at that site — a collection step counted against them.
    ///
    /// Derivation, not configuration: sites come from the item registry (every item carrying
    /// locatorProps), titles from the same Lang keys the game titles the waypoints with, and
    /// lore from the schematic scan. No mod is named anywhere, which is the §1 promise again.
    ///
    /// Deliberately NOT adopted: waypoints whose titles the story tracker already watches
    /// (the archives, the Devastation, Tobias' cave…) — those walks belong to the story
    /// block, and a second row saying "visit the Devastation" would be the same fact twice.
    /// </summary>
    public class SiteQuests
    {
        /// <summary>How close counts as having been there, horizontally. Site waypoints mark
        /// the structure's centre and the big ruins are themselves tens of blocks across, so
        /// this is "stood at the site", not "touched the centre block". Y is ignored — the
        /// centre of an underground complex can be forty blocks under the doormat.</summary>
        public const int VisitRadius = 64;

        readonly ICoreClientAPI capi;
        readonly TallyService svc;
        readonly QuestWaypoints waypoints;
        public readonly SiteLoreScan Scan;

        Dictionary<string, LocatorSite> catalogue;   // by title
        HashSet<string> storyTitles;
        bool journalWarned;

        public SiteQuests(ICoreClientAPI capi, TallyService svc, QuestWaypoints waypoints)
        {
            this.capi = capi;
            this.svc = svc;
            this.waypoints = waypoints;
            Scan = new SiteLoreScan(capi);
        }

        public void InvalidateWorld()
        {
            catalogue = null;
            storyTitles = null;
            Scan.InvalidateWorld();
        }

        // ---- the catalogue -------------------------------------------------------------

        /// <summary>
        /// Every locator-map destination this world's items can mark, grouped by the waypoint
        /// title they stamp — the exact string ItemLocatorMap writes: Lang.Get of the item's
        /// waypointtext (decompile-verified 1.22.6). Grouping by title is deliberate: the
        /// waypoint is all we ever see, and two map items titling their waypoint identically
        /// are indistinguishable from it.
        /// </summary>
        public Dictionary<string, LocatorSite> Catalogue()
        {
            if (catalogue != null) return catalogue;

            var sites = new Dictionary<string, LocatorSite>(StringComparer.Ordinal);
            try
            {
                foreach (var item in capi.World.Items)
                {
                    var props = item?.Attributes?["locatorProps"];
                    if (props == null || !props.Exists) continue;

                    string code = props["schematiccode"]?.AsString();
                    string textKey = props["waypointtext"]?.AsString();
                    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(textKey)) continue;

                    string title = Lang.Get(textKey);
                    if (string.IsNullOrEmpty(title)) title = textKey;

                    if (!sites.TryGetValue(title, out var site))
                    {
                        sites[title] = site = new LocatorSite
                        {
                            Title = title,
                            Icon = props["waypointicon"]?.AsString() ?? "x",
                            SampleStack = new ItemStack(item)
                        };
                    }
                    if (!site.SiteCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                        site.SiteCodes.Add(code);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read locator maps: {0}", e.Message);
                return sites;   // not cached — a partial read gets another chance next tick
            }

            catalogue = sites;
            return sites;
        }

        HashSet<string> StoryTitles()
        {
            if (storyTitles != null) return storyTitles;
            storyTitles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in StoryProgress.StoryLocationLangKeys)
            {
                string title = Lang.Get(key);
                if (!string.IsNullOrEmpty(title) && title != key) storyTitles.Add(title);
            }
            return storyTitles;
        }

        // ---- the tick ------------------------------------------------------------------

        /// <summary>
        /// Adopt new locator waypoints, notice arrivals, count writings, archive what is
        /// finished. Returns whether anything changed — the caller owns the save and the
        /// recount, same contract as the quest history update.
        /// </summary>
        public bool Tick()
        {
            bool changed = false;
            try
            {
                var sites = Catalogue();
                // Only a complete catalogue may seed the scan (a partial read is retried,
                // not cached) — and two titles can share one site code (vanilla's treasure
                // and dungeon maps both mark buried treasure chests), so groups are deduped
                // rather than assumed unique.
                if (catalogue != null && sites.Count > 0)
                {
                    var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var s in sites.Values)
                    {
                        string key = SiteLoreScan.GroupKey(s.SiteCodes);
                        if (!groups.ContainsKey(key)) groups[key] = s.SiteCodes;
                    }
                    Scan.EnsureStarted(groups);
                }

                if (Adopt(sites)) changed = true;
                if (Visits()) changed = true;
                if (LoreProgress()) changed = true;
                if (Completions()) changed = true;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] site quest tick failed: {0}", e.Message);
            }
            return changed;
        }

        /// <summary>
        /// A locator waypoint the player has and we have never offered becomes a quest.
        /// Offered once ever per waypoint (title + position), for the same reason errands
        /// are: this runs every tick, and a dismissed site returning would be its own bug.
        /// The waypoint read fails intermittently — a failed read adopts nothing and is
        /// indistinguishable from "no new maps read", which is exactly the safe behaviour.
        /// </summary>
        bool Adopt(Dictionary<string, LocatorSite> sites)
        {
            if (sites.Count == 0) return false;
            var wps = waypoints?.TryReadOwnWaypoints();
            if (wps == null) return false;

            bool changed = false;
            foreach (var wp in wps)
            {
                string title = wp?.Title;
                if (string.IsNullOrEmpty(title) || wp.Position == null) continue;
                if (!sites.TryGetValue(title, out var site)) continue;
                if (!string.Equals(wp.Icon, site.Icon, StringComparison.OrdinalIgnoreCase)) continue;
                if (StoryTitles().Contains(title)) continue;

                string key = $"{title}@{(int)wp.Position.X},{(int)wp.Position.Z}";
                if (!svc.Store.OfferedSites.Add(key)) continue;

                svc.Store.SiteQuests.Add(new SiteQuest
                {
                    Key = key,
                    Title = title,
                    X = wp.Position.X,
                    Y = wp.Position.Y,
                    Z = wp.Position.Z,
                    SiteCodes = site.SiteCodes.ToList()
                });
                changed = true;
                capi.ShowChatMessage(
                    $"Tallybook: tracking your map to {title} as a side quest. Press L to see it.");
            }
            return changed;
        }

        bool Visits()
        {
            var me = capi.World?.Player?.Entity?.Pos;
            if (me == null) return false;

            bool changed = false;
            foreach (var sq in svc.Store.SiteQuests)
            {
                if (sq.Visited || sq.Dismissed) continue;
                double dx = me.X - sq.X, dz = me.Z - sq.Z;
                if (dx * dx + dz * dz > (double)VisitRadius * VisitRadius) continue;

                sq.Visited = true;
                changed = true;
                // A parked quest still learns the fact, quietly — announcing what the
                // player set aside would be the mod talking over them.
                if (sq.Active) capi.ShowChatMessage($"Tallybook: you have reached {sq.Title}.");
            }
            return changed;
        }

        /// <summary>
        /// Writings proven found: a journal entry with the lore code (reading the writing
        /// consumes it into the journal, so the journal is the durable record), or a carried
        /// copy — its discoveryCode once read, its category while still sealed. Everything
        /// lands in the persisted LoreFound latch; a later failed read subtracts nothing.
        /// </summary>
        bool LoreProgress()
        {
            if (!Scan.Ready) return false;
            var open = svc.Store.SiteQuests.Where(s => !s.Dismissed).ToList();
            if (open.Count == 0) return false;

            HashSet<string> journal = null;
            List<(string Code, string Category)> carried = null;

            bool changed = false;
            foreach (var sq in open)
            {
                var lore = Scan.ExclusiveLoreFor(sq.GroupKey);
                if (lore == null || lore.Count == 0) continue;
                if (sq.LoreFound.Count >= lore.Count
                    && lore.All(d => sq.LoreFound.Contains(d.Code, StringComparer.OrdinalIgnoreCase))) continue;

                journal ??= JournalLoreCodes();
                carried ??= CarriedLoreStamps();

                foreach (var def in lore)
                {
                    if (sq.LoreFound.Contains(def.Code, StringComparer.OrdinalIgnoreCase)) continue;

                    bool found = journal.Contains(def.Code)
                        || carried.Any(c => string.Equals(c.Code, def.Code, StringComparison.OrdinalIgnoreCase));
                    // A sealed writing only names its category. That credits a specific code
                    // only where the category holds exactly one — true of every site writing
                    // seen so far, and anything looser would be a guess.
                    if (!found && carried.Any(c => string.Equals(c.Category, def.Category, StringComparison.OrdinalIgnoreCase))
                        && lore.Count(d => string.Equals(d.Category, def.Category, StringComparison.OrdinalIgnoreCase)) == 1)
                    {
                        found = true;
                    }
                    if (!found) continue;

                    sq.LoreFound.Add(def.Code);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// A visited site with every provable writing recovered is finished: archived to
        /// History and off the list, exactly like a handed-in errand. Completion waits for
        /// the scan — before it lands we cannot distinguish "nothing to collect here" from
        /// "don't know yet", and archiving on a don't-know would close a quest that still
        /// has 17 writings in the ground.
        /// </summary>
        bool Completions()
        {
            if (!Scan.Ready) return false;

            bool changed = false;
            foreach (var sq in svc.Store.SiteQuests.ToList())
            {
                if (!sq.Visited || sq.Dismissed) continue;

                var lore = Scan.ExclusiveLoreFor(sq.GroupKey);
                if (lore == null) continue;
                if (!lore.All(d => sq.LoreFound.Contains(d.Code, StringComparer.OrdinalIgnoreCase))) continue;

                var text = new List<string> { $"Marked on your map by a map artifact. Reached {sq.Title}." };
                foreach (var def in lore.OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase))
                {
                    text.Add($"Recovered: {def.Title}");
                }
                if (lore.Count > 0) text.Add($"All {lore.Count} writings hidden there, recovered.");

                svc.Store.QuestHistory.Add(new QuestRecord
                {
                    Chain = "site:" + sq.Key,
                    Name = sq.Title,
                    Stage = "completed",
                    Day = capi.World.Calendar?.TotalDays,
                    Text = text
                });
                svc.Store.SiteQuests.Remove(sq);
                changed = true;
                capi.ShowChatMessage($"Tallybook: {sq.Title} — done. The story is in your History tab.");
            }
            return changed;
        }

        // ---- what the surfaces ask -----------------------------------------------------

        /// <summary>Found / total for a lore site, or null while the scan has no answer (a
        /// site with provably nothing to collect returns (0,0)). The dialog draws only from
        /// this — never from a live read of anything.</summary>
        public (int Found, int Total)? LoreCount(SiteQuest sq)
        {
            var lore = Scan.ExclusiveLoreFor(sq.GroupKey);
            if (lore == null) return null;
            int found = lore.Count(d => sq.LoreFound.Contains(d.Code, StringComparer.OrdinalIgnoreCase));
            return (found, lore.Count);
        }

        /// <summary>Display titles of the writings already found — and only those. The
        /// unfound ones' titles stay hidden: the count is progress, the names are content
        /// the site has not given up yet.</summary>
        public List<string> FoundLoreTitles(SiteQuest sq)
        {
            var lore = Scan.ExclusiveLoreFor(sq.GroupKey);
            if (lore == null) return new List<string>();
            return lore.Where(d => sq.LoreFound.Contains(d.Code, StringComparer.OrdinalIgnoreCase))
                       .Select(d => d.Title)
                       .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        public ItemStack SampleStackFor(SiteQuest sq)
        {
            var sites = Catalogue();
            return sites.TryGetValue(sq.Title ?? "", out var site) ? site.SampleStack : null;
        }

        /// <summary>"Visit the Abandoned Mine" — without doubling an article the title
        /// already carries.</summary>
        public static string VisitPhrase(string title)
            => title != null && title.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                ? $"Visit {title}"
                : $"Visit the {title}";

        /// <summary>Site quests that belong on the HUD: tracked, checked, not set aside.</summary>
        public List<SiteQuest> HudSites()
            => svc.Store.SiteQuests.Where(s => s != null && !s.Dismissed && s.Active).ToList();

        /// <summary>Everything visible about site quests, for the shared change signature:
        /// adoption, dismissal, parking, arrival, every count move, and the scan landing.</summary>
        public string Signature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("g").Append(Scan.Generation);
            foreach (var sq in svc.Store.SiteQuests)
            {
                sb.Append('|').Append(sq.Key)
                  .Append(sq.Visited ? 'V' : 'v')
                  .Append(sq.Dismissed ? 'D' : 'd')
                  .Append(sq.Active ? 'A' : 'a')
                  .Append(sq.TextExpanded ? 'E' : 'e')
                  .Append(':').Append(sq.LoreFound?.Count ?? 0);
            }
            return sb.ToString();
        }

        /// <summary>Diagnostic tie-out for `.tallybook sites`: what the catalogue derived,
        /// what the scan concluded, and where every quest stands.</summary>
        public List<string> Report()
        {
            var lines = new List<string>();
            var sites = Catalogue();

            lines.Add($"{sites.Count} locator-map destination(s) known to this world's items:");
            foreach (var site in sites.Values.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase))
            {
                string story = StoryTitles().Contains(site.Title) ? " [story — not adopted]" : "";
                var lore = Scan.ExclusiveLoreFor(SiteLoreScan.GroupKey(site.SiteCodes));
                string loreNote = !Scan.Ready ? "lore scan running…"
                    : lore == null || lore.Count == 0 ? "no site-exclusive writings"
                    : $"{lore.Count} site-exclusive writing(s)";
                lines.Add($"  {site.Title} ({string.Join(", ", site.SiteCodes)}) — {loreNote}{story}");
            }

            lines.Add(Scan.Ready
                ? $"Lore scan: {Scan.SchematicsScanned} schematic(s) considered{(Scan.FromCache ? " (cached)" : "")}."
                : "Lore scan: still running — counts appear when it finishes.");

            if (svc.Store.SiteQuests.Count == 0)
            {
                lines.Add("No site quests tracked. Read a locator map (right-click) to mark one.");
                return lines;
            }

            lines.Add($"{svc.Store.SiteQuests.Count} site quest(s):");
            foreach (var sq in svc.Store.SiteQuests)
            {
                var count = LoreCount(sq);
                string progress = count == null ? "lore unknown"
                    : count.Value.Total == 0 ? "visit only"
                    : $"{count.Value.Found}/{count.Value.Total} writings";
                lines.Add($"  {sq.Title} at {(int)(sq.X - (capi.World?.DefaultSpawnPosition?.XYZ.X ?? 0))}," +
                          $"{(int)(sq.Z - (capi.World?.DefaultSpawnPosition?.XYZ.Z ?? 0))}" +
                          $" — {(sq.Visited ? "visited" : "not visited")}, {progress}" +
                          $"{(sq.Dismissed ? " [dismissed — '.tallybook sites track <name>' re-adds]" : "")}");
            }
            return lines;
        }

        /// <summary>Bring a dismissed site back by name — the deliberate act that undoes the
        /// deliberate act. Matching is substring, case-blind: these are typed by hand.</summary>
        public string Retrack(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Say which: .tallybook sites track sunrift";
            var match = svc.Store.SiteQuests.FirstOrDefault(s =>
                s.Dismissed && s.Title != null
                && s.Title.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null) return $"No dismissed site quest matches '{name.Trim()}'.";

            match.Dismissed = false;
            svc.Store.Save();
            svc.RecountAll();
            return $"{match.Title} is back on the Side quests tab.";
        }

        // ---- signals -------------------------------------------------------------------

        /// <summary>
        /// Lore codes in the player's own journal. The journal is synced to the client and
        /// kept in ModJournal's private ownJournal; the public DidDiscoverLore reads the
        /// server-side dictionary and is always empty here (decompile-verified 1.22.6), so
        /// reflection into ownJournal is the read. Failure is an empty set — the inventory
        /// latch still works, and LoreFound never shrinks over a bad read.
        /// </summary>
        HashSet<string> JournalLoreCodes()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var journalSys = capi.ModLoader.GetModSystem<ModJournal>();
                var journal = journalSys == null ? null
                    : AccessTools.Field(typeof(ModJournal), "ownJournal")?.GetValue(journalSys) as Journal;
                foreach (var entry in journal?.Entries ?? new List<JournalEntry>())
                {
                    if (!string.IsNullOrEmpty(entry?.LoreCode)) set.Add(entry.LoreCode);
                }
            }
            catch (Exception e)
            {
                if (!journalWarned)
                {
                    journalWarned = true;
                    capi.Logger.Warning("[tallybook] could not read the journal: {0}", e.Message);
                }
            }
            return set;
        }

        /// <summary>Lore stamps on carried items: discoveryCode once a writing has been read,
        /// category while it is still sealed.</summary>
        List<(string Code, string Category)> CarriedLoreStamps()
        {
            var stamps = new List<(string, string)>();
            try
            {
                foreach (var inv in svc.Probe.CarriedInventories())
                {
                    foreach (var slot in inv)
                    {
                        var attrs = slot?.Itemstack?.Attributes;
                        if (attrs == null) continue;
                        string code = attrs.GetString("discoveryCode");
                        string category = attrs.GetString("category");
                        if (code != null || category != null) stamps.Add((code, category));
                    }
                }
            }
            catch { /* an inventory mid-change reads as "nothing seen", never as a crash */ }
            return stamps;
        }
    }
}
