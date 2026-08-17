using System;
using Vintagestory.API.Client;

namespace Tallybook
{
    /// <summary>
    /// "Is the player typing right now?" — asked before a hotkey does anything, and answered
    /// for the whole screen rather than just for our own windows.
    ///
    /// Tallybook's hotkeys are registered as <c>HotkeyType.GUIOrOtherControls</c>, which the
    /// API documents as "controls that are always available" — they fire while a dialog is
    /// open, by design, because that is how a list window is opened from inside the inventory.
    /// The cost is that they also fire while a text field somewhere has focus: a player naming
    /// a route in another mod's planner typed an L and got the shopping list (reported by a
    /// friend of Mark's, 0.3.16). Whether the other dialog *should* have swallowed the key
    /// first is beside the point — the mod that reacted is the mod that is wrong, and a guard
    /// on our side works whoever owns the window.
    ///
    /// The signal is the game's own: <c>GuiComposer.CurrentTabIndexElement</c> is documented as
    /// "the currently tabbed index element, if there is one currently focused", and every
    /// editable field in the game derives from <c>GuiElementEditableTextBase</c> (text inputs,
    /// text areas, number inputs). No reflection, and nothing that names another mod.
    /// </summary>
    public static class TypingGuard
    {
        /// <summary>Does any open dialog — ours, vanilla's, another mod's — currently hold the
        /// keyboard in a text field? Never throws: a hotkey that fails this check should act
        /// as it always did, not die.</summary>
        public static bool AnyTextInputFocused(ICoreClientAPI capi)
        {
            try
            {
                if (Focused(capi?.Gui?.OpenedGuis)) return true;

                // Also the registered-but-not-in-OpenedGuis case. The two lists are not the
                // same thing and a dialog's presence in either is not something to bet a
                // feature on — the handbook is missing from LoadedGuis until its first open,
                // and that cost a release once.
                return Focused(capi?.Gui?.LoadedGuis);
            }
            catch { return false; }
        }

        static bool Focused(System.Collections.Generic.IEnumerable<GuiDialog> dialogs)
        {
            if (dialogs == null) return false;

            foreach (var dlg in dialogs)
            {
                if (dlg == null || !dlg.IsOpened()) continue;
                foreach (var composer in dlg.Composers?.Values ?? Array.Empty<GuiComposer>())
                {
                    if (TextInputFocusedIn(composer)) return true;
                }
            }
            return false;
        }

        /// <summary>The same question for one composer — what a dialog of our own answers when
        /// the game asks whether it wants the keyboard to itself.</summary>
        public static bool TextInputFocusedIn(GuiComposer composer)
        {
            try
            {
                return composer?.CurrentTabIndexElement is GuiElementEditableTextBase field
                       && field.HasFocus;
            }
            catch { return false; }
        }
    }
}
