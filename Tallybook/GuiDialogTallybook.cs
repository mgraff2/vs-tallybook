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
    enum TbScreen { List, ConfirmClear, Options, ChooseRecipe, LiquidCalc }

    /// <summary>Errands from villagers are a different kind of thing from things you decided
    /// to build, so they get their own tab rather than being mixed in and distinguished only
    /// by a label.</summary>
    enum TbTab { Items, Quests, History, World, Player }

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

        // Hold-to-unpin state (no confirm dialog — hold the button through the countdown).
        Pin holdPin;
        long holdStartMs;
        long holdTickId;
        int holdShownSecond;
        readonly List<(Pin Pin, ElementBounds Bounds)> unpinButtonBounds = new List<(Pin, ElementBounds)>();

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
        /// <summary>One world rule: setting name and the value this world runs with.</summary>
        class SettingRow : Row { public WorldSetting Setting; }
        /// <summary>A Player-tab row: label, value, and optionally a place a Map button can
        /// take you (absolute coordinates; all-zero means no button).</summary>
        class SpawnRow : Row
        {
            public string Label, Value, Hover;
            public double MapX, MapY, MapZ;
        }

        List<Row> allRows = new List<Row>();

        /// <summary>World tab model, built lazily per dialog-open (see BuildRows).</summary>
        List<WorldSettingsSection> worldSections;

        /// <summary>World tab filter text. Session state, reset on open — a filter that
        /// quietly survived into the next look would read as missing settings.</summary>
        string worldFilter = "";

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

        static double DefaultHudFontSize => CairoFont.WhiteSmallText().UnscaledFontsize;

        public GuiDialogTallybook(ICoreClientAPI capi, TallybookConfig config, TallyService svc,
                                  QuestHistory history, QuestWaypoints waypoints,
                                  StoryProgress story, SiteQuests sites, SpawnTracker spawnTracker,
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
            this.setHudVisible = setHudVisible;
            // OnCountsChanged is the single redraw signal: every store mutation funnels
            // through TallyService.RecountAll, whose signature covers structure and numbers.
            svc.OnCountsChanged += OnCountsChanged;
        }

        public override string ToggleKeyCombinationCode => "tallybook";
        public override bool PrefersUngrabbedMouse => true;
        public override double DrawOrder => 0.2;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            ignoreNextKeyPress = true;      // the opening hotkey's own char event
            notice = "";
            screen = TbScreen.List;
            worldSections = null;           // world config can change between opens
            worldFilter = "";

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

                foreach (var section in worldSections)
                {
                    // A matching category title keeps its whole section ("temporal" should
                    // give all of temporal stability); otherwise rows match individually,
                    // under their heading so a hit still says where it lives.
                    var shown = !filtering || section.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                        ? section.Settings
                        : section.Settings.Where(s => MatchesWorldFilter(s, f)).ToList();
                    if (shown.Count == 0) continue;

                    allRows.Add(new HeadingRow { Text = section.Title });
                    foreach (var s in shown) allRows.Add(new SettingRow { Setting = s });
                }

                if (filtering && allRows.Count == 0)
                {
                    string none = $"Nothing matches \"{f}\".";
                    allRows.Add(new InfoRow { Text = none, Full = none, Indent = 0 });
                }
                return;
            }

            if (tab == TbTab.Player) { BuildPlayerRows(); return; }

            // Rewards first: a walk you can make right now beats a list of things to find.
            if (tab == TbTab.Quests && history != null)
            {
                foreach (var waiting in history.AwaitingRewards())
                {
                    allRows.Add(new RewardRow { Name = waiting.Name, Giver = waiting.Giver, Indent = 0 });
                }
            }

            // Then the places: map-artifact destinations adopted as side quests.
            if (tab == TbTab.Quests && sites != null)
            {
                foreach (var sq in svc.Store.SiteQuests.Where(s => !s.Dismissed))
                {
                    allRows.Add(new SiteRow { Site = sq, Indent = 0 });
                    // Parked site quests keep their header row and nothing else — the same
                    // contract as an unchecked pin.
                    if (!sq.Active || !sq.TextExpanded) continue;

                    // Only what has been found. The unfound writings' titles stay the
                    // site's secret — the count above is the progress, the names are the
                    // content it has not given up yet.
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
                            : tab == TbTab.Player ? SpawnHudControlsHeight : 0));

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

        void OnTabClicked(int index)
        {
            // The handler receives the clicked tab's DataInt, not its array position
            // (decompile-verified: SetValue calls handler(tabs[i].DataInt)) — so optional
            // tabs keep stable identities here no matter which of them are showing.
            var next = index == 4 ? TbTab.Player
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
            unpinButtonBounds.Clear();
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
            var tabs = new List<GuiTab>
            {
                new GuiTab { DataInt = 0, Name = $"Items ({itemCount})" },
                new GuiTab { DataInt = 1, Name = $"Side quests ({questCount})" },
                new GuiTab { DataInt = 2, Name = $"History ({done.Count})" },
            };
            // Opt-in (Options screen), and appended last so the fixed tabs' positions never
            // move: no counts, because reference tabs have no work outstanding.
            if (config.ShowWorldTab) tabs.Add(new GuiTab { DataInt = 3, Name = "World" });
            if (config.ShowPlayerTab) tabs.Add(new GuiTab { DataInt = 4, Name = "Player" });
            c.AddHorizontalTabs(tabs.ToArray(), EB(0, y, DW, 26), OnTabClicked,
                CairoFont.WhiteSmallText(),
                CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");
            y += 34;

            if (tab == TbTab.History) { ComposeHistory(c, done, ref y); return; }
            if (tab == TbTab.World) { ComposeWorld(c, ref y); return; }
            if (tab == TbTab.Player) { ComposePlayer(c, ref y); return; }

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
                    }
                    else if (pin.Groups.Count > 0)
                    {
                        // Same word as on an ingredient row, for the same act: unfold this
                        // item's recipe beneath it. Collapsing returns the pin to plain
                        // counting, which is where anything not pinned from the handbook
                        // starts (Mark) — a recipe existing is not a reason to assume the
                        // player intends to craft rather than gather.
                        c.AddSmallButton(pin.GatherOnly ? "Expand" : "Collapse",
                            () => pin.GatherOnly
                                ? ExpandOrChoose(pin, null, pin.Groups)
                                : Collapse(pin),
                            EB(ColAct1, y, 80, 26), EnumButtonStyle.Small);

                        c.AddHoverText(pin.GatherOnly
                                ? "Counting this item only. Expand to show its recipe and plan the craft."
                                : "Showing this item's recipe. Collapse to go back to just counting it.",
                            font, 260, EB(ColAct1, y, 80, 26));

                        if (!pin.GatherOnly && pin.Groups.Count > 1)
                        {
                            c.AddSmallButton($"{pin.Groups.IndexOf(pin.Group) + 1}/{pin.Groups.Count}",
                                () => ExpandOrChoose(pin, null, pin.Groups),
                                EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);

                            c.AddHoverText(RecipeChoiceHelp(pin.Groups, pin.Group),
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
                    string unpinLabel = holdPin == pin ? $"Hold {holdShownSecond}…" : "Unpin";
                    c.AddSmallButton(unpinLabel, () => OnUnpinClicked(pin), ub, EnumButtonStyle.Small);
                    unpinButtonBounds.Add((pin, ub));
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
                int active = tab switch
                {
                    TbTab.Player => 3 + (config.ShowWorldTab ? 1 : 0),
                    TbTab.World => 3,
                    TbTab.History => 2,
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

            foreach (var (pin, bounds) in unpinButtonBounds)
            {
                if (bounds.PointInside(args.X, args.Y)) { StartHold(pin); return; }
            }
        }

        public override void OnMouseUp(MouseEvent args)
        {
            base.OnMouseUp(args);
            CancelHold();
        }

        void StartHold(Pin pin)
        {
            CancelHold();
            holdPin = pin;
            holdStartMs = capi.World.ElapsedMilliseconds;
            holdShownSecond = (int)(HoldMs / 1000);
            holdTickId = capi.Event.RegisterGameTickListener(OnHoldTick, 50);
            Recompose();    // flips the button label to the countdown
        }

        void OnHoldTick(float dt)
        {
            if (holdPin == null) { StopHoldTimer(); return; }

            // The mouse-up override is the normal cancel path; this also catches the pointer
            // drifting off the button and the dialog closing mid-hold.
            var entry = unpinButtonBounds.FirstOrDefault(e => e.Pin == holdPin);
            bool stillValid = IsOpened()
                && capi.Input.MouseButton.Left
                && entry.Bounds != null
                && entry.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY);
            if (!stillValid) { CancelHold(); return; }

            long held = capi.World.ElapsedMilliseconds - holdStartMs;
            if (held >= HoldMs)
            {
                var pin = holdPin;
                holdPin = null;
                StopHoldTimer();
                svc.Store.Remove(pin);      // Changed → recount → recompose
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
            if (holdPin == null) return;
            holdPin = null;
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
        /// on the row's sample stack.</summary>
        bool OpenHandbookForStack(ItemStack stack, string displayName)
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

        /// <summary>
        /// Quests you have finished. Dated ones first, oldest at the top; then everything
        /// that was already done the first time Tallybook looked, which cannot be dated and
        /// is ordered by how deep into the story it sits instead.
        /// </summary>
        static string SectionOf(QuestRecord r)
            => r.Stage == "open" || r.Stage == "awaiting" ? "open"
               : r.Day.HasValue ? "dated"
               : "undated";

        /// <summary>How tall this record draws, heading included when it opens a section.</summary>
        double HistoryRecordHeight(QuestRecord r, CairoFont quiet, ref string section)
        {
            double h = 0;
            string wants = SectionOf(r);
            if (wants != section) { section = wants; h += 24; }
            h += RowH;

            if (r.Text != null && expandedRecords.Contains(r.Chain + "|" + r.Stage))
            {
                foreach (var said in r.Text) h += TbText.Wrap(quiet, said, DW - 48).Count * LineStep + 6;
                h += 4;
            }
            return h;
        }

        /// <summary>
        /// The index each page starts at, for the current expansion state. Recomputed per
        /// compose because opening one record re-flows every page after it — which is exactly
        /// why a fixed records-per-page could not work here.
        /// </summary>
        List<int> HistoryPageStarts(List<QuestRecord> done, CairoFont quiet)
        {
            // What is left for rows after the window's own furniture: title, tabs, column
            // heads above; pager, Journal and Close below; dialog padding around all of it.
            double budget = Math.Max(200, capi.Render.FrameHeight / RuntimeEnv.GUIScale - 260);

            var starts = new List<int> { 0 };
            double used = 0;
            string section = null;

            for (int i = 0; i < done.Count; i++)
            {
                string sectionIfKept = section;
                double h = HistoryRecordHeight(done[i], quiet, ref sectionIfKept);

                // Never break before the first record of a page: one record taller than the
                // whole budget would otherwise start an empty page and loop forever.
                if (used > 0 && used + h > budget)
                {
                    starts.Add(i);
                    used = 0;
                    section = null;
                    h = HistoryRecordHeight(done[i], quiet, ref section);   // its heading returns
                }
                else section = sectionIfKept;

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
            var starts = HistoryPageStarts(done, quiet);
            this.page = Math.Min(Math.Max(this.page, 0), starts.Count - 1);

            int from = starts[this.page];
            int upto = this.page + 1 < starts.Count ? starts[this.page + 1] : done.Count;

            string section = null;
            var page = done.GetRange(from, upto - from);

            foreach (var record in page)
            {
                bool openQuest = record.Stage == "open" || record.Stage == "awaiting";
                string wants = SectionOf(record);

                if (wants != section)
                {
                    section = wants;
                    string heading =
                        wants == "open" ? "— still going —"
                        : wants == "dated" ? "— finished —"
                        : "— finished before Tallybook was watching —";

                    c.AddStaticText(heading, quiet, EB(8, y + 4, DW, 22));
                    y += 24;
                }

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

            var choices = choosingNode?.Choices ?? choosingFor?.Groups ?? new List<RecipeVariantGroup>();
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
