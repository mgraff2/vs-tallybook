# Tallybook

A client-side crafting shopping list for [Vintage Story](https://www.vintagestory.at/).
Pin any item, and Tallybook tells you what you still need to gather — with live
inventory tracking and at-a-glance status. The handbook already answers "how do I make X";
Tallybook answers "what do I still need, and am I done?"

**Status: in development.** The design is finalised in
[tallybook-mod-spec.md](tallybook-mod-spec.md); implementation follows the build order in
§10 of that document. Nothing is released yet.

### How it works

1. **Pin** — open the handbook (H), find the thing you want to build, click
   **"Add to Tallybook"** at the bottom of its page. Pinning again raises the count.
2. **Manage** — press **L** (rebindable): every pinned item with − / + steppers and direct
   count entry, colour-coded ingredient rows (`have/needed`), tool checks, unpin and
   clear-all behind confirms.
3. **Expand** — any craftable ingredient row has an **Expand** button that unfolds its own
   recipe beneath it, sized to what you still *lack*: need 4 spiles, carry 1 → the children
   ask for materials for 3. Crafting shrinks them live. Nested to any depth, one deliberate
   click per level, with a recipe switcher where an ingredient has several recipes and a
   cycle guard so recipe loops can't unfold forever. Never automatic (see "Design notes").
4. **Gather** — the corner HUD (toggle **K**) shows merged totals across all pins: one
   `Boards 12/48` line even when three pinned items want boards. Expanded intermediates move
   out of the gather list — they're craft-steps now, not shopping items.

Everything updates the instant your inventory changes, and the list (counts, recipe choices,
expansion state) is saved per world.

## Why it works with every content mod, for free

Servers push their content mods to connecting clients, so every modded recipe on a server is
already present in the client's recipe registries. Tallybook reads those registries directly.
That means every content mod's recipes are supported with zero compatibility patches — not
as a maintenance promise, but as a property of where the data lives.

### Configuration (`VintagestoryData/ModConfig/tallybook.json`)

| Key | Default | Meaning |
|---|---|---|
| `HudPosition` | `"topright"` | `topleft` / `topright` / `bottomleft` / `bottomright` |
| `HudMaxRows` | `12` | gather rows before `+N more…` |
| `HudVisible` | `true` | HUD default; K toggles at runtime |
| `ConfirmOnUnpin` | `true` | ask before unpinning |
| `ColorSatisfied` / `ColorPartial` / `ColorNone` | `#80FF80` / `#FFCC66` / `#909090` | status colours |

Hotkeys (L, K) are rebindable in Settings → Controls like any other key.

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

1. **Pin flow** — handbook page shows "Add to Tallybook"; clicking pins, re-clicking
   increments, and the L dialog and K HUD both reflect it.
2. **Counting** — pick up and drop ingredients; dialog rows and HUD lines must flip state
   immediately, not on a timer. "Any wood" rows must count all woods collectively.
3. **Counts** — steppers and direct numeric entry; typing must not lose focus mid-number;
   stepping to 0 asks before unpinning.
4. **Expansion math** — expand a node while partially stocked; children must size to the
   deficit, shrink as you craft, and scale with the root pin count. Confirm the cycle guard
   refuses with a visible reason, and the recipe switcher recomputes children.
5. **HUD leaves rule** — expanding a node must remove it from the HUD's merged gather totals
   and replace it with its children; collapsing restores it.
6. **With Pin Matrix active** — open both mods' dialogs and HUDs together: no hotkey
   collision, no overlapping/hidden GUI, both HUD elements readable, and the Tallybook HUD
   must not fight the vanilla coordinate overlay for its corner.
7. **Persistence** — relog and confirm pins, counts, chosen recipes, and expansion state
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
