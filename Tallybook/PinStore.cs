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
    /// <summary>One pinned item and how many of it the player wants.</summary>
    public class Pin
    {
        /// <summary>Short collectible code — the pin's identity, and all that gets persisted.
        /// Everything else is re-resolved at load, because recipe mods come and go between
        /// sessions (spec §11).</summary>
        public string Code;

        public bool IsBlock;
        public int Count = 1;

        [JsonIgnore] public ItemStack Stack;
        [JsonIgnore] public RecipeVariantGroup Group;
        [JsonIgnore] public List<Requirement> Requirements = new List<Requirement>();
        [JsonIgnore] public List<Requirement> Tools = new List<Requirement>();

        [JsonIgnore] public bool HasRecipe => Group != null;
        [JsonIgnore] public string DisplayName => Stack?.GetName() ?? Code;
    }

    /// <summary>
    /// The pinned list, saved per world (spec §7). Persists item code and count only; recipes
    /// and itemstacks are re-resolved on load so a world with different mods degrades to
    /// "no recipe known" rather than restoring something that no longer exists.
    ///
    /// A corrupt or missing file yields an empty list and never throws — losing a shopping
    /// list is a nuisance, crashing someone's client over one is not acceptable (spec §7).
    /// </summary>
    public class PinStore
    {
        readonly ICoreClientAPI capi;
        readonly List<Pin> pins = new List<Pin>();

        public IReadOnlyList<Pin> Pins => pins;
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
            if (pins.Count == 0) return;
            pins.Clear();
            Changed();
        }

        void Changed()
        {
            Save();
            OnChanged?.Invoke();
        }

        // ---- persistence -------------------------------------------------------------

        string SavePath
        {
            get
            {
                string dir = Path.Combine(GamePaths.DataPath, "ModData", "tallybook");
                GamePaths.EnsurePathExists(dir);
                string id = capi.World?.SavegameIdentifier;
                // Per-world files keep lists separate by design (spec §11). Without an
                // identifier there is no world to key on, so fall back rather than blending
                // two worlds' lists into one file.
                return Path.Combine(dir, $"{(string.IsNullOrEmpty(id) ? "unknown" : id)}.json");
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SavePath, JsonConvert.SerializeObject(pins, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not save pin list: {0}", e.Message);
            }
        }

        /// <summary>Load and re-resolve. Never throws.</summary>
        // System.Func explicitly: Vintagestory.API.Common defines its own Func and the two
        // collide once both namespaces are imported.
        public void Load(System.Func<Pin, bool> resolve)
        {
            pins.Clear();
            try
            {
                string path = SavePath;
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<List<Pin>>(File.ReadAllText(path));
                    if (loaded != null) pins.AddRange(loaded.Where(p => p != null && p.Code != null));
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] pin list unreadable, starting empty: {0}", e.Message);
                pins.Clear();
            }

            // Drop pins whose item no longer exists at all — a row we cannot name or count is
            // not a useful reminder, it is a mystery.
            pins.RemoveAll(p => !resolve(p));
            OnChanged?.Invoke();
        }
    }
}
