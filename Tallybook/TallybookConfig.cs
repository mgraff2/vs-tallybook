using System;
using System.Globalization;

namespace Tallybook
{
    /// <summary>
    /// ModConfig/tallybook.json (spec §9). Loaded once at client start; bad values are clamped
    /// rather than rejected, and a bad file falls back to defaults rather than crashing.
    ///
    /// The dialog (L) and HUD (K) hotkeys are registered through the game's hotkey system, so
    /// they are rebindable in Settings → Controls like any other key — that is the VS-native
    /// way and it wins over config strings.
    /// </summary>
    public class TallybookConfig
    {
        /// <summary>Corner for the HUD overlay: topleft | topright | bottomleft | bottomright.</summary>
        public string HudPosition { get; set; } = "topright";

        /// <summary>HUD rows before truncating with "+N more".</summary>
        public int HudMaxRows { get; set; } = 12;

        /// <summary>Show the HUD when the list is non-empty (K toggles at runtime).</summary>
        public bool HudVisible { get; set; } = true;

        /// <summary>Ask before unpinning, so a misclick doesn't eat the list (spec §4).</summary>
        public bool ConfirmOnUnpin { get; set; } = true;

        /// <summary>Status colors, themeable (spec §9).</summary>
        public string ColorSatisfied { get; set; } = "#80FF80";
        public string ColorPartial { get; set; } = "#FFCC66";
        public string ColorNone { get; set; } = "#909090";

        public void Clamp()
        {
            HudMaxRows = Math.Min(30, Math.Max(3, HudMaxRows));
            switch ((HudPosition ?? "").ToLowerInvariant())
            {
                case "topleft": case "topright": case "bottomleft": case "bottomright":
                    HudPosition = HudPosition.ToLowerInvariant(); break;
                default:
                    HudPosition = "topright"; break;
            }
            if (ParseColor(ColorSatisfied) == null) ColorSatisfied = "#80FF80";
            if (ParseColor(ColorPartial) == null) ColorPartial = "#FFCC66";
            if (ParseColor(ColorNone) == null) ColorNone = "#909090";
        }

        /// <summary>"#RRGGBB" -> rgba doubles for CairoFont, or null when unparseable.</summary>
        public static double[] ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            string s = hex.TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return null;
            return new[] { ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0, 1.0 };
        }
    }
}
