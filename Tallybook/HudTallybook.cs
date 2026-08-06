using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>
    /// The always-on corner overlay (spec §5): merged gather totals across all pins — one
    /// "Boards 12/48" line even when three pinned items want boards — plus pinned item
    /// headers that flip color when craftable. Leaves only: expanding a node in the dialog
    /// moves it from "gather this" to "craft this from the things below", so intermediates
    /// never appear here.
    ///
    /// Positioned ABSOLUTELY, never with EnumDialogArea.RightTop — a lesson inherited from
    /// Pin Matrix's map button: vanilla's coordinate overlay re-stacks itself below the first
    /// other RightTop-aligned composer every 250ms, and two aligned dialogs end up chasing
    /// each other around the corner forever. Absolute positioning keeps this HUD invisible to
    /// that stacking system; the cost is re-anchoring manually on resize/scale change.
    /// </summary>
    public class HudTallybook : GuiDialog
    {
        const double W = 250;
        const double LineH = 21;
        const double Margin = 8;

        readonly TallybookConfig config;
        readonly TallyService svc;
        long anchorListenerId;
        double composedForFrameW, composedForFrameH, composedForScale;

        /// <summary>Runtime toggle (hotkey K). Starts from config so players who keep it off
        /// stay off across relogs.</summary>
        public bool UserVisible;

        public HudTallybook(ICoreClientAPI capi, TallybookConfig config, TallyService svc) : base(capi)
        {
            this.config = config;
            this.svc = svc;
            UserVisible = config.HudVisible;
            svc.OnCountsChanged += Refresh;
            anchorListenerId = capi.Event.RegisterGameTickListener(OnAnchorTick, 1000);
        }

        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.HUD;
        public override bool Focusable => false;
        public override bool PrefersUngrabbedMouse => false;
        public override double DrawOrder => 0.05;

        public override void Dispose()
        {
            svc.OnCountsChanged -= Refresh;
            if (anchorListenerId != 0)
            {
                capi.Event.UnregisterGameTickListener(anchorListenerId);
                anchorListenerId = 0;
            }
            base.Dispose();
        }

        void OnAnchorTick(float dt)
        {
            if (!IsOpened()) return;
            if (capi.Render.FrameWidth == composedForFrameW
                && capi.Render.FrameHeight == composedForFrameH
                && RuntimeEnv.GUIScale == composedForScale) return;
            Refresh();
        }

        public void Refresh()
        {
            bool shouldShow = UserVisible && svc.Store.Pins.Count > 0 && capi.World?.Player != null;
            if (!shouldShow)
            {
                if (IsOpened()) TryClose();
                return;
            }

            Compose();
            if (!IsOpened()) TryOpen();
        }

        void Compose()
        {
            composedForFrameW = capi.Render.FrameWidth;
            composedForFrameH = capi.Render.FrameHeight;
            composedForScale = RuntimeEnv.GUIScale;

            var lines = BuildLines();
            double h = 8 + lines.Count * LineH + 8;

            double screenW = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;

            bool left = config.HudPosition.EndsWith("left");
            bool top = config.HudPosition.StartsWith("top");
            // Top offset clears the vanilla top-right HUD cluster (coordinates, clock);
            // bottom offset clears the hotbar.
            double x = left ? Margin : screenW - W - Margin;
            double y = top ? 110 : screenH - h - 96;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedPosition(x, y);
            var bgBounds = ElementBounds.Fill.WithFixedPadding(4);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("tallybook-hud", dialogBounds)
                .AddGameOverlay(bgBounds, GuiStyle.DialogLightBgColor)
                .BeginChildElements(bgBounds);

            double ly = 4;
            foreach (var (text, color, bold) in lines)
            {
                var font = bold
                    ? CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold)
                    : CairoFont.WhiteSmallText();
                if (color != null) font = font.WithColor(color);
                composer.AddStaticText(text, font, ElementBounds.Fixed(4, ly, W - 8, LineH));
                ly += LineH;
            }

            var replaced = SingleComposer;
            SingleComposer = composer.EndChildElements().Compose();
            if (replaced != null) capi.World.RegisterCallback(_ => replaced.Dispose(), 250);
        }

        List<(string Text, double[] Color, bool Bold)> BuildLines()
        {
            var satisfied = TallybookConfig.ParseColor(config.ColorSatisfied);
            var partial = TallybookConfig.ParseColor(config.ColorPartial);
            var none = TallybookConfig.ParseColor(config.ColorNone);

            var lines = new List<(string, double[], bool)>();

            // Pinned headers: what am I building, and is it ready (spec §5)
            foreach (var pin in svc.Store.Pins)
            {
                string name = pin.Count > 1 ? $"{pin.DisplayName} x{pin.Count}" : pin.DisplayName;
                lines.Add(pin.Craftable
                    ? ($"✓ {name}", satisfied, true)
                    : ($"• {name}", (double[])null, true));
            }

            // Merged gather list: what do I grab (spec §5). Per-item breakdown lives in the
            // dialog, which answers "for what".
            var gather = svc.MergedLeafTotals().Where(r => r.Needed > 0).ToList();
            if (gather.Count > 0)
            {
                lines.Add(("— gather —", none, false));
                int shown = 0;
                foreach (var row in gather)
                {
                    if (shown >= config.HudMaxRows)
                    {
                        lines.Add(($"+{gather.Count - shown} more…", none, false));
                        break;
                    }
                    var color = row.Have >= row.Needed ? satisfied : row.Have > 0 ? partial : none;
                    lines.Add(($"{row.Name}  {row.Have}/{row.Needed}", color, false));
                    shown++;
                }
            }
            return lines;
        }
    }
}
