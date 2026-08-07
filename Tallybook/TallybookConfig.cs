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

        /// <summary>When a HUD row accepts several variants ("Board, any wood"), cycle its
        /// icon through them once a second, handbook-style. Off: the first variant's icon
        /// stands for the set. Toggleable from the shopping list window; read live at render
        /// time, so flipping it needs no recompose.</summary>
        public bool HudCycleVariants { get; set; } = true;

        /// <summary>True (default): unpinning requires holding the button through a 1-second
        /// countdown — deliberate, but never a dialog. False: a single click unpins
        /// instantly.</summary>
        public bool ConfirmOnUnpin { get; set; } = true;

        /// <summary>Map marker for a tracked quest giver: the same "x" the game marks places
        /// with, in light blue instead of the usual red, so an errand you took on reads
        /// differently at a glance from the markers you placed yourself. Hex colour rather
        /// than a colour name because hex is what the waypoint command is known to accept.
        /// Both are settings rather than constants because the icon set is built into the
        /// client and can change between versions: if one is ever rejected, the waypoint
        /// command says so in chat and this is fixable without a new build.</summary>
        public string QuestWaypointColor { get; set; } = "#4fc3f7";
        public string QuestWaypointIcon { get; set; } = "x";

        /// <summary>Pin the quest marker to the map edge, as the game's own place markers
        /// are, so the way back is visible without opening the map.</summary>
        public bool QuestWaypointPinned { get; set; } = true;

        /// <summary>Mark tracked quest givers on the map at all. Off means Tallybook never
        /// touches your waypoints.</summary>
        public bool QuestWaypoints { get; set; } = true;

        /// <summary>Add a villager's fetch request to the list automatically when you accept
        /// it, rather than requiring any extra click. Requests already offered once are
        /// remembered, so unpinning one is a decision that sticks.</summary>
        public bool AutoTrackQuests { get; set; } = true;

        /// <summary>Shimmer above a quest giver once you are carrying everything they asked
        /// for, so a completed errand is visible in the world rather than only in the list.
        /// Shown only when *all* of that NPC's tracked requests are satisfied.</summary>
        public bool QuestReadyGlow { get; set; } = true;
        public string QuestReadyGlowColor { get; set; } = "#FFBE3C";

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
            if (string.IsNullOrWhiteSpace(QuestWaypointColor)) QuestWaypointColor = "#4fc3f7";
            if (string.IsNullOrWhiteSpace(QuestWaypointIcon)) QuestWaypointIcon = "x";
            // The waypoint command takes these as single tokens; a space would silently shift
            // every argument after it and land the marker somewhere absurd.
            QuestWaypointColor = QuestWaypointColor.Trim().Replace(" ", "");
            QuestWaypointIcon = QuestWaypointIcon.Trim().Replace(" ", "");

            if (ParseColor(QuestReadyGlowColor) == null) QuestReadyGlowColor = "#FFBE3C";
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
