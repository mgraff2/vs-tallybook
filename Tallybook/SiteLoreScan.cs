using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// Which lore writings exist ONLY at a given locator-map destination — the fact that
    /// turns "visit the Sunrift Experiment" into "and recover the 17 writings hidden there".
    ///
    /// The relationship is recorded nowhere else: a lore config names its pieces, a schematic
    /// places the loot randomizer that drops them, and only reading the schematics says which
    /// site holds which. So this scans the worldgen schematics of every content source that
    /// defines lore (the game itself plus mods with config/lore entries), collects which lore
    /// categories each schematic can drop, and calls a category site-exclusive when it occurs
    /// in that site's schematics and nowhere else. Exclusive is the honesty bar: a writing
    /// that can also turn up in any village chest proves nothing about having been anywhere.
    ///
    /// The scan reads tens of megabytes of JSON, so it runs once on a background thread and
    /// caches its result keyed by a fingerprint of the sources; a second login with the same
    /// mods costs one small file read. Until the scan lands, sites simply have no lore data —
    /// visit tracking works from the first tick, the counts appear when the answer exists.
    ///
    /// Two registry-backed shortcuts keep the file work honest and small:
    /// - a randomizer's possible categories are read from the live item registry (the
    ///   attributes arrive resolved per variant), never re-parsed from its JSON;
    /// - the lore assets themselves come from capi.Assets (config/lore is the game's own
    ///   convention — the journal system loads the same path), which also gives the
    ///   category → lore-code mapping and each writing's display title.
    /// </summary>
    public class SiteLoreScan
    {
        readonly ICoreClientAPI capi;

        /// <summary>Lore-code sets exclusive to each site group, keyed by the group's sorted
        /// site codes ("mine1+mine2"). Written on the main thread when the scan completes.</summary>
        Dictionary<string, List<string>> exclusiveByGroup;

        /// <summary>One lore writing as the journal knows it: the code its journal entry
        /// carries, the category its loot item is stamped with, its display title.</summary>
        public class LoreDef
        {
            public string Code;
            public string Category;
            public string Title;
        }

        List<LoreDef> loreDefs = new List<LoreDef>();

        /// <summary>Bumped when scan results land, so the change signature notices and every
        /// surface redraws with the counts that now exist.</summary>
        public int Generation { get; private set; }

        /// <summary>Lore categories only STORY schematics can place — the game's own
        /// "story/" schematic-path convention (storystructures.json references every story
        /// schematic that way), with the same exclusivity bar as site lore: a category any
        /// ordinary ruin can also drop is world lore, not story lore. Null until the scan
        /// lands (or if it fails) — "don't know" must not read as "not story".</summary>
        public HashSet<string> StoryCategories { get; private set; }

        public bool Ready { get; private set; }

        /// <summary>How many schematic files the scan read — diagnostic ground truth.</summary>
        public int SchematicsScanned { get; private set; }

        public bool FromCache { get; private set; }

        bool started;

        public SiteLoreScan(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void InvalidateWorld()
        {
            // Lore assets and item registry are per-world; the schematic scan itself is
            // keyed by the source fingerprint, so a same-mods world resolves from cache.
            started = false;
            Ready = false;
            exclusiveByGroup = null;
            StoryCategories = null;
            loreDefs = new List<LoreDef>();
        }

        /// <summary>The writings exclusive to a site group, or null while unknown. An empty
        /// list is a real answer ("nothing here proves the visit"); null means "don't know
        /// yet", and nothing may complete or display against a don't-know.</summary>
        public List<LoreDef> ExclusiveLoreFor(string groupKey)
        {
            if (!Ready || exclusiveByGroup == null || groupKey == null) return null;
            if (!exclusiveByGroup.TryGetValue(groupKey, out var categories)) return new List<LoreDef>();
            return loreDefs.Where(d => categories.Contains(d.Category)).ToList();
        }

        public static string GroupKey(IEnumerable<string> siteCodes)
            => string.Join("+", siteCodes.Select(c => c.ToLowerInvariant()).OrderBy(c => c, StringComparer.Ordinal));

        // ---- scan orchestration --------------------------------------------------------

        /// <summary>
        /// Kick the scan off once per world. Everything that needs the live registry or the
        /// asset manager is gathered here on the main thread; the background task touches
        /// only files and the captured data.
        /// </summary>
        public void EnsureStarted(Dictionary<string, List<string>> groups)
        {
            if (started || groups == null || groups.Count == 0) return;
            started = true;

            try
            {
                LoadLoreDefs();
                var randomizerCategories = RandomizerCategories();
                var sources = Sources();
                var groupsCopy = groups.ToDictionary(g => g.Key, g => g.Value.ToList());
                var knownCategories = new HashSet<string>(
                    loreDefs.Select(d => d.Category), StringComparer.OrdinalIgnoreCase);

                Task.Run(() =>
                {
                    try
                    {
                        var result = Scan(sources, groupsCopy, randomizerCategories, knownCategories,
                                          out var storyCats, out int scanned, out bool fromCache);
                        capi.Event.EnqueueMainThreadTask(() =>
                        {
                            exclusiveByGroup = result;
                            StoryCategories = new HashSet<string>(
                                storyCats ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                            SchematicsScanned = scanned;
                            FromCache = fromCache;
                            Ready = true;
                            Generation++;
                        }, "tallybook-sitelore");
                    }
                    catch (Exception e)
                    {
                        capi.Logger.Warning("[tallybook] site lore scan failed: {0}", e.Message);
                        capi.Event.EnqueueMainThreadTask(() =>
                        {
                            // A failed scan is "no site has provable lore", not a crash and
                            // not a retry loop — visit tracking carries the feature alone.
                            exclusiveByGroup = new Dictionary<string, List<string>>();
                            Ready = true;
                            Generation++;
                        }, "tallybook-sitelore");
                    }
                });
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] site lore scan could not start: {0}", e.Message);
            }
        }

        /// <summary>config/lore/*.json across every domain — the game's own journal-asset
        /// convention, read exactly the way ModJournal reads it.</summary>
        void LoadLoreDefs()
        {
            loreDefs = new List<LoreDef>();
            List<AssetLocation> files = null;
            try { files = capi.Assets.GetLocations("config/lore/"); } catch { }

            foreach (var loc in files ?? new List<AssetLocation>())
            {
                try
                {
                    var asset = capi.Assets.TryGet(loc)?.ToObject<JournalAsset>();
                    if (asset?.Code == null) continue;

                    string title = asset.Title == null ? null : Lang.Get(asset.Title);
                    loreDefs.Add(new LoreDef
                    {
                        Code = asset.Code,
                        // Category defaults to the code where a config omits it — the
                        // journal matches loot to lore by category, so a missing one means
                        // the code doubles as its own pool name.
                        Category = string.IsNullOrEmpty(asset.Category) ? asset.Code : asset.Category,
                        Title = string.IsNullOrEmpty(title) || title == asset.Title ? asset.Code : title
                    });
                }
                catch { /* one unreadable lore file must not cost the rest */ }
            }
        }

        /// <summary>
        /// Randomizer type ("grandrifttheory", "newlore") → categories it can drop, read off
        /// the live item registry where the per-variant attributes arrive already resolved.
        /// This is what lets the file scan treat "stackrandomizer-newlore" in a schematic as
        /// "could drop villager/tobias/research/…" instead of as an opaque token.
        /// </summary>
        Dictionary<string, HashSet<string>> RandomizerCategories()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in capi.World.Items)
                {
                    string path = item?.Code?.Path;
                    if (path == null || !path.StartsWith("stackrandomizer-", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var stacks = item.Attributes?["stacks"];
                    if (stacks == null || !stacks.Exists) continue;

                    var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in stacks.AsArray() ?? Array.Empty<Vintagestory.API.Datastructures.JsonObject>())
                    {
                        string c = s?["attributes"]?["category"]?.AsString();
                        if (!string.IsNullOrEmpty(c)) cats.Add(c);
                    }
                    if (cats.Count > 0) map[path.Substring("stackrandomizer-".Length)] = cats;
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read stack randomizers: {0}", e.Message);
            }
            return map;
        }

        /// <summary>The game's own assets folder plus every mod's source zip or folder. The
        /// files are the only record of what worldgen places where, and every client has
        /// them — joining a modded server requires the mods locally.</summary>
        List<string> Sources()
        {
            var sources = new List<string>();
            try
            {
                if (Directory.Exists(GamePaths.AssetsPath)) sources.Add(GamePaths.AssetsPath);
                foreach (var mod in capi.ModLoader.Mods ?? Enumerable.Empty<Mod>())
                {
                    string path = mod?.SourcePath;
                    if (path != null && (File.Exists(path) || Directory.Exists(path))
                        && !sources.Contains(path))
                    {
                        sources.Add(path);
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not enumerate mod sources: {0}", e.Message);
            }
            return sources;
        }

        // ---- the background pass -------------------------------------------------------

        // The lookbehinds are load-bearing: without them "code" matches the tail of
        // "schematiccode" and "category" the tail of "subcategory", silently associating
        // schematic lists with the wrong owner or crediting a category that was never there.
        static readonly Regex CodeRe = new Regex(
            "(?<![A-Za-z])[\"']?code[\"']?\\s*:\\s*[\"']([A-Za-z0-9_-]+)[\"']", RegexOptions.Compiled);
        static readonly Regex SchematicsRe = new Regex(
            "(?<![A-Za-z])[\"']?schematics[\"']?\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Compiled);
        static readonly Regex QuotedRe = new Regex("[\"']([^\"']+)[\"']", RegexOptions.Compiled);
        static readonly Regex RandomizerRe = new Regex(
            "stackrandomizer-([a-z0-9][a-z0-9-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>Matches the category attribute both as plain JSON and as the escaped
        /// JSON-inside-a-string that schematic block entities carry.</summary>
        static readonly Regex CategoryRe = new Regex(
            "(?<![A-Za-z])category[\\\\\"']{0,4}\\s*:\\s*[\\\\\"']{0,4}([A-Za-z0-9_-]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        class CacheFile
        {
            public List<string> Fingerprint = new List<string>();
            public Dictionary<string, List<string>> Groups = new Dictionary<string, List<string>>();
            public List<string> StoryCategories = new List<string>();
            public int SchematicsScanned;
        }

        string CachePath
        {
            get
            {
                string dir = Path.Combine(GamePaths.DataPath, "ModData", "tallybook");
                GamePaths.EnsurePathExists(dir);
                return Path.Combine(dir, "site-lore-cache.json");
            }
        }

        Dictionary<string, List<string>> Scan(
            List<string> sources,
            Dictionary<string, List<string>> groups,
            Dictionary<string, HashSet<string>> randomizerCategories,
            HashSet<string> knownCategories,
            out List<string> storyCategories, out int scanned, out bool fromCache)
        {
            storyCategories = new List<string>();
            scanned = 0;
            fromCache = false;

            // Fingerprint: a format marker (bumped when the cache payload grows a field, so
            // an older build's cache rescans instead of answering with the field missing),
            // then every source with its size and write time, plus the group keys — any mod
            // update, addition or removal reruns the scan; nothing else does.
            var fingerprint = new List<string> { "format:v2" };
            fingerprint.AddRange(sources.Select(SourceStamp));
            fingerprint.AddRange(groups.Keys.OrderBy(k => k, StringComparer.Ordinal));

            try
            {
                if (File.Exists(CachePath))
                {
                    var cached = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(CachePath));
                    if (cached?.Groups != null && cached.Fingerprint != null
                        && cached.Fingerprint.SequenceEqual(fingerprint))
                    {
                        storyCategories = cached.StoryCategories ?? new List<string>();
                        scanned = cached.SchematicsScanned;
                        fromCache = true;
                        return Normalise(cached.Groups);
                    }
                }
            }
            catch { /* a bad cache is rescanned, never trusted */ }

            // code → schematic name patterns, from every source's structure configs. These
            // are small; the schematics they point at are not.
            var schematicsByCode = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            // schematic path (domain-relative, lowercase, no extension) → categories placeable there
            var categoriesBySchematic = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                bool definesLore = false;
                ForEachEntry(source, path => path.Contains("/config/lore/"), (_, _2) => { definesLore = true; });

                ForEachEntry(source,
                    path => path.Contains("/worldgen/") && !path.Contains("/schematics/") && path.EndsWith(".json"),
                    (path, text) => CollectStructureConfigs(text, schematicsByCode));

                // Only lore-defining sources can place lore; skipping the rest is what keeps
                // the scan from reading every schematic of every cosmetic mod installed.
                if (!definesLore) continue;

                int count = 0;
                ForEachEntry(source,
                    path => path.Contains("/worldgen/schematics/") && path.EndsWith(".json"),
                    (path, text) =>
                    {
                        count++;
                        var cats = CategoriesIn(text, randomizerCategories, knownCategories);
                        if (cats.Count == 0) return;

                        string key = SchematicKey(path);
                        if (!categoriesBySchematic.TryGetValue(key, out var set))
                            categoriesBySchematic[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.UnionWith(cats);
                    });
                scanned += count;
            }

            // Story lore: every category a "story/" schematic can place, minus any that a
            // non-story schematic can also place — the same exclusivity reasoning as the
            // per-site sets below, applied to the story as a whole.
            var story = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in categoriesBySchematic)
            {
                if (kv.Key.StartsWith("story/", StringComparison.OrdinalIgnoreCase))
                    story.UnionWith(kv.Value);
            }
            foreach (var kv in categoriesBySchematic)
            {
                if (!kv.Key.StartsWith("story/", StringComparison.OrdinalIgnoreCase))
                    story.ExceptWith(kv.Value);
            }
            storyCategories = story.OrderBy(c => c, StringComparer.Ordinal).ToList();

            // Site group → its schematic keys; a category is exclusive when every schematic
            // that can place it belongs to the group.
            var result = new Dictionary<string, List<string>>();
            foreach (var group in groups)
            {
                var patterns = group.Value
                    .Where(code => schematicsByCode.ContainsKey(code))
                    .SelectMany(code => schematicsByCode[code])
                    .Select(NormalisePattern)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in categoriesBySchematic)
                {
                    if (patterns.Any(p => MatchesPattern(kv.Key, p)))
                    {
                        mine.Add(kv.Key);
                        candidates.UnionWith(kv.Value);
                    }
                }

                var exclusive = new List<string>();
                foreach (var cat in candidates)
                {
                    bool leaked = categoriesBySchematic.Any(kv => !mine.Contains(kv.Key) && kv.Value.Contains(cat));
                    if (!leaked) exclusive.Add(cat);
                }
                result[group.Key] = exclusive.OrderBy(c => c, StringComparer.Ordinal).ToList();
            }

            try
            {
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(new CacheFile
                {
                    Fingerprint = fingerprint,
                    Groups = result,
                    StoryCategories = storyCategories,
                    SchematicsScanned = scanned
                }, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not cache the site lore scan: {0}", e.Message);
            }

            return Normalise(result);
        }

        static Dictionary<string, List<string>> Normalise(Dictionary<string, List<string>> raw)
            => raw.ToDictionary(kv => kv.Key, kv => kv.Value ?? new List<string>());

        static string SourceStamp(string source)
        {
            try
            {
                if (File.Exists(source))
                {
                    var fi = new FileInfo(source);
                    return $"{source}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                }
                if (Directory.Exists(source))
                {
                    // A folder source (the game install, a dev-mode mod) is stamped by its
                    // file count and newest write — cheap, and any content change moves it.
                    var files = Directory.GetFiles(source, "*.json", SearchOption.AllDirectories);
                    long newest = files.Length == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
                    return $"{source}|dir:{files.Length}|{newest}";
                }
            }
            catch { }
            return source + "|missing";
        }

        /// <summary>Walk a source's asset entries as "<domain>/rest/of/path" (lowercase,
        /// forward slashes), reading the text only for entries the filter wants.</summary>
        void ForEachEntry(string source, System.Func<string, bool> filter, Action<string, string> handle)
        {
            try
            {
                if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(source);
                    foreach (var entry in zip.Entries)
                    {
                        string path = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                        if (!path.StartsWith("assets/")) continue;
                        path = path.Substring("assets/".Length);
                        if (!filter(path)) continue;

                        using var reader = new StreamReader(entry.Open());
                        handle(path, reader.ReadToEnd());
                    }
                    return;
                }

                string root = source;
                // A folder source is either an assets root itself (the game install) or a
                // mod folder carrying an assets/ child.
                string assetsChild = Path.Combine(source, "assets");
                if (Directory.Exists(assetsChild)) root = assetsChild;
                if (!Directory.Exists(root)) return;

                foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    string path = file.Substring(root.Length).TrimStart('\\', '/')
                        .Replace('\\', '/').ToLowerInvariant();
                    if (!filter(path)) continue;
                    handle(path, File.ReadAllText(file));
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not read source {0}: {1}", source, e.Message);
            }
        }

        /// <summary>
        /// Pull (code, schematics[]) pairs out of a structure config. The files use the
        /// game's relaxed JSON (unquoted keys, comments), so this is a token association
        /// rather than a parse: each schematics array belongs to the nearest preceding code,
        /// bounded to the same entry by distance. `.tallybook sites` prints what came out,
        /// so a misread is visible rather than silent.
        /// </summary>
        static void CollectStructureConfigs(string text, Dictionary<string, List<string>> schematicsByCode)
        {
            var codes = CodeRe.Matches(text).Cast<Match>().ToList();
            foreach (Match sm in SchematicsRe.Matches(text))
            {
                var owner = codes.LastOrDefault(c => c.Index < sm.Index && sm.Index - c.Index < 2000);
                if (owner == null) continue;

                string code = owner.Groups[1].Value;
                var names = QuotedRe.Matches(sm.Groups[1].Value).Cast<Match>()
                    .Select(m => m.Groups[1].Value).ToList();
                if (names.Count == 0) continue;

                if (!schematicsByCode.TryGetValue(code, out var list))
                    schematicsByCode[code] = list = new List<string>();
                foreach (var n in names)
                {
                    if (!list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
                }
            }
        }

        HashSet<string> CategoriesIn(string text,
            Dictionary<string, HashSet<string>> randomizerCategories, HashSet<string> knownCategories)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in RandomizerRe.Matches(text))
            {
                string type = m.Groups[1].Value;
                if (randomizerCategories.TryGetValue(type, out var set)) cats.UnionWith(set);
                // A randomizer named exactly after a category (the site-lore pattern) counts
                // even if the item registry read missed it.
                else if (knownCategories.Contains(type)) cats.Add(type);
            }
            foreach (Match m in CategoryRe.Matches(text))
            {
                string c = m.Groups[1].Value;
                if (knownCategories.Contains(c)) cats.Add(c);
            }
            return cats;
        }

        /// <summary>"survival/worldgen/schematics/story/brmine.json" → "story/brmine".</summary>
        static string SchematicKey(string path)
        {
            int idx = path.IndexOf("/worldgen/schematics/", StringComparison.Ordinal);
            string rest = idx < 0 ? path : path.Substring(idx + "/worldgen/schematics/".Length);
            if (rest.EndsWith(".json")) rest = rest.Substring(0, rest.Length - 5);
            return rest;
        }

        /// <summary>Structure configs may prefix a schematic name with its domain
        /// ("story/betterruins:brmine"); the file path never carries one.</summary>
        static string NormalisePattern(string pattern)
        {
            var parts = pattern.Replace('\\', '/').ToLowerInvariant().Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon >= 0) parts[i] = parts[i].Substring(colon + 1);
            }
            return string.Join("/", parts);
        }

        static bool MatchesPattern(string schematicKey, string pattern)
            => schematicKey.Equals(pattern, StringComparison.OrdinalIgnoreCase)
               || schematicKey.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase)
               // A trailing wildcard or folder reference covers its subtree.
               || (pattern.EndsWith("*") && schematicKey.StartsWith(
                       pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase));
    }
}
