using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// A "← Back to Tallybook" button that appears under the handbook's own Back button while
    /// you are reading a page you reached from the list — so the return trip sits where the
    /// eye already looks for going back, not at the far end of the page text.
    ///
    /// A separate floating dialog rather than an injected element: the handbook builds its
    /// chrome in one pass and there is no supported way to add to it afterwards, so putting a
    /// button *inside* it would mean patching its layout and re-deriving that patch whenever
    /// the layout shifts between versions. This anchors to the handbook's live composer
    /// bounds instead — the same trick the HUD uses to sit below the minimap — which costs
    /// approximate placement and buys never being able to break the handbook.
    ///
    /// Visible only while the handbook is open *and* the player arrived from the list, so it
    /// never offers a journey back to somewhere they were not.
    /// </summary>
    public class HandbookReturnButton : GuiDialog
    {
        const double W = 168;
        const double H = 28;
        const double Gap = 6;

        readonly Action onBack;
        long tickId;
        double anchoredX = double.NaN, anchoredY = double.NaN;

        public HandbookReturnButton(ICoreClientAPI capi, Action onBack) : base(capi)
        {
            this.onBack = onBack;
            tickId = capi.Event.RegisterGameTickListener(OnTick, 400);
        }

        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.Dialog;
        public override bool PrefersUngrabbedMouse => true;
        public override bool Focusable => false;
        public override double DrawOrder => 0.3;      // above the handbook's own chrome

        public override void Dispose()
        {
            if (tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickId);
                tickId = 0;
            }
            base.Dispose();
        }

        void OnTick(float dt)
        {
            try
            {
                var handbook = HandbookPin.CameFromList
                    ? capi.Gui.LoadedGuis?.OfType<GuiDialogHandbook>().FirstOrDefault()
                    : null;

                if (handbook == null || !handbook.IsOpened() || !TryAnchor(handbook))
                {
                    if (IsOpened()) TryClose();
                    return;
                }
                if (!IsOpened()) TryOpen();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] handbook return button disabled: {0}", e.Message);
                if (IsOpened()) TryClose();
                if (tickId != 0) { capi.Event.UnregisterGameTickListener(tickId); tickId = 0; }
            }
        }

        /// <summary>
        /// Park under the bottom-left of the handbook's largest composer — its main window,
        /// which is where the Back button lives. False when the handbook has not laid itself
        /// out yet, in which case showing nothing beats showing a button in the corner of the
        /// screen.
        /// </summary>
        bool TryAnchor(GuiDialogHandbook handbook)
        {
            double scale = RuntimeEnv.GUIScale;
            ElementBounds main = null;

            foreach (var composer in handbook.Composers.Values)
            {
                var b = composer?.Bounds;
                if (b == null || b.OuterWidth <= 0) continue;
                if (main == null || b.OuterWidth * b.OuterHeight > main.OuterWidth * main.OuterHeight) main = b;
            }
            if (main == null) return false;

            double x = main.absX / scale;
            double y = (main.absY + main.OuterHeight) / scale + Gap;

            // Off the bottom of the screen: sit just inside it rather than out of reach.
            double screenH = capi.Render.FrameHeight / scale;
            if (y + H > screenH) y = screenH - H - Gap;

            if (x == anchoredX && y == anchoredY && SingleComposer != null) return true;

            anchoredX = x;
            anchoredY = y;
            Compose(x, y);
            return true;
        }

        void Compose(double x, double y)
        {
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.None)
                .WithFixedPosition(x, y);

            var bgBounds = ElementBounds.Fill.WithFixedPadding(3);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var replaced = SingleComposer;
            SingleComposer = capi.Gui
                .CreateCompo("tallybook-handbook-return", dialogBounds)
                .AddShadedDialogBG(bgBounds, false)
                .BeginChildElements(bgBounds)
                .AddSmallButton("← Back to Tallybook", () => { onBack(); return true; },
                    ElementBounds.Fixed(0, 0, W, H), EnumButtonStyle.Normal)
                .EndChildElements()
                .Compose();

            // Permitted while paused: this composes alongside the handbook, which pauses
            // singleplayer while it is open — the dispose just waits for unpause.
            if (replaced != null) capi.Event.RegisterCallback(_ => replaced.Dispose(), 250, permittedWhilePaused: true);
        }
    }
}
