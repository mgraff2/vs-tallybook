using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Tallybook
{
    enum TbScreen { List, ConfirmClear, ConfirmUnpin }

    /// <summary>
    /// The management dialog (spec §6): pinned items with count steppers and direct numeric
    /// entry, per-ingredient status, manual expansion (spec §2a) with recipe choice, unpin
    /// with confirm, clear-all with confirm.
    ///
    /// Follows the Pin Matrix recompose-everything pattern: any data change rebuilds the
    /// composer. The one refinement is a typing grace period — live inventory recounts defer
    /// while the player is typing in a count field, because a recompose steals focus and
    /// eating half a typed number is worse than numbers arriving two seconds late.
    /// </summary>
    public class GuiDialogTallybook : GuiDialog
    {
        const double DW = 620;                 // content width
        const double RowH = 27;
        const int PageSize = 13;

        readonly TallybookConfig config;
        readonly TallyService svc;

        TbScreen screen = TbScreen.List;
        string notice = "";
        Pin unpinTarget;
        int page;
        long lastCountTypingMs;
        bool recomposeQueued;

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

        void BuildRows()
        {
            allRows = new List<Row>();
            foreach (var pin in svc.Store.Pins)
            {
                allRows.Add(new PinRow { Pin = pin, Indent = 0 });

                if (!pin.HasRecipe)
                {
                    allRows.Add(new InfoRow { Text = "no crafting recipe known — kept as a reminder", Indent = 1 });
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
                case TbScreen.ConfirmUnpin: ComposeConfirmUnpin(composer); break;
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
            TbScreen.ConfirmUnpin => "Tallybook — Unpin",
            _ => "Tallybook — Shopping list",
        };

        void OnTitleBarClose()
        {
            if (screen == TbScreen.List) TryClose();
            else BackToList();
        }

        void BackToList()
        {
            unpinTarget = null;
            screen = TbScreen.List;
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
            var font = CairoFont.WhiteSmallText();
            double y = 34;

            if (svc.Store.Pins.Count == 0)
            {
                c.AddStaticText("Your list is empty.", font, EB(8, y + 8, DW, 26));
                c.AddStaticText("Open the handbook (H), find an item, and click \"Add to Tallybook\" on its page.",
                    font.Clone().WithColor(GuiStyle.ColorParchment), EB(8, y + 38, DW, 46));
                y += 96;
                c.AddSmallButton("Close", () => { TryClose(); return true; }, EB(DW - 90, y, 90, 28));
                return;
            }

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

        void ComposeRow(GuiComposer c, Row row, ref double y)
        {
            var font = CairoFont.WhiteSmallText();
            double x = 8 + row.Indent * 20;

            switch (row)
            {
                case PinRow pr:
                {
                    var pin = pr.Pin;
                    var titleFont = CairoFont.WhiteSmallishText();
                    if (pin.Craftable) titleFont = titleFont.WithColor(TallybookConfig.ParseColor(config.ColorSatisfied));

                    string title = pin.Craftable ? $"{pin.DisplayName} — ready to craft" : pin.DisplayName;
                    c.AddStaticText(title, titleFont, EB(x, y + 3, 296, 26));

                    c.AddSmallButton("-", () => { StepCount(pin, -1); return true; }, EB(308, y, 26, 26), EnumButtonStyle.Small);
                    c.AddNumberInput(EB(338, y, 52, 26), val => OnCountTyped(pin, val), font, "cnt-" + pin.Code);
                    c.AddSmallButton("+", () => { StepCount(pin, +1); return true; }, EB(394, y, 26, 26), EnumButtonStyle.Small);

                    if (pin.Groups.Count > 1)
                    {
                        int idx = pin.Groups.IndexOf(pin.Group) + 1;
                        c.AddSmallButton($"Recipe {idx}/{pin.Groups.Count}",
                            () => { svc.CyclePinRecipe(pin); return true; },
                            EB(428, y, 104, 26), EnumButtonStyle.Small);
                    }

                    c.AddSmallButton("Unpin", () => { RequestUnpin(pin); return true; },
                        EB(DW - 76, y, 76, 26), EnumButtonStyle.Small);
                    break;
                }

                case NodeRow nr:
                {
                    var node = nr.Node;
                    var color = StatusColor(node.Have, node.Needed);
                    string mark = node.Have >= node.Needed ? "✓" : node.Have > 0 ? "◑" : "○";
                    string extra = node.ReadyToCraft ? "  (ready to craft)" : "";
                    c.AddStaticText($"{mark} {node.Req.DisplayName}  {node.Have}/{node.Needed}{extra}",
                        font.Clone().WithColor(color), EB(x, y + 4, Math.Max(140, 420 - x), 24));

                    if (node.Expanded)
                    {
                        c.AddSmallButton("Collapse", () => { svc.CollapseNode(node); return true; },
                            EB(428, y, 84, 26), EnumButtonStyle.Small);
                        if (node.Choices != null && node.Choices.Count > 1)
                        {
                            int idx = node.Choices.IndexOf(node.Choice) + 1;
                            c.AddSmallButton($"{idx}/{node.Choices.Count}",
                                () => { svc.CycleNodeRecipe(nr.Pin, node); return true; },
                                EB(516, y, 50, 26), EnumButtonStyle.Small);
                        }
                    }
                    else if (svc.HasExpansion(node))
                    {
                        // Only craftable rows carry the affordance (spec §2a); raw materials
                        // get no button rather than one that scolds when clicked.
                        c.AddSmallButton("Expand", () => { TryExpand(nr.Pin, node); return true; },
                            EB(428, y, 84, 26), EnumButtonStyle.Small);
                    }
                    break;
                }

                case ToolRow tr:
                {
                    var color = tr.Tool.Present
                        ? TallybookConfig.ParseColor(config.ColorSatisfied)
                        : TallybookConfig.ParseColor(config.ColorNone);
                    string mark = tr.Tool.Present ? "✓" : "✗";
                    c.AddStaticText($"{mark} requires: {tr.Tool.DisplayName} (not consumed)",
                        font.Clone().WithColor(color), EB(x, y + 4, 460, 24));
                    break;
                }

                case InfoRow ir:
                    c.AddStaticText(ir.Text, font.Clone().WithColor(GuiStyle.ColorParchment), EB(x, y + 4, 460, 24));
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
                // Visible pins only: inputs exist just for the composed page, and asking the
                // composer for a key it never composed is unhealthy whether it throws or not.
                foreach (var row in allRows.Skip(page * PageSize).Take(PageSize).OfType<PinRow>())
                {
                    SingleComposer.GetNumberInput("cnt-" + row.Pin.Code)
                        ?.SetValue(row.Pin.Count.ToString(CultureInfo.InvariantCulture));
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
                // Decrement to 0 unpins, behind the same confirm as the button (spec §4).
                RequestUnpin(pin);
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
                RequestUnpin(pin);
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

        void RequestUnpin(Pin pin)
        {
            if (!config.ConfirmOnUnpin)
            {
                svc.Store.Remove(pin.Code);
                return;
            }
            unpinTarget = pin;
            screen = TbScreen.ConfirmUnpin;
            Recompose();
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

        void ComposeConfirmUnpin(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            double y = 40;
            if (unpinTarget == null) { BackToList(); return; }

            c.AddStaticText($"Unpin {unpinTarget.DisplayName} x{unpinTarget.Count}?", font, EB(8, y, DW, 26));
            y += 40;
            c.AddSmallButton("Unpin", () =>
            {
                svc.Store.Remove(unpinTarget.Code);
                BackToList();
                return true;
            }, EB(8, y, 90, 30));
            c.AddSmallButton("Cancel", () => { BackToList(); return true; }, EB(106, y, 90, 30));
        }
    }
}
