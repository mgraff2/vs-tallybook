using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Tallybook
{
    enum TbScreen { List, ConfirmClear, Options, ChooseRecipe, LiquidCalc, EditPlace }

    /// <summary>Errands from villagers are a different kind of thing from things you decided
    /// to build, so they get their own tab rather than being mixed in and distinguished only
    /// by a label.</summary>
    enum TbTab { Items, Quests, History, World, Player, Lore, Explore }

    /// <summary>
    /// Text that must stay on its own row. GuiElementStaticText does not clip: a line longer
    /// than its bounds wraps and overpaints the row below ("Bundle of bamboo stakes" did
    /// exactly that). GetTextExtents returns GUIScale-scaled pixels — verified empirically:
    /// the same string measures 2x wider at GUIScale 2 — so the unscaled column width scales
    /// up for the comparison.
    /// </summary>
    static class TbText
    {
        /// <summary>
        /// Break a passage into lines that fit a width. Used where the text *is* the content —
        /// a quest you are re-reading — rather than a cell in a row, so it wraps properly
        /// instead of being cut off with an ellipsis.
        /// </summary>
        /// <summary>
        /// Strip line breaks out of text bound for a single row.
        ///
        /// Applied here, at the last moment before drawing, rather than only where text is
        /// captured: a static text element honours embedded breaks and silently paints over
        /// the rows beneath, and text captured by an earlier build is already saved with them
        /// in. Doing it at the display end means no stored data can reintroduce the problem.
        /// </summary>
        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0) return text;

            var flat = text.Replace("\r", " ").Replace("\n", " ");
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
            return flat.Trim();
        }

        internal static List<string> Wrap(CairoFont font, string text, double maxWidth)
        {
            var lines = new List<string>();
            text = OneLine(text);
            if (string.IsNullOrWhiteSpace(text)) return lines;

            double max = maxWidth * RuntimeEnv.GUIScale;
            var line = new System.Text.StringBuilder();

            foreach (var word in text.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && font.GetTextExtents(candidate).Width > max)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
        }

        internal static string Fit(CairoFont font, string text, double maxWidth)
        {
            text = OneLine(text);
            if (string.IsNullOrEmpty(text)) return text;
            double max = maxWidth * RuntimeEnv.GUIScale;
            if (font.GetTextExtents(text).Width <= max) return text;

            int lo = 1, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (font.GetTextExtents(text.Substring(0, mid).TrimEnd() + "…").Width <= max) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + "…";
        }
    }

    /// <summary>
    /// The management dialog (spec §6), laid out as a table: icon, name (indented by tree
    /// depth), have/needed, wanted count, actions — every row type aligned on the same
    /// columns so the eye can scan one column instead of re-finding fields per row. Column
    /// alignment carries the row on its own; banded row backgrounds were tried and removed
    /// for hurting readability against this dialog's texture (Mark). Count steppers with
    /// direct numeric entry, manual expansion (spec §2a) with recipe choice, hold-to-unpin
    /// (1s countdown on the button, no dialog), clear-all with confirm.
    ///
    /// Follows a recompose-everything pattern: any data change rebuilds the
    /// composer. The one refinement is a typing grace period — live inventory recounts defer
    /// while the player is typing in a count field, because a recompose steals focus and
    /// eating half a typed number is worse than numbers arriving two seconds late.
    /// </summary>
    public class GuiDialogTallybook : GuiDialog
    {
        const double DW = 934;                 // content width

        /// <summary>One text size for the whole mod: the HUD's slider also governs the table
        /// (Mark — "just link their font sizes together"). The right size is the one that
        /// looks right, and it does not look right in one place and wrong in the other.</summary>
        double TablePx => config.HudFontSize > 0 ? config.HudFontSize : DefaultHudFontSize;

        /// <summary>Row and wrapped-line heights follow the text — a bigger font in fixed
        /// rows overdraws, a smaller one saves no space.</summary>
        double RowH => Math.Max(28, TablePx + 11);
        double LineStep => Math.Round(TablePx + 8);

        CairoFont TableFont() => CairoFont.WhiteSmallText().WithFontSize((float)TablePx);

        // Table columns. Every row type lines up on these, so the eye can run down a single
        // column instead of re-finding each field per row — the whole point of the table.
        const double ColCheck = 2;             // pin active checkbox (pin rows only)
        const double ColIcon = 32;             // item icon
        const double ColName = 58;             // name, indented by tree depth
        const double ColProg = 330;            // have/needed
        const double ColWant = 414;            // how many I want (pin rows only)
        const double ColAct1 = 526;            // expand / collapse (or Gather on errands)
        const double ColAct2 = 610;            // recipe switcher, once expanded
        const double ColCalc = 654;            // volume calculator (liquid pins only)
        const double ColBook = 770;            // handbook
        const double ColUnpin = 858;
        const double IndentW = 16;

        // World tab columns. Setting names are short; values ("Approx. every 10-20 days,
        // increase strength/frequency…") are where the room goes.
        const double WColName = 8;
        const double WColValue = 400;

        /// <summary>How long Unpin must be held. Long enough that a stray click cannot wipe a
        /// row, short enough that meaning it never feels like a punishment.</summary>
        const long HoldMs = 1000;

        readonly TallybookConfig config;
        readonly TallyService svc;

        TbScreen screen = TbScreen.List;
        TbTab tab = TbTab.Items;
        string notice = "";
        int page;
        long lastCountTypingMs;
        bool recomposeQueued;

        // Hold-to-remove state (no confirm dialog — hold the button through the countdown).
        // Generalised beyond pins: any destructive row button (Unpin, a saved place's
        // Remove) joins by registering its target, bounds and completion at compose time —
        // one workflow for every "this loses something" button (Mark: consistency).
        object holdTarget;
        Action holdComplete;
        long holdStartMs;
        long holdTickId;
        int holdShownSecond;
        readonly List<(object Target, ElementBounds Bounds, Action Complete)> holdButtons
            = new List<(object, ElementBounds, Action)>();

        // Flattened render rows for the current page
        abstract class Row { public double Indent; }
        class PinRow : Row { public Pin Pin; }
        class NodeRow : Row { public Pin Pin; public TallyNode Node; }
        class ToolRow : Row { public Requirement Tool; }
        /// <summary>A quest handed in whose reward is uncollected — no pin behind it (the
        /// errand pin left with the goods), just the walk still owed to the player.</summary>
        class RewardRow : Row { public string Name; public string Giver; }
        /// <summary>A map-artifact destination tracked as a side quest — no pin behind it
        /// either; the goal is a place, and where writings are hidden there, a count.</summary>
        class SiteRow : Row { public SiteQuest Site; }
        class InfoRow : Row
        {
            public string Text;
            /// <summary>Shown on hover when the row is longer than its column — a quest
            /// briefing is a paragraph and will always be.</summary>
            public string Full;
        }
        /// <summary>A section title on the World tab — one of the create-world screen's
        /// category headings.</summary>
        class HeadingRow : Row { public string Text; }
        /// <summary>A collapsible World-tab section heading: the fold control, with counts
        /// so a folded section still says what it is hiding.</summary>
        class WorldHeadRow : Row
        {
            public string Title, Key;
            public bool DefaultExpanded;
            public int Count, Changed;
        }
        /// <summary>One world rule: setting name and the value this world runs with.</summary>
        class SettingRow : Row { public WorldSetting Setting; }
        /// <summary>A Player-tab row: label, value, and optionally a place a Map button can
        /// take you (absolute coordinates; all-zero means no button).</summary>
        class SpawnRow : Row
        {
            public string Label, Value, Hover;
            public double MapX, MapY, MapZ;
        }
        /// <summary>Two found lore volumes side by side — the Lore tab's cells are small
        /// enough that a full-width row wasted half the window.</summary>
        class LoreRow : Row { public LoreBook.Volume A, B; }
        /// <summary>A saved place on the Explore tab.</summary>
        class PlaceRow : Row { public SavedPlace Place; }
        /// <summary>One line of a place's notes, shown while the place is unfolded —
        /// bullets and checkboxes render as marks, checkbox lines toggle with a click.
        /// Editing happens in the Edit window, never inline.</summary>
        class PlaceNoteRow : Row { public SavedPlace Place; public int Index; }

        List<Row> allRows = new List<Row>();

        /// <summary>World tab model, built lazily per dialog-open (see BuildRows).</summary>
        List<WorldSettingsSection> worldSections;

        /// <summary>World tab filter text. Session state, reset on open — a filter that
        /// quietly survived into the next look would read as missing settings.</summary>
        string worldFilter = "";

        /// <summary>The one World-tab section currently open — an accordion, not
        /// independent folds (Mark): opening a section closes whatever was open,
        /// including the changed-settings lead. Null is everything folded. Session
        /// state like the filter: the tab opens on the changed section every time.</summary>
        string worldOpenSection = "~changed";

        bool WorldSectionExpanded(string key) => worldOpenSection == key;

        /// <summary>Whether the recompose about to happen must hand focus back to the
        /// filter box: recomposing steals focus, and a filter that drops its cursor after
        /// every keystroke cannot be typed into at all.</summary>
        bool refocusWorldFilter;

        readonly QuestHistory history;

        /// <summary>Which archive entries are open for reading.</summary>
        readonly HashSet<string> expandedRecords = new HashSet<string>();

        readonly Action<bool> setHudVisible;
        readonly Action onHudChanged;
        readonly QuestWaypoints waypoints;
        readonly StoryProgress story;
        readonly SiteQuests sites;
        readonly SpawnTracker spawnTracker;
        readonly LoreBook lore;
        readonly ExplorePlaces explore;

        /// <summary>Explore-tab session state: the save-a-spot inputs and the place
        /// editor's target and drafts.</summary>
        string exploreName = "", exploreNote = "";
        SavedPlace editingPlace;
        string editingNameDraft, editingNoteDraft, editingNotesDraft;

        static double DefaultHudFontSize => CairoFont.WhiteSmallText().UnscaledFontsize;

        public GuiDialogTallybook(ICoreClientAPI capi, TallybookConfig config, TallyService svc,
                                  QuestHistory history, QuestWaypoints waypoints,
                                  StoryProgress story, SiteQuests sites, SpawnTracker spawnTracker,
                                  LoreBook lore, ExplorePlaces explore,
                                  Action<bool> setHudVisible, Action onHudChanged)
            : base(capi)
        {
            this.onHudChanged = onHudChanged;
            this.config = config;
            this.svc = svc;
            this.history = history;
            this.waypoints = waypoints;
            this.story = story;
            this.sites = sites;
            this.spawnTracker = spawnTracker;
            this.lore = lore;
            this.explore = explore;
            this.setHudVisible = setHudVisible;
            // OnCountsChanged is the single redraw signal: every store mutation funnels
            // through TallyService.RecountAll, whose signature covers structure and numbers.
            svc.OnCountsChanged += OnCountsChanged;
        }

        public override string ToggleKeyCombinationCode => "tallybook";
        public override bool PrefersUngrabbedMouse => true;
        public override double DrawOrder => 0.2;

        /// <summary>
        /// The other half of the typing courtesy: while one of THIS window's fields has the
        /// keyboard — a count, the world filter, a place's name or notes — take the keys, so
        /// another mod's always-available hotkey does not fire on a letter that was meant for
        /// the box. Exactly what the API documents this for ("should this dialog (e.g. textbox)
        /// capture all the keyboard events except for escape"), and Escape still closes.
        ///
        /// Only WHILE a field is focused, never merely because the window is open: capturing
        /// the keyboard for the whole session would be the same rudeness pointing the other
        /// way, and the player would lose every hotkey they have while reading their list.
        /// </summary>
        public override bool CaptureAllInputs() => TypingGuard.TextInputFocusedIn(SingleComposer);

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            ignoreNextKeyPress = true;      // the opening hotkey's own char event
            notice = "";
            screen = TbScreen.List;
            worldSections = null;           // world config can change between opens
            worldFilter = "";
            worldOpenSection = "~changed";  // the tab opens concise every time
            loreFilter = "all";             // a slice that quietly survived would read as missing lore
            loreShowWorld = loreShowStory = true;
            loreSource = null;

            svc.RecountAll();
            Recompose();
        }

        public override void Dispose()
        {
            svc.OnCountsChanged -= OnCountsChanged;
            StopHoldTimer();
            base.Dispose();
        }

        void OnCountsChanged()
        {
            if (!IsOpened()) return;

            // Typing grace: while a count is being typed, a recompose would steal focus.
            if (capi.World.ElapsedMilliseconds - lastCountTypingMs < 2000)
            {
                if (recomposeQueued) return;
                recomposeQueued = true;
                capi.Event.RegisterCallback(_ => { recomposeQueued = false; OnCountsChanged(); }, 1000, permittedWhilePaused: true);
                return;
            }
            Recompose();
        }

        // ------------------------------------------------------------------ rows

        static bool IsQuestPin(Pin pin) => pin.QuestGiver != null;

        IEnumerable<Pin> PinsForTab(TbTab which)
            => svc.Store.Pins.Where(p => IsQuestPin(p) == (which == TbTab.Quests));

        void BuildRows()
        {
            allRows = new List<Row>();

            if (tab == TbTab.World)
            {
                // Read per dialog-open, not per recompose: an admin can change world config
                // mid-session, so a stale forever-cache would lie, while re-reading on every
                // inventory-driven recompose would be pure waste for data this static.
                worldSections ??= WorldRules.Read(capi);
                string f = worldFilter?.Trim();
                bool filtering = !string.IsNullOrEmpty(f);

                if (filtering)
                {
                    // The filter overrides every fold: a match inside a folded section MUST
                    // surface (Mark) — a search that respected the folds would look like the
                    // setting does not exist. A matching category title keeps its whole
                    // section ("temporal" should give all of temporal stability); otherwise
                    // rows match individually, under their heading so a hit still says
                    // where it lives.
                    foreach (var section in worldSections)
                    {
                        var shown = section.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                            ? section.Settings
                            : section.Settings.Where(s => MatchesWorldFilter(s, f)).ToList();
                        if (shown.Count == 0) continue;

                        allRows.Add(new HeadingRow { Text = section.Title });
                        foreach (var s in shown) allRows.Add(new SettingRow { Setting = s });
                    }

                    if (allRows.Count == 0)
                    {
                        string none = $"Nothing matches \"{f}\".";
                        allRows.Add(new InfoRow { Text = none, Full = none, Indent = 0 });
                    }
                    return;
                }

                // Unfiltered, the tab leads with its actual point — what this server
                // changed — and folds every category to a counted heading (Mark: concise).
                var changed = worldSections.SelectMany(sec => sec.Settings)
                    .Where(s => !s.IsDefault).ToList();
                if (changed.Count > 0)
                {
                    allRows.Add(new WorldHeadRow
                    {
                        Title = "Changed on this server", Key = "~changed",
                        DefaultExpanded = true, Count = changed.Count,
                    });
                    if (WorldSectionExpanded("~changed"))
                        foreach (var s in changed) allRows.Add(new SettingRow { Setting = s });
                }

                foreach (var section in worldSections)
                {
                    int changedIn = section.Settings.Count(s => !s.IsDefault);
                    allRows.Add(new WorldHeadRow
                    {
                        Title = section.Title, Key = section.Title,
                        DefaultExpanded = false,
                        Count = section.Settings.Count, Changed = changedIn,
                    });
                    if (WorldSectionExpanded(section.Title))
                        foreach (var s in section.Settings) allRows.Add(new SettingRow { Setting = s });
                }
                return;
            }

            if (tab == TbTab.Player) { BuildPlayerRows(); return; }
            if (tab == TbTab.Lore) { BuildLoreRows(); return; }
            if (tab == TbTab.Explore) { BuildExploreRows(); return; }

            if (tab == TbTab.Quests)
            {
                // Rewards first: a walk you can make right now beats a list of things to
                // find. They are transient (paid out, they vanish), so they sit above the
                // orderable list rather than in it.
                foreach (var waiting in history?.AwaitingRewards()
                         ?? new List<(string, string, string)>())
                {
                    allRows.Add(new RewardRow { Name = waiting.Item2, Giver = waiting.Item3, Indent = 0 });
                }

                // Errands and site quests as ONE ordered list — the sort dropdown and the
                // ^ / v arranging cover every row, whichever kind it is (Mark: the map
                // quests would not rearrange, and sorting "did nothing" because it only
                // touched the pin rows below the sites).
                var entries = new List<PinStore.QuestEntry>();
                foreach (var sq in svc.Store.SiteQuests.Where(s => !s.Dismissed))
                    entries.Add(new PinStore.QuestEntry { Site = sq });
                foreach (var pin in PinsForTab(TbTab.Quests))
                    entries.Add(new PinStore.QuestEntry { Pin = pin });
                entries = svc.Store.OrderQuestEntries(entries, capi.World?.Player?.Entity?.Pos?.XYZ);

                foreach (var entry in entries)
                {
                    if (entry.Site != null) AddSiteQuestRows(entry.Site);
                    else AddQuestPinRows(entry.Pin);
                }
                return;
            }

            foreach (var pin in PinsForTab(tab))
            {
                allRows.Add(new PinRow { Pin = pin, Indent = 0 });

                // Unchecked pins are parked: one dimmed header row, no tree. Their state
                // (count, recipe choice, expansions) is all kept for when they're re-checked.
                if (!pin.Active) continue;

                if (!pin.HasRecipe)
                {
                    // Not craftable is not untrackable: the count above this row is live.
                    // The conversation, only when asked for. An errand is one line until you
                    // want the story behind it.
                    if (pin.QuestGiver != null && pin.QuestTextExpanded)
                    {
                        foreach (var said in pin.QuestText ?? new List<string>())
                        {
                            string line = QuestScanner.Attributed(said, pin.QuestGiver);
                            allRows.Add(new InfoRow { Text = line, Full = line, Indent = 1 });
                        }

                        // The place attached to the errand, when one came with it.
                        if (pin.QuestMaps?.Count > 0)
                        {
                            string maps = "came with " + string.Join(", ", pin.QuestMaps);
                            allRows.Add(new InfoRow { Text = maps, Full = maps, Indent = 1 });
                        }
                    }
                    // Everything else that used to be said here — "gathering, press Expand",
                    // "no crafting recipe" — is on the row's own controls as hover help. It was
                    // the same sentence under every row, which is a caption, not information.
                    continue;
                }
                foreach (var node in pin.RootNodes) AddNodeRows(pin, node, 1);
                foreach (var tool in pin.Tools) allRows.Add(new ToolRow { Tool = tool, Indent = 1 });
            }
        }

        /// <summary>The ^ / v hand-arranging pair, identical on errand and site rows —
        /// only under the custom sort, where a move would not be undone by the very next
        /// redraw. ^ / v, not ▲ / ▼: the game's fonts carry no triangle-down.</summary>
        void QuestMoveButtons(GuiComposer c, string key, double y, CairoFont font)
        {
            if (!string.IsNullOrEmpty(svc.Store.QuestSort) && svc.Store.QuestSort != "custom") return;

            c.AddSmallButton("^",
                () => { svc.Store.MoveQuestEntry(key, -1); Recompose(); onHudChanged?.Invoke(); return true; },
                EB(ColCalc + 2, y, 26, 26), EnumButtonStyle.Small);
            c.AddSmallButton("v",
                () => { svc.Store.MoveQuestEntry(key, +1); Recompose(); onHudChanged?.Invoke(); return true; },
                EB(ColCalc + 32, y, 26, 26), EnumButtonStyle.Small);
            c.AddHoverText("Move this row up or down — the HUD follows this order.",
                font, 240, EB(ColCalc + 2, y, 56, 26));
        }

        /// <summary>A map-artifact site quest's rows: the header, and — checked and
        /// unfolded — what has been found there. The unfound writings' titles stay the
        /// site's secret; the count is the progress, the names are content the site has
        /// not given up yet.</summary>
        void AddSiteQuestRows(SiteQuest sq)
        {
            allRows.Add(new SiteRow { Site = sq, Indent = 0 });
            // Parked site quests keep their header row and nothing else — the same
            // contract as an unchecked pin.
            if (!sq.Active || !sq.TextExpanded) return;

            foreach (var title in sites.FoundLoreTitles(sq))
            {
                string line = $"√ {title}";
                allRows.Add(new InfoRow { Text = line, Full = line, Indent = 1 });
            }
            var count = sites.LoreCount(sq);
            if (count.HasValue && count.Value.Total > count.Value.Found)
            {
                int left = count.Value.Total - count.Value.Found;
                string line = $"{left} writing(s) still hidden there.";
                allRows.Add(new InfoRow { Text = line, Full = line, Indent = 1 });
            }
        }

        /// <summary>An errand pin's rows: the header, and — checked and unfolded — the
        /// conversation and the maps that came with it. Errands are counted, never
        /// decomposed, so there is no tree here by design.</summary>
        void AddQuestPinRows(Pin pin)
        {
            allRows.Add(new PinRow { Pin = pin, Indent = 0 });
            if (!pin.Active || !pin.QuestTextExpanded) return;

            foreach (var said in pin.QuestText ?? new List<string>())
            {
                string line = QuestScanner.Attributed(said, pin.QuestGiver);
                allRows.Add(new InfoRow { Text = line, Full = line, Indent = 1 });
            }
            if (pin.QuestMaps?.Count > 0)
            {
                string maps = "came with " + string.Join(", ", pin.QuestMaps);
                allRows.Add(new InfoRow { Text = maps, Full = maps, Indent = 1 });
            }
        }

        /// <summary>
        /// The Player tab's rows: spawn points first — the walk home is the actionable part —
        /// then the numbers. Everything here is either synced by the server (spawn point,
        /// deaths), world config (budgets), or the player's own entity (class, stability);
        /// distances are computed at compose time and deliberately kept OUT of the change
        /// signature, so walking doesn't redraw the dialog every step.
        /// </summary>
        void BuildPlayerRows()
        {
            var spawn = capi.World?.DefaultSpawnPosition?.XYZ;
            var plr = capi.World?.Player;
            if (spawn == null || plr?.Entity == null || spawnTracker == null) return;
            double sx = spawn.X, sz = spawn.Z;
            var here = plr.Entity.Pos;

            string Coords(double x, double y, double z)
            {
                string at = string.Create(CultureInfo.InvariantCulture,
                    $"{(int)(x - sx)}, {(int)y}, {(int)(z - sz)}");
                int dist = (int)Math.Sqrt((here.X - x) * (here.X - x) + (here.Z - z) * (here.Z - z));
                return dist < 10 ? $"{at} — you are here" : $"{at} — {dist:n0} blocks away";
            }

            allRows.Add(new HeadingRow { Text = "Spawn points" });

            int radius = capi.World.Config?.GetAsInt("spawnRadius", 0) ?? 0;
            allRows.Add(new SpawnRow
            {
                Label = SpawnTracker.HomeTitle,
                Value = Coords(spawn.X, spawn.Y, spawn.Z),
                MapX = spawn.X, MapY = spawn.Y, MapZ = spawn.Z,
                Hover = "Where you re-emerge when no returning point is set. Coordinates are "
                    + "spawn-relative, the same numbers the coordinate overlay shows."
                    + (radius > 0 ? $" Respawns scatter up to {radius} blocks around it." : ""),
            });

            var st = spawnTracker.State;
            if (spawnTracker.HasTemp)
            {
                allRows.Add(new SpawnRow
                {
                    Label = SpawnTracker.TempTitle,
                    Value = Coords(st.TempX, st.TempY, st.TempZ),
                    MapX = st.TempX, MapY = st.TempY, MapZ = st.TempZ,
                    Hover = "Your temporal-gear respawn point. Tallybook keeps a map marker "
                        + "on it, and removes the marker when the point is used up or moved.",
                });

                int? left = spawnTracker.UsesLeft();
                string leftText = left == null
                    ? "not known — it was set before Tallybook was watching"
                    : left == int.MaxValue
                        ? "unlimited on this server"
                        : st.UsesAtSet > 0 ? $"{left} of {st.UsesAtSet}" : left.ToString();
                allRows.Add(new SpawnRow
                {
                    Label = "Respawns left there",
                    Value = leftText,
                    Hover = "Counted as deaths since the point was set, against the "
                        + "temporalGearRespawnUses budget the server granted it. The server "
                        + "keeps the real number to itself between logins, so the respawn "
                        + "message in chat is the authority when they disagree.",
                });
            }
            else
            {
                allRows.Add(new SpawnRow
                {
                    Label = SpawnTracker.TempTitle,
                    Value = "none — set one with a temporal gear",
                    Hover = "Using a temporal gear on the ground sets a personal respawn "
                        + "point. When you set one, it appears here with a map marker and "
                        + "a count of the respawns it has left.",
                });
            }

            allRows.Add(new HeadingRow { Text = "You" });

            allRows.Add(new SpawnRow
            {
                Label = "Deaths in this world",
                Value = spawnTracker.Deaths.ToString("n0"),
                Hover = "The highest count the server has ever told this client, plus deaths "
                    + "Tallybook has watched happen. The game does not sync this number "
                    + "reliably, so it can start low after installing and catch up the next "
                    + "time the server mentions it.",
            });

            int lives = capi.World.Config?.GetAsInt("playerlives", -1) ?? -1;
            if (lives >= 0)
            {
                allRows.Add(new SpawnRow
                {
                    Label = "Lives left",
                    Value = Math.Max(0, lives - spawnTracker.Deaths).ToString("n0"),
                    Hover = $"This server grants {lives} lives (the playerlives setting).",
                });
            }

            string classCode = plr.Entity.WatchedAttributes?.GetString("characterClass");
            if (!string.IsNullOrEmpty(classCode))
            {
                allRows.Add(new SpawnRow
                {
                    Label = "Character class",
                    Value = Lang.GetIfExists("characterclass-" + classCode) ?? classCode,
                });
            }

            if (capi.World.Config?.GetBool("temporalStability", true) == true)
            {
                double stab = plr.Entity.WatchedAttributes?.GetDouble("temporalStability", -1) ?? -1;
                if (stab >= 0)
                {
                    allRows.Add(new SpawnRow
                    {
                        Label = "Temporal stability",
                        Value = $"{(int)Math.Round(stab * 100)}%",
                        Hover = "How firmly you are anchored in time. Drains near rifts and "
                            + "in temporal storms; low stability invites the things that "
                            + "live outside it.",
                    });
                }
            }

            string today = null;
            try { today = capi.World.Calendar?.PrettyDate(); } catch { }
            if (!string.IsNullOrEmpty(today))
            {
                allRows.Add(new SpawnRow { Label = "Today", Value = today });
            }
        }

        /// <summary>Lore tab model for the current recompose — built once in BuildRows,
        /// read again by ComposeLore for the intro numbers and chips.</summary>
        LoreBook.Model loreModel;

        /// <summary>Which slice of found volumes the Lore tab shows: "all", "progress"
        /// (started, chapters missing) or "complete". Session state, reset on open.</summary>
        string loreFilter = "all";

        /// <summary>Kind toggles: world lore (droppable anywhere) and story lore (held
        /// only by the story's own places — scan-derived, never a name list). Both on by
        /// default; a volume of unknown kind always shows, whatever the toggles say.</summary>
        bool loreShowWorld = true, loreShowStory = true;

        /// <summary>Source filter: null = every source, else the asset domain ("game",
        /// "betterruins", …) whose volumes alone are shown. Session state, reset on open.</summary>
        string loreSource;

        static bool LoreComplete(LoreBook.Volume v)
            => v.TotalChapters > 0 && v.FoundChapters >= v.TotalChapters;

        List<LoreBook.Volume> LoreFiltered()
        {
            var found = loreModel?.Found ?? new List<LoreBook.Volume>();
            if (loreFilter == "progress") found = found.Where(v => !LoreComplete(v)).ToList();
            if (loreFilter == "complete") found = found.Where(LoreComplete).ToList();
            found = found.Where(v => v.IsStory == null || (v.IsStory == true ? loreShowStory : loreShowWorld)).ToList();
            if (loreSource != null)
                found = found.Where(v => string.Equals(v.SourceKey, loreSource, StringComparison.OrdinalIgnoreCase)).ToList();
            // Clustered by source — vanilla first, then mods alphabetically (Mark) — and
            // inside a cluster the unfinished stories first, then alphabetical.
            return found.OrderBy(LoreVanillaFirst)
                .ThenBy(v => v.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(LoreComplete)
                .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static int LoreVanillaFirst(LoreBook.Volume v)
            => string.Equals(v.SourceKey, "game", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        /// <summary>
        /// The Lore tab's rows: found volumes two to a row (a volume cell is small — title,
        /// count, Read — and a single column wasted half the window and ran to pages, Mark).
        /// Found volumes show by name; everything undiscovered is counts only, in the intro.
        /// </summary>
        void BuildLoreRows()
        {
            loreModel = lore?.Snapshot();
            if (loreModel == null || loreModel.TotalVolumes == 0)
            {
                string none = "This world's content defines no lore.";
                allRows.Add(new InfoRow { Text = none, Full = none, Indent = 0 });
                return;
            }

            if (loreModel.FoundVolumes == 0)
            {
                string none = "Nothing found yet — the books, scrolls and tapestries you "
                    + "read land in your journal, and from there on this page.";
                allRows.Add(new InfoRow { Text = none, Full = none, Indent = 0 });
                return;
            }

            var shown = LoreFiltered();
            if (shown.Count == 0)
            {
                string none = "Nothing in this view — widen the filters above.";
                allRows.Add(new InfoRow { Text = none, Full = none, Indent = 0 });
                return;
            }

            // With every source showing, each mod's volumes sit under their own heading;
            // filtered to one source, the dropdown already names it and headings would
            // just repeat one word down the page.
            bool cluster = loreSource == null
                && shown.Select(v => v.SourceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

            foreach (var group in shown.GroupBy(v => cluster ? v.SourceKey : "", StringComparer.OrdinalIgnoreCase))
            {
                var vols = group.ToList();
                if (cluster) allRows.Add(new HeadingRow { Text = vols[0].Source });
                for (int i = 0; i < vols.Count; i += 2)
                {
                    allRows.Add(new LoreRow
                    {
                        A = vols[i],
                        B = i + 1 < vols.Count ? vols[i + 1] : null,
                    });
                }
            }
        }

        /// <summary>Matched against everything a player might remember about a setting —
        /// its label, its value, its raw code, and the description in its hover — so
        /// "monsters", "graceTimer" and "grace" all find the grace timer.</summary>
        static bool MatchesWorldFilter(WorldSetting s, string f)
            => (s.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.Value?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.Code?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.Hover?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);

        void AddNodeRows(Pin pin, TallyNode node, int depth)
        {
            allRows.Add(new NodeRow { Pin = pin, Node = node, Indent = depth });
            if (!node.Expanded) return;

            foreach (var child in node.Children) AddNodeRows(pin, child, depth + 1);
            foreach (var tool in node.Tools) allRows.Add(new ToolRow { Tool = tool, Indent = depth + 1 });
        }

        List<int> pageStarts = new List<int> { 0 };

        int MaxPage => pageStarts.Count - 1;

        /// <summary>What is left for rows once the window's own furniture is accounted for:
        /// title, tabs and column heads above; pager and buttons below; padding around.</summary>
        double PageBudget => Math.Max(200, capi.Render.FrameHeight / RuntimeEnv.GUIScale - 260);

        /// <summary>
        /// How tall a row draws. Nearly all of them are one RowH, but an errand's quoted line
        /// wraps to as many lines as it needs — which is what makes counting rows per page
        /// wrong, and why pages are measured instead.
        /// </summary>
        double RowHeight(Row row)
        {
            if (row is HeadingRow) return RowH + 8;
            if (row is WorldHeadRow) return RowH + 8;
            if (row is PlaceNoteRow pnr)
            {
                var lines = NotesLines(pnr.Place);
                if (pnr.Index >= lines.Length) return 0;
                var (kind, text) = ParseNoteLine(lines[pnr.Index]);
                if (string.IsNullOrWhiteSpace(text)) return LineStep;

                double tx = ColName + pnr.Indent * IndentW + (kind == 1 ? 22 : kind >= 2 ? 32 : 0);
                return TbText.Wrap(TableFont(), text, DW - tx - 24).Count * LineStep + 2;
            }
            if (!(row is InfoRow ir)) return RowH;

            double w = DW - (ColName + row.Indent * IndentW) - 16;
            return TbText.Wrap(TableFont(), ir.Full ?? ir.Text, w).Count * LineStep + 6;
        }

        /// <summary>
        /// The row index each page starts at, filled to a height budget rather than a count.
        /// A page never breaks before its own first row, so a single row taller than the whole
        /// budget cannot produce an empty page and an endless list of them.
        /// </summary>
        /// <summary>The rows the current page draws. One definition, because composing and
        /// restoring inputs must agree exactly — asking the composer for a control it never
        /// composed is unhealthy whether or not it throws.</summary>
        List<Row> VisibleRows()
        {
            int p = Math.Min(Math.Max(page, 0), MaxPage);
            int from = pageStarts[p];
            int upto = p + 1 < pageStarts.Count ? pageStarts[p + 1] : allRows.Count;
            return allRows.GetRange(from, Math.Max(0, upto - from));
        }

        /// <summary>How much of the page the story block will take, measured the same way it
        /// composes — the pager must know the table starts lower on the Quests tab.</summary>
        double StoryBlockHeight()
        {
            if (story == null || !story.Enabled || !story.AnyRevealed) return 0;

            var quiet = TableFont();
            double h = 28;
            var step = story.Current;
            var paragraphs = new List<string>();
            if (step == null) paragraphs.Add("x");
            else
            {
                paragraphs.Add(step.Text);
                string detail = null;
                try { detail = step.Detail?.Invoke(); } catch { }
                if (!string.IsNullOrEmpty(detail)) paragraphs.Add(detail);
            }
            foreach (var para in paragraphs)
                h += TbText.Wrap(quiet, para, DW - 40).Count * LineStep + 4;
            return h + 8;
        }

        List<int> PageStarts(List<Row> rows)
        {
            var starts = new List<int> { 0 };
            double used = 0, budget = Math.Max(120,
                PageBudget - (tab == TbTab.Quests ? StoryBlockHeight()
                            : tab == TbTab.World ? WorldHeaderHeight()
                            : tab == TbTab.Player ? SpawnHudControlsHeight
                            : tab == TbTab.Lore ? LoreHeaderHeight()
                            : tab == TbTab.Explore ? 40 : 0));

            for (int i = 0; i < rows.Count; i++)
            {
                double h = RowHeight(rows[i]);
                if (used > 0 && used + h > budget) { starts.Add(i); used = 0; }
                used += h;
            }
            return starts;
        }

        // ------------------------------------------------------------------ composing

        void Recompose()
        {
            // The World and Player tabs exist only while their options are on; a selection
            // pointing at a tab that is no longer drawn would compose its rows under the
            // wrong header.
            if (tab == TbTab.World && !config.ShowWorldTab) { tab = TbTab.Items; page = 0; }
            if (tab == TbTab.Player && !config.ShowPlayerTab) { tab = TbTab.Items; page = 0; }
            if (tab == TbTab.Lore && !config.ShowLoreTab) { tab = TbTab.Items; page = 0; }
            if (tab == TbTab.Explore && !config.ShowExploreTab) { tab = TbTab.Items; page = 0; }

            // Whether the filter box holds the cursor is a fact about the composer being
            // thrown away — read it before it goes, so RestoreCountInputs can hand focus
            // back and typing survives the rebuild.
            refocusWorldFilter = screen == TbScreen.List && tab == TbTab.World
                && SingleComposer?.GetTextInput("world-filter")?.HasFocus == true;

            BuildRows();
            pageStarts = PageStarts(allRows);

            // The History tab has no pins behind it, so allRows describes a different tab
            // entirely and its page count says nothing about the archive. ComposeHistory
            // clamps against its own pages instead.
            if (tab != TbTab.History) page = Math.Min(page, MaxPage);

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("tallybook-" + screen, dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(TitleFor(), OnTitleBarClose)
                .BeginChildElements(bgBounds);

            switch (screen)
            {
                case TbScreen.List: ComposeList(composer); break;
                case TbScreen.ConfirmClear: ComposeConfirmClear(composer); break;
                case TbScreen.Options: ComposeOptions(composer); break;
                case TbScreen.ChooseRecipe: ComposeChooseRecipe(composer); break;
                case TbScreen.LiquidCalc: ComposeLiquidCalc(composer); break;
                case TbScreen.EditPlace: ComposeEditPlace(composer); break;
            }

            var replaced = SingleComposer;
            SingleComposer = composer.EndChildElements().Compose();
            if (replaced != null)
            {
                // Deferred: the old composer may still be mid-iteration in the event loop.
                // Recomposes can be triggered while singleplayer is paused (the handbook
                // pauses the game and stays interactive), so permit the registration then —
                // the dispose just waits for unpause.
                capi.Event.RegisterCallback(_ => replaced.Dispose(), 250, permittedWhilePaused: true);
            }

            if (screen == TbScreen.List) RestoreCountInputs();
            else if (screen == TbScreen.Options) RestoreOptionSwitches();
            else if (screen == TbScreen.LiquidCalc) RestoreCalcInput();
            else if (screen == TbScreen.EditPlace) RestoreEditPlace();
        }

        string TitleFor() => screen switch
        {
            // These screens keep a qualifier — there it says which screen you are on.
            TbScreen.ConfirmClear => "Tallybook — Clear list",
            TbScreen.Options => "Tallybook — Options",
            TbScreen.ChooseRecipe => "Tallybook — How do you want to make it?",
            TbScreen.LiquidCalc => "Tallybook — Liquid calculator",
            _ => "Tallybook",
        };

        void OnTitleBarClose()
        {
            if (screen == TbScreen.List) TryClose();
            else BackToList();
        }

        void BackToList()
        {
            screen = TbScreen.List;
            Recompose();
        }

        /// <summary>This window's on-screen rectangle in real pixels, for cropping a
        /// showcase shot down to it. Null when there is nothing composed to measure.</summary>
        public ShowcaseShots.Rect ShowcaseBounds()
        {
            var b = SingleComposer?.Bounds;
            if (b == null || !IsOpened() || b.OuterWidth < 1 || b.OuterHeight < 1) return null;
            return new ShowcaseShots.Rect
            {
                X = (int)b.absX,
                Y = (int)b.absY,
                W = (int)b.OuterWidth,
                H = (int)b.OuterHeight,
            };
        }

        /// <summary>
        /// Open the dialog on a named view, for the screenshot walker. NAVIGATION ONLY —
        /// it opens and selects, and touches no pin, count, expansion or preference, so a
        /// showcase run cannot alter the list it is photographing. Returns false when the
        /// view does not exist right now (an optional tab switched off), so the caller can
        /// skip that shot rather than photograph the wrong screen under its name.
        /// </summary>
        public bool ShowcaseView(string view)
        {
            if (!IsOpened()) TryOpen();
            if (!IsOpened()) return false;

            // TryOpen runs OnGuiOpened, which resets to the list screen and clears filters —
            // so the selection has to happen after it, not before.
            switch ((view ?? "").ToLowerInvariant())
            {
                case "items": screen = TbScreen.List; tab = TbTab.Items; break;
                case "quests": screen = TbScreen.List; tab = TbTab.Quests; break;
                case "history": screen = TbScreen.List; tab = TbTab.History; break;
                case "world":
                    if (!config.ShowWorldTab) return false;
                    screen = TbScreen.List; tab = TbTab.World; break;
                case "player":
                    if (!config.ShowPlayerTab) return false;
                    screen = TbScreen.List; tab = TbTab.Player; break;
                case "lore":
                    if (!config.ShowLoreTab) return false;
                    screen = TbScreen.List; tab = TbTab.Lore; break;
                case "explore":
                    if (!config.ShowExploreTab) return false;
                    screen = TbScreen.List; tab = TbTab.Explore; break;
                case "options": screen = TbScreen.Options; break;
                default: return false;
            }

            page = 0;
            Recompose();
            return true;
        }

        void OnTabClicked(int index)
        {
            // The handler receives the clicked tab's DataInt, not its array position
            // (decompile-verified: SetValue calls handler(tabs[i].DataInt)) — so optional
            // tabs keep stable identities here no matter which of them are showing.
            var next = index == 6 ? TbTab.Explore
                : index == 5 ? TbTab.Lore
                : index == 4 ? TbTab.Player
                : index == 3 ? TbTab.World
                : index == 2 ? TbTab.History
                : index == 1 ? TbTab.Quests
                : TbTab.Items;
            if (next == tab) return;
            tab = next;
            page = 0;              // page numbers do not carry across two different lists
            Recompose();
        }

        static ElementBounds EB(double x, double y, double w, double h) => ElementBounds.Fixed(x, y, w, h);

        double[] StatusColor(int have, int needed)
        {
            if (needed <= 0 || have >= needed) return TallybookConfig.ParseColor(config.ColorSatisfied);
            if (have > 0) return TallybookConfig.ParseColor(config.ColorPartial);
            return TallybookConfig.ParseColor(config.ColorNone);
        }

        void ComposeList(GuiComposer c)
        {
            holdButtons.Clear();
            var font = TableFont();
            double y = 34;

            int itemCount = PinsForTab(TbTab.Items).Count();
            // Uncollected rewards count as side quests — they are the tab's most actionable
            // rows, and a tab reading (0) over a table with a row in it is a lie.
            var awaiting = history?.AwaitingRewards()
                ?? new List<(string Chain, string Name, string Giver)>();
            int siteCount = sites == null ? 0 : svc.Store.SiteQuests.Count(s => !s.Dismissed);
            int questCount = PinsForTab(TbTab.Quests).Count() + awaiting.Count + siteCount;

            // Open quests first, then what is finished — the archive reads as a story so far.
            var done = new List<QuestRecord>();
            if (history != null)
            {
                done.AddRange(history.InProgress());
                done.AddRange(history.Records());
            }
            // Mark's order: the play tabs first, the reference tabs after, the archive
            // last. DataInts are stable IDENTITIES (the click handler receives them), so
            // reordering the array moves nothing else — but the restore path computes
            // array POSITIONS from which optional tabs are shown, and must match this
            // order exactly.
            var tabs = new List<GuiTab>
            {
                new GuiTab { DataInt = 0, Name = $"Items ({itemCount})" },
                new GuiTab { DataInt = 1, Name = $"Side quests ({questCount})" },
            };
            if (config.ShowExploreTab)
            {
                int placeCount = svc.Store.Places.Count;
                tabs.Add(new GuiTab { DataInt = 6, Name = placeCount > 0 ? $"Explore ({placeCount})" : "Explore" });
            }
            if (config.ShowPlayerTab) tabs.Add(new GuiTab { DataInt = 4, Name = "Player" });
            if (config.ShowWorldTab) tabs.Add(new GuiTab { DataInt = 3, Name = "World" });
            if (config.ShowLoreTab) tabs.Add(new GuiTab { DataInt = 5, Name = "Lore" });
            tabs.Add(new GuiTab { DataInt = 2, Name = $"History ({done.Count})" });
            c.AddHorizontalTabs(tabs.ToArray(), EB(0, y, DW, 26), OnTabClicked,
                CairoFont.WhiteSmallText(),
                CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");
            y += 34;

            if (tab == TbTab.History) { ComposeHistory(c, done, ref y); return; }
            if (tab == TbTab.World) { ComposeWorld(c, ref y); return; }
            if (tab == TbTab.Player) { ComposePlayer(c, ref y); return; }
            if (tab == TbTab.Lore) { ComposeLore(c, ref y); return; }
            if (tab == TbTab.Explore) { ComposeExplore(c, ref y); return; }

            if (tab == TbTab.Quests) ComposeStoryBlock(c, ref y);

            if (!PinsForTab(tab).Any() && !(tab == TbTab.Quests && (awaiting.Count > 0 || siteCount > 0)))
            {
                string empty = tab == TbTab.Quests
                    ? "No errands tracked."
                    : "Your list is empty.";
                string hint = tab == TbTab.Quests
                    ? "Accept a fetch quest from a villager and it turns up here by itself."
                    : "Open the handbook (H), find an item, and click \"Add to Tallybook\" on its page.";

                c.AddStaticText(empty, font, EB(8, y + 8, DW, 26));
                c.AddStaticText(hint, font.Clone().WithColor(GuiStyle.ColorParchment), EB(8, y + 38, DW, 46));
                y += 96;
                c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
                return;
            }

            c.AddSmallButton("Options", () => { screen = TbScreen.Options; Recompose(); return true; },
                EB(8, y - 2, 92, 26), EnumButtonStyle.Small);

            if (tab == TbTab.Quests)
            {
                // One ordering for the tab AND the HUD — sorting here rearranges both.
                // "Custom" is the hand-arranged order; the ^ / v buttons on the rows only
                // appear in that mode, because moving a row under an active sort would be
                // undone by the very next redraw.
                var sortValues = new[] { "custom", "distance", "progress", "name", "giver" };
                var sortNames = new[] { "Custom order", "By distance", "By progress", "By item", "By giver" };
                int sel = Math.Max(0, Array.IndexOf(sortValues, svc.Store.QuestSort ?? "custom"));
                c.AddStaticText("Sort", font, EB(110, y + 2, 40, 24));
                c.AddDropDown(sortValues, sortNames, sel, (code, _) =>
                {
                    svc.Store.QuestSort = code;
                    svc.Store.Save();
                    page = 0;
                    Recompose();
                    onHudChanged?.Invoke();
                }, EB(152, y - 2, 150, 26), "quest-sort");
            }

            // One bulk toggle instead of a confirm dialog per item: unchecking loses nothing,
            // so it needs no confirmation — that is the whole point of parking over unpinning.
            bool anyActive = svc.Store.Pins.Any(p => p.Active)
                || svc.Store.SiteQuests.Any(s => !s.Dismissed && s.Active);
            c.AddSmallButton(anyActive ? "Uncheck all" : "Check all",
                () => { svc.Store.SetAllActive(!anyActive); return true; },
                EB(DW - 112, y - 2, 112, 26), EnumButtonStyle.Small);
            y += 36;

            // Each header runs to the next *header*, not the next column — the stepper column
            // has no label of its own, so bounding "Have / Want" at its edge left too little
            // room and the text wrapped onto the row of numbers below. Fitted as well, so a
            // translation longer than its space truncates rather than wrapping.
            var headFont = font.Clone().WithColor(GuiStyle.ColorParchment);
            void Head(string text, double x, double w)
                => c.AddStaticText(TbText.Fit(headFont, text, w), headFont, EB(x, y, w, 22));

            Head("Item", ColName, ColProg - ColName - 8);
            Head("Have / Want", ColProg, ColAct1 - ColProg - 8);
            Head("Actions", ColAct1, DW - ColAct1 - 8);
            y += 22;
            c.AddGameOverlay(EB(0, y, DW, 2), GuiStyle.DialogBorderColor);
            y += 6;

            foreach (var row in VisibleRows())
            {
                ComposeRow(c, row, ref y);
            }

            y += 8;
            if (notice.Length > 0)
            {
                c.AddStaticText(notice, font.Clone().WithColor(GuiStyle.ErrorTextColor), EB(8, y, DW, 24));
            }
            y += 28;

            c.AddSmallButton("Clear all", () => { screen = TbScreen.ConfirmClear; Recompose(); return true; },
                EB(8, y, 92, 28), EnumButtonStyle.Small);

            if (MaxPage > 0)
            {
                c.AddSmallButton("< Prev", () => { if (page > 0) { page--; Recompose(); } return true; },
                    EB(DW / 2 - 130, y, 78, 28), EnumButtonStyle.Small);
                c.AddStaticText($"Page {page + 1}/{MaxPage + 1}", font, EB(DW / 2 - 44, y + 5, 90, 24));
                c.AddSmallButton("Next >", () => { if (page < MaxPage) { page++; Recompose(); } return true; },
                    EB(DW / 2 + 52, y, 78, 28), EnumButtonStyle.Small);
            }

            c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
        }

        /// <summary>
        /// The story's current step, above the errand table. Shows exactly one step — the one
        /// the player is on — and only once the game itself has revealed it (the tracker's
        /// reveal gates); before the story finds the player, this draws nothing at all, so
        /// the tab cannot become a table of contents for unplayed content.
        /// </summary>
        void ComposeStoryBlock(GuiComposer c, ref double y)
        {
            if (story == null || !story.Enabled || !story.AnyRevealed) return;

            var step = story.Current;
            var titleFont = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2));
            string title = step != null ? $"The story so far — {step.Title}" : "The story so far";
            c.AddStaticText(title, titleFont, EB(8, y, DW - 16, 26));
            y += 28;

            var quiet = TableFont().Clone().WithColor(GuiStyle.ColorParchment);
            var paragraphs = new List<string>();
            if (step == null)
            {
                paragraphs.Add(story.AllDone
                    ? "You have followed the story to its end — for now."
                    : "The story waits on what you do next.");
            }
            else
            {
                paragraphs.Add(step.Text);
                string detail = null;
                try { detail = step.Detail?.Invoke(); } catch { }
                if (!string.IsNullOrEmpty(detail)) paragraphs.Add(detail);
            }

            foreach (var para in paragraphs)
            {
                foreach (var line in TbText.Wrap(quiet, para, DW - 40))
                {
                    c.AddStaticText(line, quiet, EB(16, y, DW - 24, 22));
                    y += LineStep;
                }
                y += 4;
            }

            c.AddGameOverlay(EB(0, y, DW, 2), GuiStyle.DialogBorderColor);
            y += 8;
        }

        /// <summary>
        /// The world's rules: every world-config setting the installed mods declare, grouped
        /// under the create-world screen's own category headings, resolved against what this
        /// world actually runs with. Values that differ from the game's defaults draw in the
        /// partial colour — "what has been changed on this server" being the question that
        /// brings a player to this tab.
        /// </summary>
        /// <summary>The Player tab: a short table of rows BuildPlayerRows chose. No filter
        /// and no column heads — a dozen self-labelled rows do not need furniture.</summary>
        void ComposePlayer(GuiComposer c, ref double y)
        {
            var font = TableFont();

            if (allRows.Count == 0)
            {
                c.AddStaticText("Nothing to show yet — the world is still loading.",
                    font, EB(8, y + 8, DW, 26));
                y += 60;
                c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
                return;
            }

            foreach (var row in VisibleRows())
            {
                ComposeRow(c, row, ref y);
            }

            ComposeSpawnHudControls(c, font, ref y);

            y += 8;
            if (MaxPage > 0)
            {
                c.AddSmallButton("< Prev", () => { if (page > 0) { page--; Recompose(); } return true; },
                    EB(DW / 2 - 130, y, 78, 28), EnumButtonStyle.Small);
                c.AddStaticText($"Page {page + 1}/{MaxPage + 1}", font, EB(DW / 2 - 44, y + 5, 90, 24));
                c.AddSmallButton("Next >", () => { if (page < MaxPage) { page++; Recompose(); } return true; },
                    EB(DW / 2 + 52, y, 78, 28), EnumButtonStyle.Small);
            }
            c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
        }

        /// <summary>
        /// The Player tab's own settings, at the foot of the table: a HUD line with the
        /// distance back to your current spawn, and — only once that is on — an optional
        /// warning distance that turns the line the partial colour when you range past it.
        /// Same switch/label/"?" idiom as the Options screen; the distance is a typed field
        /// guarded by the same restore/typing-grace machinery as the count fields.
        /// </summary>
        void ComposeSpawnHudControls(GuiComposer c, CairoFont font, ref double y)
        {
            var hint = font.Clone().WithColor(GuiStyle.LinkTextColor);

            y += 6;
            c.AddGameOverlay(EB(0, y, DW, 2), GuiStyle.DialogBorderColor);
            y += 8;

            c.AddSwitch(v =>
            {
                config.HudSpawnDistance = v;
                capi.StoreModConfig(config, "tallybook.json");
                onHudChanged?.Invoke();
                Recompose();   // the warning sub-row appears and leaves with this switch
            }, EB(8, y, 25, 25), "opt-spawndist", 25);
            c.AddStaticText(TbText.Fit(font, "Show my distance from spawn in the HUD", 380),
                font, EB(44, y + 4, 380, 26));
            c.AddStaticText("?", hint, EB(430, y + 4, 16, 24));
            c.AddHoverText("A HUD line with how far you are from where you would respawn "
                + "right now — your temporal-gear returning point when one is set, "
                + "otherwise the world spawn.", font, 340, EB(430, y + 4, 16, 24));
            y += 32;

            if (config.HudSpawnDistance)
            {
                c.AddSwitch(v =>
                {
                    config.HudSpawnDistanceWarn = v;
                    capi.StoreModConfig(config, "tallybook.json");
                    onHudChanged?.Invoke();
                }, EB(40, y, 25, 25), "opt-spawnwarn", 25);
                c.AddStaticText(TbText.Fit(font, "Colour it as a warning beyond", 240),
                    font, EB(76, y + 4, 240, 26));
                c.AddTextInput(EB(322, y, 76, 26), OnSpawnWarnBlocksTyped, font, "spawnwarn-blocks");
                c.AddStaticText("blocks", font, EB(404, y + 4, 56, 26));

                // The colour it turns. A dropdown of named colours rather than a hex field:
                // choosing a colour by eye is the job, and the config file still accepts any
                // hex — an unlisted one shows up here as Custom rather than being clobbered.
                var (colorValues, colorNames, colorIdx) = SpawnWarnColorChoices();
                c.AddDropDown(colorValues, colorNames, colorIdx, (code, _) =>
                {
                    config.HudSpawnDistanceWarnColor = code;
                    capi.StoreModConfig(config, "tallybook.json");
                    onHudChanged?.Invoke();
                }, EB(466, y, 120, 26), "spawnwarn-color");

                c.AddStaticText("?", hint, EB(600, y + 4, 16, 24));
                c.AddHoverText("Optional. Past this many blocks from spawn, the HUD line "
                    + "turns the colour picked here — a leash length for how far you are "
                    + "comfortable ranging from a respawn. Leave the switch off and the "
                    + "line never changes colour.", font, 340, EB(600, y + 4, 16, 24));
                y += 32;
            }
        }

        /// <summary>The warning-colour dropdown's entries, with the config's current colour
        /// selected. A hex someone wrote into the file that matches no preset is offered as
        /// Custom so the dropdown never lies about, or overwrites, their choice.</summary>
        (string[] Values, string[] Names, int Selected) SpawnWarnColorChoices()
        {
            var presets = new (string Hex, string Name)[]
            {
                ("#FF4040", "Red"),
                ("#FFA040", "Orange"),
                ("#FFE060", "Yellow"),
                ("#80FF80", "Green"),
                ("#4FC3F7", "Blue"),
                ("#C080FF", "Purple"),
                ("#FFFFFF", "White"),
            };

            string current = config.HudSpawnDistanceWarnColor ?? "#FF4040";
            var values = presets.Select(p => p.Hex).ToList();
            var names = presets.Select(p => p.Name).ToList();

            int idx = values.FindIndex(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                values.Insert(0, current);
                names.Insert(0, $"Custom ({current})");
                idx = 0;
            }
            return (values.ToArray(), names.ToArray(), idx);
        }

        /// <summary>What ComposeSpawnHudControls will add below the rows — the pager must
        /// budget for it or the last row lands under the controls.</summary>
        double SpawnHudControlsHeight => 14 + 32 + (config.HudSpawnDistance ? 32 : 0);

        /// <summary>Every volume — found or not — inside the current source and kind
        /// filters. The intro's totals come from this, so filtering to one mod re-counts
        /// the top line too (Mark); only the status chips slice further.</summary>
        List<LoreBook.Volume> LoreScopeVolumes()
        {
            var vols = (loreModel?.Volumes ?? new List<LoreBook.Volume>())
                .Where(v => v.IsStory == null || (v.IsStory == true ? loreShowStory : loreShowWorld));
            if (loreSource != null)
                vols = vols.Where(v => string.Equals(v.SourceKey, loreSource, StringComparison.OrdinalIgnoreCase));
            return vols.ToList();
        }

        // Volumes and chapters only — an earlier draft also counted lore "kinds" (the
        // game's internal draw pools), and it read as double-counting the volumes with
        // extra words attached (Mark: "confusing and too verbose"). Do not bring it back.
        string LoreIntroText()
        {
            if (loreModel == null) return "";
            var scope = LoreScopeVolumes();
            int totalVols = scope.Count;
            int foundVols = scope.Count(v => v.FoundChapters > 0);
            int totalCh = scope.Sum(v => v.TotalChapters);
            int foundCh = scope.Sum(v => v.FoundChapters);

            // Filtered numbers say whose they are — a total that shrank without a name
            // on it reads as lore going missing.
            string prefix = loreSource == null ? ""
                : (scope.FirstOrDefault()?.Source ?? loreSource) + " — ";
            return $"{prefix}You have discovered {foundVols} of {totalVols} volumes — "
                + $"{foundCh} of {totalCh} chapters. "
                + $"{totalVols - foundVols} volume(s) are still hidden in the world.";
        }

        /// <summary>What the intro and slice buttons cost the table — the pager must know
        /// where the volume cells actually start, plus the button row at the foot.</summary>
        double LoreHeaderHeight()
            => TbText.Wrap(TableFont(), LoreIntroText(), DW - 16).Count * LineStep + 6 + 34 + 36;

        /// <summary>
        /// The Lore tab: intro numbers, then the found volumes sliced by state — All /
        /// In progress / Complete — two to a row, each with a Read button that opens the
        /// game's journal on that entry. Undiscovered lore is the counts in the intro and
        /// nothing else.
        /// </summary>
        void ComposeLore(GuiComposer c, ref double y)
        {
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);

            foreach (var line in TbText.Wrap(quiet, LoreIntroText(), DW - 16))
            {
                c.AddStaticText(line, quiet, EB(8, y, DW - 16, 22));
                y += LineStep;
            }
            y += 6;

            if (loreModel != null && loreModel.FoundVolumes > 0)
            {
                // Each control's counts are scoped by the OTHER filters, so every number
                // answers "what would I see if I clicked this" — except the source
                // dropdown, which always lists every source or a filtered-out mod could
                // never be filtered back in.
                var allFound = loreModel.Found;
                var sourceScoped = loreSource == null ? allFound
                    : allFound.Where(v => string.Equals(v.SourceKey, loreSource, StringComparison.OrdinalIgnoreCase)).ToList();
                var kindScoped = sourceScoped
                    .Where(v => v.IsStory == null || (v.IsStory == true ? loreShowStory : loreShowWorld)).ToList();

                int inProgress = kindScoped.Count(v => !LoreComplete(v));
                double chipY = y;   // a local copy: a ref parameter cannot enter a lambda
                void Chip(string key, string label, int count, double x, double w)
                {
                    bool selected = loreFilter == key;
                    c.AddSmallButton((selected ? "▶ " : "") + $"{label} ({count})",
                        () => { loreFilter = key; page = 0; Recompose(); return true; },
                        EB(x, chipY, w, 26), selected ? EnumButtonStyle.Normal : EnumButtonStyle.Small);
                }
                Chip("all", "All", kindScoped.Count, 8, 110);
                Chip("progress", "In progress", inProgress, 126, 150);
                Chip("complete", "Complete", kindScoped.Count - inProgress, 284, 150);

                // Where the found lore comes from more than one mod, a dropdown narrows
                // the tab to one source — "whatever mod" is an open set, which is what
                // makes this a dropdown rather than a chip per mod.
                var sources = allFound.GroupBy(v => v.SourceKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.Key, g.First().Source, Count: g.Count()))
                    .OrderBy(s => string.Equals(s.Key, "game", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sources.Count > 1)
                {
                    var values = new List<string> { "all" };
                    var names = new List<string> { $"All sources ({allFound.Count})" };
                    foreach (var s in sources)
                    {
                        values.Add(s.Key);
                        names.Add($"{s.Source} ({s.Count})");
                    }
                    int sel = loreSource == null ? 0
                        : Math.Max(0, values.FindIndex(v => string.Equals(v, loreSource, StringComparison.OrdinalIgnoreCase)));
                    c.AddDropDown(values.ToArray(), names.ToArray(), sel, (code, _) =>
                    {
                        loreSource = code == "all" ? null : code;
                        page = 0;
                        Recompose();
                    }, EB(442, chipY, 148, 26), "lore-source");
                }

                // Kind toggles, only once the scan has classified AND both kinds actually
                // occur in the current source's found set — a lone toggle that can only
                // empty the list is furniture. √/· and never a checkbox glyph (the fonts
                // have no ☐/☑).
                int storyCount = sourceScoped.Count(v => v.IsStory == true);
                int worldCount = sourceScoped.Count(v => v.IsStory == false);
                if (storyCount > 0 && worldCount > 0)
                {
                    void KindToggle(string label, int count, bool on, Action flip, double x, double w, string explain)
                    {
                        c.AddSmallButton($"{(on ? "√" : "·")} {label} ({count})",
                            () => { flip(); page = 0; Recompose(); return true; },
                            EB(x, chipY, w, 26), EnumButtonStyle.Small);
                        c.AddHoverText(explain, TableFont(), 340, EB(x, chipY, w, 26));
                    }
                    KindToggle("World lore", worldCount, loreShowWorld,
                        () => loreShowWorld = !loreShowWorld, DW - 336, 160,
                        "Writings the wider world can drop — ruins, vessels, dungeons.");
                    KindToggle("Story lore", storyCount, loreShowStory,
                        () => loreShowStory = !loreShowStory, DW - 168, 160,
                        "Writings held only by the story's own places — recognised from "
                        + "the world's files, not a hand-kept list.");
                }
            }
            y += 34;

            foreach (var row in VisibleRows())
            {
                ComposeRow(c, row, ref y);
            }

            y += 8;
            if (MaxPage > 0)
            {
                c.AddSmallButton("< Prev", () => { if (page > 0) { page--; Recompose(); } return true; },
                    EB(DW / 2 - 130, y, 78, 28), EnumButtonStyle.Small);
                c.AddStaticText($"Page {page + 1}/{MaxPage + 1}", font, EB(DW / 2 - 44, y + 5, 90, 24));
                c.AddSmallButton("Next >", () => { if (page < MaxPage) { page++; Recompose(); } return true; },
                    EB(DW / 2 + 52, y, 78, 28), EnumButtonStyle.Small);
            }

            c.AddSmallButton("Open journal",
                () => { if (lore?.OpenJournal() == true) journalSideBySide = true; return true; },
                EB(8, y, 120, 28));
            c.AddHoverText("The game's own journal — everything here is read from it.",
                font, 340, EB(8, y, 120, 28));

            c.AddSmallButton("Export book", () => { ExportLoreBook(); return true; },
                EB(136, y, 120, 28));
            c.AddHoverText("Writes everything you have found as a single printable HTML "
                + "book — open it in a browser and print to PDF from there. Only found "
                + "chapters go in.", font, 340, EB(136, y, 120, 28));

            c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
        }

        // ---- side-by-side with the journal ----------------------------------------------

        /// <summary>While true, this window and the game's journal are held side by side
        /// instead of stacked in the screen centre — both are centre-aligned dialogs, so
        /// Read would otherwise open the journal exactly on top of the list it came from.
        /// Set by the Read / Open journal buttons, dropped (and both windows re-centred)
        /// when either closes. Re-applied every frame because BOTH dialogs rebuild their
        /// bounds on every recompose — a one-shot nudge lasts exactly until the journal's
        /// next internal redraw snapped it back to centre.</summary>
        bool journalSideBySide;

        public override void OnFinalizeFrame(float dt)
        {
            base.OnFinalizeFrame(dt);
            if (!journalSideBySide) return;

            var journal = lore?.JournalDialog();
            if (journal == null || !journal.IsOpened() || !IsOpened())
            {
                EndSideBySide();
                return;
            }

            try
            {
                double scale = RuntimeEnv.GUIScale;
                double screenW = capi.Render.FrameWidth / scale;
                var mine = SingleComposer?.Bounds;
                if (mine == null) return;
                double myW = mine.OuterWidth / scale;

                double jW = 0;
                foreach (var composer in journal.Composers.Values)
                {
                    var b = composer?.Bounds;
                    if (b != null) jW = Math.Max(jW, b.OuterWidth / scale);
                }
                if (jW <= 0 || myW <= 0) return;

                // Both windows centred as one block; on a screen too narrow for that, this
                // window keeps the left edge and the journal takes what remains at the right.
                const double gap = 10;
                double left = Math.Max(4, (screenW - (myW + gap + jW)) / 2);
                ApplyDialogOffset(mine, left - (screenW - myW) / 2);

                double jLeft = Math.Min(left + myW + gap, Math.Max(0, screenW - jW - 4));
                double jOffset = jLeft - (screenW - jW) / 2;
                foreach (var composer in journal.Composers.Values)
                {
                    if (composer?.Bounds != null) ApplyDialogOffset(composer.Bounds, jOffset);
                }
            }
            catch { journalSideBySide = false; }   // cosmetics never get to throw per frame
        }

        void EndSideBySide()
        {
            journalSideBySide = false;
            try
            {
                if (SingleComposer?.Bounds != null) ApplyDialogOffset(SingleComposer.Bounds, 0);
                var journal = lore?.JournalDialog();
                if (journal != null)
                {
                    foreach (var composer in journal.Composers.Values)
                    {
                        if (composer?.Bounds != null) ApplyDialogOffset(composer.Bounds, 0);
                    }
                }
            }
            catch { }
        }

        static void ApplyDialogOffset(ElementBounds bounds, double offsetX)
        {
            if (Math.Abs(bounds.fixedOffsetX - offsetX) < 0.5) return;
            bounds.WithFixedAlignmentOffset(offsetX, bounds.fixedOffsetY);
            bounds.CalcWorldBounds();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (journalSideBySide) EndSideBySide();
        }

        // ---- Explore tab ----------------------------------------------------------------

        void BuildExploreRows()
        {
            // The Side quests tab's contract, verbatim (Mark: "should look a lot more like
            // the side quests tab"): checked rides the HUD, unchecked is parked — one
            // dimmed header row, kept and saved. The notes additionally sit behind the
            // same leading +/− an errand's conversation uses (Mark: without the fold,
            // "the window will get huge") — opened state remembered per place.
            foreach (var place in svc.Store.Places)
            {
                allRows.Add(new PlaceRow { Place = place });
                if (!place.ShowOnHud || !place.NotesExpanded) continue;

                var lines = NotesLines(place);
                for (int i = 0; i < lines.Length; i++)
                    allRows.Add(new PlaceNoteRow { Place = place, Index = i, Indent = 1 });
            }
        }

        /// <summary>
        /// The Explore tab: save the spot you are standing on with a name and a one-line
        /// "what it is", then a table of your saved places — distance back, a HUD switch, a
        /// Map button, longer notes behind a fold, and a two-click Remove (a place's notes
        /// go with it, so it asks twice — but never a dialog).
        /// </summary>
        void ComposeExplore(GuiComposer c, ref double y)
        {
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);

            c.AddStaticText("Name", font, EB(8, y + 4, 48, 24));
            c.AddTextInput(EB(58, y, 210, 26), OnExploreNameTyped, font, "explore-name");
            c.AddStaticText("What is it?", font, EB(280, y + 4, 86, 24));
            c.AddTextInput(EB(368, y, 240, 26), OnExploreNoteTyped, font, "explore-note");
            c.AddSmallButton("Save this spot", () => SaveSpotClicked(), EB(618, y, 130, 26));
            c.AddHoverText("Saves where you are standing right now — with a map marker, and "
                + "a switch to keep the distance back on the HUD. Longer notes can be added "
                + "on the row afterwards.", font, 300, EB(618, y, 130, 26));
            y += 36;

            if (svc.Store.Places.Count == 0)
            {
                c.AddStaticText("No places saved yet.", font, EB(8, y + 8, DW, 26));
                c.AddStaticText("Standing somewhere worth coming back to — a mine, a ruin, a "
                    + "cave mouth — name it above and save it. '.tallybook spot <name>' does "
                    + "the same without opening this window.",
                    quiet, EB(8, y + 38, DW, 46));
                y += 96;
                c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
                return;
            }

            foreach (var row in VisibleRows())
            {
                ComposeRow(c, row, ref y);
            }

            y += 8;
            if (MaxPage > 0)
            {
                c.AddSmallButton("< Prev", () => { if (page > 0) { page--; Recompose(); } return true; },
                    EB(DW / 2 - 130, y, 78, 28), EnumButtonStyle.Small);
                c.AddStaticText($"Page {page + 1}/{MaxPage + 1}", font, EB(DW / 2 - 44, y + 5, 90, 24));
                c.AddSmallButton("Next >", () => { if (page < MaxPage) { page++; Recompose(); } return true; },
                    EB(DW / 2 + 52, y, 78, 28), EnumButtonStyle.Small);
            }
            c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
        }

        void OnExploreNameTyped(string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            exploreName = val;
        }

        void OnExploreNoteTyped(string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            exploreNote = val;
        }

        /// <summary>The notes as display lines. Empty notes are a single empty array —
        /// no rows — and blank lines inside real notes stay, as paragraph spacing.</summary>
        static string[] NotesLines(SavedPlace place)
            => place.HasNotes
                ? place.NotesText.Replace("\r\n", "\n").Split('\n')
                : Array.Empty<string>();

        /// <summary>What a note line means: 0 plain, 1 bullet ("- " / "* "),
        /// 2 unchecked checkbox ("[ ]"), 3 checked ("[x]") — bullets may carry the
        /// checkbox too ("- [ ] fetch props").</summary>
        static (int Kind, string Text) ParseNoteLine(string line)
        {
            string t = (line ?? "").TrimStart();
            bool bullet = t.StartsWith("- ") || t.StartsWith("* ");
            if (bullet) t = t.Substring(2).TrimStart();

            if (t.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
                return (3, t.Substring(3).TrimStart());
            if (t.StartsWith("[]") || t.StartsWith("[ ]"))
                return (2, t.Substring(t.StartsWith("[]") ? 2 : 3).TrimStart());
            return (bullet ? 1 : 0, bullet ? t : line);
        }

        /// <summary>Tick or untick a checkbox line from the reading view — the one edit
        /// that does not need the editor window, because it IS the point of a checkbox.</summary>
        void ToggleNoteCheckbox(SavedPlace place, int index)
        {
            var lines = NotesLines(place);
            if (index >= lines.Length) return;
            string line = lines[index];

            int at = line.IndexOf("[x]", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) line = line.Substring(0, at) + "[ ]" + line.Substring(at + 3);
            else if ((at = line.IndexOf("[ ]")) >= 0) line = line.Substring(0, at) + "[x]" + line.Substring(at + 3);
            else if ((at = line.IndexOf("[]")) >= 0) line = line.Substring(0, at) + "[x]" + line.Substring(at + 2);
            else return;

            lines[index] = line;
            place.NotesText = string.Join("\n", lines);
            svc.Store.Save();
            svc.RecountAll();
            Recompose();
        }

        bool OnRemovePlaceClicked(SavedPlace place)
        {
            if (!config.ConfirmOnUnpin)
            {
                RemovePlaceNow(place);
                return true;
            }
            // Click fires on mouse-up. If the hold ran to completion the place is already
            // gone; a tap that never finished the countdown lands here.
            if (svc.Store.Places.Contains(place))
            {
                notice = "Hold Remove for a second to let a place go — its notes go with it.";
                Recompose();
            }
            return true;
        }

        void RemovePlaceNow(SavedPlace place)
        {
            explore?.Remove(place);
            svc.RecountAll();
            Recompose();
            onHudChanged?.Invoke();
        }

        bool OpenPlaceEditor(SavedPlace place)
        {
            editingPlace = place;
            editingNameDraft = place.Name ?? "";
            editingNoteDraft = place.Note ?? "";
            editingNotesDraft = place.NotesText ?? "";
            screen = TbScreen.EditPlace;
            Recompose();
            return true;
        }

        void OnEditNameTyped(string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            editingNameDraft = val;
        }

        void OnEditWhatTyped(string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            editingNoteDraft = val;
        }

        void OnEditNotesTyped(string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            editingNotesDraft = val;
        }

        /// <summary>The toolbar's insert: a fresh line carrying the marker, appended at the
        /// end of the draft — cursor-position insertion needs caret internals the text area
        /// does not expose, and the end is where a new entry goes anyway.</summary>
        bool AppendNoteMarker(string marker)
        {
            string d = editingNotesDraft ?? "";
            if (d.Length > 0 && !d.EndsWith("\n")) d += "\n";
            editingNotesDraft = d + marker;
            RestoreEditPlace();
            return true;
        }

        bool SavePlaceEditor()
        {
            if (editingPlace != null)
            {
                explore?.Apply(editingPlace, editingNameDraft, editingNoteDraft, editingNotesDraft);
                editingPlace.NotesExpanded = editingPlace.HasNotes;   // show what was just written
                svc.RecountAll();
                onHudChanged?.Invoke();
            }
            editingPlace = null;
            BackToList();
            return true;
        }

        /// <summary>
        /// The place editor, in its own window (Mark) — name and "what is it" up top, then
        /// the notes with a small formatting toolbar: Bullet and Checkbox put their marker
        /// on a fresh line, ready to type after. Save writes everything; Cancel walks away
        /// untouched.
        /// </summary>
        void ComposeEditPlace(GuiComposer c)
        {
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);
            double y = 40;

            c.AddStaticText($"Edit — {editingPlace?.Name}",
                CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2)),
                EB(8, y, DW - 16, 26));
            y += 34;

            c.AddStaticText("Name", font, EB(8, y + 4, 48, 24));
            c.AddTextInput(EB(58, y, 240, 26), OnEditNameTyped, font, "edit-name");
            c.AddStaticText("What is it?", font, EB(312, y + 4, 86, 24));
            c.AddTextInput(EB(400, y, 240, 26), OnEditWhatTyped, font, "edit-what");
            y += 36;

            c.AddStaticText("Notes", font, EB(8, y + 4, 60, 24));
            c.AddSmallButton("• Bullet", () => AppendNoteMarker("- "),
                EB(70, y, 84, 26), EnumButtonStyle.Small);
            c.AddSmallButton("√ Checkbox", () => AppendNoteMarker("[ ] "),
                EB(160, y, 110, 26), EnumButtonStyle.Small);
            c.AddHoverText("A checkbox line can be ticked off later with a click, straight "
                + "from the list.", font, 260, EB(160, y, 110, 26));
            y += 32;

            c.AddTextArea(EB(8, y, DW - 16, 300), OnEditNotesTyped, font, "place-notes");
            y += 312;

            c.AddSmallButton("Save", () => SavePlaceEditor(), EB(8, y, 90, 30));
            c.AddSmallButton("Cancel", () => { editingPlace = null; BackToList(); return true; },
                EB(106, y, 90, 30));
        }

        /// <summary>Inputs compose empty; hand them the drafts and the notes the cursor.</summary>
        void RestoreEditPlace()
        {
            restoringInputs = true;
            try
            {
                SingleComposer.GetTextInput("edit-name")?.SetValue(editingNameDraft ?? "");
                var what = SingleComposer.GetTextInput("edit-what");
                if (what != null)
                {
                    what.SetPlaceHolderText("mine, ruin, cave…");
                    what.SetValue(editingNoteDraft ?? "");
                }
                var area = SingleComposer.GetTextArea("place-notes");
                if (area != null)
                {
                    area.SetValue(editingNotesDraft ?? "");
                    SingleComposer.FocusElement(area.TabIndex);
                }
            }
            finally { restoringInputs = false; }
        }

        bool SaveSpotClicked()
        {
            var place = explore?.SaveHere(exploreName, exploreNote);
            if (place == null)
            {
                capi.ShowChatMessage("[tallybook] Give the place a name first.");
                return true;
            }
            exploreName = "";
            exploreNote = "";
            svc.RecountAll();
            Recompose();
            onHudChanged?.Invoke();
            return true;
        }

        /// <summary>One volume in the two-a-row grid: title (hover gives the full text when
        /// trimmed), chapter count in the status colour, and Read — the journal opened
        /// straight on this entry.</summary>
        void ComposeLoreCell(GuiComposer c, LoreBook.Volume vol, double x, double y)
        {
            if (vol == null) return;
            var font = TableFont();
            double cellW = DW / 2 - 24;
            const double readW = 58, countW = 86;
            double titleW = cellW - countW - readW - 16;

            FittedText(c, vol.Title, font, EB(x, y + 4, titleW, 24), titleW);

            var countFont = font.Clone().WithColor(StatusColor(vol.FoundChapters, vol.TotalChapters));
            string count = LoreComplete(vol)
                ? $"√ {vol.FoundChapters}/{vol.TotalChapters}"
                : $"{vol.FoundChapters}/{vol.TotalChapters}";
            c.AddStaticText(count, countFont, EB(x + titleW + 8, y + 4, countW, 24));

            c.AddSmallButton("Read",
                () => { if (lore?.OpenJournal(vol.Code) == true) journalSideBySide = true; return true; },
                EB(x + cellW - readW, y, readW, 26), EnumButtonStyle.Small);
        }

        /// <summary>Write the book and say where it landed — in chat, so the path survives
        /// closing the window and can be copied. A failure says so rather than half-acting.</summary>
        void ExportLoreBook()
        {
            if (lore == null) return;
            string path = lore.ExportBook(out int volumes, out int chapters);
            if (path == null)
            {
                capi.ShowChatMessage("[tallybook] Nothing to export yet — no lore found, "
                    + "or the file could not be written (see client log).");
                return;
            }
            capi.ShowChatMessage($"[tallybook] Lore book written — {volumes} volume(s), "
                + $"{chapters} chapter(s): {path} — open it in a browser and print to PDF "
                + "from there.");
        }

        string WorldIntroText()
        {
            var ba = capi.World.BlockAccessor;
            return $"Seed {capi.World.Seed} — {ba.MapSizeX}×{ba.MapSizeZ} blocks, "
                + $"{ba.MapSizeY} tall. Coloured values differ from the game's defaults; "
                + "hover a setting for what it does.";
        }

        /// <summary>What the intro and filter row cost the table, measured the same way
        /// ComposeWorld draws them — the pager must know where the rows actually start.</summary>
        double WorldHeaderHeight()
            => TbText.Wrap(TableFont(), WorldIntroText(), DW - 16).Count * LineStep + 6 + 34;

        void ComposeWorld(GuiComposer c, ref double y)
        {
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);

            foreach (var line in TbText.Wrap(quiet, WorldIntroText(), DW - 16))
            {
                c.AddStaticText(line, quiet, EB(8, y, DW - 16, 22));
                y += LineStep;
            }
            y += 6;

            bool filtering = !string.IsNullOrWhiteSpace(worldFilter);
            if (allRows.Count == 0 && !filtering)
            {
                c.AddStaticText("No installed mod declares world settings.", font, EB(8, y + 8, DW, 26));
                y += 60;
                c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
                return;
            }

            c.AddStaticText("Filter", font, EB(8, y + 4, 56, 26));
            c.AddTextInput(EB(66, y, 250, 26), OnWorldFilterTyped, font, "world-filter");
            if (filtering)
            {
                c.AddSmallButton("×", () =>
                {
                    worldFilter = "";
                    page = 0;
                    Recompose();
                    return true;
                }, EB(322, y, 26, 26), EnumButtonStyle.Small);
            }
            y += 34;

            var headFont = font.Clone().WithColor(GuiStyle.ColorParchment);
            c.AddStaticText("Setting", headFont, EB(WColName, y, 200, 22));
            c.AddStaticText("Value", headFont, EB(WColValue, y, 200, 22));
            y += 22;
            c.AddGameOverlay(EB(0, y, DW, 2), GuiStyle.DialogBorderColor);
            y += 6;

            foreach (var row in VisibleRows())
            {
                ComposeRow(c, row, ref y);
            }

            y += 8;
            if (MaxPage > 0)
            {
                c.AddSmallButton("< Prev", () => { if (page > 0) { page--; Recompose(); } return true; },
                    EB(DW / 2 - 130, y, 78, 28), EnumButtonStyle.Small);
                c.AddStaticText($"Page {page + 1}/{MaxPage + 1}", font, EB(DW / 2 - 44, y + 5, 90, 24));
                c.AddSmallButton("Next >", () => { if (page < MaxPage) { page++; Recompose(); } return true; },
                    EB(DW / 2 + 52, y, 78, 28), EnumButtonStyle.Small);
            }
            c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
        }

        /// <summary>
        /// Every way of making this thing, with the current one marked — so choosing between
        /// them is a choice about what you would go and gather, which is the actual question,
        /// rather than about the number on a button.
        /// </summary>
        static string RecipeChoiceHelp(List<RecipeVariantGroup> choices, RecipeVariantGroup current)
        {
            var lines = new List<string> { "Ways to make this — click to change:" };

            for (int i = 0; i < choices.Count; i++)
            {
                string materials = string.IsNullOrEmpty(choices[i].Materials) ? "?" : choices[i].Materials;
                lines.Add($"{(choices[i] == current ? "▶" : "  ")} {i + 1}. {materials}");
            }
            return string.Join("\n", lines);
        }

        /// <summary>Name column width at this indent, leaving the progress column clear.</summary>
        static double NameW(double indent) => ColProg - (ColName + indent) - 10;

        /// <summary>
        /// Text fitted to its column, with the full text on hover whenever it had to be cut.
        /// A row that reads "Bundle of bamboo sta…" and offers nothing further is a dead end;
        /// the hover is what keeps truncation honest. Added only when something was actually
        /// removed, so short rows carry no pointless hover target.
        /// </summary>
        void FittedText(GuiComposer c, string text, CairoFont font, ElementBounds bounds, double maxW)
        {
            // Fitted a little inside the box, not exactly to it. Fit's measurement and the
            // element's own wrap decision can disagree by a pixel, and when "fits exactly"
            // loses, the last word wraps onto the row below and overdraws it — the screenshot
            // form of this was "(for" on one line and a lone "G" bleeding into the next
            // (Mark). A few units of slack costs an ellipsis a moment earlier; a wrap costs
            // a legible table.
            string shown = TbText.Fit(font, text, maxW - 8);
            c.AddStaticText(shown, font, bounds);
            if (shown == text) return;

            // Its own bounds instance: two elements sharing one ElementBounds fight over
            // layout, since each expects to own the object it was handed.
            c.AddHoverText(text, font, 340,
                EB(bounds.fixedX, bounds.fixedY, bounds.fixedWidth, bounds.fixedHeight));
        }

        void ComposeRow(GuiComposer c, Row row, ref double y)
        {
            var font = TableFont();
            double indent = row.Indent * IndentW;
            double nx = ColName + indent;
            double ry = y;                     // local copies cannot capture the ref parameter

            void Icon(List<ItemStack> stacks)
            {
                if (stacks != null && stacks.Count > 0)
                    c.AddInteractiveElement(new GuiElementItemIcon(capi, stacks, config, EB(ColIcon + indent, ry + 2, 20, 20)));
            }

            void RowHandbookButton(Requirement req, double rowY)
            {
                // Ingredient and tool rows can visit the handbook too (Mark) — any row that
                // names an item should be able to show its page. The sample stack is what
                // the row's icon draws, so the page always matches the picture; on a liquid
                // row that is the liquid itself, not the vessel.
                var stack = req?.SampleStacks(capi.World).FirstOrDefault();
                if (stack?.Collectible == null) return;
                c.AddSmallButton("Handbook", () => OpenHandbookForStack(stack, stack.GetName()),
                    EB(ColBook, rowY, 84, 26), EnumButtonStyle.Small);
            }

            void Progress(int have, int needed, bool dim, Requirement req = null)
            {
                // Only glyphs the game's fonts actually carry (Montserrat/Lora/Almendra have
                // no ✓, ○ or ◑ — those drew as tofu boxes, the "little boxes" of Mark's
                // report; verified against the shipped TTFs' character maps). The radical
                // sign reads as a check, and faint dot → bullet → check is its own progress
                // bar.
                string mark = have >= needed ? "√" : have > 0 ? "•" : "·";
                string counts = req?.CountText(have, needed) ?? $"{have}/{needed}";
                var pf = font.Clone().WithColor(dim ? TallybookConfig.ParseColor(config.ColorNone) : StatusColor(have, needed));
                c.AddStaticText($"{mark} {counts}", pf, EB(ColProg, ry + 4, 80, 24));
            }

            switch (row)
            {
                case RewardRow rr:
                {
                    // Reads as done in the checkbox column — same glyph vocabulary as
                    // everywhere else (the fonts have no real checkmark).
                    var doneFont = font.Clone().WithColor(TallybookConfig.ParseColor(config.ColorSatisfied));
                    c.AddStaticText("√", doneFont, EB(ColCheck + 6, y + 4, 20, 24));

                    string who = rr.Giver ?? rr.Name;
                    string title = $"{rr.Name} — done, collect your reward";
                    if (!string.Equals(who, rr.Name, StringComparison.OrdinalIgnoreCase))
                        title += $" from {who}";

                    var titleFont = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2))
                        .WithColor(TallybookConfig.ParseColor(config.ColorSatisfied));
                    double titleW = ColAct2 - nx - 10;
                    FittedText(c, title, titleFont, EB(nx, ry + 3, titleW, 26), titleW);

                    // Same contract as an errand's Map button: always present, persisted
                    // knowledge only, and clicking with nothing known explains what would
                    // teach us — a button that is sometimes missing reads as broken.
                    var place = PlaceOf(who);
                    c.AddSmallButton("Map", () => ShowOnMapAt(place, who),
                        EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
                    c.AddHoverText(place == null
                            ? $"No location known for {who} yet — talk to them once, stand "
                              + "there and use .tallybook here, or name a map waypoint after them."
                            : $"Open the map centred on {who}.",
                        font, 260, EB(ColAct2, y, 40, 26));
                    break;
                }
                case SiteRow sr:
                {
                    var sq = sr.Site;
                    var count = sites?.LoreCount(sq);
                    bool loreDone = count.HasValue && count.Value.Found >= count.Value.Total;

                    // The same checkbox every pin has: unchecked parks it — off the HUD, no
                    // announcements, row dimmed — and nothing is lost (Mark).
                    c.AddSwitch(on =>
                    {
                        sq.Active = on;
                        svc.Store.Save();
                        svc.RecountAll();
                    }, EB(ColCheck, y + 1, 25, 25), "sitact-" + sq.Key, 25);

                    var mapStack = sites?.SampleStackFor(sq);
                    Icon(mapStack == null ? null : new List<ItemStack> { mapStack });

                    // The found-writings list opens under the row via the same leading toggle
                    // an errand's conversation uses; the column is reserved either way so the
                    // name column stays a column.
                    double siteNameX = nx + 28;
                    if (sq.Active && count.HasValue && count.Value.Total > 0)
                    {
                        c.AddSmallButton(sq.TextExpanded ? "−" : "+", () =>
                        {
                            sq.TextExpanded = !sq.TextExpanded;
                            svc.Store.Save();
                            Recompose();
                            return true;
                        }, EB(nx, y, 24, 26), EnumButtonStyle.Small);
                        c.AddHoverText(sq.TextExpanded
                                ? "Hide what you have found."
                                : "See what you have found here so far.",
                            font, 220, EB(nx, y, 24, 26));
                    }

                    var siteFont = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2));
                    if (!sq.Active)
                        siteFont = siteFont.WithColor(TallybookConfig.ParseColor(config.ColorNone));
                    else if (sq.Visited && loreDone)
                        siteFont = siteFont.WithColor(TallybookConfig.ParseColor(config.ColorSatisfied));
                    string siteTitle = sq.Visited ? $"{sq.Title} — visited" : SiteQuests.VisitPhrase(sq.Title);
                    double siteTitleW = ColProg - siteNameX - 10;
                    FittedText(c, siteTitle, siteFont, EB(siteNameX, ry + 3, siteTitleW, 26), siteTitleW);

                    if (count == null)
                    {
                        // The lore scan has not finished for this world; a number here would
                        // be a guess, and the row says so by saying nothing yet.
                        c.AddStaticText("…", font.Clone().WithColor(GuiStyle.ColorParchment),
                            EB(ColProg, ry + 4, 80, 24));
                    }
                    else if (count.Value.Total > 0)
                    {
                        Progress(count.Value.Found, count.Value.Total, !sq.Active);
                        c.AddHoverText(
                            $"You have found {count.Value.Found} of the {count.Value.Total} "
                            + "writings hidden at this site.",
                            font, 260, EB(ColProg, y, 90, 26));
                    }
                    else
                    {
                        Progress(sq.Visited ? 1 : 0, 1, !sq.Active);
                        c.AddHoverText(
                            "Nothing provable to collect here — reaching the place completes it.",
                            font, 260, EB(ColProg, y, 90, 26));
                    }

                    // Position was captured at adoption, so unlike an errand's this button
                    // always knows where it is going — even if the waypoint is deleted later.
                    c.AddSmallButton("Map", () => ShowOnMapAt(
                            new BlockPos((int)sq.X, (int)sq.Y, (int)sq.Z, 0), sq.Title),
                        EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
                    c.AddHoverText($"Open the map centred on {sq.Title}.",
                        font, 240, EB(ColAct2, y, 40, 26));

                    QuestMoveButtons(c, "site:" + sq.Key, y, font);

                    // One click, no hold: dismissing loses nothing — the quest is kept and
                    // '.tallybook sites track <name>' brings it back.
                    c.AddSmallButton("Dismiss", () =>
                    {
                        sq.Dismissed = true;
                        svc.Store.Save();
                        svc.RecountAll();
                        Recompose();
                        return true;
                    }, EB(ColUnpin, y, 74, 26), EnumButtonStyle.Small);
                    c.AddHoverText(
                        "Set this site aside. Nothing is lost — '.tallybook sites track "
                        + "<name>' brings it back.",
                        font, 260, EB(ColUnpin, y, 74, 26));
                    break;
                }
                case PinRow pr:
                {
                    var pin = pr.Pin;
                    c.AddSwitch(on => svc.Store.SetActive(pin, on), EB(ColCheck, y + 1, 25, 25), "act-" + pin.Key, 25);
                    Icon(pin.Stack == null ? null : new List<ItemStack> { pin.Stack });

                    // Errands carry their conversation; a leading toggle opens it, in the
                    // place a tree control belongs rather than in the crowded action columns.
                    //
                    // The column is reserved on every quest row, toggle or not: a row whose
                    // errand has no captured text otherwise started its name a toggle-width
                    // to the left of its neighbours, and a column that only sometimes exists
                    // reads as a mess rather than a column (Mark, from a screenshot where
                    // every quest row began at a different x).
                    double nameX = pin.QuestGiver != null ? nx + 28 : nx;
                    if (pin.QuestGiver != null && pin.QuestText?.Count > 0)
                    {
                        c.AddSmallButton(pin.QuestTextExpanded ? "−" : "+", () =>
                        {
                            pin.QuestTextExpanded = !pin.QuestTextExpanded;
                            svc.Store.Save();
                            Recompose();
                            return true;
                        }, EB(nx, y, 24, 26), EnumButtonStyle.Small);

                        c.AddHoverText(pin.QuestTextExpanded ? "Hide what was said." : "Read what was said.",
                            font, 200, EB(nx, y, 24, 26));
                    }

                    var titleFont = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2));
                    if (!pin.Active) titleFont = titleFont.WithColor(TallybookConfig.ParseColor(config.ColorNone));
                    else if (pin.Complete || pin.Craftable) titleFont = titleFont.WithColor(TallybookConfig.ParseColor(config.ColorSatisfied));

                    string title = pin.DisplayName;
                    if (pin.QuestGiver != null) title += $"  (for {pin.QuestGiver})";
                    if (pin.Active && pin.Complete) title += " — got it";
                    else if (pin.Active && pin.Craftable) title += " — ready to craft";
                    // Batched recipes say how many rounds cover what is still missing —
                    // pot loads for cooking, barrel seals (with the wait) for ferments —
                    // so a big cook is planned as batches, not as a mystery.
                    if (pin.Active && !pin.Complete)
                    {
                        string batches = TallyTree.BatchText(pin.CountInItems - pin.Have, pin.Group);
                        if (batches != null) title += $" — {batches}";
                    }
                    double titleW = ColProg - nameX - 10;
                    FittedText(c, title, titleFont, EB(nameX, ry + 3, titleW, 26), titleW);

                    Progress(pin.Have, pin.CountInItems, !pin.Active, pin.SelfNode?.Req);

                    // Plain text input, not AddNumberInput: the number input draws its own
                    // up/down spinner arrows, which duplicate the − / + buttons flanking it.
                    // One set of steppers is enough (Mark); typing stays.
                    c.AddSmallButton("-", () => { StepCount(pin, -1); return true; }, EB(ColWant, y, 26, 26), EnumButtonStyle.Small);
                    c.AddTextInput(EB(ColWant + 30, y, 46, 26), val => OnCountTyped(pin, val), font, "cnt-" + pin.Key);
                    c.AddSmallButton("+", () => { StepCount(pin, +1); return true; }, EB(ColWant + 80, y, 26, 26), EnumButtonStyle.Small);
                    if (pin.LiquidUnits)
                    {
                        // The one place the unit switch could surprise: the field itself.
                        c.AddHoverText("Litres — liquids are counted in L.", font, 200,
                            EB(ColWant + 30, y, 46, 26));
                    }

                    if (pin.QuestGiver != null)
                    {
                        // Errands stay undecomposed, but the item itself may well be
                        // craftable — this hands it to the Items tab. The errand row stays
                        // put and keeps counting; the two are separate goals.
                        c.AddSmallButton("Gather", () => SendToItems(pin),
                            EB(ColAct1, y, 80, 26), EnumButtonStyle.Small);
                        c.AddHoverText(
                            $"Add {pin.DisplayName} to the gathering list in Tallybook. "
                            + "The errand keeps its own row and count.",
                            font, 260, EB(ColAct1, y, 80, 26));

                        // Always present, and it says which of the errand's places it means —
                        // "go to the Devastation" and "go back to Tobias" are opposite
                        // directions. With no location known it still shows, and clicking
                        // explains what would teach us one: a button that silently is not
                        // there reads as broken, not as "walk past them once" (Mark —
                        // Agnieszka's row after a relearn, before re-walking the village).
                        bool anywhere = MapTargetFor(pin) != null;
                        c.AddSmallButton("Map", () => ShowOnMap(pin),
                            EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
                        c.AddHoverText(
                            !anywhere
                                ? $"No location known for {pin.QuestGiver} yet — talk to them "
                                  + "once, stand there and use .tallybook here, or name a map "
                                  + "waypoint after them."
                                : GoingToSite(pin)
                                    ? $"Open the map where {string.Join(", ", pin.QuestMaps)} points."
                                      + $" Once you have the goods, this points back to {pin.QuestGiver}."
                                    : $"Open the map centred on {pin.QuestGiver}.",
                            font, 260, EB(ColAct2, y, 40, 26));

                        // Hand-arranging, only under the custom sort — under an active sort
                        // a move would be undone by the next redraw. ^ / v, not ▲ / ▼: the
                        // game's fonts carry no triangle-down (verified glyph set).
                        QuestMoveButtons(c, pin.Key, y, font);
                    }
                    else if (pin.Groups.Count > 0)
                    {
                        // A construction group never answers an ITEM pin's Expand — that
                        // is the one-or-the-other trap (Mark), and worse, a build unfolded
                        // on the item's own pin zeroes out the moment the starter is
                        // carried. Build pins invert the filter: their Expand IS the build.
                        var expandChoices = pin.Groups
                            .Where(g => (g.Construction != null) == pin.BuildSite).ToList();

                        if (expandChoices.Count > 0)
                        {
                            // Same word as on an ingredient row, for the same act: unfold
                            // this item's recipe beneath it. Collapsing returns the pin to
                            // plain counting, which is where anything not pinned from the
                            // handbook starts (Mark) — a recipe existing is not a reason to
                            // assume the player intends to craft rather than gather.
                            c.AddSmallButton(pin.GatherOnly ? "Expand" : "Collapse",
                                () => pin.GatherOnly
                                    ? ExpandOrChoose(pin, null, expandChoices)
                                    : Collapse(pin),
                                EB(ColAct1, y, 80, 26), EnumButtonStyle.Small);

                            c.AddHoverText(pin.GatherOnly
                                    ? "Counting this item only. Expand to show its recipe and plan the craft."
                                    : "Showing this item's recipe. Collapse to go back to just counting it.",
                                font, 260, EB(ColAct1, y, 80, 26));
                        }

                        // A construction-site item (Shipwright's kits, the vanilla rollers,
                        // the sailboat's own page) gets a Build materials button that adds
                        // the BUILD as its own separate pin — this row keeps tracking the
                        // item and its recipe untouched, so the roller craft and the boat
                        // materials count at the same time (Mark).
                        var constructGroup = pin.Groups.FirstOrDefault(g => g.Construction != null);

                        // A build pin with a material variable gets the wood selector: an
                        // oak boat wants oak, and committing here makes every bound row
                        // name and count exactly that (Mark). "Any" returns to the honest
                        // best-single-material counting.
                        if (pin.BuildSite && constructGroup?.BuildMaterialChoices?.Count > 1)
                        {
                            var mats = constructGroup.BuildMaterialChoices;
                            var values = new List<string> { "~any" };
                            var names = new List<string> { $"Any {constructGroup.BuildMaterialName ?? "material"}" };
                            foreach (var m in mats)
                            {
                                values.Add(m);
                                names.Add(Lang.GetIfExists("material-" + m)
                                    ?? char.ToUpperInvariant(m[0]) + m.Substring(1));
                            }
                            int sel = pin.BuildMaterial == null ? 0
                                : Math.Max(0, values.FindIndex(v =>
                                    string.Equals(v, pin.BuildMaterial, StringComparison.OrdinalIgnoreCase)));
                            c.AddDropDown(values.ToArray(), names.ToArray(), sel, (code, _) =>
                            {
                                pin.BuildMaterial = code == "~any" ? null : code;
                                foreach (var g in pin.Groups)
                                {
                                    if (g.Construction == null) continue;
                                    g.BuildMaterial = pin.BuildMaterial;
                                    g.Materials = null;
                                    g.MaterialsBrief = null;
                                }
                                svc.Store.Save();
                                if (pin.Group?.Construction != null) svc.ChoosePinRecipe(pin, pin.Group);
                                else svc.RecountAll();
                            }, EB(ColCalc + 2, y, 112, 26), "buildmat-" + pin.Key);
                        }

                        if (constructGroup != null && !pin.BuildSite)
                        {
                            c.AddSmallButton("Build materials",
                                () => StartConstruction(pin, constructGroup),
                                EB(ColCalc + 2, y, 112, 26), EnumButtonStyle.Small);
                            c.AddHoverText("Add the whole build as its own row: everything "
                                + "the construction site will ask for across its stages — "
                                + "rollers, planks, beams, rope and the rest — counted like "
                                + "any other ingredients. This row stays as it is, so the "
                                + "item and the build track side by side.",
                                font, 300, EB(ColCalc + 2, y, 112, 26));
                        }

                        if (!pin.GatherOnly && expandChoices.Count > 1)
                        {
                            c.AddSmallButton($"{expandChoices.IndexOf(pin.Group) + 1}/{expandChoices.Count}",
                                () => ExpandOrChoose(pin, null, expandChoices),
                                EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);

                            c.AddHoverText(RecipeChoiceHelp(expandChoices, pin.Group),
                                font, 320, EB(ColAct2, y, 40, 26));
                        }
                    }

                    // Liquid pins carry the volume calculator in a column of their own —
                    // "five barrels" instead of litre arithmetic — leaving Expand/Collapse
                    // and the recipe cycler exactly where every other pin has them (a first
                    // version displaced those, and a liquid with twenty recipes silently
                    // planned down the first one with no visible way to choose — Mark).
                    if (pin.LiquidUnits && pin.QuestGiver == null)
                    {
                        c.AddSmallButton("Volume Calc", () => { OpenLiquidCalc(pin); return true; },
                            EB(ColCalc, y, 110, 26), EnumButtonStyle.Small);
                        c.AddHoverText(
                            "Volume calculator — plan by containers (barrels, buckets…) instead of litres.",
                            font, 260, EB(ColCalc, y, 110, 26));
                    }

                    // Back to where pinning started: the handbook page IS pin.Key, so the
                    // page that opens is exactly the variant that was pinned (spec: the
                    // handbook owns recipe layouts; this list owns the counting).
                    c.AddSmallButton("Handbook", () => OpenHandbookFor(pin),
                        EB(ColBook, y, 84, 26), EnumButtonStyle.Small);

                    var ub = EB(ColUnpin, y, 74, 26);
                    string unpinLabel = ReferenceEquals(holdTarget, pin) ? $"Hold {holdShownSecond}…" : "Unpin";
                    c.AddSmallButton(unpinLabel, () => OnUnpinClicked(pin), ub, EnumButtonStyle.Small);
                    holdButtons.Add((pin, ub, () => svc.Store.Remove(pin)));
                    break;
                }

                case NodeRow nr:
                {
                    var node = nr.Node;
                    Icon(node.Req.SampleStacks(capi.World));

                    var nodeFont = font.Clone().WithColor(StatusColor(node.Have, node.Needed));
                    string name = node.Req.DisplayName + (node.ReadyToCraft ? "  (ready to craft)" : "");
                    if (node.Expanded)
                    {
                        string batches = TallyTree.BatchText(node.Needed - node.Have, node.Choice);
                        if (batches != null) name += $"  ({batches})";
                    }
                    FittedText(c, name, nodeFont, EB(nx, ry + 4, NameW(indent), 24), NameW(indent));

                    Progress(node.Have, node.Needed, false, node.Req);

                    if (node.Expanded)
                    {
                        c.AddSmallButton("Collapse", () => { svc.CollapseNode(node); return true; },
                            EB(ColAct1, y, 80, 26), EnumButtonStyle.Small);
                        if (node.Choices != null && node.Choices.Count > 1)
                        {
                            int idx = node.Choices.IndexOf(node.Choice) + 1;
                            c.AddSmallButton($"{idx}/{node.Choices.Count}",
                                () => ExpandOrChoose(nr.Pin, node, node.Choices),
                                EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);

                            c.AddHoverText(RecipeChoiceHelp(node.Choices, node.Choice),
                                font, 320, EB(ColAct2, y, 40, 26));
                        }
                    }
                    else if (svc.HasExpansion(node))
                    {
                        // Only craftable rows carry the affordance (spec §2a); raw materials
                        // get no button rather than one that scolds when clicked.
                        c.AddSmallButton("Expand", () => TryExpand(nr.Pin, node),
                            EB(ColAct1, y, 80, 26), EnumButtonStyle.Small);
                    }

                    RowHandbookButton(node.Req, y);
                    break;
                }

                case ToolRow tr:
                {
                    Icon(tr.Tool.SampleStacks(capi.World));
                    var color = tr.Tool.Present
                        ? TallybookConfig.ParseColor(config.ColorSatisfied)
                        : TallybookConfig.ParseColor(config.ColorNone);
                    var toolFont = font.Clone().WithColor(color);
                    FittedText(c, tr.Tool.DisplayName + " (tool)", toolFont,
                        EB(nx, ry + 4, NameW(indent), 24), NameW(indent));
                    c.AddStaticText(tr.Tool.Present ? "√ carried" : "× missing", toolFont, EB(ColProg, y + 4, 100, 24));

                    RowHandbookButton(tr.Tool, y);
                    break;
                }

                case HeadingRow hr:
                {
                    var hf = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2))
                        .WithColor(GuiStyle.ColorParchment);
                    c.AddStaticText(hr.Text, hf, EB(WColName, y + 10, DW - 16, 26));
                    y += RowH + 8;
                    return;
                }
                case WorldHeadRow whr:
                {
                    // The heading is the fold control, accordion-style: opening one closes
                    // the rest. Folded, it counts what it hides — and says how many of
                    // those differ from the game's defaults, so "which section did the
                    // server touch" survives the folding.
                    bool expanded = WorldSectionExpanded(whr.Key);
                    string extra = whr.Changed > 0 ? $", {whr.Changed} changed" : "";
                    string label = expanded
                        ? $"- {whr.Title}"
                        : $"+ {whr.Title} ({whr.Count}{extra})";
                    c.AddSmallButton(label, () =>
                    {
                        worldOpenSection = expanded ? null : whr.Key;
                        page = 0;   // the section that opened may live on another page
                        Recompose();
                        return true;
                    }, EB(8, y + 4, 430, 26), EnumButtonStyle.Small);
                    y += RowH + 8;
                    return;
                }
                case SettingRow str:
                {
                    var s = str.Setting;

                    // One hover per cell: the name's carries the description (and the full
                    // name when the column cut it) — two hover elements on the same bounds
                    // would fight, so FittedText is not used here.
                    double nameW = WColValue - WColName - 12;
                    string shownName = TbText.Fit(font, s.Name, nameW - 8);
                    c.AddStaticText(shownName, font, EB(WColName, y + 4, nameW, 24));
                    string hover = s.Hover;
                    if (shownName != s.Name) hover = s.Name + (hover == null ? "" : "\n" + hover);
                    if (hover != null)
                        c.AddHoverText(hover, font, 340, EB(WColName, y + 4, nameW, 24));

                    var vf = s.IsDefault
                        ? font
                        : font.Clone().WithColor(TallybookConfig.ParseColor(config.ColorPartial));
                    double valW = DW - WColValue - 8;
                    FittedText(c, s.Value, vf, EB(WColValue, y + 4, valW, 24), valW);
                    break;
                }
                case SpawnRow spr:
                {
                    // Same shape as a SettingRow — label, value, hover on the label — plus
                    // a Map button when the row names a place. The button follows the errand
                    // rows' contract: it draws from captured coordinates, never a live map
                    // read, so it cannot flicker out.
                    double nameW = WColValue - WColName - 12;
                    string shownLabel = TbText.Fit(font, spr.Label, nameW - 8);
                    c.AddStaticText(shownLabel, font, EB(WColName, y + 4, nameW, 24));
                    string hover = spr.Hover;
                    if (shownLabel != spr.Label) hover = spr.Label + (hover == null ? "" : "\n" + hover);
                    if (hover != null)
                        c.AddHoverText(hover, font, 340, EB(WColName, y + 4, nameW, 24));

                    bool hasPlace = spr.MapX != 0 || spr.MapY != 0 || spr.MapZ != 0;
                    double valW = DW - WColValue - (hasPlace ? 64 : 8);
                    FittedText(c, spr.Value, font, EB(WColValue, y + 4, valW, 24), valW);

                    if (hasPlace)
                    {
                        var target = new BlockPos((int)spr.MapX, (int)spr.MapY, (int)spr.MapZ);
                        c.AddSmallButton("Map", () => ShowOnMapAt(target, spr.Label),
                            EB(DW - 56, y, 48, 26), EnumButtonStyle.Small);
                    }
                    break;
                }
                case LoreRow lr:
                {
                    ComposeLoreCell(c, lr.A, 8, y);
                    ComposeLoreCell(c, lr.B, DW / 2 + 8, y);
                    break;
                }
                case PlaceRow plr:
                {
                    var place = plr.Place;
                    bool tracked = place.ShowOnHud;

                    // The Side quests tab's own checkbox, doing that tab's own job: checked
                    // is tracked — notes below, distance on the HUD; unchecked is parked.
                    c.AddSwitch(on =>
                    {
                        place.ShowOnHud = on;
                        svc.Store.Save();
                        svc.RecountAll();
                        Recompose();
                        onHudChanged?.Invoke();
                    }, EB(ColCheck, y + 1, 25, 25), "plact-" + place.Key, 25);
                    c.AddHoverText("Checked: notes shown here, distance shown on the HUD. "
                        + "Unchecked parks it — kept and saved, off the HUD.",
                        font, 260, EB(ColCheck, y + 1, 25, 25));

                    // The notes open under the row via the same leading toggle an errand's
                    // conversation uses; the column is reserved either way so the name
                    // column stays a column. Offered only when there ARE notes — writing
                    // the first one is Edit's job.
                    double placeNameX = ColName + 28;
                    if (tracked && place.HasNotes)
                    {
                        c.AddSmallButton(place.NotesExpanded ? "−" : "+", () =>
                        {
                            place.NotesExpanded = !place.NotesExpanded;
                            svc.Store.Save();
                            Recompose();
                            return true;
                        }, EB(ColName, y, 24, 26), EnumButtonStyle.Small);
                        c.AddHoverText(place.NotesExpanded
                                ? "Fold the notes away."
                                : "Read this place's notes.",
                            font, 220, EB(ColName, y, 24, 26));
                    }

                    var titleFont = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2));
                    if (!tracked) titleFont = titleFont.Clone().WithColor(GuiStyle.ColorParchment);
                    double titleW = ColProg - placeNameX - 10;
                    FittedText(c, place.Name, titleFont, EB(placeNameX, ry + 3, titleW, 26), titleW);

                    if (place.Note != null)
                    {
                        var quietNote = font.Clone().WithColor(GuiStyle.ColorParchment);
                        FittedText(c, place.Note, quietNote, EB(ColProg, y + 4, 130, 24), 130);
                    }

                    // Distance computed at compose time and kept OUT of the change
                    // signature — walking must not redraw the dialog every step (the
                    // Player tab's rule). Its own column, clear of the note's (the two
                    // overlapped in the first build — Mark).
                    var me = capi.World?.Player?.Entity?.Pos;
                    if (me != null)
                    {
                        double dx = me.X - place.X, dz = me.Z - place.Z;
                        int m = (int)Math.Sqrt(dx * dx + dz * dz);
                        c.AddStaticText(m < 10 ? "here" : $"{m:n0} blocks", font,
                            EB(470, y + 4, 130, 24));
                    }

                    c.AddSmallButton("Map", () => ShowOnMapAt(
                            new BlockPos((int)place.X, (int)place.Y, (int)place.Z, 0), place.Name),
                        EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
                    c.AddHoverText($"Open the map centred on {place.Name}.",
                        font, 240, EB(ColAct2, y, 40, 26));

                    c.AddSmallButton("Edit", () => OpenPlaceEditor(place),
                        EB(ColCalc + 2, y, 60, 26), EnumButtonStyle.Small);
                    c.AddHoverText("Edit this place — name, what it is, and the notes — in "
                        + "its own window.", font, 240, EB(ColCalc + 2, y, 60, 26));

                    // The same hold-through-the-countdown as Unpin — one workflow for
                    // every button that loses something (Mark: consistency).
                    var rb = EB(ColUnpin, y, 74, 26);
                    string removeLabel = ReferenceEquals(holdTarget, place)
                        ? $"Hold {holdShownSecond}…" : "Remove";
                    c.AddSmallButton(removeLabel, () => OnRemovePlaceClicked(place), rb, EnumButtonStyle.Small);
                    holdButtons.Add((place, rb, () => RemovePlaceNow(place)));
                    break;
                }
                case PlaceNoteRow pnr:
                {
                    var lines = NotesLines(pnr.Place);
                    if (pnr.Index >= lines.Length) return;
                    var (kind, text) = ParseNoteLine(lines[pnr.Index]);

                    // A blank line inside the notes is paragraph spacing, kept as such.
                    if (string.IsNullOrWhiteSpace(text)) { y += LineStep; return; }

                    var noteFont = font.Clone().WithColor(kind == 3
                        ? TallybookConfig.ParseColor(config.ColorSatisfied)
                        : GuiStyle.ColorParchment);

                    double tx = nx;
                    if (kind == 1)
                    {
                        c.AddStaticText("•", noteFont, EB(nx, y, 18, LineStep));
                        tx = nx + 22;
                    }
                    else if (kind == 2 || kind == 3)
                    {
                        // √/· and never ☐/☑ — the fonts carry no checkbox glyphs.
                        c.AddSmallButton(kind == 3 ? "√" : "·",
                            () => { ToggleNoteCheckbox(pnr.Place, pnr.Index); return true; },
                            EB(nx, y, 24, 24), EnumButtonStyle.Small);
                        c.AddHoverText(kind == 3 ? "Done — click to untick." : "Click to tick off.",
                            font, 200, EB(nx, y, 24, 24));
                        tx = nx + 32;
                    }

                    double noteW = DW - tx - 24;
                    foreach (var wrapped in TbText.Wrap(noteFont, text, noteW))
                    {
                        c.AddStaticText(wrapped, noteFont, EB(tx, y, noteW, LineStep));
                        y += LineStep;
                    }
                    y += 2;
                    return;
                }
                case InfoRow ir:
                {
                    // Read as the transcript it is — the same shape the History page uses:
                    // full width, wrapped over as many lines as it takes. Squeezed into the
                    // name column it was cut at the first few words, which is where the
                    // speaker's name and the thing they asked for both live, so the quote
                    // stopped being a quote and became a caption.
                    var infoFont = font.Clone().WithColor(GuiStyle.ColorParchment);
                    double infoW = DW - nx - 16;
                    var lines = TbText.Wrap(infoFont, ir.Full ?? ir.Text, infoW);
                    if (lines.Count == 0) break;

                    foreach (var wrapped in lines)
                    {
                        c.AddStaticText(wrapped, infoFont, EB(nx, y, infoW, LineStep));
                        y += LineStep;
                    }
                    // A gap between speakers, so a two-sided exchange reads as two turns.
                    y += 6;
                    return;
                }
            }
            y += RowH;
        }

        long optionsFontChangedMs;
        bool optionsRecomposeQueued;

        /// <summary>
        /// Recompose the Options screen once the slider has settled, never mid-drag.
        ///
        /// The options text follows the size slider, but a recompose rebuilds the slider
        /// element itself — doing that per drag-notch snatches the handle out from under the
        /// cursor, which is precisely the interference this screen promises not to have. Same
        /// pattern as the count-field typing grace: note when the value last moved, recompose
        /// only after a quiet moment.
        /// </summary>
        void QueueOptionsRecompose()
        {
            optionsFontChangedMs = capi.ElapsedMilliseconds;
            if (optionsRecomposeQueued) return;
            optionsRecomposeQueued = true;
            capi.Event.RegisterCallback(CheckOptionsRecompose, 650, permittedWhilePaused: true);
        }

        void CheckOptionsRecompose(float _)
        {
            if (capi.ElapsedMilliseconds - optionsFontChangedMs < 550)
            {
                capi.Event.RegisterCallback(CheckOptionsRecompose, 300, permittedWhilePaused: true);
                return;
            }
            optionsRecomposeQueued = false;
            if (IsOpened() && screen == TbScreen.Options) Recompose();
        }

        /// <summary>Switches compose in the off state; setting On directly fires no callback.</summary>
        void RestoreOptionSwitches()
        {
            // Sliders compose empty; give it its range and where it currently sits.
            var fontSlider = SingleComposer.GetSlider("opt-hudfont");
            fontSlider?.SetValues(
                (int)Math.Round(config.HudFontSize > 0 ? config.HudFontSize : DefaultHudFontSize),
                10, 28, 1, " px");

            var hudSwitch = SingleComposer.GetSwitch("opt-hud");
            if (hudSwitch != null) hudSwitch.On = config.HudVisible;

            var group = SingleComposer.GetSwitch("opt-group");
            if (group != null) group.On = config.HudGroupByItem;

            var cycle = SingleComposer.GetSwitch("opt-cycle");
            if (cycle != null) cycle.On = config.HudCycleVariants;

            var bags = SingleComposer.GetSwitch("opt-mountbags");
            if (bags != null) bags.On = config.IncludeMountBags;

            var worldTab = SingleComposer.GetSwitch("opt-worldtab");
            if (worldTab != null) worldTab.On = config.ShowWorldTab;

            var playerTab = SingleComposer.GetSwitch("opt-playertab");
            if (playerTab != null) playerTab.On = config.ShowPlayerTab;

            var loreTab = SingleComposer.GetSwitch("opt-loretab");
            if (loreTab != null) loreTab.On = config.ShowLoreTab;

            var exploreTab = SingleComposer.GetSwitch("opt-exploretab");
            if (exploreTab != null) exploreTab.On = config.ShowExploreTab;
        }

        bool restoringInputs;

        void RestoreCountInputs()
        {
            // SetValue fires the input's change callback; without the guard, every recompose
            // would look like the player typing and defer the next live update for no reason.
            restoringInputs = true;
            try
            {
                // Tabs compose with the first one active; re-assert the real selection. Must
                // cover every tab — a missing case silently highlights the wrong one.
                // SetValue takes the ARRAY POSITION, unlike the click handler's DataInt
                // (decompile-verified) — with optional tabs the two disagree, so the
                // position is computed from which optional tabs are actually composed.
                // Array positions under the Items, Side quests, [Explore], [Player],
                // [World], [Lore], History order — must mirror the tabs list exactly.
                int e = config.ShowExploreTab ? 1 : 0;
                int p = config.ShowPlayerTab ? 1 : 0;
                int w = config.ShowWorldTab ? 1 : 0;
                int l = config.ShowLoreTab ? 1 : 0;
                int active = tab switch
                {
                    TbTab.Explore => 2,
                    TbTab.Player => 2 + e,
                    TbTab.World => 2 + e + p,
                    TbTab.Lore => 2 + e + p + w,
                    TbTab.History => 2 + e + p + w + l,
                    TbTab.Quests => 1,
                    _ => 0,
                };
                SingleComposer.GetHorizontalTabs("tabs")?.SetValue(active, false);

                if (tab == TbTab.World)
                {
                    var filter = SingleComposer.GetTextInput("world-filter");
                    if (filter != null)
                    {
                        filter.SetPlaceHolderText("type to filter…");
                        filter.SetValue(worldFilter);
                        if (refocusWorldFilter) SingleComposer.FocusElement(filter.TabIndex);
                    }
                }

                if (tab == TbTab.Player)
                {
                    var sd = SingleComposer.GetSwitch("opt-spawndist");
                    if (sd != null) sd.On = config.HudSpawnDistance;
                    var sw = SingleComposer.GetSwitch("opt-spawnwarn");
                    if (sw != null) sw.On = config.HudSpawnDistanceWarn;
                    SingleComposer.GetTextInput("spawnwarn-blocks")?.SetValue(
                        config.HudSpawnDistanceWarnBlocks.ToString(CultureInfo.InvariantCulture));
                }

                if (tab == TbTab.Explore)
                {
                    var nameInput = SingleComposer.GetTextInput("explore-name");
                    if (nameInput != null)
                    {
                        nameInput.SetPlaceHolderText("e.g. Old copper mine");
                        nameInput.SetValue(exploreName);
                    }
                    var noteInput = SingleComposer.GetTextInput("explore-note");
                    if (noteInput != null)
                    {
                        noteInput.SetPlaceHolderText("mine, ruin, cave…");
                        noteInput.SetValue(exploreNote);
                    }
                    foreach (var row in VisibleRows().OfType<PlaceRow>())
                    {
                        var actSw = SingleComposer.GetSwitch("plact-" + row.Place.Key);
                        if (actSw != null) actSw.On = row.Place.ShowOnHud;
                    }
                }

                // Visible pins only: inputs exist just for the composed page, and asking the
                // composer for a key it never composed is unhealthy whether it throws or not.
                foreach (var row in VisibleRows().OfType<PinRow>())
                {
                    SingleComposer.GetTextInput("cnt-" + row.Pin.Key)
                        ?.SetValue(row.Pin.Count.ToString(CultureInfo.InvariantCulture));
                    // Switches compose in the off state; setting On directly fires no callback.
                    var sw = SingleComposer.GetSwitch("act-" + row.Pin.Key);
                    if (sw != null) sw.On = row.Pin.Active;
                }
                foreach (var row in VisibleRows().OfType<SiteRow>())
                {
                    var sw = SingleComposer.GetSwitch("sitact-" + row.Site.Key);
                    if (sw != null) sw.On = row.Site.Active;
                }
            }
            finally { restoringInputs = false; }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>The warning-distance field on the Player tab. Same contract as the
        /// count fields: the guard keeps a restore's SetValue from reading as typing, the
        /// stamp keeps inventory recomposes from stealing focus mid-number, and a value
        /// that does not parse yet ("", mid-edit) changes nothing rather than clamping the
        /// player's half-typed number out from under them.</summary>
        void OnSpawnWarnBlocksTyped(string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;

            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int blocks)
                || blocks < 1) return;

            config.HudSpawnDistanceWarnBlocks = Math.Min(1_000_000, blocks);
            capi.StoreModConfig(config, "tallybook.json");
            onHudChanged?.Invoke();
        }

        void StepCount(Pin pin, int delta)
        {
            int next = pin.Count + delta;
            if (next <= 0)
            {
                // Decrement to 0 unpins immediately — pressing − at count 1 is deliberate,
                // and no-confirm-dialogs is the rule (Mark). Re-pinning recovers.
                svc.Store.Remove(pin);
                return;
            }
            svc.Store.SetCount(pin, Math.Min(9999, next));
        }

        /// <summary>
        /// Filters live, recomposing per keystroke: the row list must answer the letters as
        /// they land, and the recompose is what redraws it. The count fields defer instead —
        /// but their recomposes only move numbers, while this one is the feature. Focus is
        /// captured before the rebuild (Recompose) and handed back after (RestoreCountInputs);
        /// lastCountTypingMs still stamps here so inventory-driven recounts hold off and
        /// don't rebuild the same screen mid-word.
        /// </summary>
        void OnWorldFilterTyped(string val)
        {
            if (restoringInputs) return;
            worldFilter = val ?? "";
            page = 0;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            Recompose();
        }

        void OnCountTyped(Pin pin, string val)
        {
            if (restoringInputs) return;
            lastCountTypingMs = capi.World.ElapsedMilliseconds;
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return;

            if (n <= 0)
            {
                svc.Store.Remove(pin);
                return;
            }
            n = Math.Min(9999, n);
            if (n != pin.Count)
            {
                // Direct mutation + save, not Store.SetCount: the typing grace defers the
                // recompose that Changed() would trigger, so keystrokes are never eaten
                // (typing "12" passes through 1).
                pin.Count = n;
                svc.Store.Save();
                svc.RecountAll();
            }
        }

        // ------------------------------------------------------------------ hold-to-unpin
        //
        // No confirm dialog (Mark): destructive intent is proven by holding the button
        // through a visible countdown instead. Release or drift off the button
        // and nothing happens. A short tap teaches the gesture via the notice line.
        // ConfirmOnUnpin=false keeps instant single-click unpinning for those who want it.

        bool OnUnpinClicked(Pin pin)
        {
            if (!config.ConfirmOnUnpin)
            {
                svc.Store.Remove(pin);
                return true;
            }
            // Click fires on mouse-up. If the hold ran to completion the pin is already
            // gone; a tap that never finished the countdown lands here.
            if (svc.Store.Pins.Contains(pin))
            {
                notice = $"Hold Unpin for {HoldMs / 1000} second to remove.";
                Recompose();
            }
            return true;
        }

        public override void OnMouseDown(MouseEvent args)
        {
            base.OnMouseDown(args);
            if (screen != TbScreen.List || !config.ConfirmOnUnpin || !IsOpened()) return;

            foreach (var (target, bounds, complete) in holdButtons)
            {
                if (bounds.PointInside(args.X, args.Y)) { StartHold(target, complete); return; }
            }
        }

        public override void OnMouseUp(MouseEvent args)
        {
            base.OnMouseUp(args);
            CancelHold();
        }

        void StartHold(object target, Action complete)
        {
            CancelHold();
            holdTarget = target;
            holdComplete = complete;
            holdStartMs = capi.World.ElapsedMilliseconds;
            holdShownSecond = (int)(HoldMs / 1000);
            holdTickId = capi.Event.RegisterGameTickListener(OnHoldTick, 50);
            Recompose();    // flips the button label to the countdown
        }

        void OnHoldTick(float dt)
        {
            if (holdTarget == null) { StopHoldTimer(); return; }

            // The mouse-up override is the normal cancel path; this also catches the pointer
            // drifting off the button and the dialog closing mid-hold.
            var entry = holdButtons.FirstOrDefault(e => ReferenceEquals(e.Target, holdTarget));
            bool stillValid = IsOpened()
                && capi.Input.MouseButton.Left
                && entry.Bounds != null
                && entry.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY);
            if (!stillValid) { CancelHold(); return; }

            long held = capi.World.ElapsedMilliseconds - holdStartMs;
            if (held >= HoldMs)
            {
                var complete = holdComplete;
                holdTarget = null;
                holdComplete = null;
                StopHoldTimer();
                complete?.Invoke();
                return;
            }

            int second = (int)((HoldMs - held + 999) / 1000);
            if (second != holdShownSecond)
            {
                holdShownSecond = second;
                Recompose();
            }
        }

        void CancelHold()
        {
            StopHoldTimer();
            if (holdTarget == null) return;
            holdTarget = null;
            holdComplete = null;
            Recompose();    // restore the button label
        }

        void StopHoldTimer()
        {
            if (holdTickId != 0)
            {
                capi.Event.UnregisterGameTickListener(holdTickId);
                holdTickId = 0;
            }
        }

        /// <summary>
        /// Copy an errand's item across to the Items tab as a goal of your own, where it can
        /// be broken down and expanded. The errand keeps its own row and its own count — the
        /// villager still wants what they wanted.
        ///
        /// Stays on the current tab deliberately: working down a list of errands is the
        /// normal case, and jumping away after each one would mean navigating back before
        /// the next (Mark). The notice line is the confirmation instead.
        /// </summary>
        /// <summary>
        /// Add the build a construction item starts as its own pin — keyed apart from the
        /// plain item pin, so both live on the list at once — and unfold it with the
        /// construction group. Its Have never counts the carried starter (see Resolve);
        /// the starter is the tree's first row instead, expandable to its own recipe.
        /// </summary>
        bool StartConstruction(Pin source, RecipeVariantGroup group)
        {
            var build = svc.Store.Add(source.Stack, 1, setCount: true, activate: true,
                buildSite: true);
            if (build == null) return true;

            // A page that already names the material commits the build to it: pinning the
            // OAK sailboat means an oak boat, and the rows should say and count oak
            // (Mark). Only a value the build actually offers counts as a commitment.
            if (build.BuildMaterial == null && group.BuildMaterialChoices != null)
            {
                string material = source.Stack?.Collectible?.Code?.Path?.Split('-').LastOrDefault();
                if (material != null && group.BuildMaterialChoices
                        .Contains(material, StringComparer.OrdinalIgnoreCase))
                {
                    build.BuildMaterial = material;
                }
            }

            svc.Resolve(build);
            var chosen = build.Groups.FirstOrDefault(g => g.Construction != null && g.Pattern == group.Pattern)
                         ?? build.Groups.FirstOrDefault(g => g.Construction != null);
            if (chosen != null) svc.ChoosePinRecipe(build, chosen);
            return true;
        }

        bool SendToItems(Pin quest)
        {
            if (quest.Stack == null) return true;

            var copy = svc.Store.Add(quest.Stack, quest.Count, setCount: true);
            if (copy == null) return true;

            // Gathering is the whole point of the button: you said you would go and find
            // these, so the row is the item and its count. The Recipe button is there if you
            // change your mind and want it broken down.
            copy.GatherOnly = true;
            svc.Resolve(copy);
            // Saved now, not left for the next incidental save: Add() saved before the flag
            // was set, and a crash between the two would bring the pin back decomposed.
            svc.Store.Save();
            svc.RecountAll();

            notice = $"{copy.DisplayName} x{copy.Count} added to gathering (Items tab).";
            Recompose();
            return true;
        }

        /// <summary>
        /// Open the world map centred on the quest giver. The marker is already there; this
        /// saves hunting for it on a map the size of a world.
        /// </summary>
        bool ShowOnMap(Pin pin) => ShowOnMapAt(MapTargetFor(pin), pin.QuestGiver);

        bool ShowOnMapAt(BlockPos target, string who)
        {
            var maps = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (maps == null)
            {
                notice = "The map is not available.";
                Recompose();
                return true;
            }

            if (target == null)
            {
                notice = $"No location known for {who} yet — walk past them once.";
                Recompose();
                return true;
            }

            TryClose();

            // The minimap also counts as "opened", so IsOpened alone is not the question; the
            // question is which type is showing.
            var dlg = maps.worldMapDlg;
            if (dlg != null && dlg.IsOpened() && dlg.DialogType == EnumDialogType.Dialog)
            {
                // Already the map we want — centre it and change nothing else.
                capi.Event.RegisterCallback(_ => Centre(maps, target), 100, permittedWhilePaused: true);
                return true;
            }

            // Open it the way the player's own M key does, by invoking vanilla's hotkey
            // handler — not by driving ToggleMap by hand. Manual toggling has produced two
            // distinct messes: with the minimap open it left two dialogs on one slot (a map
            // that only Escape could close), and closing the minimap first skipped whatever
            // state the real handler keeps, which is the prime suspect for the map coming up
            // wrongly sized (Mark). The game knows how to open its own map; the whole job
            // here is to ask it and then centre.
            try
            {
                HarmonyLib.AccessTools.Method(maps.GetType(), "OnHotKeyWorldMapDlg")
                    ?.Invoke(maps, new object[] { new KeyCombination() });
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] map hotkey handler failed, falling back: {0}", e.Message);
                maps.ToggleMap(EnumDialogType.Dialog);
            }

            // Once, after it has had time to compose. Repeating the centre was a hedge against
            // it not being ready; it is also a second chance to fight whatever the player has
            // done in the meantime, so it happens once and is allowed to fail.
            capi.Event.RegisterCallback(_ => Centre(maps, target), 400, permittedWhilePaused: true);
            return true;
        }

        /// <summary>
        /// Where the Map button goes. While the errand is still being fetched and a map that
        /// belongs to it (tied by a shared quest variable in the dialogue — never merely by
        /// who handed it over) has been read, it goes where that map points: the lens is in
        /// the Devastation, and that is the walk being made. Once the goods are in hand —
        /// or when no tied map exists — it goes to the giver: a recorded position first,
        /// else a waypoint that names them ("Map to Tobias' cave" is how you learn where
        /// Tobias is before you have ever stood next to him). Null when nothing is known,
        /// in which case the row says so rather than offering a button that guesses.
        ///
        /// It used to prefer the destination of a map that came with the errand — Tobias hands
        /// over a map to the Devastation, so "go to the Devastation, then come back" reads
        /// well. It does not survive contact with the data. Which maps an NPC hands out is
        /// known per dialogue *file*, and a file covers several unrelated quest threads:
        /// Agnieszka takes iron ingots at her forge and separately gives the map to Tobias'
        /// cave, so her errand pointed across the world (found by Mark, twice — the second
        /// time every errand pointed at the Devastation). Nothing in the files ties a
        /// particular map to a particular fetch request, so the tie was invented, and an
        /// invented tie sends the player on a long walk to the wrong place.
        ///
        /// The destination is not lost by this: reading a locator map puts vanilla's own
        /// waypoint on the map, which is where that information belongs, and the row still
        /// says which map came with the errand. What this button answers is "who wants this
        /// and where are they" — one question, answered correctly.
        /// </summary>
        BlockPos MapTargetFor(Pin pin)
        {
            // Persisted pin fields only — never a live waypoint read. The waypoint list is
            // known to read back empty at random (the fifty-marker incident was that), and a
            // button driven by the live read vanished whenever the read failed. The resolver
            // on the 1s tick captures successful reads into the pin; by the time anything is
            // drawn, the knowledge is ours.
            if (GoingToSite(pin))
                return new BlockPos((int)pin.SiteX, (int)pin.SiteY, (int)pin.SiteZ, 0);

            if (pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0)
                return new BlockPos((int)pin.QuestX, (int)pin.QuestY, (int)pin.QuestZ, 0);

            return null;
        }

        /// <summary>The destination leg of the errand, while it still matters — pointing
        /// there after the goods are in hand sends the player the wrong way down a very
        /// long road.</summary>
        static bool GoingToSite(Pin pin) => !pin.Complete && pin.HasSite;

        /// <summary>
        /// A named NPC's recorded position from the conversation-filled directory — what a
        /// reward row's Map button points at, since there is no pin left to carry a place.
        /// Persisted data only, same as every Map button; case-insensitive on miss because
        /// the name can come from a dialogue filename or a live entity, which only
        /// coincidentally agree.
        /// </summary>
        BlockPos PlaceOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (!svc.Store.NpcPlaces.TryGetValue(name, out string place))
            {
                place = svc.Store.NpcPlaces.FirstOrDefault(
                    kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
                if (place == null) return null;
            }

            var parts = place.Split(',');
            if (parts.Length != 3) return null;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double py)) return null;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) return null;
            return new BlockPos((int)x, (int)py, (int)z, 0);
        }

        /// <summary>
        /// Point every map widget the dialog owns at a spot.
        ///
        /// Fetched by its element key through the composer's public accessor, and applied to
        /// each composer rather than picked out of one: an earlier version reflected into the
        /// dialog's private full-map composer and matched the widget by type, which found
        /// something that was evidently not the one on screen — the map opened and stayed
        /// looking at the player. Asking for the element by name, everywhere, is both simpler
        /// and the approach already proven to work.
        /// </summary>
        static void Centre(WorldMapManager maps, BlockPos target)
        {
            var dlg = maps?.worldMapDlg;

            // Only while the big map is actually the thing on screen. This runs on a delay,
            // and the player can close the map before it fires — at which point the only
            // widget left to pan is the corner minimap, which must never be steered by a
            // stale click from half a second ago.
            if (dlg == null || !dlg.IsOpened() || dlg.DialogType != EnumDialogType.Dialog) return;

            foreach (var composer in dlg.Composers.Values)
            {
                if (composer?.GetElement("mapElem") is GuiElementMap map) map.CenterMapTo(target);
            }
        }

        bool OpenHandbookFor(Pin pin) => OpenHandbookForStack(pin.Stack, pin.DisplayName);

        /// <summary>Every row is somebody's item — ingredient and tool rows share the pin
        /// rows' whole handbook flow (first-open dance, page-build wait, fallbacks), keyed
        /// on the row's sample stack. Public because the HUD's rows link here too: one
        /// handbook-opening path, or the HUD's links would rot separately from the
        /// dialog's buttons.</summary>
        public bool OpenHandbookForStack(ItemStack stack, string displayName)
        {
            // Derive the page from the stack, NOT from pin.Key: a key carries the quest giver
            // ("…|for:Agnieszka") so an errand and your own goal can be separate rows, and the
            // handbook has never heard of that suffix. HandbookPageCode, not PageCode: opening
            // must take the same IHandBookPageCodeProvider hop the game's own
            // open-handbook-for-stack flow takes, or any collectible that names its
            // representative page (meals, mod classes) sends us to a code the index never held.
            string page = RecipeProbe.HandbookPageCode(stack, capi.World);
            if (page == null)
            {
                notice = $"No handbook page for {displayName}.";
                Recompose();
                return true;
            }

            // Trade places rather than stack two centered dialogs; L reopens the list, and so
            // does the button that this flag puts under the handbook's Back button.
            HandbookPin.CameFromList = true;
            TryClose();

            var handbook = HandbookPin.FindDialog(capi);
            if (handbook == null || !handbook.IsOpened()) HandbookPin.OpenLikeThePlayerWould(capi);

            // A frame later: opening may have been the handbook's first, which is when it
            // registers itself with the GUI manager and builds its pages. The frame queue
            // rather than RegisterCallback because this button can be clicked while the game
            // is paused (an already-open handbook pauses singleplayer) — a delayed callback
            // would not fire until unpause, and registering one then is the crash a ModDB
            // report caught in developer mode.
            capi.Event.EnqueueMainThreadTask(() => ShowHandbookPage(stack, displayName, page), "tallybook-showpage");
            return true;
        }

        void ShowHandbookPage(ItemStack stack, string displayName, string page)
        {
            var handbook = HandbookPin.FindDialog(capi);
            if (handbook == null)
            {
                capi.ShowChatMessage("Tallybook: the handbook is not available.");
                return;
            }
            if (!handbook.IsOpened()) handbook.TryOpen();

            // Say so up front when a wait is coming: the handbook will sit on a blank
            // overview until its rebuild finishes, and an unexplained blank screen reads
            // as the button being broken no matter how well the landing works afterwards.
            if (HandbookPin.StillLoadingPages(handbook))
                capi.ShowChatMessage(
                    $"Tallybook: the handbook is rebuilding its pages — {displayName} will open when it finishes.");

            WaitForPagesThenShow(stack, displayName, page, handbook,
                deadline: Environment.TickCount64 + 60_000);
        }

        /// <summary>
        /// The handbook's page index builds on a background thread — from world join, and
        /// again from scratch every time any mod registers a hotkey (vanilla wires
        /// HotkeysChanged straight to a full reload), which on a well-modded world takes
        /// long enough to click into. While it builds, every page lookup below can only
        /// fail, and the search fallback would filter an empty page list, leaving an open
        /// handbook showing nothing at all (found by Mark twice: linen right after login,
        /// then rusty gear outlasting a frame-counted wait). So: wait for the index,
        /// re-queued per frame because the handbook we just opened is what pauses
        /// singleplayer and a delayed callback would sit until unpause. The deadline is
        /// wall-clock — the in-world timer freezes while paused — and only guards against
        /// the loading flag being stuck forever; hitting it says so in chat, because
        /// half-acting on an empty index is exactly the blank screen this exists to avoid.
        /// </summary>
        void WaitForPagesThenShow(ItemStack stack, string displayName, string page, GuiDialogHandbook handbook, long deadline)
        {
            // Closed while we were waiting: the player changed their mind — reopening the
            // handbook at them every frame is not landing a page, it is a fight.
            if (!handbook.IsOpened()) return;

            if (HandbookPin.StillLoadingPages(handbook))
            {
                if (Environment.TickCount64 < deadline)
                {
                    capi.Event.EnqueueMainThreadTask(
                        () => WaitForPagesThenShow(stack, displayName, page, handbook, deadline),
                        "tallybook-showpage");
                    return;
                }
                capi.ShowChatMessage(
                    "Tallybook: the handbook is still building its pages — try the button again in a moment.");
                return;
            }

            if (handbook.OpenDetailPageFor(page)) return;

            // Attribute-carrying variants can name a page the handbook does not index; the
            // plain item's page is the honest second choice.
            string basePage = stack?.Collectible == null
                ? null
                : RecipeProbe.HandbookPageCode(new ItemStack(stack.Collectible), capi.World);

            if (basePage != null && basePage != page && handbook.OpenDetailPageFor(basePage)) return;

            // Neither code is indexed, so ask the collectible which pages the handbook built
            // FROM it and try those, most specific first. Clutter is why: the handbook's page
            // for a globe is built from a bare { type } stack while the one in your bag also
            // carries { collected }, so the exact code misses and the bare block code — which
            // names no page at all for a shape-from-attributes block — misses too. The
            // handbook itself confirms each candidate; nothing here assumes a page exists.
            foreach (var candidate in RecipeProbe.RepresentativePageCodes(stack, capi))
            {
                if (candidate == page || candidate == basePage) continue;
                if (handbook.OpenDetailPageFor(candidate)) return;
            }

            // No code we can derive names an indexed page. Searching by display name lands
            // the player on a list with the right entry in it — self-explanatory on screen —
            // where stopping at the root reads as the button doing nothing at all.
            string name = displayName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                handbook.Search(name);
                capi.ShowChatMessage(
                    $"Tallybook: no exact handbook page for {name} — searched by name instead.");
            }
            else
            {
                capi.ShowChatMessage(
                    $"Tallybook: no handbook page for {stack?.Collectible?.Code?.ToShortString() ?? "this item"}.");
            }
        }

        bool Collapse(Pin pin)
        {
            svc.ToggleGatherOnly(pin);
            return true;
        }

        bool TryExpand(Pin pin, TallyNode node)
        {
            if (!svc.CanExpand(pin, node, out string reason))
            {
                // The cycle guard and no-recipe cases explain themselves here rather than
                // silently doing nothing (spec §2a: disallow with a visible reason).
                notice = $"Cannot expand {node.Req.DisplayName}: {reason}";
                Recompose();
                return true;
            }
            notice = "";
            return ExpandOrChoose(pin, node, node.Choices);
        }

        // ------------------------------------------------------------------ confirm screens

        static string SectionOf(QuestRecord r)
            => r.Stage == "open" || r.Stage == "awaiting" ? "open"
               : r.Day.HasValue ? "dated"
               : "undated";

        /// <summary>One line of the History tab: a group heading (Rec null) or a record.
        /// Collapsible headings carry the persistence key and their default state.</summary>
        class HistEntry
        {
            public string Heading;
            public string GroupKey;
            public bool DefaultExpanded;
            public int Count;
            public QuestRecord Rec;
        }

        bool HistoryGroupExpanded(string key, bool def)
            => svc.Store.HistoryGroups.TryGetValue(key, out bool v) ? v : def;

        /// <summary>Only deviations from the default are stored, so a group toggled back to
        /// its default drops out of the save rather than pinning today's default forever.</summary>
        void ToggleHistoryGroup(string key, bool def)
        {
            bool now = !HistoryGroupExpanded(key, def);
            if (now == def) svc.Store.HistoryGroups.Remove(key);
            else svc.Store.HistoryGroups[key] = now;
            svc.Store.Save();
            Recompose();
        }

        /// <summary>
        /// The archive as a story so far (Mark's ordering): what is still going, then the
        /// pre-install finishes ("earlier", collapsed by default — they cannot be dated and
        /// there can be a lot of them), then the dated finishes under one heading per
        /// in-game year, each expanded by default and collapsible, the fold remembered per
        /// world. A collapsed group contributes its heading and nothing else.
        /// </summary>
        List<HistEntry> BuildHistoryEntries(List<QuestRecord> done)
        {
            var entries = new List<HistEntry>();

            void Group(string heading, string key, bool defExpanded, List<QuestRecord> recs)
            {
                if (recs.Count == 0) return;
                if (key == null)
                {
                    entries.Add(new HistEntry { Heading = heading });
                    entries.AddRange(recs.Select(r => new HistEntry { Rec = r }));
                    return;
                }
                entries.Add(new HistEntry
                {
                    Heading = heading, GroupKey = key,
                    DefaultExpanded = defExpanded, Count = recs.Count,
                });
                if (HistoryGroupExpanded(key, defExpanded))
                    entries.AddRange(recs.Select(r => new HistEntry { Rec = r }));
            }

            Group("— still going —", null, true,
                done.Where(r => SectionOf(r) == "open").ToList());

            // Newest first, years and records alike (Mark): the tab opens on what you
            // finished most recently, and the deeper past reads further down.
            double daysPerYear = Math.Max(1, capi.World?.Calendar?.DaysPerYear ?? 1);
            foreach (var year in done.Where(r => SectionOf(r) == "dated")
                .GroupBy(r => (int)(r.Day.Value / daysPerYear) + 1)
                .OrderByDescending(g => g.Key))
            {
                Group($"Year {year.Key}", "year:" + year.Key, true,
                    year.OrderByDescending(r => r.Day.Value).ToList());
            }

            // Last, not first-chronologically (Mark): the undated pile is background, and
            // the tab's job is the story you actually played.
            Group("Finished before Tallybook was watching", "earlier", false,
                done.Where(r => SectionOf(r) == "undated").ToList());
            return entries;
        }

        double HistoryEntryHeight(HistEntry e, CairoFont quiet)
        {
            if (e.Rec == null) return e.GroupKey == null ? 24 : 34;

            double h = RowH;
            var r = e.Rec;
            if (r.Text != null && expandedRecords.Contains(r.Chain + "|" + r.Stage))
            {
                foreach (var said in r.Text) h += TbText.Wrap(quiet, said, DW - 48).Count * LineStep + 6;
                h += 4;
            }
            return h;
        }

        /// <summary>
        /// The index each page starts at, for the current fold and expansion state.
        /// Recomputed per compose because opening one record — or one year — re-flows every
        /// page after it, which is exactly why a fixed records-per-page could not work here.
        /// </summary>
        List<int> HistoryPageStarts(List<HistEntry> entries, CairoFont quiet)
        {
            // What is left for rows after the window's own furniture: title, tabs, column
            // heads above; pager, Journal and Close below; dialog padding around all of it.
            double budget = Math.Max(200, capi.Render.FrameHeight / RuntimeEnv.GUIScale - 260);

            var starts = new List<int> { 0 };
            double used = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                double h = HistoryEntryHeight(entries[i], quiet);

                // Never break before the first entry of a page: one record taller than the
                // whole budget would otherwise start an empty page and loop forever.
                if (used > 0 && used + h > budget) { starts.Add(i); used = 0; }
                used += h;
            }
            return starts;
        }

        void ComposeHistory(GuiComposer c, List<QuestRecord> done, ref double y)
        {
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);

            if (done.Count == 0)
            {
                c.AddStaticText("Nothing recorded yet.", font, EB(8, y + 8, DW, 26));
                c.AddStaticText("Quests you take on and finish are kept here, including ones you finished before Tallybook was installed.",
                    quiet, EB(8, y + 38, DW, 46));
                y += 96;
                c.AddSmallButton("Journal", () => OpenJournal(), EB(DW - 190, y, 92, 28), EnumButtonStyle.Small);
                c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
                return;
            }

            // Pages are worked out by height, not by counting records: a record that is open
            // for reading is a whole conversation tall, so thirteen of those ran off the
            // bottom of the screen with no way to reach them (found by Mark). Walking the
            // list against a height budget is the only honest page boundary when rows are not
            // a fixed size.
            var entries = BuildHistoryEntries(done);
            var starts = HistoryPageStarts(entries, quiet);
            this.page = Math.Min(Math.Max(this.page, 0), starts.Count - 1);

            int from = starts[this.page];
            int upto = this.page + 1 < starts.Count ? starts[this.page + 1] : entries.Count;

            foreach (var entry in entries.GetRange(from, upto - from))
            {
                if (entry.Rec == null)
                {
                    if (entry.GroupKey == null)
                    {
                        c.AddStaticText(entry.Heading, quiet, EB(8, y + 4, DW, 22));
                        y += 24;
                    }
                    else
                    {
                        // The heading is the fold control: click anywhere on it. +/− over a
                        // caret pair because the fonts carry no ▼ (verified glyph set).
                        bool expanded = HistoryGroupExpanded(entry.GroupKey, entry.DefaultExpanded);
                        string label = expanded
                            ? $"- {entry.Heading}"
                            : $"+ {entry.Heading} ({entry.Count})";
                        c.AddSmallButton(label,
                            () => { ToggleHistoryGroup(entry.GroupKey, entry.DefaultExpanded); return true; },
                            EB(8, y + 2, 430, 26), EnumButtonStyle.Small);
                        y += 34;
                    }
                    continue;
                }

                var record = entry.Rec;
                bool openQuest = record.Stage == "open" || record.Stage == "awaiting";

                // Same title face as the pin rows on the other tabs (base size + 2): a record
                // here IS that quest's row, and drawing it a step smaller made the whole tab
                // read like fine print next to Items and Side quests (Mark).
                var line = CairoFont.WhiteSmallishText().WithFontSize((float)(TablePx + 2))
                    .WithColor(TallybookConfig.ParseColor(
                        openQuest ? config.ColorPartial : config.ColorSatisfied));

                string when = record.Day.HasValue ? $"day {(int)record.Day.Value}" : "earlier";
                string detail =
                    record.Stage == "open" ? "under way"
                    : record.Stage == "awaiting" ? "done — go and collect"
                    : record.Stage == "rewarded" ? $"rewarded, {when}"
                    : record.Stage == "completed" ? $"handed in, {when}"
                    : when;

                FittedText(c, $"{(openQuest ? "•" : "√")} {record.Name}", line, EB(8, y + 4, 300, 24), 300);
                c.AddStaticText(detail, quiet, EB(320, y + 4, 220, 24));

                // Keyed by stage as well as quest: the same quest can legitimately appear in
                // two sections at once, and two rows sharing an identity open together.
                string key = record.Chain + "|" + record.Stage;
                bool open = expandedRecords.Contains(key);

                if (record.Text?.Count > 0)
                {
                    c.AddSmallButton(open ? "Hide" : "Read", () =>
                    {
                        if (!expandedRecords.Add(key)) expandedRecords.Remove(key);
                        Recompose();
                        return true;
                    }, EB(DW - 90, y, 76, 26), EnumButtonStyle.Small);
                }
                y += RowH;

                if (!open || record.Text == null) continue;

                // The words themselves, at the width of the window — this is the reminiscing
                // part, so it is given room rather than trimmed to a column.
                foreach (var said in record.Text)
                {
                    foreach (var wrapped in TbText.Wrap(quiet, said, DW - 48))
                    {
                        c.AddStaticText(wrapped, quiet, EB(28, y, DW - 48, LineStep));
                        y += LineStep;
                    }
                    y += 6;
                }
                y += 4;
            }

            y += 12;
            int pages = starts.Count - 1;
            if (pages > 0)
            {
                c.AddSmallButton("< Prev", () => { if (this.page > 0) { this.page--; Recompose(); } return true; },
                    EB(DW / 2 - 130, y, 78, 28), EnumButtonStyle.Small);
                c.AddStaticText($"Page {this.page + 1}/{pages + 1}", font, EB(DW / 2 - 44, y + 5, 90, 24));
                c.AddSmallButton("Next >", () => { if (this.page < pages) { this.page++; Recompose(); } return true; },
                    EB(DW / 2 + 52, y, 78, 28), EnumButtonStyle.Small);
            }

            c.AddSmallButton("Journal", () => OpenJournal(), EB(DW - 190, y, 92, 28), EnumButtonStyle.Small);
            c.AddHoverText("Open your journal — the lore you have collected along the way.",
                font, 260, EB(DW - 190, y, 92, 28));
            c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
        }

        /// <summary>
        /// Hand over to the game's journal. Opened the way the player would, by its own
        /// hotkey, so whatever it does on first use happens by its hand rather than ours —
        /// and deferred a tick, since this runs from inside our own dialog's click handling.
        /// </summary>
        bool OpenJournal()
        {
            TryClose();
            // Frame queue: works while paused too (see OpenHandbookFor).
            capi.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    // Whatever the journal's hotkey is actually called — asking the registry
                    // beats guessing at codes that vary between versions.
                    foreach (var entry in capi.Input.HotKeys)
                    {
                        if (entry.Key?.IndexOf("journal", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (entry.Value?.Handler == null) continue;

                        entry.Value.Handler(entry.Value.CurrentMapping);
                        return;
                    }

                    // Failing that, the journal dialog itself.
                    var journal = capi.ModLoader.GetModSystem<ModJournal>();
                    if (journal != null
                        && HarmonyLib.AccessTools.Field(typeof(ModJournal), "dialog")?.GetValue(journal) is GuiDialog dlg)
                    {
                        if (!dlg.IsOpened()) dlg.TryOpen();
                        return;
                    }
                }
                catch (Exception e)
                {
                    capi.Logger.Warning("[tallybook] could not open the journal: {0}", e.Message);
                }

                capi.ShowChatMessage("Tallybook: could not open the journal — try its own key.");
            }, "tallybook-journal");
            return true;
        }

        Pin choosingFor;
        TallyNode choosingNode;

        /// <summary>
        /// Pick a recipe before unfolding anything.
        ///
        /// Expanding used to choose for you and leave a "1/4" cycler to argue with afterwards,
        /// which is not the same question: what you want to know is what each way would have
        /// you go and fetch. Each way is listed with its materials and what it yields — the
        /// hunter backpack's four ways differ by *how many* pelts and whether you get one
        /// backpack or two, which a bare list of ingredient names hides completely.
        /// </summary>
        void ComposeChooseRecipe(GuiComposer c)
        {
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);
            double y = 40;

            // Pin-level choices keep the construction/craft split (see ExpandableGroups):
            // a build belongs to a build pin, never to the item's own Expand chooser.
            var choices = choosingNode?.Choices
                ?? choosingFor?.Groups.Where(g => (g.Construction != null) == choosingFor.BuildSite).ToList()
                ?? new List<RecipeVariantGroup>();
            var current = choosingNode != null ? choosingNode.Choice : choosingFor?.Group;

            // A group only learns what it is made of when its requirements are built, and the
            // only group that ever happens to is the one already in use. Every other choice
            // reached this screen with nothing to say and printed "?" — which is the one thing
            // a chooser must never do, since materials are the entire basis for choosing.
            foreach (var g in choices)
            {
                if (g.Materials == null) svc.Probe.BuildRequirements(g);
            }

            string what = choosingNode?.Req?.DisplayName ?? choosingFor?.DisplayName ?? "this";
            c.AddStaticText($"{choices.Count} ways to make {what}:", font, EB(8, y, DW - 16, 26));
            y += 34;

            // Past a handful of choices the two-line layout runs off the screen — Aqua
            // Vitae has thirty-two paths. Compact mode groups them: when the choices span
            // several recipe KINDS (an alloy, nine melt-downs and a grid recycler), the
            // method is the story — "Alloyed in a crucible" vs "Smelted…" vs "Crafting
            // grid" (Mark: "separate smelting methods from crafting"). When they are all
            // one kind, the origin is (Fruit / Grain via PathCategory). Sorted, two
            // columns, one line per path either way.
            if (choices.Count > 8)
            {
                bool mixedKinds = choices.Select(g => g.KindLabel()).Distinct().Count() > 1;
                var byCategory = choices
                    .GroupBy(g => mixedKinds ? g.KindLabel() : svc.Probe.PathCategory(g))
                    .OrderBy(grp => string.IsNullOrEmpty(grp.Key) ? "~" : grp.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();
                double colW = (DW - 16) / 2.0;

                foreach (var cat in byCategory)
                {
                    string head = string.IsNullOrEmpty(cat.Key) ? "Other" : cat.Key;
                    c.AddStaticText($"—  {head}  ({cat.Count()})  —", quiet, EB(8, y, DW - 16, 22));
                    y += 26;

                    var entries = cat
                        .OrderBy(g => g.MaterialsBrief ?? g.Materials ?? "", StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    int perColumn = (entries.Count + 1) / 2;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var choice = entries[i];
                        bool chosen = choice == current;
                        double x = 8 + (i / perColumn) * colW;
                        double ey = y + (i % perColumn) * 28;

                        var entryFont = font.Clone().WithColor(TallybookConfig.ParseColor(
                            chosen ? config.ColorSatisfied : config.ColorNone));
                        string brief = choice.MaterialsBrief ?? choice.Materials ?? "?";
                        FittedText(c, $"{(chosen ? "▶" : " ")} {brief}",
                            entryFont, EB(x, ey + 2, colW - 92, 24), colW - 92);

                        var picked = choice;
                        c.AddSmallButton(chosen ? "In use" : "Use",
                            () => { UseRecipe(picked); return true; },
                            EB(x + colW - 86, ey, 80, 26), EnumButtonStyle.Small);
                    }
                    y += perColumn * 28 + 6;
                }
            }
            else for (int i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                bool chosen = choice == current;

                var nameFont = font.Clone().WithColor(TallybookConfig.ParseColor(
                    chosen ? config.ColorSatisfied : config.ColorNone));
                string materials = string.IsNullOrEmpty(choice.Materials) ? "?" : choice.Materials;

                var picked = choice;
                c.AddStaticText($"{(chosen ? "▶" : " ")} {choice.MakesLabel()}",
                    nameFont, EB(8, y, 320, 24));

                c.AddSmallButton(chosen ? "In use" : "Use this",
                    () => { UseRecipe(picked); return true; },
                    EB(DW - 120, y - 2, 104, 26), EnumButtonStyle.Small);
                y += 24;

                FittedText(c, materials, quiet, EB(28, y, DW - 160, 22), DW - 160);
                y += 28;
            }

            y += 8;
            c.AddSmallButton("Back", () => { BackToList(); return true; }, EB(8, y, 90, 30));
        }

        void UseRecipe(RecipeVariantGroup choice)
        {
            if (choosingNode != null) svc.ChooseNodeRecipe(choosingFor, choosingNode, choice);
            else if (choosingFor != null) svc.ChoosePinRecipe(choosingFor, choice);

            choosingFor = null;
            choosingNode = null;
            BackToList();
        }

        // ------------------------------------------------------------- liquid calculator

        Pin calcPin;
        int calcContainerIdx;
        int calcCount = 1;
        long lastCalcTypingMs;

        void OpenLiquidCalc(Pin pin)
        {
            calcPin = pin;
            calcContainerIdx = 0;
            calcCount = 1;
            screen = TbScreen.LiquidCalc;
            Recompose();
        }

        /// <summary>
        /// Plan a liquid in container terms — "five barrels of acid" — instead of doing
        /// litre arithmetic by hand. Every liquid container this world has is offered,
        /// largest first (which puts the barrel on top without matching any name); the
        /// result lands in the pin's count, in litres, where the scaled ingredient rows and
        /// pot-load estimate already live.
        /// </summary>
        void ComposeLiquidCalc(GuiComposer c)
        {
            const int MaxRows = 12;
            var font = TableFont();
            var quiet = font.Clone().WithColor(GuiStyle.ColorParchment);
            double y = 40;

            var pin = calcPin;
            var options = svc.Probe.LiquidContainerOptions();
            if (pin == null || options.Count == 0)
            {
                c.AddStaticText("No liquid containers known in this world.", font, EB(8, y, DW - 16, 26));
                c.AddSmallButton("Back", () => { BackToList(); return true; }, EB(8, y + 40, 90, 30));
                return;
            }
            if (calcContainerIdx >= options.Count) calcContainerIdx = 0;

            c.AddStaticText($"How much {pin.DisplayName}? Pick a container to fill:",
                font, EB(8, y, DW - 16, 26));
            y += 34;

            // Two columns so the small containers stay visible instead of falling off a
            // capped list (Mark wanted to see them) — capacity order runs down the left
            // column first, so barrels still lead.
            int shown = Math.Min(options.Count, MaxRows * 2);
            int perColumn = (shown + 1) / 2;
            double colW = (DW - 16) / 2.0;
            double rowsTop = y;
            for (int i = 0; i < shown; i++)
            {
                bool chosen = i == calcContainerIdx;
                var rowFont = font.Clone().WithColor(TallybookConfig.ParseColor(
                    chosen ? config.ColorSatisfied : config.ColorNone));
                var opt = options[i];

                double x = 8 + (i / perColumn) * colW;
                double ry2 = rowsTop + (i % perColumn) * 30;

                c.AddInteractiveElement(new GuiElementItemIcon(
                    capi, new List<ItemStack> { opt.Sample }, config, EB(x + 2, ry2 + 3, 20, 20)));
                FittedText(c, $"{(chosen ? "▶" : " ")} {opt.Name} — {LitresLabel(opt.CapacityLitres)} L",
                    rowFont, EB(x + 28, ry2 + 4, colW - 110, 24), colW - 110);

                int picked = i;
                c.AddSmallButton(chosen ? "Picked" : "Pick",
                    () => { calcContainerIdx = picked; Recompose(); return true; },
                    EB(x + colW - 76, ry2, 70, 26), EnumButtonStyle.Small);
            }
            y = rowsTop + perColumn * 30;
            if (options.Count > shown)
            {
                c.AddStaticText($"…and {options.Count - shown} more containers.",
                    quiet, EB(38, y + 2, DW - 60, 22));
                y += 26;
            }

            y += 10;
            c.AddStaticText("How many:", font, EB(8, y + 4, 100, 26));
            c.AddSmallButton("-", () => { StepCalcCount(-1); return true; },
                EB(112, y, 26, 26), EnumButtonStyle.Small);
            c.AddTextInput(EB(142, y, 46, 26), OnCalcCountTyped, font, "calccnt");
            c.AddSmallButton("+", () => { StepCalcCount(+1); return true; },
                EB(192, y, 26, 26), EnumButtonStyle.Small);
            y += 38;

            int litres = CalcLitres(options);
            string summary = $"{calcCount} × {options[calcContainerIdx].Name} = {litres} L";

            // The number the player is really planning around: how many batches — pot
            // loads or barrel seals — producing this much takes.
            var batchGroup = pin.Group != null && (pin.Group.Cooking != null || pin.Group.Barrel != null)
                ? pin.Group
                : pin.Groups.FirstOrDefault(g => g.Cooking != null || g.Barrel != null);
            if (batchGroup != null && pin.SelfNode?.Req != null)
            {
                int items = (int)Math.Round(litres * pin.SelfNode.Req.ItemsPerLitre);
                string batches = TallyTree.BatchText(items, batchGroup);
                if (batches != null) summary += $"   —   about {batches}";
            }
            c.AddStaticText(summary, quiet, EB(8, y, DW - 16, 26));
            y += 40;

            c.AddSmallButton($"Set {litres} L", () => { ApplyLiquidCalc(litres); return true; },
                EB(8, y, 140, 30));
            c.AddSmallButton("Back", () => { calcPin = null; BackToList(); return true; },
                EB(160, y, 90, 30));

            // The liquid row's action columns are spent on the Volume Calc button, so the
            // recipe fold toggle (and, through it, the chooser) lives here instead.
            if (pin.Groups.Count > 0)
            {
                c.AddSmallButton(pin.GatherOnly ? "Show recipe" : "Hide recipe", () =>
                {
                    var p = pin;
                    calcPin = null;
                    BackToList();
                    if (p.GatherOnly) ExpandOrChoose(p, null, p.Groups);
                    else Collapse(p);
                    return true;
                }, EB(262, y, 120, 30));
                c.AddHoverText(pin.GatherOnly
                        ? "Unfold the recipe under the pin (and pick one, when there are several)."
                        : "Fold the recipe away and just count the liquid.",
                    font, 260, EB(262, y, 120, 30));
            }

            // Aqua Vitae distills from twenty-odd different spirits — a liquid with several
            // recipes must offer the choice here, because its row has no cycler (found by
            // Mark: the pin silently expanded down the apple path with no way to switch).
            if (pin.Groups.Count > 1)
            {
                int idx = pin.Groups.IndexOf(pin.Group) + 1;
                string label = idx > 0 ? $"Recipe {idx}/{pin.Groups.Count}…" : $"Recipe: {pin.Groups.Count} ways…";
                var chooseFor = pin;
                c.AddSmallButton(label, () =>
                {
                    choosingFor = chooseFor;
                    choosingNode = null;
                    calcPin = null;
                    screen = TbScreen.ChooseRecipe;
                    Recompose();
                    return true;
                }, EB(394, y, 150, 30));
                c.AddHoverText(
                    $"This liquid can be made {pin.Groups.Count} different ways — pick which "
                    + "path the ingredient list should plan for.",
                    font, 280, EB(394, y, 150, 30));
            }
        }

        static string LitresLabel(float litres)
            => litres.ToString("0.##", CultureInfo.InvariantCulture);

        int CalcLitres(List<RecipeProbe.ContainerOption> options)
        {
            float cap = options[calcContainerIdx].CapacityLitres;
            return (int)Math.Clamp(Math.Round(calcCount * cap), 1, 9999);
        }

        void StepCalcCount(int delta)
        {
            calcCount = Math.Clamp(calcCount + delta, 1, 999);
            Recompose();
        }

        void OnCalcCountTyped(string val)
        {
            if (restoringInputs) return;
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0) return;
            calcCount = Math.Min(999, n);

            // The summary line goes stale until a recompose, but recomposing steals focus —
            // same conflict the count column has, same answer: wait for a typing pause.
            lastCalcTypingMs = capi.World.ElapsedMilliseconds;
            capi.Event.RegisterCallback(_ =>
            {
                if (IsOpened() && screen == TbScreen.LiquidCalc
                    && capi.World.ElapsedMilliseconds - lastCalcTypingMs >= 850) Recompose();
            }, 900, permittedWhilePaused: true);
        }

        void RestoreCalcInput()
        {
            restoringInputs = true;
            try
            {
                SingleComposer.GetTextInput("calccnt")
                    ?.SetValue(calcCount.ToString(CultureInfo.InvariantCulture));
            }
            finally { restoringInputs = false; }
        }

        void ApplyLiquidCalc(int litres)
        {
            var pin = calcPin;
            calcPin = null;
            BackToList();
            if (pin == null) return;

            svc.Store.SetCount(pin, Math.Clamp(litres, 1, 9999));

            // The calculator's whole point is "what do I need for this much" — returning to
            // a folded pin would be a dead end, so unfold it, the same act as Expand.
            if (pin.GatherOnly && pin.Groups.Count > 0) ExpandOrChoose(pin, null, pin.Groups);
        }

        /// <summary>Ask which recipe when there is more than one way; just unfold when there
        /// is only one, since a chooser with a single entry is a dialog that wastes a click.</summary>
        bool ExpandOrChoose(Pin pin, TallyNode node, List<RecipeVariantGroup> choices)
        {
            if (choices == null || choices.Count == 0) return true;

            if (choices.Count > 1)
            {
                choosingFor = pin;
                choosingNode = node;
                screen = TbScreen.ChooseRecipe;
                Recompose();
                return true;
            }

            if (node != null) svc.ChooseNodeRecipe(pin, node, choices[0]);
            else svc.ToggleGatherOnly(pin);
            return true;
        }

        /// <summary>
        /// Settings that change what the list shows or counts, gathered in one place rather
        /// than sat above the table taking up a row on every visit.
        /// </summary>
        void ComposeOptions(GuiComposer c)
        {
            // The options text follows the size slider too — but ONLY the glyphs. Every box,
            // row and control keeps its fixed bounds regardless of font, so the window (sized
            // from its children) cannot resize under the cursor and the slider never moves
            // while being dragged (Mark: "don't let the window resize... so I can shrink and
            // grow it without interference"). A big font in a fixed box truncates with an
            // ellipsis; that costs a label its tail, where reflowing would cost the player
            // their grip on the slider.
            var font = TableFont();
            var hint = font.Clone().WithColor(GuiStyle.ColorParchment);
            double y = 40;

            // Switch, label, and a "?" carrying the explanation on hover. The reasoning used
            // to sit under each row in full, which reads well with three options and buries
            // the screen with ten.
            // The "?" column sits clear of the widest control on a row. The slider ends at
            // 372 and its handle and value bubble overhang that edge, so the mark needs real
            // space rather than merely a non-overlapping rectangle.
            const double markX = 408;

            void Option(string key, bool on, string label, string explain, Action<bool> set)
            {
                c.AddSwitch(v => { set(v); capi.StoreModConfig(config, "tallybook.json"); },
                    EB(8, y, 25, 25), key, 25);
                c.AddStaticText(TbText.Fit(font, label, markX - 52), font, EB(44, y + 4, markX - 52, 26));

                c.AddStaticText("?", hint.Clone().WithColor(GuiStyle.LinkTextColor), EB(markX, y + 4, 16, 24));
                c.AddHoverText(explain, font, 340, EB(markX, y + 4, 16, 24));

                y += 32;
            }

            // A slider rather than a number: the right size is the one that looks right, and
            // the HUD redraws on every step so you are choosing by eye, not by arithmetic.
            void Slider(string key, string label, string explain, Action<int> set)
            {
                c.AddStaticText(TbText.Fit(font, label, 236), font, EB(8, y + 4, 236, 26));
                c.AddSlider(v => { set(v); return true; }, EB(252, y + 2, 120, 22), key);

                c.AddStaticText("?", hint.Clone().WithColor(GuiStyle.LinkTextColor), EB(markX, y + 4, 16, 24));
                c.AddHoverText(explain, font, 340, EB(markX, y + 4, 16, 24));
                y += 32;
            }

            Slider("opt-hudfont", "Text size (HUD and this window)",
                "One size for everything Tallybook draws — the HUD, this window's table, and "
                + "these options. The HUD moves as you drag; this text follows the moment you "
                + "pause, so the slider never shifts under your hand.",
                v =>
                {
                    config.HudFontSize = v;
                    capi.StoreModConfig(config, "tallybook.json");
                    onHudChanged?.Invoke();
                    QueueOptionsRecompose();
                });

            Option("opt-hud", config.HudVisible,
                "Show the HUD overlay",
                "The corner readout, also toggled with K. Here as well because a hotkey can be "
                + "taken by another mod, and a HUD you cannot turn back on looks broken.",
                v => setHudVisible?.Invoke(v));

            Option("opt-group", config.HudGroupByItem,
                "Group the HUD under each item",
                "Each pinned item followed by what it needs. Off pools everything into one "
                + "shopping list, merging an item two builds both want into a single line.",
                v => { config.HudGroupByItem = v; svc.RecountAll(); });

            Option("opt-cycle", config.HudCycleVariants,
                "Cycle icons for \"any\" items",
                "A row that accepts several woods flips its icon through them, as the handbook does.",
                v => config.HudCycleVariants = v);

            Option("opt-mountbags", config.IncludeMountBags,
                $"Count bags on my animals within {config.MountBagRange} blocks",
                "Bags strapped to an animal you own — ridden or standing beside you — count "
                + "toward Have. Only animals the game says are yours, and only bags on their "
                + "backs: one lying on the ground or in a chest is not counted.",
                v => { config.IncludeMountBags = v; svc.RecountAll(); });

            Option("opt-worldtab", config.ShowWorldTab,
                "Show the World tab",
                "A reference card of this world's rules: every world-generation and gameplay "
                + "setting with the value this world runs — changes from the game's defaults "
                + "in colour — plus every mod the server runs, with versions. Handy on a "
                + "server whose settings you didn't write yourself.",
                v => config.ShowWorldTab = v);

            Option("opt-exploretab", config.ShowExploreTab,
                "Show the Explore tab",
                "Places you save to revisit — a mine, a ruin, a cave — each with a note "
                + "about what it is, longer notes if you want them, a map marker, and an "
                + "optional line on the HUD with the distance back.",
                v => config.ShowExploreTab = v);

            Option("opt-loretab", config.ShowLoreTab,
                "Show the Lore tab",
                "Your journal against everything this world's content hides: found volumes "
                + "with chapter counts, how many volumes and categories are still out there "
                + "(counts only — titles stay secret until found), and an Export button that "
                + "writes your found lore as a printable book.",
                v => config.ShowLoreTab = v);

            Option("opt-playertab", config.ShowPlayerTab,
                "Show the Player tab",
                "Your spawn points — the world spawn and your temporal-gear returning point, "
                + "each with a Map button and a map marker — plus respawns left there, "
                + "deaths, and a few other numbers about you. The markers follow the point: "
                + "set, moved, used up or turned off here, the map keeps up.",
                v => config.ShowPlayerTab = v);

            y += 8;
            c.AddSmallButton("Back", () => { BackToList(); return true; }, EB(8, y, 90, 30));
        }

        void ComposeConfirmClear(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            double y = 40;
            c.AddStaticText($"Remove all {svc.Store.Pins.Count} pinned item(s) and forget recipe choices?",
                font, EB(8, y, DW, 26));
            y += 40;
            c.AddSmallButton("Clear the list", () => { svc.Store.Clear(); BackToList(); return true; },
                EB(8, y, 130, 30));
            c.AddSmallButton("Cancel", () => { BackToList(); return true; }, EB(146, y, 90, 30));
        }

    }
}
