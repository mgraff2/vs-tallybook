using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// The Lore tab's model: what the player's journal holds against what this world's
    /// content defines, plus the "export a book" writer. Nothing here is Tallybook's own
    /// record — found lore comes from the journal (server-persisted per player, synced in
    /// full at login, which is why it follows the player between computers), and totals
    /// come from the world's lore assets (config/lore/*.json, the same files ModJournal
    /// reads; JournalAsset.Pieces is the chapter list, so every volume's size is exact).
    /// Modded lore counts identically for free — the files arrive like any other asset.
    ///
    /// Spoiler rule, same as everywhere else: found volumes show by name with chapter
    /// progress; volumes and categories not yet touched are COUNTS only. The count is
    /// progress, the names are content the world has not given up yet.
    ///
    /// The journal read reflects into ModJournal's private ownJournal — the public
    /// DidDiscoverLore reads the SERVER-side dictionary and is always empty on a client
    /// (decompile-verified 1.22.6, same finding SiteQuests is built on). A failed read is
    /// an empty journal for that look, never an error: the tab reads "nothing found yet",
    /// and the next good read repairs it.
    /// </summary>
    public class LoreBook
    {
        readonly ICoreClientAPI capi;

        /// <summary>The site-lore scan, for its story-category classification — set by the
        /// mod system, may be null or not Ready; both mean "kind unknown".</summary>
        public SiteLoreScan Scan;

        public class Volume
        {
            public string Code;
            public string Category;
            public string Title;
            /// <summary>true: only the story's own places hold this (scan-derived).
            /// false: the wider world can drop it. null: the scan has not answered yet —
            /// unknown must not read as either kind.</summary>
            public bool? IsStory;
            /// <summary>Asset domain the lore file arrived in — "game" for vanilla, the
            /// mod id otherwise. The filter key.</summary>
            public string SourceKey;
            /// <summary>What to call that source: "Vanilla", or the mod's display name.</summary>
            public string Source;
            public int TotalChapters;
            public int FoundChapters;
            /// <summary>Found chapter texts in ChapterId order, lang-resolved. Only filled
            /// by Snapshot(forExport: true) — display never needs the prose.</summary>
            public List<string> Texts;
        }

        public class Model
        {
            public List<Volume> Volumes = new List<Volume>();   // every defined volume
            public int TotalVolumes, FoundVolumes;
            public int TotalChapters, FoundChapters;
            public int TotalCategories, FoundCategories;
            public List<Volume> Found => Volumes.Where(v => v.FoundChapters > 0).ToList();
        }

        class DefEntry
        {
            public JournalAsset Asset;
            public string Domain;
        }

        List<DefEntry> defs;
        bool journalWarned;
        string lastSignature = "";

        public LoreBook(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        public void InvalidateWorld()
        {
            defs = null;
            lastSignature = "";
        }

        /// <summary>config/lore/*.json across every domain — the same read SiteLoreScan
        /// does, cached per world since assets cannot change mid-session. The location's
        /// domain rides along: it is what says which mod a volume came from.</summary>
        List<DefEntry> Defs()
        {
            if (defs != null) return defs;
            defs = new List<DefEntry>();
            List<AssetLocation> files = null;
            try { files = capi.Assets.GetLocations("config/lore/"); } catch { }

            foreach (var loc in files ?? new List<AssetLocation>())
            {
                try
                {
                    var asset = capi.Assets.TryGet(loc)?.ToObject<JournalAsset>();
                    if (asset?.Code != null)
                        defs.Add(new DefEntry { Asset = asset, Domain = loc.Domain ?? "game" });
                }
                catch { /* one unreadable lore file must not cost the rest */ }
            }
            return defs;
        }

        /// <summary>"game" is the base game; anything else is a mod id, shown by the mod's
        /// own display name so the filter says "Better Ruins", not "betterruins".</summary>
        string SourceName(string domain)
        {
            if (string.Equals(domain, "game", StringComparison.OrdinalIgnoreCase)) return "Vanilla";
            try { return capi.ModLoader.GetMod(domain)?.Info?.Name ?? domain; }
            catch { return domain; }
        }

        /// <summary>Category a volume draws from — defaults to the code where a config
        /// omits it, the journal's own convention (see SiteLoreScan).</summary>
        static string CategoryOf(JournalAsset def)
            => string.IsNullOrEmpty(def.Category) ? def.Code : def.Category;

        /// <summary>The player's journal entries, or empty on any failure.</summary>
        List<JournalEntry> JournalEntries()
        {
            try
            {
                var journalSys = capi.ModLoader.GetModSystem<ModJournal>();
                var journal = journalSys == null ? null
                    : AccessTools.Field(typeof(ModJournal), "ownJournal")?.GetValue(journalSys) as Journal;
                return journal?.Entries ?? new List<JournalEntry>();
            }
            catch (Exception e)
            {
                if (!journalWarned)
                {
                    journalWarned = true;
                    capi.Logger.Warning("[tallybook] journal read failed: {0}", e.Message);
                }
                return new List<JournalEntry>();
            }
        }

        public Model Snapshot(bool forExport = false)
        {
            var model = new Model();
            var byCode = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in JournalEntries())
            {
                if (!string.IsNullOrEmpty(entry?.LoreCode)) byCode[entry.LoreCode] = entry;
            }

            var foundCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var storyCategories = Scan?.StoryCategories;

            foreach (var defEntry in Defs())
            {
                var def = defEntry.Asset;
                string category = CategoryOf(def);
                allCategories.Add(category);

                var vol = new Volume
                {
                    Code = def.Code,
                    Category = category,
                    Title = ResolveOrFallback(def.Title, def.Code),
                    IsStory = storyCategories == null ? (bool?)null : storyCategories.Contains(category),
                    SourceKey = defEntry.Domain,
                    Source = SourceName(defEntry.Domain),
                    TotalChapters = def.Pieces?.Length ?? 0,
                };

                if (byCode.TryGetValue(def.Code, out var entry))
                {
                    // Distinct ids: the server guards against double-discovery, but a
                    // count that could exceed the total on bad data would read as a bug.
                    var chapters = (entry.Chapters ?? new List<JournalChapter>())
                        .Where(ch => ch != null)
                        .GroupBy(ch => ch.ChapterId).Select(g => g.First())
                        .OrderBy(ch => ch.ChapterId).ToList();
                    vol.FoundChapters = vol.TotalChapters > 0
                        ? Math.Min(chapters.Count, vol.TotalChapters)
                        : chapters.Count;
                    if (forExport)
                    {
                        vol.Texts = chapters
                            .Select(ch => ResolveOrFallback(ch.Text, null))
                            .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    }
                    if (vol.FoundChapters > 0) foundCategories.Add(category);
                }

                model.Volumes.Add(vol);
                model.TotalChapters += vol.TotalChapters;
                model.FoundChapters += vol.FoundChapters;
            }

            model.Volumes = model.Volumes
                .OrderBy(v => v.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase).ToList();
            model.TotalVolumes = model.Volumes.Count;
            model.FoundVolumes = model.Volumes.Count(v => v.FoundChapters > 0);
            model.TotalCategories = allCategories.Count;
            model.FoundCategories = foundCategories.Count;
            return model;
        }

        /// <summary>Lang.Get, with "the key came back unchanged" treated as a miss — the
        /// same convention as the quest scanner. Chapter texts may already BE resolved
        /// prose (they come from the server), in which case Lang returns them untouched.</summary>
        static string ResolveOrFallback(string keyOrText, string fallback)
        {
            if (string.IsNullOrEmpty(keyOrText)) return fallback;
            string resolved = Lang.Get(keyOrText);
            return string.IsNullOrEmpty(resolved) ? (fallback ?? keyOrText) : resolved;
        }

        /// <summary>Everything the Lore tab draws, flattened for the shared change
        /// signature — a new chapter landing in the journal must redraw the surfaces.</summary>
        public string Signature()
        {
            int entries = 0, chapters = 0;
            foreach (var entry in JournalEntries())
            {
                if (string.IsNullOrEmpty(entry?.LoreCode)) continue;
                entries++;
                chapters += entry.Chapters?.Count ?? 0;
            }
            return string.Create(CultureInfo.InvariantCulture, $"{entries}:{chapters}");
        }

        /// <summary>For the 1s tick: has the journal moved since the last look? Reading a
        /// tapestry changes no inventory slot, so without this poll a discovery would not
        /// redraw anything until some unrelated event happened by.</summary>
        public bool Poll()
        {
            string sig = Signature();
            if (sig == lastSignature) return false;
            lastSignature = sig;
            return true;
        }

        /// <summary>The game's journal dialog, when ModJournal currently holds one open —
        /// for the side-by-side window arranging. Null is "not open".</summary>
        public GuiDialog JournalDialog()
        {
            try
            {
                var sys = capi.ModLoader.GetModSystem<ModJournal>();
                return sys == null ? null
                    : AccessTools.Field(typeof(ModJournal), "dialog")?.GetValue(sys) as GuiDialogJournal;
            }
            catch { return null; }
        }

        /// <summary>
        /// Open the game's own journal dialog, optionally straight onto one volume's entry.
        /// The dialog is ModJournal's private field and its open path is the hotkey handler
        /// (decompile-verified 1.22.6: OnHotkeyJournal TOGGLES — builds the dialog from
        /// ownJournal.Entries and wires the on-close cleanup — so it is only invoked when
        /// the field is null, never as a blind toggle). Entry selection is the dialog's own
        /// onClickItem(index), where index is the position in its journalitems list — found
        /// by lore code at click time, so it cannot go stale against a re-sorted journal.
        /// Deferred a tick so the selection composes after the dialog's own open compose.
        /// </summary>
        public bool OpenJournal(string loreCode = null)
        {
            try
            {
                var sys = capi.ModLoader.GetModSystem<ModJournal>();
                if (sys == null) return false;
                var dlgField = AccessTools.Field(typeof(ModJournal), "dialog");
                if (dlgField == null) return false;

                var dlg = dlgField.GetValue(sys) as GuiDialogJournal;
                if (dlg == null)
                {
                    AccessTools.Method(typeof(ModJournal), "OnHotkeyJournal")
                        ?.Invoke(sys, new object[] { null });
                    dlg = dlgField.GetValue(sys) as GuiDialogJournal;
                }
                else if (!dlg.IsOpened())
                {
                    dlg.TryOpen();
                }
                if (dlg == null) return false;

                if (loreCode != null)
                {
                    capi.Event.EnqueueMainThreadTask(() =>
                    {
                        try
                        {
                            var d = dlgField.GetValue(sys) as GuiDialogJournal;
                            if (d == null || !d.IsOpened()) return;
                            var items = AccessTools.Field(typeof(GuiDialogJournal), "journalitems")
                                ?.GetValue(d) as List<JournalEntry>;
                            int idx = items?.FindIndex(en =>
                                string.Equals(en?.LoreCode, loreCode, StringComparison.OrdinalIgnoreCase)) ?? -1;
                            if (idx >= 0)
                            {
                                AccessTools.Method(typeof(GuiDialogJournal), "onClickItem")
                                    ?.Invoke(d, new object[] { idx });
                            }
                        }
                        catch { /* the journal is open either way — selection is best-effort */ }
                    }, "tallybook-journal");
                }
                return true;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] could not open the journal: {0}", e.Message);
                return false;
            }
        }

        // ---- export --------------------------------------------------------------------

        /// <summary>
        /// Write the found lore as a single self-contained HTML book — title page, one
        /// section per category, chapters in discovery order — styled for print, so
        /// "export to PDF" is the browser's print dialog away. Only what the journal
        /// holds goes in; per volume, a closing line counts what is still missing.
        /// Returns the file path, or null (with the reason logged) on failure.
        /// </summary>
        public string ExportBook(out int volumes, out int chapters)
        {
            volumes = 0; chapters = 0;
            try
            {
                var model = Snapshot(forExport: true);
                var found = model.Found;
                if (found.Count == 0) return null;
                volumes = found.Count;
                chapters = model.FoundChapters;

                string dir = Path.Combine(GamePaths.DataPath, "ModData", "tallybook");
                Directory.CreateDirectory(dir);
                string worldId = SafeFileName(capi.World?.SavegameIdentifier ?? "world");
                string path = Path.Combine(dir, $"lorebook-{worldId}.html");

                File.WriteAllText(path, BuildHtml(model, found), Encoding.UTF8);
                return path;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[tallybook] lore book export failed: {0}", e.Message);
                return null;
            }
        }

        static string SafeFileName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return sb.Length == 0 ? "world" : sb.ToString();
        }

        string BuildHtml(Model model, List<Volume> found)
        {
            string reader = capi.World?.Player?.PlayerName ?? "an unknown seraph";
            string date = null;
            try { date = capi.World?.Calendar?.PrettyDate(); } catch { }

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Collected Lore</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(@"
body { font-family: Georgia, 'Times New Roman', serif; color: #2b2317; background: #f4ecd8;
       max-width: 46em; margin: 0 auto; padding: 3em 2em; line-height: 1.55; }
.cover { text-align: center; margin: 18vh 0 20vh 0; page-break-after: always; }
.cover h1 { font-size: 2.6em; letter-spacing: 0.08em; margin-bottom: 0.2em; }
.cover .rule { border: none; border-top: 1px solid #8a7a5c; width: 40%; margin: 1.4em auto; }
.cover p { color: #6b5d43; font-style: italic; }
h2 { font-size: 1.7em; margin-top: 2.2em; border-bottom: 1px solid #8a7a5c;
     padding-bottom: 0.2em; page-break-before: always; }
.count { font-size: 0.62em; font-weight: normal; font-style: italic; color: #6b5d43; }
h1.part { font-size: 2em; text-align: center; letter-spacing: 0.06em; margin-top: 2.5em;
          page-break-before: always; border-bottom: 2px solid #8a7a5c; padding-bottom: 0.3em; }
.chapter { margin: 1em 0 1.6em 0; page-break-inside: avoid; }
.missing { text-align: center; color: #8a7a5c; font-style: italic; margin: 1.4em 0; }
.contents li { margin: 0.25em 0; }
@media print { body { background: #fff; padding: 0; } }
");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<div class=\"cover\">");
            sb.AppendLine("<h1>Collected Lore</h1>");
            sb.AppendLine("<hr class=\"rule\">");
            sb.AppendLine($"<p>as recovered by {Escape(reader)}"
                + (string.IsNullOrEmpty(date) ? "" : $"<br>{Escape(date)}") + "</p>");
            sb.AppendLine($"<p>{model.FoundVolumes} of {model.TotalVolumes} known volumes &mdash; "
                + $"{model.FoundChapters} of {model.TotalChapters} chapters</p>");
            sb.AppendLine("</div>");

            // The same order as the Lore tab (Mark — an alphabetical mix read as vanilla
            // and mod stories shuffled together): vanilla first, then each mod as its own
            // part, unfinished volumes before complete ones, alphabetical inside. No
            // category headings: the game's category names are internal codes (some of
            // them character and place names), never player-facing words.
            var ordered = found
                .OrderBy(v => string.Equals(v.SourceKey, "game", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.TotalChapters > 0 && v.FoundChapters >= v.TotalChapters)
                .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var parts = ordered.GroupBy(v => v.Source, StringComparer.OrdinalIgnoreCase).ToList();
            bool multiSource = parts.Count > 1;

            sb.AppendLine("<h2 style=\"page-break-before: avoid;\">Contents</h2><ul class=\"contents\">");
            foreach (var part in parts)
            {
                if (multiSource) sb.AppendLine($"<li><strong>{Escape(part.Key)}</strong><ul>");
                foreach (var vol in part)
                    sb.AppendLine($"<li>{Escape(vol.Title)} <em>({vol.FoundChapters} of {vol.TotalChapters})</em></li>");
                if (multiSource) sb.AppendLine("</ul></li>");
            }
            sb.AppendLine("</ul>");

            foreach (var part in parts)
            {
                if (multiSource) sb.AppendLine($"<h1 class=\"part\">{Escape(part.Key)}</h1>");
                foreach (var vol in part)
                {
                    sb.AppendLine($"<h2>{Escape(vol.Title)} "
                        + $"<span class=\"count\">{vol.FoundChapters} of {vol.TotalChapters} chapters</span></h2>");
                    foreach (var text in vol.Texts ?? new List<string>())
                        sb.AppendLine($"<div class=\"chapter\">{FormatChapter(text)}</div>");
                    if (vol.FoundChapters < vol.TotalChapters)
                        sb.AppendLine($"<p class=\"missing\">&#8258; {vol.TotalChapters - vol.FoundChapters} "
                            + "chapter(s) still undiscovered &#8258;</p>");
                }
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Chapter prose → safe HTML. Lore texts are VTML: mostly plain prose with \n
        /// breaks, occasionally simple tags (tapestries use &lt;strong&gt;). Everything is
        /// escaped first, then a small whitelist of harmless formatting tags is restored —
        /// so an unexpected tag from a mod renders as visible text rather than as markup.
        /// </summary>
        static string FormatChapter(string text)
        {
            string s = Escape(text);
            foreach (var tag in new[] { "strong", "em", "i" })
            {
                s = s.Replace($"&lt;{tag}&gt;", $"<{tag}>").Replace($"&lt;/{tag}&gt;", $"</{tag}>");
            }
            s = s.Replace("&lt;br&gt;", "<br>").Replace("&lt;br/&gt;", "<br>");

            var paragraphs = s.Replace("\r\n", "\n")
                .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", paragraphs.Select(p => $"<p>{p.Trim().Replace("\n", "<br>")}</p>"));
        }

        static string Escape(string s)
            => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
