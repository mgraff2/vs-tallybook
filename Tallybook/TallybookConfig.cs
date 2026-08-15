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

        /// <summary>
        /// Group the HUD's gathering section under each pinned item — "Resonator" then what
        /// the resonator needs, then "Wooden table" then what that needs. **Off by default**:
        /// pooling merges an item two builds both want into one line, which is what you want
        /// while out fetching. Grouping answers the other question — what does this one thing
        /// still need — and costs a repeated row per shared material.
        /// </summary>
        public bool HudGroupByItem { get; set; } = false;

        /// <summary>
        /// Which set of defaults this file was written against, so a default that changes
        /// later can reach a config that already exists. Without it, changing a default only
        /// affects players who have never run the mod — everyone else keeps a value they
        /// never chose (the HUD's grouping, and the grey "nothing yet" colour, both hit this).
        /// </summary>
        public int ConfigVersion { get; set; }

        const int CurrentConfigVersion = 3;

        /// <summary>Let a HUD line too long for its column slide left every 15 seconds to
        /// show the rest of itself, instead of ending in "…". A HUD cannot be hovered for the
        /// full text the way the list can, so otherwise the tail is simply unreadable.</summary>
        public bool HudScrollLongLines { get; set; } = true;

        /// <summary>HUD text size. 0 means the game's own small-text size, whatever that is on
        /// this version — better than baking in a number that could drift.</summary>
        public double HudFontSize { get; set; }

        /// <summary>HUD rows before truncating with "+N more".</summary>
        public int HudMaxRows { get; set; } = 12;

        /// <summary>Show the HUD when the list is non-empty (K toggles at runtime).</summary>
        public bool HudVisible { get; set; } = true;

        /// <summary>When a HUD row accepts several variants ("Board, any wood"), cycle its
        /// icon through them once a second, handbook-style. Off: the first variant's icon
        /// stands for the set. Toggleable from the shopping list window; read live at render
        /// time, so flipping it needs no recompose.</summary>
        public bool HudCycleVariants { get; set; } = true;

        /// <summary>Show the World tab — this world's rules (every world-config setting and
        /// its value) and the mods it runs. On by default (Mark, 0.3.14 — they shipped
        /// opt-in as reference material, but "I don't think there are spoilers"); the
        /// Options switch remains for anyone who wants the header space back.</summary>
        public bool ShowWorldTab { get; set; } = true;

        /// <summary>Show the Player tab — spawn points (world spawn and the temporal-gear
        /// returning point, with respawns left there), deaths, and the like. On by default,
        /// same history as the World tab. Also gates the spawn map markers.</summary>
        public bool ShowPlayerTab { get; set; } = true;

        /// <summary>Show the Lore tab — journal progress against everything this world's
        /// content defines (found volumes with chapter counts, undiscovered volumes and
        /// categories as counts only), plus the lore-book export. ON by default (Mark),
        /// unlike the other reference tabs: lore is play, not reference.</summary>
        public bool ShowLoreTab { get; set; } = true;

        /// <summary>Show the Explore tab — places the player saved to revisit, with notes
        /// and map markers. On by default: like Lore, it is play, not reference.</summary>
        public bool ShowExploreTab { get; set; } = true;

        /// <summary>Map marker for saved places. A star, distinct from the quest x and the
        /// spawn home: these are the player's own discoveries. Same contract as the other
        /// marker settings — settings rather than constants because the icon set lives in
        /// the client; a rejected icon is reported in chat and fixable here.</summary>
        public string PlaceWaypointIcon { get; set; } = "star1";
        public string PlaceWaypointColor { get; set; } = "#E8A33C";

        /// <summary>Show a HUD line with the distance back to your current spawn — the
        /// temporal-gear returning point when one is set, else the world spawn. Lives on
        /// the Player tab rather than Options, next to the spawn rows it echoes.</summary>
        public bool HudSpawnDistance { get; set; } = false;

        /// <summary>Turn that HUD line the warning colour once you are farther out than
        /// HudSpawnDistanceWarnBlocks. Optional — off, the line never changes colour.</summary>
        public bool HudSpawnDistanceWarn { get; set; } = false;

        /// <summary>How far from spawn counts as "far", in blocks.</summary>
        public int HudSpawnDistanceWarnBlocks { get; set; } = 1000;

        /// <summary>The colour the spawn-distance line turns past the warning distance,
        /// chosen from a dropdown on the Player tab (any hex works from the file).</summary>
        public string HudSpawnDistanceWarnColor { get; set; } = "#FF4040";

        /// <summary>Map marker for spawn points, distinct from the quest blue so the way
        /// home reads differently from an errand. Same contract as the quest marker
        /// settings: settings rather than constants because the icon set lives in the
        /// client and can change; a rejected icon is reported in chat and fixable here.</summary>
        public string SpawnWaypointIcon { get; set; } = "home";
        public string SpawnWaypointColor { get; set; } = "#66cc66";

        /// <summary>Count what is in bags on animals you own and are near — the one you are
        /// riding included — as well as what you are carrying. Off by default: "what do I
        /// have on me" is the question this mod answers, and quietly counting the pack mule
        /// would change the answer without being asked. Ownership is the game's own, so on a
        /// shared server another player's animals are never counted.</summary>
        public bool IncludeMountBags { get; set; } = false;

        /// <summary>How near one of your animals must be to count, in blocks.</summary>
        public int MountBagRange { get; set; } = 15;

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

        /// <summary>Nothing collected yet. White, to sit level with the coordinates readout
        /// the HUD parks under — the old grey was legible on a dialog background but murky
        /// over the world (Mark).</summary>
        public string ColorNone { get; set; } = "#FFFFFF";

        /// <summary>The grey this used to be. A config still carrying it was never a choice,
        /// just the old default, so it is quietly brought forward — see Clamp.</summary>
        const string RetiredColorNone = "#909090";

        public void Clamp()
        {
            HudMaxRows = Math.Min(30, Math.Max(3, HudMaxRows));
            HudSpawnDistanceWarnBlocks = Math.Min(1_000_000, Math.Max(1, HudSpawnDistanceWarnBlocks));
            if (ParseColor(HudSpawnDistanceWarnColor) == null) HudSpawnDistanceWarnColor = "#FF4040";
            MountBagRange = Math.Min(64, Math.Max(1, MountBagRange));
            if (HudFontSize != 0) HudFontSize = Math.Min(32, Math.Max(8, HudFontSize));
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

            if (string.IsNullOrWhiteSpace(SpawnWaypointIcon)) SpawnWaypointIcon = "home";
            if (ParseColor(SpawnWaypointColor) == null) SpawnWaypointColor = "#66cc66";
            SpawnWaypointIcon = SpawnWaypointIcon.Trim().Replace(" ", "");
            SpawnWaypointColor = SpawnWaypointColor.Trim().Replace(" ", "");

            if (string.IsNullOrWhiteSpace(PlaceWaypointIcon)) PlaceWaypointIcon = "star1";
            if (ParseColor(PlaceWaypointColor) == null) PlaceWaypointColor = "#E8A33C";
            PlaceWaypointIcon = PlaceWaypointIcon.Trim().Replace(" ", "");
            PlaceWaypointColor = PlaceWaypointColor.Trim().Replace(" ", "");

            // Carry an untouched old default forward rather than leaving existing players on
            // a colour that was changed precisely because it read badly.
            if (string.Equals(ColorNone, RetiredColorNone, StringComparison.OrdinalIgnoreCase))
                ColorNone = "#FFFFFF";

            if (ConfigVersion < 1)
            {
                // Pooled totals became the default after grouping shipped; adopt it for files
                // written in between, which recorded the old default rather than a preference.
                HudGroupByItem = false;
            }
            if (ConfigVersion < 2)
            {
                // A hidden HUD is indistinguishable from a broken one, and the only way back
                // was a hotkey that other mods can and do claim. Anyone carrying "off" from
                // before there was a visible switch for it gets it back on; the switch in the
                // Options screen is now how you turn it off on purpose.
                HudVisible = true;
            }
            if (ConfigVersion < 3)
            {
                // The tabs went on-by-default; a false in an older file is the old default
                // on record, not a decision, so it comes forward. Anyone who had switched
                // them off on purpose pays one revisit to Options — the migration cannot
                // tell the two apart.
                ShowWorldTab = true;
                ShowPlayerTab = true;
            }
            ConfigVersion = CurrentConfigVersion;

            if (ParseColor(ColorSatisfied) == null) ColorSatisfied = "#80FF80";
            if (ParseColor(ColorPartial) == null) ColorPartial = "#FFCC66";
            if (ParseColor(ColorNone) == null) ColorNone = "#FFFFFF";
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
