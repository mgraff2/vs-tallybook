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

        [JsonIgnore] public bool HasRecipe => Group != null;
        [JsonIgnore] public string DisplayName => Stack?.GetName() ?? Code;

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
    public class SaveFile
    {
        public List<Pin> Pins = new List<Pin>();
        public Dictionary<string, string> RecipePrefs = new Dictionary<string, string>();
    }

    public class PinStore
    {
        readonly ICoreClientAPI capi;
        readonly List<Pin> pins = new List<Pin>();

        public IReadOnlyList<Pin> Pins => pins;
        public Dictionary<string, string> RecipePrefs { get; private set; } = new Dictionary<string, string>();
        public event Action OnChanged;

        public PinStore(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public Pin Find(string code) => pins.FirstOrDefault(p => p.Code == code);

        /// <summary>Pinning an already-pinned item increments it — never a duplicate row
        /// (spec §3).</summary>
        public Pin Add(ItemStack stack, int amount = 1)
        {
            var code = stack?.Collectible?.Code?.ToShortString();
            if (code == null) return null;

            var pin = Find(code);
            if (pin == null)
            {
                pin = new Pin
                {
                    Code = code,
                    IsBlock = stack.Class == EnumItemClass.Block,
                    Count = Math.Max(1, amount),
                    Stack = stack.Clone()
                };
                pins.Add(pin);
            }
            else
            {
                pin.Count += Math.Max(1, amount);
            }

            Changed();
            return pin;
        }

        public void SetCount(Pin pin, int count)
        {
            if (pin == null || count < 1 || pin.Count == count) return;
            pin.Count = count;
            Changed();
        }

        public bool Remove(string code)
        {
            var pin = Find(code);
            if (pin == null) return false;
            pins.Remove(pin);
            Changed();
            return true;
        }

        public void Clear()
        {
            if (pins.Count == 0 && RecipePrefs.Count == 0) return;
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
                var file = new SaveFile { Pins = pins, RecipePrefs = RecipePrefs };
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
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] pin list unreadable, starting empty: {0}", e.Message);
                pins.Clear();
                RecipePrefs = new Dictionary<string, string>();
            }

            // Drop pins whose item no longer exists at all — a row we cannot name or count is
            // not a useful reminder, it is a mystery.
            pins.RemoveAll(p => !resolve(p));
            OnChanged?.Invoke();
        }
    }
}
