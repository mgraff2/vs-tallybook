using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

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

        /// <summary>Got what was asked for. Outranks "ready to craft": no reason to build
        /// what is already in the bag.</summary>
        [JsonIgnore] public bool Complete => Have >= Count;

        [JsonIgnore] public bool HasRecipe => Group != null;
        [JsonIgnore] public string DisplayName => Stack?.GetName() ?? Code;

        /// <summary>Identity at handbook-page granularity — the dedup/removal/preference key.
        /// Code alone is not enough once attribute-distinct variants can be pinned. Cached
        /// only once the stack is resolved, so an early read never locks in the bare code.
        ///
        /// The quest giver is part of it: an errand for Agnieszka and your own plan to make
        /// the same item are two different rows living in two different tabs, and merging
        /// them would let her request quietly rewrite your own goal's count.</summary>
        [JsonIgnore]
        public string Key => Stack == null
            ? MakeKey(Code, QuestGiver)
            : key ??= MakeKey(RecipeProbe.PageCode(Stack) ?? Code, QuestGiver);
        string key;

        public static string MakeKey(string pageCode, string questGiver)
            => questGiver == null ? pageCode : $"{pageCode}|for:{questGiver}";

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
        public event Action OnChanged;

        public PinStore(ICoreClientAPI capi)
        {
            this.capi = capi;
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
                       string questGiver = null)
        {
            var code = stack?.Collectible?.Code?.ToShortString();
            if (code == null) return null;

            string key = Pin.MakeKey(RecipeProbe.PageCode(stack) ?? code, questGiver);
            var pin = pins.FirstOrDefault(p => p.Key == key);
            if (pin == null)
            {
                pin = new Pin
                {
                    Code = code,
                    IsBlock = stack.Class == EnumItemClass.Block,
                    Attributes = RecipeProbe.AttributesJson(stack),
                    QuestGiver = questGiver,
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
            if (!pins.Any(p => p.Active != active)) return;
            foreach (var pin in pins) pin.Active = active;
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

        public void Save()
        {
            try
            {
                string path = SavePath;
                if (path == null) return;

                // Expansion state lives in the tree; serialize it back onto the pin first.
                foreach (var pin in pins)
                {
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
                    ChainStates = ChainStates
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not save pin list: {0}", e.Message);
            }
        }

        /// <summary>Load and re-resolve. Never throws.</summary>
        public void Load(System.Func<Pin, bool> resolve)
        {
            pins.Clear();
            RecipePrefs = new Dictionary<string, string>();
            try
            {
                string path = SavePath;
                if (path != null && File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path));
                    if (loaded?.Pins != null) pins.AddRange(loaded.Pins.Where(p => p != null && p.Code != null));
                    if (loaded?.RecipePrefs != null) RecipePrefs = loaded.RecipePrefs;
                    if (loaded?.OfferedQuests != null) OfferedQuests = new HashSet<string>(loaded.OfferedQuests);
                    if (loaded?.NpcPlaces != null) NpcPlaces = loaded.NpcPlaces;
                    if (loaded?.QuestHistory != null) QuestHistory = loaded.QuestHistory;
                    if (loaded?.ChainStates != null) ChainStates = loaded.ChainStates;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] pin list unreadable, starting empty: {0}", e.Message);
                pins.Clear();
                RecipePrefs = new Dictionary<string, string>();
            }

            // Drop pins whose item no longer exists at all — a row we cannot name or count is
            // not a useful reminder, it is a mystery. A pin that throws on resolve goes the
            // same way: one bad entry must not abort the load and leave the surfaces with
            // nothing, which looks exactly like the mod having failed to start.
            pins.RemoveAll(p =>
            {
                try { return !resolve(p); }
                catch (Exception e)
                {
                    capi.Logger.Warning("[tallybook] dropping unreadable pin {0}: {1}", p.Code, e.Message);
                    return true;
                }
            });
            OnChanged?.Invoke();
        }
    }
}
