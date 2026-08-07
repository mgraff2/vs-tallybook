using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Tallybook
{
    /// <summary>
    /// A live item icon for a HUD/dialog line. When the line accepts several variants and
    /// the config says so, it flips through them once a second like the handbook's recipe
    /// slideshow — so an "any wood" row shows woods, not one arbitrary member. The config is
    /// read at render time: toggling the setting takes effect without recomposing.
    /// </summary>
    class GuiElementItemIcon : GuiElement
    {
        readonly List<ItemStack> stacks;
        readonly TallybookConfig config;

        /// <summary>
        /// The slot must belong to an inventory. Drawing a perishable item (a raw hide, any
        /// food) makes the renderer ask the collectible for its transition state, and that
        /// path dereferences the slot's inventory — with a bare DummySlot it throws inside
        /// the render loop, which is fatal to the client rather than merely ugly.
        /// </summary>
        readonly DummySlot slot;

        bool renderFailed;

        public GuiElementItemIcon(ICoreClientAPI capi, List<ItemStack> stacks, TallybookConfig config, ElementBounds bounds)
            : base(capi, bounds)
        {
            this.stacks = stacks;
            this.config = config;
            slot = new DummySlot(null, new DummyInventory(capi));
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (renderFailed || stacks == null || stacks.Count == 0) return;

            int idx = config.HudCycleVariants && stacks.Count > 1
                ? (int)(api.World.ElapsedMilliseconds / 1000 % stacks.Count)
                : 0;
            slot.Itemstack = stacks[idx];

            try
            {
                api.Render.RenderItemstackToGui(
                    slot,
                    Bounds.renderX + Bounds.InnerWidth / 2,
                    Bounds.renderY + Bounds.InnerHeight / 2,
                    100,
                    (float)Bounds.InnerHeight * 0.75f,
                    ColorUtil.WhiteArgb,
                    true, false, false);
            }
            catch (Exception e)
            {
                // Belt and braces. This runs every frame inside the render loop, so anything
                // that escapes takes the whole client down — a shopping list is never worth
                // that. Draw nothing from here on rather than throw once per frame forever.
                renderFailed = true;
                (api as ICoreClientAPI)?.Logger.Warning(
                    "[tallybook] icon render failed, hiding it: {0}", e.Message);
            }
        }
    }

    /// <summary>
    /// The always-on corner overlay (spec §5): merged gather totals across all pins — one
    /// "Boards 12/48" line even when three pinned items want boards — plus pinned item
    /// headers that flip color when craftable. Leaves only: expanding a node in the dialog
    /// moves it from "gather this" to "craft this from the things below", so intermediates
    /// never appear here.
    ///
    /// Behaves like a second minimap panel: appears whenever the list has pins, disappears
    /// when it empties, and in a top corner tucks itself underneath whatever HUD elements
    /// already occupy that corner — minimap, coordinates, clock, other mods' overlays —
    /// (re-checked on the same 1s anchor tick that follows resize/scale changes).
    ///
    /// Positioned ABSOLUTELY, never with EnumDialogArea.RightTop: vanilla's coordinate
    /// overlay re-stacks itself below the first other RightTop-aligned composer every
    /// 250ms, and two aligned dialogs end up chasing
    /// each other around the corner forever. Absolute positioning keeps this HUD invisible to
    /// that stacking system; the cost is re-anchoring manually on resize/scale change.
    /// </summary>
    public class HudTallybook : GuiDialog
    {
        const double W = 250;
        const double LineH = 21;
        const double Margin = 8;
        const double CountW = 54;      // reserved right-hand column for "3/10"

        readonly TallybookConfig config;
        readonly TallyService svc;
        long anchorListenerId;
        double composedForFrameW, composedForFrameH, composedForScale, composedForAnchorBottom;
        string composedForDistances = "";

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
                && RuntimeEnv.GUIScale == composedForScale
                && CurrentAnchorBottom() == composedForAnchorBottom
                && DistanceSignature() == composedForDistances) return;
            Refresh();
        }

        public void Refresh()
        {
            // Unchecked pins are parked, not shopping — a list of only unchecked pins is an
            // empty list as far as the HUD is concerned.
            bool shouldShow = UserVisible && svc.Store.Pins.Any(p => p.Active) && capi.World?.Player != null;
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
            // Top corners sit directly below whatever already occupies that corner — minimap,
            // coordinates, clock, other mods' HUDs — measured from their real bounds, so the
            // HUD reads as one more panel in the stack and overlaps nothing. With the corner
            // clear it rises to the top margin. The bottom offset clears the hotbar.
            double x = left ? Margin : screenW - W - Margin;
            composedForAnchorBottom = CurrentAnchorBottom();
            composedForDistances = DistanceSignature();
            double y = top ? Math.Max(Margin, composedForAnchorBottom + Margin) : screenH - h - 96;

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
            foreach (var line in lines)
            {
                var font = line.Bold
                    ? CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold)
                    : CairoFont.WhiteSmallText();
                if (line.Color != null) font = font.WithColor(line.Color);

                double tx = 4;
                if (line.Stacks != null && line.Stacks.Count > 0)
                {
                    composer.AddInteractiveElement(new GuiElementItemIcon(
                        capi, line.Stacks, config, ElementBounds.Fixed(4, ly + 1, 18, 18)));
                    tx = 26;
                }

                // A trailing count gets its own reserved column. Baking it into the text
                // would let truncation eat the numbers — the one part of the line that has
                // to survive — on exactly the long labels that need trimming most.
                double textW = line.Trailing == null ? W - tx - 8 : W - tx - CountW - 8;
                composer.AddStaticText(TbText.Fit(font, line.Text, textW), font,
                    ElementBounds.Fixed(tx, ly, textW, LineH));

                if (line.Trailing != null)
                {
                    composer.AddStaticText(line.Trailing, font,
                        ElementBounds.Fixed(W - CountW - 4, ly, CountW, LineH));
                }
                ly += LineH;
            }

            var replaced = SingleComposer;
            SingleComposer = composer.EndChildElements().Compose();
            if (replaced != null) capi.World.RegisterCallback(_ => replaced.Dispose(), 250);
        }

        /// <summary>
        /// Bottom edge (unscaled GUI units) of whatever HUD elements occupy this HUD's
        /// configured column above it — the minimap, the coordinates readout, the clock, any
        /// mod's corner HUD; 0 when the column is clear or the position is a bottom corner.
        /// Reads every open HUD dialog's real composer bounds rather than special-casing
        /// types, so elements toggled off (coordinates hidden) cost no space and a
        /// repositioned minimap is still respected. Only the top half of the screen counts —
        /// hotbar and chat live at the bottom and are never in this HUD's way — and
        /// screen-filling composers are ignored as not really corner residents.
        /// </summary>
        double CurrentAnchorBottom()
        {
            if (!config.HudPosition.StartsWith("top")) return 0;

            double scale = RuntimeEnv.GUIScale;
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;
            double x0 = config.HudPosition.EndsWith("left") ? Margin : screenW - W - Margin;
            double x1 = x0 + W;

            double bottom = 0;
            var opened = capi.Gui?.OpenedGuis;
            if (opened == null) return 0;
            foreach (var gui in opened)
            {
                if (ReferenceEquals(gui, this)) continue;
                if (gui is not GuiDialog dlg || dlg.DialogType != EnumDialogType.HUD) continue;
                foreach (var composer in dlg.Composers.Values)
                {
                    var b = composer?.Bounds;
                    if (b == null) continue;
                    double top = b.absY / scale;
                    double bot = (b.absY + b.OuterHeight) / scale;
                    if (top > screenH * 0.5 || bot > screenH * 0.6) continue;
                    double cx0 = b.absX / scale, cx1 = (b.absX + b.OuterWidth) / scale;
                    if (cx1 < x0 || cx0 > x1) continue;
                    bottom = Math.Max(bottom, bot);
                }
            }
            return bottom;
        }

        class HudLine
        {
            public string Text;
            public string Trailing;
            public double[] Color;
            public bool Bold;
            public List<ItemStack> Stacks;
        }

        List<HudLine> BuildLines()
        {
            var satisfied = TallybookConfig.ParseColor(config.ColorSatisfied);
            var partial = TallybookConfig.ParseColor(config.ColorPartial);
            var none = TallybookConfig.ParseColor(config.ColorNone);

            var lines = new List<HudLine>();
            List<ItemStack> One(ItemStack s) => s == null ? null : new List<ItemStack> { s };
            void Header(string t) => lines.Add(new HudLine { Text = t, Color = none });

            double[] Status(int have, int needed)
                => have >= needed ? satisfied : have > 0 ? partial : none;

            var active = svc.Store.Pins.Where(p => p.Active).ToList();

            // Errands first: they are someone else's deadline, and they name a place to walk
            // back to, which the rest of the list never does.
            var quests = active.Where(p => p.QuestGiver != null).ToList();
            if (quests.Count > 0)
            {
                Header("— side quests —");
                foreach (var pin in quests)
                {
                    lines.Add(new HudLine
                    {
                        Text = $"{pin.DisplayName} for {pin.QuestGiver}{Where(pin)}",
                        Trailing = $"{pin.Have}/{pin.Count}",
                        Color = Status(pin.Have, pin.Count),
                        Stacks = One(pin.Stack)
                    });
                }
            }

            // Everything else you are working towards, and the materials it needs. Errands
            // contribute nothing here by design — an errand is counted, not decomposed, so
            // its ingredients only appear once "To list" has made it a goal of your own.
            var goals = active.Where(p => p.QuestGiver == null).ToList();
            var gather = svc.MergedLeafTotals().Where(r => r.Needed > 0).ToList();

            if (goals.Count > 0 || gather.Count > 0) Header("— gathering —");

            foreach (var pin in goals)
            {
                string mark = pin.Complete ? "✓" : pin.Craftable ? "⚒" : "•";
                lines.Add(new HudLine
                {
                    Text = $"{mark} {pin.DisplayName}",
                    Trailing = $"{pin.Have}/{pin.Count}",
                    Color = pin.Complete || pin.Craftable ? satisfied : null,
                    Bold = true,
                    Stacks = One(pin.Stack)
                });
            }

            int shown = 0;
            foreach (var row in gather)
            {
                if (shown >= config.HudMaxRows)
                {
                    Header($"+{gather.Count - shown} more…");
                    break;
                }
                lines.Add(new HudLine
                {
                    Text = row.Name,
                    Trailing = $"{row.Have}/{row.Needed}",
                    Color = Status(row.Have, row.Needed),
                    Stacks = row.Req?.SampleStacks(capi.World)
                });
                shown++;
            }

            // Tools: the recipe won't craft without them, so a list that only shows
            // consumables is incomplete — a glass slab needs the saw as much as the glass.
            // Presence-checked, never counted (spec §4); merged so one saw row covers all pins.
            var tools = svc.MergedTools();
            if (tools.Count > 0)
            {
                Header("— tools —");
                foreach (var t in tools)
                {
                    lines.Add(new HudLine
                    {
                        Text = $"{(t.Present ? "✓" : "•")} {t.DisplayName}",
                        Trailing = t.Present ? null : "missing",
                        Color = t.Present ? satisfied : partial,
                        Stacks = t.SampleStacks(capi.World)
                    });
                }
            }
            return lines;
        }

        /// <summary>
        /// How far away the quest giver is, when we know where they were standing. Distance
        /// rather than a place name: the game has no notion of "the village" to ask for, and
        /// inventing one would be a guess dressed up as a fact. Rounded to 5m, which is also
        /// what keeps the display from churning — see DistanceSignature.
        /// </summary>
        string Where(Pin pin)
        {
            int m = DistanceTo(pin);
            return m < 0 ? "" : $" ({m}m)";
        }

        int DistanceTo(Pin pin)
        {
            if (pin.QuestX == 0 && pin.QuestY == 0 && pin.QuestZ == 0) return -1;

            var me = capi.World?.Player?.Entity?.Pos;
            if (me == null) return -1;

            double dx = me.X - pin.QuestX, dz = me.Z - pin.QuestZ;
            int m = (int)(Math.Sqrt(dx * dx + dz * dz) / 5) * 5;
            return m > 5000 ? -1 : m;
        }

        /// <summary>
        /// The quest distances as currently displayed. Walking changes them, and nothing else
        /// would trigger a redraw — counts have not moved — so the anchor tick watches this.
        /// Comparing the *rounded* values means a recompose happens every five metres rather
        /// than every step.
        /// </summary>
        string DistanceSignature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var pin in svc.Store.Pins)
            {
                if (pin.QuestGiver == null || !pin.Active) continue;
                sb.Append(DistanceTo(pin)).Append(',');
            }
            return sb.ToString();
        }
    }
}
