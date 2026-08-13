using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Tallybook
{
    /// <summary>One rule of the current world: a world-configuration attribute some installed
    /// mod declared, resolved against the value this world actually runs with.</summary>
    public class WorldSetting
    {
        public string Code;
        public string Name;          // player-facing label
        public string Value;         // player-facing value, resolved the way the create-world screen writes it
        public string DefaultValue;  // player-facing default, same resolution
        public bool IsDefault;
        public string Hover;         // description, default, and which mod added it
    }

    public class WorldSettingsSection
    {
        public string Title;
        public List<WorldSetting> Settings = new List<WorldSetting>();
    }

    /// <summary>
    /// The world's rules, read from the same two places the game itself uses: each mod's
    /// embedded world-config declaration (Mod.WorldConfig.WorldConfigAttributes — the data
    /// behind the create-world customize screen, carried by the mod whether or not this
    /// client created the world) and capi.World.Config, the value tree the server synced for
    /// this world. Definitions give names, categories and defaults; the tree gives what this
    /// world actually runs with. A value the tree does not carry runs at the definition's
    /// default — that is how the game reads it too, so it is shown as the rule in effect,
    /// not as missing.
    ///
    /// Nothing here is mod-named: any content mod that declares world config gets its
    /// settings listed with zero per-mod work, the same promise the recipe reads make.
    /// </summary>
    public static class WorldRules
    {
        public static List<WorldSettingsSection> Read(ICoreClientAPI capi)
        {
            var sections = new List<WorldSettingsSection>();
            var byCategory = new Dictionary<string, WorldSettingsSection>();
            var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var config = capi.World.Config;

            foreach (var mod in capi.ModLoader.Mods)
            {
                var attrs = mod?.WorldConfig?.WorldConfigAttributes;
                if (attrs == null) continue;

                foreach (var attr in attrs)
                {
                    // First definition wins: mods load in dependency order, so the game/
                    // survival declaration outranks a re-declaration by a later mod.
                    if (attr?.Code == null || !defined.Add(attr.Code)) continue;

                    string raw = config?.GetAsString(attr.Code, null) ?? attr.Default;
                    bool isDefault = SameValue(raw, attr.Default);

                    var setting = new WorldSetting
                    {
                        Code = attr.Code,
                        Name = L(mod, "worldattribute-" + attr.Code) ?? attr.Code,
                        Value = Friendly(mod, attr, raw),
                        DefaultValue = Friendly(mod, attr, attr.Default),
                        IsDefault = isDefault,
                    };
                    setting.Hover = BuildHover(mod, attr, setting);

                    string cat = string.IsNullOrEmpty(attr.Category) ? "other" : attr.Category;
                    if (!byCategory.TryGetValue(cat, out var section))
                    {
                        section = new WorldSettingsSection
                        {
                            Title = L(mod, "worldconfig-category-" + cat) ?? cat,
                            Settings = new List<WorldSetting>(),
                        };
                        byCategory[cat] = section;
                        sections.Add(section);
                    }
                    section.Settings.Add(setting);
                }
            }

            // Values the world carries that no installed mod declares — a server-side-only
            // mod's setting, or one left behind by a mod since removed. No definition means
            // no name, no default and no description, but the value is still one of this
            // world's rules and hiding it would make the list claim to be complete when it
            // is not.
            List<WorldSetting> leftovers = null;
            if (config != null)
            {
                foreach (var entry in config)
                {
                    if (defined.Contains(entry.Key)) continue;
                    string raw = entry.Value?.GetValue()?.ToString();
                    if (raw == null) continue;

                    (leftovers ??= new List<WorldSetting>()).Add(new WorldSetting
                    {
                        Code = entry.Key,
                        Name = entry.Key,
                        Value = raw,
                        IsDefault = true,   // no default known, so nothing to differ from
                        Hover = "No installed mod declares this setting — it may belong to a "
                              + "server-side mod, or to a mod since removed.",
                    });
                }
            }
            if (leftovers != null)
            {
                leftovers.Sort((a, b) => string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
                sections.Add(new WorldSettingsSection { Title = "Other settings", Settings = leftovers });
            }

            var mods = ReadMods(capi);
            if (mods != null) sections.Add(mods);

            return sections;
        }

        /// <summary>
        /// The mods this world runs, as a name → version listing. Two sources, because no
        /// single list is the whole truth: the server's own handshake announcement
        /// (ClientMain.ServerMods, captured from ServerIdentification — the only place a
        /// server-side-only mod is visible to a client), then whatever this client loaded
        /// that the server never heard of (side:client mods like Tallybook itself). The
        /// handshake read is reflection into a private field and fails soft: worst case the
        /// section lists only the client's own mods, which on singleplayer is everything
        /// anyway.
        /// </summary>
        static WorldSettingsSection ReadMods(ICoreClientAPI capi)
        {
            var rows = new List<WorldSetting>();
            var announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var field = capi.World.GetType().GetField("ServerMods",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field?.GetValue(capi.World) is System.Collections.IEnumerable serverMods)
                {
                    foreach (var entry in serverMods)
                    {
                        var t = entry?.GetType();
                        if (t == null) continue;
                        string id = t.GetField("Id")?.GetValue(entry) as string;
                        string name = t.GetField("Name")?.GetValue(entry) as string;
                        string version = t.GetField("Version")?.GetValue(entry) as string;
                        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name)) continue;

                        announced.Add(id ?? name);
                        bool loadedHere = !string.IsNullOrEmpty(id) && capi.ModLoader.GetMod(id) != null;
                        rows.Add(new WorldSetting
                        {
                            Code = id ?? name,
                            Name = string.IsNullOrEmpty(name) ? id : name,
                            Value = string.IsNullOrEmpty(version) ? "?" : version,
                            IsDefault = true,
                            Hover = id + (loadedHere
                                ? null
                                : " — runs on the server; not installed on this client."),
                        });
                    }
                }
            }
            catch
            {
                // A game update moving the field costs the server list, not the tab.
            }

            foreach (var mod in capi.ModLoader.Mods)
            {
                string id = mod?.Info?.ModID;
                if (string.IsNullOrEmpty(id) || announced.Contains(id)) continue;
                rows.Add(new WorldSetting
                {
                    Code = id,
                    Name = string.IsNullOrEmpty(mod.Info.Name) ? id : mod.Info.Name,
                    Value = mod.Info.Version ?? "?",
                    IsDefault = true,
                    Hover = id + " — client-side; the server never sees it.",
                });
            }

            if (rows.Count == 0) return null;
            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new WorldSettingsSection { Title = $"Mods ({rows.Count})", Settings = rows };
        }

        /// <summary>The value the way a player knows it: the dropdown label the create-world
        /// screen showed ("5 days before monsters appear"), On/Off for switches, the raw
        /// value where the world was created with something the definition never listed.</summary>
        static string Friendly(Mod mod, WorldConfigurationAttribute attr, string raw)
        {
            if (raw == null) return "?";

            if (attr.Values != null)
            {
                for (int i = 0; i < attr.Values.Length; i++)
                {
                    if (!SameValue(raw, attr.Values[i])) continue;
                    string name = attr.Names != null && i < attr.Names.Length ? attr.Names[i] : attr.Values[i];
                    return L(mod, $"worldconfig-{attr.Code}-{name}") ?? name;
                }
            }

            if (attr.DataType == EnumDataType.Bool
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ? "On" : "Off";
            }

            return raw;
        }

        static string BuildHover(Mod mod, WorldConfigurationAttribute attr, WorldSetting setting)
        {
            var parts = new List<string>();

            string desc = L(mod, "worldattribute-" + attr.Code + "-desc");
            if (desc != null) parts.Add(TbText.OneLine(desc));

            if (!setting.IsDefault) parts.Add($"Default: {setting.DefaultValue}");
            if (mod?.Info != null && !mod.Info.CoreMod) parts.Add($"Added by {mod.Info.Name}.");

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        /// <summary>The tree stores what the create screen or server config wrote — "0.5",
        /// "true", sometimes a typed attribute whose ToString casing differs — so values
        /// compare case-insensitively and, where both sides are numbers, numerically
        /// ("1" and "1.0" are the same rule).</summary>
        static bool SameValue(string a, string b)
        {
            if (a == null || b == null) return a == b;
            a = a.Trim(); b = b.Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db)
                && da == db;
        }

        /// <summary>Translate with the declaring mod's domain tried first — a mod's lang keys
        /// live in its own domain — falling back to the game domain, then to null (Lang.Get
        /// answering with the key back is Lang saying it has no translation).</summary>
        static string L(Mod mod, string key)
        {
            string domain = mod?.Info?.ModID;
            if (!string.IsNullOrEmpty(domain) && domain != "game" && domain != "survival" && domain != "creative")
            {
                string qualified = Lang.GetIfExists(domain + ":" + key);
                if (qualified != null) return qualified;
            }
            return Lang.GetIfExists(key);
        }
    }
}
