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
    enum TbScreen { List, ConfirmClear, Options, ChooseRecipe }

    /// <summary>Errands from villagers are a different kind of thing from things you decided
    /// to build, so they get their own tab rather than being mixed in and distinguished only
    /// by a label.</summary>
    enum TbTab { Items, Quests, History }

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
        const double DW = 818;                 // content width

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
        const double ColBook = 654;            // handbook
        const double ColUnpin = 742;
        const double IndentW = 16;

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
        class InfoRow : Row
        {
            public string Text;
            /// <summary>Shown on hover when the row is longer than its column — a quest
            /// briefing is a paragraph and will always be.</summary>
            public string Full;
        }

        List<Row> allRows = new List<Row>();

        readonly QuestHistory history;

        /// <summary>Which archive entries are open for reading.</summary>
        readonly HashSet<string> expandedRecords = new HashSet<string>();

        readonly Action<bool> setHudVisible;
        readonly Action onHudChanged;
        readonly QuestWaypoints waypoints;

        static double DefaultHudFontSize => CairoFont.WhiteSmallText().UnscaledFontsize;

        public GuiDialogTallybook(ICoreClientAPI capi, TallybookConfig config, TallyService svc,
                                  QuestHistory history, QuestWaypoints waypoints,
                                  Action<bool> setHudVisible, Action onHudChanged)
            : base(capi)
        {
            this.onHudChanged = onHudChanged;
            this.config = config;
            this.svc = svc;
            this.history = history;
            this.waypoints = waypoints;
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
                capi.Event.RegisterCallback(_ => { recomposeQueued = false; OnCountsChanged(); }, 1000);
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

        List<int> PageStarts(List<Row> rows)
        {
            var starts = new List<int> { 0 };
            double used = 0, budget = PageBudget;

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
            }

            var replaced = SingleComposer;
            SingleComposer = composer.EndChildElements().Compose();
            if (replaced != null)
            {
                // Deferred: the old composer may still be mid-iteration in the event loop
                capi.World.RegisterCallback(_ => replaced.Dispose(), 250);
            }

            if (screen == TbScreen.List) RestoreCountInputs();
            else if (screen == TbScreen.Options) RestoreOptionSwitches();
        }

        string TitleFor() => screen switch
        {
            // These screens keep a qualifier — there it says which screen you are on.
            TbScreen.ConfirmClear => "Tallybook — Clear list",
            TbScreen.Options => "Tallybook — Options",
            TbScreen.ChooseRecipe => "Tallybook — How do you want to make it?",
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
            var next = index == 2 ? TbTab.History : index == 1 ? TbTab.Quests : TbTab.Items;
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
            int questCount = PinsForTab(TbTab.Quests).Count();

            // Open quests first, then what is finished — the archive reads as a story so far.
            var done = new List<QuestRecord>();
            if (history != null)
            {
                done.AddRange(history.InProgress());
                done.AddRange(history.Records());
            }
            var tabs = new[]
            {
                new GuiTab { DataInt = 0, Name = $"Items ({itemCount})" },
                new GuiTab { DataInt = 1, Name = $"Side quests ({questCount})" },
                new GuiTab { DataInt = 2, Name = $"History ({done.Count})" },
            };
            c.AddHorizontalTabs(tabs, EB(0, y, DW, 26), OnTabClicked,
                CairoFont.WhiteSmallText(),
                CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");
            y += 34;

            if (tab == TbTab.History) { ComposeHistory(c, done, ref y); return; }

            if (!PinsForTab(tab).Any())
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
            bool anyActive = svc.Store.Pins.Any(p => p.Active);
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
                lines.Add($"{(choices[i] == current ? "▸" : "  ")} {i + 1}. {materials}");
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

            void Progress(int have, int needed, bool dim)
            {
                string mark = have >= needed ? "✓" : have > 0 ? "◑" : "○";
                var pf = font.Clone().WithColor(dim ? TallybookConfig.ParseColor(config.ColorNone) : StatusColor(have, needed));
                c.AddStaticText($"{mark} {have}/{needed}", pf, EB(ColProg, ry + 4, 80, 24));
            }

            switch (row)
            {
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
                    double titleW = ColProg - nameX - 10;
                    FittedText(c, title, titleFont, EB(nameX, ry + 3, titleW, 26), titleW);

                    Progress(pin.Have, pin.Count, !pin.Active);

                    // Plain text input, not AddNumberInput: the number input draws its own
                    // up/down spinner arrows, which duplicate the − / + buttons flanking it.
                    // One set of steppers is enough (Mark); typing stays.
                    c.AddSmallButton("-", () => { StepCount(pin, -1); return true; }, EB(ColWant, y, 26, 26), EnumButtonStyle.Small);
                    c.AddTextInput(EB(ColWant + 30, y, 46, 26), val => OnCountTyped(pin, val), font, "cnt-" + pin.Key);
                    c.AddSmallButton("+", () => { StepCount(pin, +1); return true; }, EB(ColWant + 80, y, 26, 26), EnumButtonStyle.Small);

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
                    FittedText(c, name, nodeFont, EB(nx, ry + 4, NameW(indent), 24), NameW(indent));

                    Progress(node.Have, node.Needed, false);

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
                    c.AddStaticText(tr.Tool.Present ? "✓ carried" : "✗ missing", toolFont, EB(ColProg, y + 4, 100, 24));
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
            capi.Event.RegisterCallback(CheckOptionsRecompose, 650);
        }

        void CheckOptionsRecompose(float _)
        {
            if (capi.ElapsedMilliseconds - optionsFontChangedMs < 550)
            {
                capi.Event.RegisterCallback(CheckOptionsRecompose, 300);
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
                int active = tab == TbTab.History ? 2 : tab == TbTab.Quests ? 1 : 0;
                SingleComposer.GetHorizontalTabs("tabs")?.SetValue(active, false);

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
            }
            finally { restoringInputs = false; }
        }

        // ------------------------------------------------------------------ actions

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
        bool ShowOnMap(Pin pin)
        {
            var maps = capi.ModLoader.GetModSystem<WorldMapManager>();
            if (maps == null)
            {
                notice = "The map is not available.";
                Recompose();
                return true;
            }

            var target = MapTargetFor(pin);
            if (target == null)
            {
                notice = $"No location known for {pin.QuestGiver} yet — walk past them once.";
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
                capi.World.RegisterCallback(_ => Centre(maps, target), 100);
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
            capi.World.RegisterCallback(_ => Centre(maps, target), 400);
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

        bool OpenHandbookFor(Pin pin)
        {
            // Derive the page from the stack, NOT from pin.Key: a key carries the quest giver
            // ("…|for:Agnieszka") so an errand and your own goal can be separate rows, and the
            // handbook has never heard of that suffix.
            string page = RecipeProbe.PageCode(pin.Stack);
            if (page == null)
            {
                notice = $"No handbook page for {pin.DisplayName}.";
                Recompose();
                return true;
            }

            // Trade places rather than stack two centered dialogs; L reopens the list, and so
            // does the button that this flag puts under the handbook's Back button.
            HandbookPin.CameFromList = true;
            TryClose();

            var handbook = HandbookPin.FindDialog(capi);
            if (handbook == null || !handbook.IsOpened()) HandbookPin.OpenLikeThePlayerWould(capi);

            // A tick later: opening may have been the handbook's first, which is when it
            // registers itself with the GUI manager and builds its pages.
            capi.Event.RegisterCallback(_ => ShowHandbookPage(pin, page), 0);
            return true;
        }

        void ShowHandbookPage(Pin pin, string page)
        {
            var handbook = HandbookPin.FindDialog(capi);
            if (handbook == null)
            {
                capi.ShowChatMessage("Tallybook: the handbook is not available.");
                return;
            }
            if (!handbook.IsOpened()) handbook.TryOpen();

            if (handbook.OpenDetailPageFor(page)) return;

            // Attribute-carrying variants can name a page the handbook does not index; the
            // plain item's page is the honest second choice, and saying so beats leaving the
            // handbook sitting on whatever it showed last.
            string basePage = pin.Stack?.Collectible == null
                ? null
                : RecipeProbe.PageCode(new ItemStack(pin.Stack.Collectible));

            if (basePage == null || basePage == page || !handbook.OpenDetailPageFor(basePage))
            {
                capi.ShowChatMessage($"Tallybook: no handbook page for {pin.DisplayName}.");
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

                var line = font.Clone().WithColor(TallybookConfig.ParseColor(
                    openQuest ? config.ColorPartial : config.ColorSatisfied));

                string when = record.Day.HasValue ? $"day {(int)record.Day.Value}" : "earlier";
                string detail =
                    record.Stage == "open" ? "under way"
                    : record.Stage == "awaiting" ? "done — go and collect"
                    : record.Stage == "rewarded" ? $"rewarded, {when}"
                    : record.Stage == "completed" ? $"handed in, {when}"
                    : when;

                c.AddStaticText($"{(openQuest ? "•" : "✓")} {record.Name}", line, EB(8, y + 4, 300, 24));
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
            capi.Event.RegisterCallback(_ =>
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
            }, 0);
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

            for (int i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                bool chosen = choice == current;

                var nameFont = font.Clone().WithColor(TallybookConfig.ParseColor(
                    chosen ? config.ColorSatisfied : config.ColorNone));

                c.AddStaticText($"{(chosen ? "▸" : " ")} Makes {choice.OutputQuantity} × {choice.OutputName}",
                    nameFont, EB(8, y, 320, 24));

                var picked = choice;
                c.AddSmallButton(chosen ? "In use" : "Use this",
                    () => { UseRecipe(picked); return true; },
                    EB(DW - 120, y - 2, 104, 26), EnumButtonStyle.Small);
                y += 24;

                string materials = string.IsNullOrEmpty(choice.Materials) ? "?" : choice.Materials;
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
