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
    enum TbScreen { List, ConfirmClear }

    /// <summary>Errands from villagers are a different kind of thing from things you decided
    /// to build, so they get their own tab rather than being mixed in and distinguished only
    /// by a label.</summary>
    enum TbTab { Items, Quests }

    /// <summary>
    /// Text that must stay on its own row. GuiElementStaticText does not clip: a line longer
    /// than its bounds wraps and overpaints the row below ("Bundle of bamboo stakes" did
    /// exactly that). GetTextExtents returns GUIScale-scaled pixels — verified empirically:
    /// the same string measures 2x wider at GUIScale 2 — so the unscaled column width scales
    /// up for the comparison.
    /// </summary>
    static class TbText
    {
        internal static string Fit(CairoFont font, string text, double maxWidth)
        {
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
        const double RowH = 28;
        const int PageSize = 13;

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
        class InfoRow : Row { public string Text; }

        List<Row> allRows = new List<Row>();

        public GuiDialogTallybook(ICoreClientAPI capi, TallybookConfig config, TallyService svc)
            : base(capi)
        {
            this.config = config;
            this.svc = svc;
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
                    allRows.Add(new InfoRow
                    {
                        Text = pin.QuestGiver != null
                            ? $"errand for {pin.QuestGiver} — gather these and hand them over"
                            : pin.Groups.Count > 0
                                ? "gathering — press Expand to show its recipe instead"
                                : "no crafting recipe — tracking what you gather",
                        Indent = 1
                    });
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

        int MaxPage => Math.Max(0, (allRows.Count - 1) / PageSize);

        // ------------------------------------------------------------------ composing

        void Recompose()
        {
            BuildRows();
            page = Math.Min(page, MaxPage);

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
            }

            var replaced = SingleComposer;
            SingleComposer = composer.EndChildElements().Compose();
            if (replaced != null)
            {
                // Deferred: the old composer may still be mid-iteration in the event loop
                capi.World.RegisterCallback(_ => replaced.Dispose(), 250);
            }

            if (screen == TbScreen.List) RestoreCountInputs();
        }

        string TitleFor() => screen switch
        {
            TbScreen.ConfirmClear => "Tallybook — Clear list",
            _ => "Tallybook — Shopping list",
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
            var next = index == 1 ? TbTab.Quests : TbTab.Items;
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
            var font = CairoFont.WhiteSmallText();
            double y = 34;

            int itemCount = PinsForTab(TbTab.Items).Count();
            int questCount = PinsForTab(TbTab.Quests).Count();

            var tabs = new[]
            {
                new GuiTab { DataInt = 0, Name = $"Items ({itemCount})" },
                new GuiTab { DataInt = 1, Name = $"Side quests ({questCount})" },
            };
            c.AddHorizontalTabs(tabs, EB(0, y, DW, 26), OnTabClicked,
                CairoFont.WhiteSmallText(),
                CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");
            y += 34;

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

            // Items tab only: the setting is about rows that accept many variants, which is a
            // recipe-ingredient idea. Errands name one exact item, so it has nothing to say
            // there and would just be clutter on that tab.
            if (tab == TbTab.Items)
            {
                c.AddSwitch(on =>
                {
                    config.HudCycleVariants = on;
                    capi.StoreModConfig(config, "tallybook.json");
                }, EB(8, y, 25, 25), "cyclevariants", 25);
                c.AddStaticText("Cycle icons for \"any\" items", font, EB(40, y + 4, 300, 26));
            }

            // One bulk toggle instead of a confirm dialog per item: unchecking loses nothing,
            // so it needs no confirmation — that is the whole point of parking over unpinning.
            bool anyActive = svc.Store.Pins.Any(p => p.Active);
            c.AddSmallButton(anyActive ? "Uncheck all" : "Check all",
                () => { svc.Store.SetAllActive(!anyActive); return true; },
                EB(DW - 112, y - 2, 112, 26), EnumButtonStyle.Small);
            y += 36;

            // Widths bounded by the next column: an over-wide header ran into its neighbour
            // and the two read as one word ("Have / needWant").
            var headFont = font.Clone().WithColor(GuiStyle.ColorParchment);
            c.AddStaticText("Item", headFont, EB(ColName, y, ColProg - ColName - 8, 22));
            c.AddStaticText("Have / Want", headFont, EB(ColProg, y, ColWant - ColProg - 8, 22));
            c.AddStaticText("Actions", headFont, EB(ColAct1, y, DW - ColAct1 - 8, 22));
            y += 22;
            c.AddGameOverlay(EB(0, y, DW, 2), GuiStyle.DialogBorderColor);
            y += 6;

            var visible = allRows.Skip(page * PageSize).Take(PageSize).ToList();
            foreach (var row in visible)
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
            string shown = TbText.Fit(font, text, maxW);
            c.AddStaticText(shown, font, bounds);
            if (shown == text) return;

            // Its own bounds instance: two elements sharing one ElementBounds fight over
            // layout, since each expects to own the object it was handed.
            c.AddHoverText(text, font, 340,
                EB(bounds.fixedX, bounds.fixedY, bounds.fixedWidth, bounds.fixedHeight));
        }

        void ComposeRow(GuiComposer c, Row row, ref double y)
        {
            var font = CairoFont.WhiteSmallText();
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

                    var titleFont = CairoFont.WhiteSmallishText();
                    if (!pin.Active) titleFont = titleFont.WithColor(TallybookConfig.ParseColor(config.ColorNone));
                    else if (pin.Complete || pin.Craftable) titleFont = titleFont.WithColor(TallybookConfig.ParseColor(config.ColorSatisfied));

                    string title = pin.DisplayName;
                    if (pin.QuestGiver != null) title += $"  (for {pin.QuestGiver})";
                    if (pin.Active && pin.Complete) title += " — got it";
                    else if (pin.Active && pin.Craftable) title += " — ready to craft";
                    FittedText(c, title, titleFont, EB(nx, ry + 3, NameW(indent), 26), NameW(indent));

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

                        if (pin.QuestX != 0 || pin.QuestY != 0 || pin.QuestZ != 0)
                        {
                            c.AddSmallButton("Map", () => ShowOnMap(pin),
                                EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
                            c.AddHoverText($"Open the map centred on {pin.QuestGiver}.",
                                font, 220, EB(ColAct2, y, 40, 26));
                        }
                    }
                    else if (pin.Groups.Count > 0)
                    {
                        // Same word as on an ingredient row, for the same act: unfold this
                        // item's recipe beneath it. Collapsing returns the pin to plain
                        // counting, which is where anything not pinned from the handbook
                        // starts (Mark) — a recipe existing is not a reason to assume the
                        // player intends to craft rather than gather.
                        c.AddSmallButton(pin.GatherOnly ? "Expand" : "Collapse",
                            () => { svc.ToggleGatherOnly(pin); return true; },
                            EB(ColAct1, y, 80, 26), EnumButtonStyle.Small);

                        if (!pin.GatherOnly && pin.Groups.Count > 1)
                        {
                            c.AddSmallButton($"{pin.Groups.IndexOf(pin.Group) + 1}/{pin.Groups.Count}",
                                () => { svc.CyclePinRecipe(pin); return true; },
                                EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
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
                                () => { svc.CycleNodeRecipe(nr.Pin, node); return true; },
                                EB(ColAct2, y, 40, 26), EnumButtonStyle.Small);
                        }
                    }
                    else if (svc.HasExpansion(node))
                    {
                        // Only craftable rows carry the affordance (spec §2a); raw materials
                        // get no button rather than one that scolds when clicked.
                        c.AddSmallButton("Expand", () => { TryExpand(nr.Pin, node); return true; },
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
                    FittedText(c, ir.Text, font.Clone().WithColor(GuiStyle.ColorParchment),
                        EB(nx, ry + 4, ColAct1 - nx, 24), ColAct1 - nx);
                    break;
            }
            y += RowH;
        }

        bool restoringInputs;

        void RestoreCountInputs()
        {
            // SetValue fires the input's change callback; without the guard, every recompose
            // would look like the player typing and defer the next live update for no reason.
            restoringInputs = true;
            try
            {
                // Present on the Items tab only.
                var cyc = SingleComposer.GetSwitch("cyclevariants");
                if (cyc != null) cyc.On = config.HudCycleVariants;

                // Tabs compose with the first one active; re-assert the real selection.
                SingleComposer.GetHorizontalTabs("tabs")?.SetValue(tab == TbTab.Quests ? 1 : 0, false);

                // Visible pins only: inputs exist just for the composed page, and asking the
                // composer for a key it never composed is unhealthy whether it throws or not.
                foreach (var row in allRows.Skip(page * PageSize).Take(PageSize).OfType<PinRow>())
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

            TryClose();
            if (!maps.IsOpened) maps.ToggleMap(EnumDialogType.Dialog);

            // A tick later: the map dialog composes on open, and its map element does not
            // exist until it has.
            capi.Event.RegisterCallback(_ =>
            {
                try
                {
                    var element = FindMapElement(maps.worldMapDlg);
                    element?.CenterMapTo(new BlockPos((int)pin.QuestX, (int)pin.QuestY, (int)pin.QuestZ, 0));
                }
                catch (Exception e)
                {
                    capi.Logger.Warning("[tallybook] could not centre the map: {0}", e.Message);
                }
            }, 60);
            return true;
        }

        /// <summary>
        /// The map widget inside the world map dialog. Found by type rather than by element
        /// key, because the key is an internal detail of a dialog we do not own.
        /// </summary>
        static GuiElementMap FindMapElement(GuiDialogWorldMap dialog)
        {
            if (dialog == null) return null;

            foreach (var composer in dialog.Composers.Values)
            {
                if (composer == null) continue;
                var field = HarmonyLib.AccessTools.Field(typeof(GuiComposer), "interactiveElements");
                if (field?.GetValue(composer) is System.Collections.IDictionary elements)
                {
                    foreach (var value in elements.Values)
                    {
                        if (value is GuiElementMap map) return map;
                    }
                }
            }
            return null;
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

        void TryExpand(Pin pin, TallyNode node)
        {
            if (!svc.CanExpand(pin, node, out string reason))
            {
                // The cycle guard and no-recipe cases explain themselves here rather than
                // silently doing nothing (spec §2a: disallow with a visible reason).
                notice = $"Cannot expand {node.Req.DisplayName}: {reason}";
                Recompose();
                return;
            }
            notice = "";
            svc.ExpandNode(pin, node);
        }

        // ------------------------------------------------------------------ confirm screens

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
