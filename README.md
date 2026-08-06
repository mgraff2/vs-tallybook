# Tallybook

A client-side crafting shopping list for [Vintage Story](https://www.vintagestory.at/).
Pin any item, and Tallybook tells you what you still need to gather — with live
inventory tracking and at-a-glance status. The handbook already answers "how do I make X";
Tallybook answers "what do I still need, and am I done?"

**Status: in development.** The design is finalised in
[tallybook-mod-spec.md](tallybook-mod-spec.md); implementation follows the build order in
§10 of that document. Nothing is released yet.

## Why it works with every content mod, for free

Servers push their content mods to connecting clients, so every modded recipe on a server is
already present in the client's recipe registries. Tallybook reads those registries directly.
That means every content mod's recipes are supported with zero compatibility patches — not
as a maintenance promise, but as a property of where the data lives.

## Planned features

- **Pin from the handbook** — any craftable item, with a recipe picker when an item has
  several recipes.
- **Live ingredient tracking** — `have/needed` per ingredient, recomputed on inventory
  change, colour-coded satisfied / partial / none.
- **Manual expansion tree** — expand any craftable ingredient into its own recipe, scaled to
  what you still *lack*, nested to any depth. Never automatic (see "Design notes" below).
- **HUD overlay** — always-on corner readout merging totals across all pins: one
  `Boards 12/48` line even when three pinned items want boards.
- **Management dialog** — counts, steppers, direct numeric entry, recipe swaps, unpin,
  clear-all.
- **Per-world persistence** — your list is still there tomorrow.
- **Client-side only** — installable per player, works on any server, no server component.

## Install

Not yet released. Once it is: drop `tallybook_X.Y.Z.zip` into
`%APPDATA%\VintagestoryData\Mods\`.

## Building from source

The system `dotnet` is SDK 9 and refuses the net10.0 game references; build with the
user-scoped SDK:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" build Tallybook\Tallybook.csproj -c Release
```

Game assemblies resolve from `%APPDATA%\Vintagestory`; override with
`-p:VintageStoryPath=<dir>`.

## Compat regression testing

Two automated gates, both run from PowerShell at the repo root.

**`.\tools\compat-test.ps1`** — run after any code change and before every commit. Builds
the zip and boots a headless dedicated server for every mod combination (solo, +Pin Matrix,
and all together), failing on any `[Error]`/`[Warning]` in the server log, a wrong mod count
or load order, or a violated marker. Because Tallybook is client-side only, the pinned
markers are: the server must still *load* the assembly and instantiate its mod systems
(proves the DLL works against that game version), and the mod must stay completely silent
otherwise — exactly one `tallybook` mention in `server-main.log` (its load-order entry) in
every combo. Companion zips are cached in `tools/compat-cache/` (gitignored), pulled from
the live Mods folder or the mod DB on first use. `-SkipBuild` reuses the packaged zip.

**`.\tools\version-sweep.ps1`** — run at the end of every version, before the release commit.
Builds the zip once, then runs that same artifact through the full compat matrix against real
dedicated servers for **1.22.0 through 1.22.6**, downloaded from the official CDN and cached
in `tools/server-cache/` (gitignored). This is what backs the `"game": "1.22.0"` dependency
declaration. `-Versions 1.22.0,1.22.6` checks just the endpoints; `-KeepGoing` reports every
version rather than stopping at the first failure.

Headless boots validate zip packaging, modinfo/dependency declarations, assembly loading
across game versions, and the client-only gate — **not** client behaviour, which for this mod
is most of it. Manual pre-release checklist for what the server can't see:

1. **Recipe resolution** — pin items from several crafting systems (grid, smithing, clay
   forming, barrel, cooking) and confirm ingredients and quantities match the handbook.
2. **Live counting** — pick up and drop ingredients; rows must flip state immediately, not
   on a timer. Check wildcard ingredients ("any plank") count matching items collectively.
3. **Expansion math** — expand a node while partially stocked; children must size to the
   deficit, shrink as you craft, and scale with the root pin count. Confirm the cycle guard
   refuses to expand an item into its own ancestor.
4. **HUD leaves rule** — expanding a node must remove it from the HUD's merged gather totals
   and replace it with its children.
5. **With Pin Matrix active** — open both mods' dialogs and HUDs together: no hotkey
   collision, no overlapping/hidden GUI, both HUD elements readable.
6. **Persistence** — relog and confirm pins, counts, chosen recipes, and expansion state
   survive; corrupt the JSON by hand and confirm it degrades to an empty list, never a crash.

## Design notes

Two decisions that look like missing features and are not:

- **Ingredient expansion is never automatic.** Auto-recursion runs into recipe cycles,
  per-level recipe-choice explosions (six woods × three grids × smith-vs-cast), and silent
  wrong guesses — every one of those makes the list lie. Expansion is always a deliberate
  click with a recipe attached to it.
- **Only carried inventory counts** (hotbar, backpacks, and your mount's bags while riding) —
  not nearby chests. The question is "what do I still need on me", and answering a different
  question confidently is worse than not answering it.

## License

MIT — see [LICENSE](LICENSE).
