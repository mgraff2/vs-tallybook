using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Tallybook
{
    /// <summary>One pinned item, its count, and its live requirement tree.</summary>
    public class Pin
    {
        // ---- persisted ---------------------------------------------------------------
        public string Code;
        public bool IsBlock;

        /// <summary>Stack attributes of the exact variant pinned, as a JSON token; null for
        /// plain items. Attributes are identity here, not decoration: the four bookshelf
        /// shapes share one block code and differ only in attributes, and pinning one of them
        /// must never resolve to another (spec: pin what the handbook page showed).</summary>
        public string Attributes;

        /// <summary>Who asked for this, when it came from a villager's fetch request. Shown
        /// on the row so a quest item reads as an errand rather than an unexplained pin, and
        /// kept per-pin so quest items and self-set goals live in one list.</summary>
        public string QuestGiver;

        /// <summary>Where that NPC was standing when the errand was taken on, so the list can
        /// point you back at them without depending on a map waypoint surviving.</summary>
        public double QuestX, QuestY, QuestZ;

        /// <summary>
        /// Where this errand's own map points, captured from the waypoint reading it created
        /// and kept. Persisted because the live waypoint read this comes from is *known* to
        /// fail intermittently (the fifty-marker incident was that read coming back empty) —
        /// a Map button driven by the live read vanished whenever the read did. Resolved on
        /// the tick, never at draw time; the dialog only ever looks at these fields.
        /// </summary>
        public double SiteX, SiteY, SiteZ;

        [JsonIgnore] public bool HasSite => SiteX != 0 || SiteY != 0 || SiteZ != 0;

        /// <summary>What the villager said when they asked, kept so the errand can be re-read
        /// long after the conversation is closed.</summary>
        public List<string> QuestText = new List<string>();

        /// <summary>Is this errand's conversation open for reading? Closed to begin with — an
        /// errand is one line until you ask for the story — and remembered either way, so a
        /// quest you opened stays open next time you look.</summary>
        public bool QuestTextExpanded;

        /// <summary>Maps that came with the errand, by name. "Fetch a lens" and "fetch a lens
        /// from the Devastation" are different errands to be handed.</summary>
        public List<string> QuestMaps = new List<string>();

        /// <summary>We have placed a map marker for this errand. Persisted, and the *only*
        /// thing that decides whether another gets placed — reading the map back to find out
        /// is not reliable, and a check that silently fails means a marker every few seconds
        /// forever (it did).</summary>
        public bool WaypointPlaced;

        /// <summary>The VS Quest quest this errand belongs to, or null. Set means the errand
        /// is the framework's, not the dialogue files': its counting rule is the framework's
        /// own (every accepted code, summed), its completion comes from the giver rather than
        /// from a dialogue variable, and the vanilla history sweep leaves it alone.</summary>
        public string VsQuestId;

        /// <summary>Which of that quest's gather objectives this pin is. A quest asking for
        /// two different things is two rows, and they must not collapse into one.</summary>
        public int VsQuestObjective;

        /// <summary>The giver's entity id — the framework's own identity for a quest giver,
        /// and the only exact one: quest givers can share a display name, so the glow and the
        /// completion check both key on this rather than on who they are called.</summary>
        public long VsQuestGiverId;

        /// <summary>
        /// Count this item, do not break it down — "I am going to go and find these". The
        /// difference matters whenever a recipe exists but is not how anyone would actually
        /// obtain the thing: iron ingots have exactly one grid recipe, chiselling an iron
        /// anvil back into ingots, so a decomposed ingot pin demands an anvil nobody has
        /// while the real source, smelting, is not a grid recipe at all. Cycling the recipe
        /// button past the last recipe returns here.
        /// </summary>
        public bool GatherOnly;

        /// <summary>Unchecked pins stay in the list and in the save file but stop counting:
        /// no ingredient rows, no HUD presence. Deactivating is deliberately confirm-free —
        /// it exists so emptying the working list is one action, not one dialog per item —
        /// which is safe because nothing is lost.</summary>
        public bool Active = true;

        public int Count = 1;

        /// <summary>Which recipe the player chose for this item (spec §3); resolved back to a
        /// group by signature at load.</summary>
        public string RecipeSignature;

        /// <summary>Which rows are expanded, with which recipe each (spec §2a).</summary>
        public List<SavedExpansion> Expansions = new List<SavedExpansion>();

        // ---- resolved at load / recompute, never persisted -----------------------------
        [JsonIgnore] public ItemStack Stack;
        [JsonIgnore] public List<RecipeVariantGroup> Groups = new List<RecipeVariantGroup>();
        [JsonIgnore] public RecipeVariantGroup Group;
        [JsonIgnore] public List<TallyNode> RootNodes = new List<TallyNode>();
        [JsonIgnore] public List<Requirement> Tools = new List<Requirement>();

        /// <summary>"How many of this do I have" — every pin tracks its own acquisition, not
        /// just its ingredients. For an item with no recipe this is the whole point of the
        /// pin; for a craftable one it is what the ingredient counts scale down against.</summary>
        [JsonIgnore] public TallyNode SelfNode;

        [JsonIgnore] public int Have => SelfNode?.Have ?? 0;

        /// <summary>For a pinned liquid, Count is in LITRES — the only unit anyone discusses
        /// a liquid in ("3" means three litres of acid, not three hundredth-litre portions) —
        /// while Have and the tree math stay in portion items. This is the converter.</summary>
        [JsonIgnore]
        public bool LiquidUnits => SelfNode?.Req?.ShowLitres == true;

        [JsonIgnore]
        public int CountInItems => LiquidUnits
            ? System.Math.Max(1, (int)System.Math.Round(Count * SelfNode.Req.ItemsPerLitre))
            : Count;

        /// <summary>Got what was asked for. Outranks "ready to craft": no reason to build
        /// what is already in the bag.</summary>
        [JsonIgnore] public bool Complete => Have >= CountInItems;

        /// <summary>This pin is a CONSTRUCTION goal — the boat the rollers start, not the
        /// rollers themselves. It gets its own Key (so it coexists with a plain pin of the
        /// same item), and its Have never counts the carried starter item: holding rollers
        /// is not having a boat (Mark — the two must be trackable at the same time).</summary>
        public bool BuildSite;

        /// <summary>The material this build is committed to ("oak"), or null for "any one
        /// material". Bound rows name and count that material only — an oak boat wants oak
        /// (Mark). Set automatically when the pinned page already names the material (an
        /// oak sailboat's own page), or by the selector on the build row.</summary>
        public string BuildMaterial;

        [JsonIgnore] public bool HasRecipe => Group != null;

        /// <summary>Build pins say "Drakkar build", not "Drakkar Construction construction"
        /// (Mark) — a starter item already NAMED "… Construction" (Shipwright's kits all
        /// are) loses that word before the suffix goes on.</summary>
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string name = Stack?.GetName() ?? Code;
                if (!BuildSite) return name;
                if (name.EndsWith(" construction", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - " construction".Length);
                return name + " build";
            }
        }

        /// <summary>Identity at handbook-page granularity — the dedup/removal/preference key.
        /// Code alone is not enough once attribute-distinct variants can be pinned. Cached
        /// only once the stack is resolved, so an early read never locks in the bare code.
        ///
        /// The quest giver is part of it: an errand for Agnieszka and your own plan to make
        /// the same item are two different rows living in two different tabs, and merging
        /// them would let her request quietly rewrite your own goal's count.</summary>
        [JsonIgnore]
        public string Key => Stack == null
            ? MakeKey(Code, QuestGiver) + Suffix(BuildSite, VsQuestId, VsQuestObjective)
            : key ??= MakeKey(RecipeProbe.PageCode(Stack) ?? Code, QuestGiver)
                      + Suffix(BuildSite, VsQuestId, VsQuestObjective);
        string key;

        public static string MakeKey(string pageCode, string questGiver)
            => questGiver == null ? pageCode : $"{pageCode}|for:{questGiver}";

        /// <summary>What distinguishes rows that share an item and a giver. A VS Quest errand
        /// carries its quest and objective: one giver can be on several quests at once, and two
        /// of them wanting the same item are two errands, not one doubled.</summary>
        public static string Suffix(bool buildSite, string vsQuestId, int vsObjective)
            => (buildSite ? "|build" : "")
               + (vsQuestId == null ? "" : $"|q:{vsQuestId}#{vsObjective}");

        /// <summary>All direct rows satisfied and every tool present (spec §4's rollup).</summary>
        [JsonIgnore]
        public bool Craftable => HasRecipe
            && RootNodes.Count > 0
            && RootNodes.All(n => n.Satisfied)
            && Tools.All(t => t.Present);
    }

    /// <summary>
    /// The pinned list, saved per world (spec §7). Persists item code, count, recipe choice
    /// and expansion state; itemstacks and recipes are re-resolved on load so a world with
    /// different mods degrades to "no recipe known" rather than restoring something that no
    /// longer exists.
    ///
    /// A corrupt or missing file yields an empty list and never throws — losing a shopping
    /// list is a nuisance, crashing someone's client over one is not acceptable (spec §7).
    /// </summary>
    /// <summary>On-disk shape: pins plus per-world recipe preferences (spec §2a: choosing a
    /// recipe for an item records it as that item's default; cleared with the list).</summary>
    /// <summary>One finished quest, kept after its pins are gone.</summary>
    public class QuestRecord
    {
        public string Chain;        // "beataquest"
        public string Name;         // "Beata"
        public string Stage;        // completed | rewarded

        /// <summary>In-game day it finished, or null when it was already done the first time
        /// we looked — a quest finished before this mod existed can be known about but not
        /// dated, and inventing a day would be worse than admitting that.</summary>
        public double? Day;

        /// <summary>Where it sits in the story when there is no date: how many other quests
        /// must come before it. Prerequisites are visible in the dialogue gates, so undated
        /// entries can still be put in a sensible order rather than an arbitrary one.</summary>
        public int Depth;

        /// <summary>What they said when they asked, kept so a finished quest can be re-read
        /// years later. The whole point of an archive is that it still means something.</summary>
        public List<string> Text = new List<string>();
    }

    public class SaveFile
    {
        public List<Pin> Pins = new List<Pin>();
        public Dictionary<string, string> RecipePrefs = new Dictionary<string, string>();

        /// <summary>Errands the load-time scan has already offered. It runs every login, so
        /// without this an errand you unpinned would return each time you logged in. Talking
        /// to the NPC still re-adds it — that is a deliberate act, this is not.</summary>
        public List<string> OfferedQuests = new List<string>();

        /// <summary>Where each NPC was last seen, so an errand recovered at load can still be
        /// marked on the map. Only ever filled in for NPCs you have actually been near.</summary>
        public Dictionary<string, string> NpcPlaces = new Dictionary<string, string>();

        public List<QuestRecord> QuestHistory = new List<QuestRecord>();

        /// <summary>Last seen stage per quest chain. What makes dating possible: a chain that
        /// was mid-flight when we last looked and is finished now finished *while we watched*,
        /// so it gets today's date; one that was already finished the first time we ever saw
        /// it did not.</summary>
        public Dictionary<string, string> ChainStates = new Dictionary<string, string>();

        /// <summary>Story-step facts, recorded forward-only: seen:/done: per step,
        /// obs: for entity-scope state observed in conversation, pinned:/parked: guards.
        /// Monotonic because half the underlying signals are transient — an item handed
        /// over or a waypoint read that fails must never un-happen a step.</summary>
        public Dictionary<string, string> StoryStates = new Dictionary<string, string>();

        /// <summary>Map-artifact destinations tracked as side quests.</summary>
        public List<SiteQuest> SiteQuests = new List<SiteQuest>();

        /// <summary>Locator waypoints already offered as site quests (title@x,z). The
        /// adoption pass runs every tick, so without this a dismissed site would return
        /// every second — same contract as OfferedQuests.</summary>
        public List<string> OfferedSites = new List<string>();

        /// <summary>The player's spawn tracking (Player tab): returning point, its use
        /// budget, marker flags.</summary>
        public SpawnState Spawn = new SpawnState();

        /// <summary>History-tab group folds the player changed from the defaults
        /// (key "year:2" / "earlier" → expanded). Only deviations are stored, so a new
        /// default reaches everyone who never touched that group.</summary>
        public Dictionary<string, bool> HistoryGroups = new Dictionary<string, bool>();

        /// <summary>How the Side quests tab (and the HUD's side-quest section) is ordered:
        /// "custom" (the hand-arranged list order), "distance", "progress", "name",
        /// "giver". Per world, because the arrangement it preserves is per world.</summary>
        public string QuestSort = "custom";

        /// <summary>The hand-arranged Side-quests order, as row keys — errand pins by
        /// Pin.Key, map-site quests as "site:&lt;key&gt;" — one list because the tab is one
        /// list: rearranging must cover every row, not just the pin rows (Mark).</summary>
        public List<string> QuestOrder = new List<string>();

        /// <summary>VS Quest errands being tracked: what evidence they rest on, and what the
        /// framework's own counters read the last time it told us. The pins carry the
        /// shopping list; this carries what a pin cannot know.</summary>
        public List<VsQuestTrack> VsQuests = new List<VsQuestTrack>();

        /// <summary>Locations the player saved to revisit (the Explore tab).</summary>
        public List<SavedPlace> Places = new List<SavedPlace>();

        /// <summary>Place markers owed a removal ("title|x|y|z") — same retry contract as
        /// SpawnState.PendingRemovals: the waypoint list reads back empty at random, and a
        /// removal attempted against a failed read removes nothing.</summary>
        public List<string> PlaceRemovals = new List<string>();
    }

    /// <summary>
    /// A location the player saved to revisit — a mine half-dug, a ruin to come back to, a
    /// cave mouth worth remembering. Entirely player-authored: the name, the one-line "what
    /// it is", and any longer notes are theirs, and nothing here is derived from the world.
    /// Coordinates are absolute, like every stored position; display converts.
    /// </summary>
    public class SavedPlace
    {
        public string Name;
        /// <summary>One line: what this place is. Shows on the tab and (with the name) on
        /// the HUD when the place is put there.</summary>
        public string Note;
        /// <summary>Longer notes: one free-text field (Mark), newline-separated. Lines
        /// starting "- " or "* " draw as bullets; "[ ]" / "[x]" as checkboxes, toggleable
        /// from the reading view. Dialog-only, never on the HUD.</summary>
        public string NotesText;

        /// <summary>Pre-0.3.15 shape (a list of note lines), read once and folded into
        /// NotesText at load — never written again. Deleting the field instead would make
        /// Json.NET throw on any save written during the iteration that used it.</summary>
        public List<string> Notes;

        /// <summary>Are the notes open for reading? Closed to begin with — a place is one
        /// line until you ask for the story — and remembered either way, same contract as
        /// an errand's conversation (Pin.QuestTextExpanded).</summary>
        public bool NotesExpanded;

        [JsonIgnore]
        public bool HasNotes => !string.IsNullOrWhiteSpace(NotesText);
        public double X, Y, Z;
        public bool ShowOnHud;
        public bool WaypointPlaced;
        /// <summary>In-game day it was saved, for "saved day 47" on the row.</summary>
        public double? Day;

        [JsonIgnore]
        public string Key => $"{Name}@{(int)X},{(int)Z}";
    }

    public class PinStore
    {
        readonly ICoreClientAPI capi;
        readonly List<Pin> pins = new List<Pin>();

        public IReadOnlyList<Pin> Pins => pins;
        public Dictionary<string, string> RecipePrefs { get; private set; } = new Dictionary<string, string>();
        public HashSet<string> OfferedQuests { get; private set; } = new HashSet<string>();
        public Dictionary<string, string> NpcPlaces { get; private set; } = new Dictionary<string, string>();
        public List<QuestRecord> QuestHistory { get; private set; } = new List<QuestRecord>();
        public Dictionary<string, string> ChainStates { get; private set; } = new Dictionary<string, string>();
        public Dictionary<string, string> StoryStates { get; private set; } = new Dictionary<string, string>();
        public List<SiteQuest> SiteQuests { get; private set; } = new List<SiteQuest>();
        public HashSet<string> OfferedSites { get; private set; } = new HashSet<string>();
        public SpawnState Spawn { get; private set; } = new SpawnState();
        public Dictionary<string, bool> HistoryGroups { get; private set; } = new Dictionary<string, bool>();
        public string QuestSort { get; set; } = "custom";
        public List<string> QuestOrder { get; private set; } = new List<string>();
        public List<SavedPlace> Places { get; private set; } = new List<SavedPlace>();
        public List<string> PlaceRemovals { get; private set; } = new List<string>();
        public List<VsQuestTrack> VsQuests { get; private set; } = new List<VsQuestTrack>();
        public event Action OnChanged;

        public PinStore(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        /// <summary>One Side-quests row, whichever kind it is — an errand pin or a
        /// map-site quest. The tab and the HUD are one list of these, so ordering and
        /// rearranging cover every row, not just the pins (Mark: "I should be able to
        /// rearrange all 5, not just the 2 bottom ones").</summary>
        public class QuestEntry
        {
            public Pin Pin;
            public SiteQuest Site;
            public string Key => Pin != null ? Pin.Key : "site:" + Site.Key;
            public string Name => Pin != null ? Pin.DisplayName : Site.Title;
        }

        /// <summary>
        /// One ordering rule for everywhere side quests are listed — the tab and the HUD
        /// must agree or "rearrange the list" would rearrange only half of it. "custom"
        /// (or anything unrecognised) follows the hand-arranged QuestOrder key list, rows
        /// it has never seen appended in their natural order; distance puts placeless
        /// errands last rather than pretending they stand at 0,0.
        /// </summary>
        public List<QuestEntry> OrderQuestEntries(List<QuestEntry> entries, Vec3d me)
        {
            switch (QuestSort)
            {
                case "distance":
                    if (me == null) return entries;
                    return entries.OrderBy(e =>
                    {
                        double x, z;
                        if (e.Pin != null)
                        {
                            if (e.Pin.QuestX == 0 && e.Pin.QuestY == 0 && e.Pin.QuestZ == 0)
                                return double.MaxValue;
                            x = e.Pin.QuestX; z = e.Pin.QuestZ;
                        }
                        else { x = e.Site.X; z = e.Site.Z; }
                        double dx = me.X - x, dz = me.Z - z;
                        return dx * dx + dz * dz;
                    }).ToList();
                case "progress":
                    // Nearest to done first — the walkable hand-ins float to the top. A
                    // site's honest fraction needs the lore scan; reached-or-not is the
                    // part the store can know.
                    return entries.OrderByDescending(e => e.Pin != null
                        ? (e.Pin.Count > 0 ? (double)e.Pin.Have / e.Pin.Count : 0)
                        : (e.Site.Visited ? 1.0 : 0.0)).ToList();
                case "name":
                    return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                case "giver":
                    // Sites have no giver; they gather after the errands rather than
                    // scattering between them.
                    return entries.OrderBy(e => e.Pin?.QuestGiver ?? "￿", StringComparer.OrdinalIgnoreCase)
                        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                default:
                    return CustomOrder(entries);
            }
        }

        List<QuestEntry> CustomOrder(List<QuestEntry> entries)
            => entries.OrderBy(e =>
            {
                int i = QuestOrder.IndexOf(e.Key);
                return i < 0 ? int.MaxValue : i;    // stable: unknowns keep natural order, at the end
            }).ToList();

        /// <summary>
        /// Move one Side-quests row a step within the hand-arranged order — offered only
        /// under the custom sort, where the arrangement is what displays. The whole
        /// current arrangement is written back as keys, so rows the order list had never
        /// seen become part of it the first time anything moves.
        /// </summary>
        public bool MoveQuestEntry(string key, int delta)
        {
            var entries = new List<QuestEntry>();
            foreach (var s in SiteQuests)
            {
                if (!s.Dismissed) entries.Add(new QuestEntry { Site = s });
            }
            foreach (var p in pins)
            {
                if (p.QuestGiver != null) entries.Add(new QuestEntry { Pin = p });
            }

            var keys = CustomOrder(entries).Select(e => e.Key).ToList();
            int pos = keys.IndexOf(key);
            if (pos < 0) return false;
            int newPos = Math.Clamp(pos + delta, 0, keys.Count - 1);
            if (newPos == pos) return false;

            (keys[pos], keys[newPos]) = (keys[newPos], keys[pos]);
            QuestOrder = keys;
            Save();
            Changed();
            return true;
        }

        /// <summary>Pinning an already-pinned item increments it — never a duplicate row
        /// (spec §3). "Already pinned" is judged by page identity, not code: the 8-plank and
        /// 5-plank bookshelves share a code but are different pins.</summary>
        /// <param name="setCount">
        /// False (the handbook): pinning again means "one more of these", so the count adds
        /// up. True (a fetch request): the NPC wants a fixed 10, so an existing pin is raised
        /// to 10 rather than pushed to 13 — asking for more than the errand needs would be a
        /// lie, and lowering a larger goal the player set themselves would be presumptuous.
        /// </param>
        /// <param name="activate">
        /// False (auto-tracked errands): leave an existing pin's checked state alone. A pin
        /// the player parked is a decision to set it aside, and quietly re-checking it every
        /// time they talk to that villager would override them.
        /// </param>
        public Pin Add(ItemStack stack, int amount = 1, bool setCount = false, bool activate = true,
                       string questGiver = null, bool buildSite = false,
                       string vsQuestId = null, int vsObjective = 0)
        {
            var code = stack?.Collectible?.Code?.ToShortString();
            if (code == null) return null;

            string key = Pin.MakeKey(RecipeProbe.PageCode(stack) ?? code, questGiver)
                         + Pin.Suffix(buildSite, vsQuestId, vsObjective);
            var pin = pins.FirstOrDefault(p => p.Key == key);
            if (pin == null)
            {
                pin = new Pin
                {
                    Code = code,
                    IsBlock = stack.Class == EnumItemClass.Block,
                    Attributes = RecipeProbe.AttributesJson(stack),
                    QuestGiver = questGiver,
                    BuildSite = buildSite,
                    VsQuestId = vsQuestId,
                    VsQuestObjective = vsObjective,
                    Count = Math.Max(1, amount),
                    Stack = stack.Clone()
                };
                pins.Add(pin);
            }
            else
            {
                pin.Count = setCount
                    ? Math.Max(pin.Count, Math.Max(1, amount))
                    : pin.Count + Math.Max(1, amount);
                // Pinning by hand is an unambiguous "I want this on my list now", so it
                // re-checks a parked pin. Auto-tracking gets no such licence.
                if (activate) pin.Active = true;
            }

            Changed();
            return pin;
        }

        public void SetActive(Pin pin, bool active)
        {
            if (pin == null || pin.Active == active) return;
            pin.Active = active;
            Changed();
        }

        public void SetAllActive(bool active)
        {
            bool moved = pins.Any(p => p.Active != active)
                || SiteQuests.Any(s => !s.Dismissed && s.Active != active);
            if (!moved) return;
            foreach (var pin in pins) pin.Active = active;
            foreach (var s in SiteQuests)
            {
                if (!s.Dismissed) s.Active = active;
            }
            Changed();
        }

        public void SetCount(Pin pin, int count)
        {
            if (pin == null || count < 1 || pin.Count == count) return;
            pin.Count = count;
            Changed();
        }

        /// <summary>Raised with the pin as it leaves the list, while its state can still be
        /// read — anything hanging off a pin (a map marker) has to be told before it is gone.</summary>
        public event Action<Pin> OnPinRemoved;

        public bool Remove(Pin pin)
        {
            if (pin == null || !pins.Remove(pin)) return false;
            OnPinRemoved?.Invoke(pin);
            Changed();
            return true;
        }

        public void Clear()
        {
            if (pins.Count == 0 && RecipePrefs.Count == 0) return;
            foreach (var pin in pins.ToList()) OnPinRemoved?.Invoke(pin);
            pins.Clear();
            RecipePrefs.Clear();    // prefs clear with the list (spec §2a)
            Changed();
        }

        public void Changed()
        {
            Save();
            OnChanged?.Invoke();
        }

        // ---- persistence -------------------------------------------------------------

        /// <summary>Null when there is no world to key on — save and load both bail then.
        /// During client shutdown Dispose can run after the world is gone; writing the
        /// in-memory pins to a fallback file at that point would smear one world's list into
        /// another's session (per-world separation is the design, spec §11).</summary>
        string SavePath
        {
            get
            {
                string id = capi.World?.SavegameIdentifier;
                if (string.IsNullOrEmpty(id)) return null;

                string dir = Path.Combine(GamePaths.DataPath, "ModData", "tallybook");
                GamePaths.EnsurePathExists(dir);
                return Path.Combine(dir, $"{id}.json");
            }
        }

        /// <summary>
        /// Has this world's file been read yet? Nothing is written before it has.
        ///
        /// Every path that writes — a quest arriving, the history updating, a map site being
        /// noticed — can fire early, and writing the in-memory list before it has been filled
        /// means writing an empty list over a real one. The list is the player's own record of
        /// what they are doing; losing it is the worst thing this mod can do, and it cannot
        /// undo it afterwards.
        /// </summary>
        bool loaded;

        public void Save()
        {
            try
            {
                string path = SavePath;
                if (path == null) return;
                if (!loaded) return;

                // Emptying the list is a real thing a player does — but it is also what a
                // failed load looks like, so keep a copy of what was there. Costs one file
                // write on the rare transition from "had pins" to "has none".
                if (pins.Count == 0) BackUpBeforeEmptying(path);

                // Expansion state lives in the tree; serialize it back onto the pin first.
                //
                // Only for pins that have actually resolved. An unresolved pin has an empty
                // tree and no group — its *saved* Expansions and RecipeSignature are the only
                // copy of that state, and writing the in-memory blanks over them strips a
                // pin's expansion tree and recipe choice while leaving the pin itself intact
                // (found by Fable's review: keeping the pins was 0.3.4's fix, but any save
                // during the unresolved window still lost everything they carried).
                foreach (var pin in pins)
                {
                    if (pin.Stack == null) continue;
                    pin.Expansions = TallyTree.SaveExpansions(pin.RootNodes);
                    pin.RecipeSignature = pin.Group?.Signature;
                }
                var file = new SaveFile
                {
                    Pins = pins,
                    RecipePrefs = RecipePrefs,
                    OfferedQuests = OfferedQuests.ToList(),
                    NpcPlaces = NpcPlaces,
                    QuestHistory = QuestHistory,
                    ChainStates = ChainStates,
                    StoryStates = StoryStates,
                    SiteQuests = SiteQuests,
                    OfferedSites = OfferedSites.ToList(),
                    Spawn = Spawn,
                    HistoryGroups = HistoryGroups,
                    QuestSort = QuestSort,
                    QuestOrder = QuestOrder,
                    Places = Places,
                    PlaceRemovals = PlaceRemovals,
                    VsQuests = VsQuests
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not save pin list: {0}", e.Message);
            }
        }

        /// <summary>Keep the last version that still had pins, next to the save as .bak.</summary>
        void BackUpBeforeEmptying(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                var previous = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path));
                if (previous?.Pins == null || previous.Pins.Count == 0) return;

                File.Copy(path, path + ".bak", overwrite: true);
                capi.Logger.Warning(
                    "[tallybook] pin list is now empty; previous {0} pin(s) kept in {1}.bak",
                    previous.Pins.Count, Path.GetFileName(path));
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not back up the pin list: {0}", e.Message);
            }
        }

        /// <summary>Load and re-resolve. Never throws.</summary>
        public void Load(System.Func<Pin, bool> resolve)
        {
            pins.Clear();
            RecipePrefs = new Dictionary<string, string>();
            // Per-world separation is the design (spec §11): a world with no save file starts
            // clean, it does not inherit whatever world was loaded before it in this session.
            OfferedQuests = new HashSet<string>();
            NpcPlaces = new Dictionary<string, string>();
            QuestHistory = new List<QuestRecord>();
            ChainStates = new Dictionary<string, string>();
            StoryStates = new Dictionary<string, string>();
            SiteQuests = new List<SiteQuest>();
            OfferedSites = new HashSet<string>();
            Spawn = new SpawnState();
            HistoryGroups = new Dictionary<string, bool>();
            QuestSort = "custom";
            QuestOrder = new List<string>();
            Places = new List<SavedPlace>();
            PlaceRemovals = new List<string>();
            VsQuests = new List<VsQuestTrack>();
            try
            {
                string path = SavePath;
                if (path != null && File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path));
                    if (loaded?.Pins != null) pins.AddRange(loaded.Pins.Where(p => p != null && p.Code != null));

                    // A blank-but-not-null giver is a broken identity from an old capture: it
                    // renders as "(for )", keys as "code|for:", and no NPC can ever match it
                    // to repair or complete it. Normalising to a personal pin keeps the count
                    // and loses only the claim nobody can back.
                    foreach (var pin in pins)
                    {
                        if (pin.QuestGiver != null && pin.QuestGiver.Trim().Length == 0)
                            pin.QuestGiver = null;
                    }
                    if (loaded?.RecipePrefs != null) RecipePrefs = loaded.RecipePrefs;
                    if (loaded?.OfferedQuests != null) OfferedQuests = new HashSet<string>(loaded.OfferedQuests);
                    if (loaded?.NpcPlaces != null) NpcPlaces = loaded.NpcPlaces;
                    if (loaded?.QuestHistory != null) QuestHistory = loaded.QuestHistory;
                    if (loaded?.ChainStates != null) ChainStates = loaded.ChainStates;
                    if (loaded?.StoryStates != null) StoryStates = loaded.StoryStates;
                    if (loaded?.SiteQuests != null) SiteQuests = loaded.SiteQuests.Where(s => s?.Key != null).ToList();
                    if (loaded?.OfferedSites != null) OfferedSites = new HashSet<string>(loaded.OfferedSites);
                    if (loaded?.Spawn != null) Spawn = loaded.Spawn;
                    if (loaded?.HistoryGroups != null) HistoryGroups = loaded.HistoryGroups;
                    if (!string.IsNullOrEmpty(loaded?.QuestSort)) QuestSort = loaded.QuestSort;
                    if (loaded?.QuestOrder != null) QuestOrder = loaded.QuestOrder;
                    if (loaded?.Places != null)
                    {
                        Places = loaded.Places.Where(p => !string.IsNullOrEmpty(p?.Name)).ToList();
                        foreach (var p in Places)
                        {
                            // Legacy list-notes fold into the text field once.
                            if (string.IsNullOrEmpty(p.NotesText) && p.Notes?.Count > 0)
                                p.NotesText = string.Join("\n", p.Notes);
                            p.Notes = null;
                        }
                    }
                    if (loaded?.PlaceRemovals != null) PlaceRemovals = loaded.PlaceRemovals;
                    if (loaded?.VsQuests != null)
                        VsQuests = loaded.VsQuests.Where(q => q?.QuestId != null).ToList();
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] pin list unreadable, starting empty: {0}", e.Message);
                pins.Clear();
                RecipePrefs = new Dictionary<string, string>();
            }

            // Pins that will not resolve are KEPT, not dropped.
            //
            // They used to be deleted here on the reasoning that a row we cannot name or count
            // is a mystery rather than a reminder. That reasoning is sound about the row and
            // catastrophic about the file: "the world does not know this item" and "the world
            // does not know this item *yet*" are indistinguishable at this moment, and the
            // second one deleted a player's entire list and then saved the deletion over their
            // save (found by Mark, 0.3.4 — every pin gone, silently, while the quest history
            // in the same file survived untouched because nothing resolves that).
            //
            // Keeping them costs a row showing a bare item code until it resolves; dropping
            // them costs work that cannot be got back. RetryUnresolved re-tries on every
            // recount, so a registry that was not ready fixes itself within a tick.
            int unresolved = 0;
            foreach (var pin in pins)
            {
                try { if (!resolve(pin)) unresolved++; }
                catch (Exception e)
                {
                    unresolved++;
                    capi.Logger.Warning("[tallybook] pin {0} could not be read: {1}", pin.Code, e.Message);
                }
            }
            if (unresolved > 0)
            {
                capi.Logger.Warning(
                    "[tallybook] {0} of {1} pin(s) did not resolve on load — keeping them and retrying",
                    unresolved, pins.Count);
            }

            loaded = true;
            OnChanged?.Invoke();
        }

        /// <summary>
        /// Try again on anything the world could not identify at load. Cheap: it only touches
        /// pins that are still unresolved, which is normally none at all.
        /// </summary>
        public void RetryUnresolved(System.Func<Pin, bool> resolve)
        {
            foreach (var pin in pins)
            {
                if (pin.Stack != null) continue;
                try { resolve(pin); } catch { /* still not ready; try again next recount */ }
            }
        }
    }
}
