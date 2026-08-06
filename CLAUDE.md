# Tallybook — working notes for Claude

Client-side-only Vintage Story mod (`"side": "Client"`): Satisfactory-style crafting
shopping list. Pin an item, see live inventory-tracked ingredient requirements. No assets,
no server code — the zip is `modinfo.json` + `Tallybook.dll`.

Full design in `tallybook-mod-spec.md`. Read it before implementing; the sections below are
the parts that are easy to get wrong twice.

## Build

The system `dotnet` is SDK 9 and refuses the net10.0 game references. Build with the
user-scoped SDK:

```
& "$env:USERPROFILE\.dotnet\dotnet.exe" build Tallybook\Tallybook.csproj -c Release
```

Game references resolve from `%APPDATA%\Vintagestory` (override with `-p:VintageStoryPath=...`).

## Testing — two gates, both mandatory

### 1. Compat matrix — after any code change, before any commit or release

```
.\tools\compat-test.ps1
```

Builds the zip into `dist/`, then boots a headless dedicated server
(`%APPDATA%\Vintagestory\VintagestoryServer.exe --dataPath <temp>`) once per combo — solo,
+each companion mod, all together — and fails on any `[Error]`/`[Warning]`, a wrong mod
count/load order, or a violated marker (invariants below). `-SkipBuild` reuses the packaged
zip. Companion zips are cached in `tools/compat-cache/` (gitignored; sourced live-Mods-
folder-first, else mod DB API) — delete the cache to re-source. Server data paths are keyed by
PID, so a hand-run test and a running sweep no longer delete each other's directory mid-boot
(that collision reports as "server did not start" and looks like a mod failure).

**Companion set: `pinmatrix` (Pin Matrix — Waypoint Manager).** Our own other client-side
mod: same author, same client, competing for hotkeys, HUD corners, and GUI dialog space, and
a user running both is the expected case. These two must never break each other. Grow this
set as Tallybook's surface grows — add recipe-adding content mods once the §8 registry reads
land, and HUD-corner mods (e.g. `statushudcont`) once the §5 overlay ships. Derive additions
from Tallybook's *real* interaction surface; do not copy another project's list wholesale.

### 2. Game-version sweep — at the end of every version, before the release commit

```
.\tools\version-sweep.ps1
```

`modinfo.json` declares `"game": "1.22.0"`, which is a promise to every player on every
patch release. This keeps it honest: it builds the zip **once**, then runs the whole compat
matrix against a real dedicated server for **1.22.0 through 1.22.6**, each downloaded from
`https://cdn.vintagestory.at/gamefiles/stable/vs_server_win-x64_<ver>.zip` and cached
extracted in `tools/server-cache/` (gitignored). One artifact, N servers — that is the claim
being tested. `-Versions 1.22.0,1.22.6` checks just the endpoints when iterating;
`-KeepGoing` reports every version instead of stopping at the first failure.

When a new patch ships, append it to the `-Versions` default. The CDN 404s on versions that
don't exist, which is how you find the current latest.

Read the summary carefully: it reports `PASS` / `FAIL` / `SETUP`, and `SETUP` is **not** a mod
failure — it means that version could not be tested at all (download or extraction problem).
The distinction exists because a half-extracted server package boots without its worldgen and
worldproperty assets, floods `server-main.log` with `[Error]`s that have nothing to do with
us, and looks exactly like the mod being broken on that version. Extraction is now verified
against the archive's own entry count and marked with a `.extract-complete` stamp, so a
partial or interrupted extract is never silently reused. Don't switch back to
`Expand-Archive` — it was caught truncating the ~9600-file server archive to ~1400 files
without raising an error.

## Compat invariants (what the tests pin, and why)

- **Total server-side silence.** Tallybook must contribute *exactly one* line to
  `server-main.log` on a dedicated server: its entry in the `Mods, sorted by dependency:`
  line. That holds in **every** combo, companions present or not. A second mention means
  server-side code started running or logging — e.g. someone weakened
  `ShouldLoad(EnumAppSide.Client)` or `"side": "Client"` in modinfo — and the test fails.
  This one matters more than usual here: Tallybook reads recipe registries and inventory,
  both of which also exist server-side, so client-only discipline is easy to lose by
  accident.
- **The DLL must still load server-side.** Even though it never runs there, the server
  unpacks the zip and loads the assembly; `server-debug.log` must show
  `[tallybook] Loaded assembly` and `Instantiate mod systems for tallybook`. This is what
  catches an assembly that no longer loads against a game version — the single most likely
  way a patch release breaks us.
- **No conditional compat registration exists today.** All cross-mod support is meant to be
  dynamic and nameless: Tallybook reads whatever recipes the server pushed into the client's
  registries, so content mods work with zero compat patches (this is a headline feature —
  see spec §1). There should be no `api.ModLoader.IsModEnabled(...)` branch anywhere.
  **If you ever add one**, also add an exact-count `Notification` log line at the
  registration site (e.g. `"[tallybook] X detected: N somethings registered"`) and pin it in
  `compat-test.ps1` as a `require` marker for combos with X and a `forbid` marker for combos
  without X — that way an upstream change that silently breaks the integration changes the
  count and fails the test.
- **Cross-mod grid recipes trap (learned the hard way on another mod; N/A here today).**
  This mod has no assets folder at all — keep it that way unless there's a strong reason. If
  assets are ever added: cross-mod grid recipes must NOT go in `recipes/grid/` — the vanilla
  loader logs an `[Error]` when an ingredient's mod is missing. Register them from code,
  gated on `api.ModLoader.IsModEnabled(...)`, with a count marker as above.

## What the headless test cannot see

Everything client-side, which for this mod is nearly all of it: recipe registry reads,
inventory-change events, the HUD overlay, the management dialog, handbook pin integration,
expansion-tree math. The manual checklist for those lives in README.md ("Compat regression
testing"). Run it before any release that touches GUI, recipe resolution, or counting.

## Findings from the step-1 probe that the spec did not anticipate

Observed on a real modded client (~30,300 grid recipes), not theorised:

- **Wildcard recipes do NOT stay one recipe — this contradicts spec §2a.** The spec says
  "one recipe accepting 'any log' stays one recipe." The registry says otherwise: the game
  expands a wildcard ingredient into one concrete recipe per variant at resolve time.
  `bookshelf.json` declares `"P": { code: "plank-*", name: "wood" }`, and the registry holds
  a separate resolved recipe for every wood. That is why a modded client carries ~30,300 grid
  recipes and why one query matched enough to write ~231 chat lines.

  Consequence: **never present a raw registry recipe to the player.** Group by output code
  (plus `RecipeGroup`), then collapse the variants inside a group into one requirement that
  accepts them all and counts them collectively. Watching a single raw recipe reports
  "Board (Aged oak) 0/7" while the player carries twenty birch boards — accurate about that
  registry entry, and a lie about the question being asked.

  **Grid layout is deliberately not part of the grouping key** (product decision, Mark): an
  item craftable four ways is still one thing to shop for, and the handbook is where layouts
  belong — link to it with `handbook://block-<path>` / `handbook://item-<path>` (chat renders
  `<a href="...">`). Because layouts want different amounts, a group is represented by its
  **cheapest** layout, and the row says so. Cheapest is the honest floor: gather that much and
  you can definitely build one, whereas showing a larger layout's numbers sends the player
  after materials they may not need. `BuildRequirements` only merges variants whose shape and
  quantities match the representative, so a 5-plank layout never absorbs an 8-plank one.
- **Registry access and live inventory events are both confirmed working** on a real modded
  client, so §1's "every content mod's recipes for free" holds in practice, not just in
  principle, and §4's event-driven counting needs no polling fallback.
- **Ingredient `Name` survives expansion** ("wood"), which is what lets a collapsed row read
  "Board (any wood)" rather than an anonymous "any".
- **The handbook groups more coarsely than we do, deliberately.** It shows one cycling preview
  per item (`SlideshowGridRecipeTextComponent`, "flips through given array of grid recipes
  every second"), splittable by `GridRecipe.RecipeGroup` — documented as "info used by the
  handbook". Our grouping includes `RecipeGroup` to respect that authoring intent, and
  otherwise matches the handbook's granularity — one entry per item, layouts left to the
  handbook. There is **no reusable public recipe index** — the handbook builds its own, so we
  build our own (`RecipeProbe.EnsureIndex`, output code -> recipes, invalidated on join/leave
  since recipes arrive from the server). Rescanning ~30,000 recipes per lookup is invisible in
  a chat command and ruinous in a HUD that refreshes on every inventory change.
- **Coalesce inventory events.** Moving one stack raises `SlotModified` for the source and
  destination separately; recounting per event both wastes work and briefly displays a number
  that was never true. Defer to one recount via `capi.Event.RegisterCallback(..., 0)`. This
  also avoids mutating event subscriptions from inside an event handler.
- **Verified independently:** a bookshelf's 7 planks match `bookshelf.json`'s
  `P_P,PPP,P_P` pattern, confirming that merging duplicate grid cells produces the right
  quantity. When checking quantities, read the recipe JSON under
  `tools/server-cache/<ver>/assets/survival/recipes/grid/` rather than trusting the mod's own
  output.

## Handbook integration (the entry point)

Pinning is a click on the item's handbook page, not a typed item code. The player is already
looking at the thing they want, and the handbook hands over a real `ItemStack` — so there is
nothing to search for and nothing to guess. `RecipeProbe.FindGroupFor(stack)` is the product
lookup; `FindVariantGroups(substring)` survives only for the diagnostic command.

There is **no registration hook** for adding to a handbook page, so `HandbookPin` uses a
Harmony postfix on `CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo` (Harmony
ships with the game *and* the dedicated server, verified across 1.22.0–1.22.6). The patch only
appends a `LinkTextComponent` to the returned array — it reads nothing and alters nothing the
game produced, and a failure to apply is caught and logged so the handbook keeps working
without the button. Patching happens in `StartClientSide`, so `ShouldLoad` keeps it off
servers entirely.

Note this is the first `IsModEnabled`-adjacent machinery in the mod. It is not conditional on
another mod, so the "no conditional compat registration" invariant above still holds — but if
a handbook integration ever *does* branch on another mod, add the counted log marker described
there.

## Design invariants — do not "fix" these

These are deliberate and each one has a failure mode behind it (spec §2, §2a, §4):

- **No automatic recursion.** A pinned item shows its *direct* ingredients only. Expansion
  is always a deliberate player action with a recipe choice attached. Auto-expansion hits
  recipe cycles, per-level recipe-choice explosions, and silent wrong guesses — every one of
  those makes the list lie. Permanently rejected, not a backlog item.
- **HUD shows leaves only.** Expanding a node moves it from "gather this" to "craft this
  from the things below"; intermediates belong in the dialog's tree, not the HUD totals.
- **Deficit-based scaling**, not gross requirement:
  `craftsNeeded = ceil(max(0, parentNeeded − parentHave) / recipeOutputQuantity)`.
- **Event-driven inventory counting, never per-frame polling.** The instant green-flip when
  you pick up the last board is the core loop; degrading it to a poll guts the mod.
- **Carried inventory only** — no nearby-chest scanning. The question is "what do I have on
  me", and answering a different question dishonestly is worse than not answering.

## API caveat (standing rule)

Registry class names, handbook dialog extensibility, and inventory event signatures must be
verified against 1.22 docs/source at implementation time — **not trusted from memory**. The
spec's step-1 read-only prototype exists to surface exactly these unknowns before anything
is built on top of them.

To verify: reflect over the real assemblies rather than guessing or trusting these notes.
`VintagestoryAPI.xml` next to the game DLLs carries the doc comments (member *names* only, no
types), and a throwaway net10.0 console app referencing `VintagestoryAPI.dll` /
`VSSurvivalMod.dll` and calling `Assembly.LoadFrom` + `GetMembers` gives full signatures.
`Assembly.Load` by simple name does not work, and PowerShell cannot do this — it lacks
`MetadataLoadContext` and cannot load the net10.0 assemblies directly.

### Verified surface (checked against 1.22 assemblies, step-1 probe)

- **Recipes:** `capi.World.GridRecipes` is a `List<GridRecipe>`, client-resident.
  `GridRecipe.ResolvedIngredients` is a `CraftingRecipeIngredient[]` shaped like the crafting
  grid and **sparse — empty cells are null**. The same ingredient appears once per grid cell,
  so quantities must be merged by matcher, not read off a single entry.
- **Output count:** `Output.Quantity` and `Output.StackSize` are documented aliases of each
  other; either is correct. This is the divisor in the §2a deficit math.
- **Ingredient matching:** `IsWildCard` is **obsolete** — 1.22 has
  `MatchingType` (`EnumRecipeMatchType`: `Exact`, `Wildcard`, `NamedWildcard`,
  `AdvancedWildcard`, `Regex`, `TagsOnly`) plus a tag system on `Tags`
  (`ComplexTagCondition<TagSet>`, a **struct** — `?.` does not compile on it).
  **Do not reimplement matching.** `CraftingRecipeIngredient.SatisfiesAsIngredient(stack,
  checkStackSize)` is the game's own matcher and covers every mode including tags. Pass
  `checkStackSize: false` — we sum across slots ourselves, and asking whether one slot alone
  satisfies the whole requirement undercounts every split stack. Delegating also guarantees
  we can never claim a player has materials the crafting grid would refuse.
- **Inventory:** `capi.World.Player.InventoryManager.Inventories` is a
  `Dictionary<string, IInventory>`; filter by `inv.ClassName` against
  `GlobalConstants.hotBarInvClassName` and `backpackInvClassName` for carried-only counting.
  `IInventory.SlotModified` is an `Action<int>` — this is the event-driven hook §4 requires.
  Re-scan for new inventories on change; equipping a bag adds one after login.
- **Commands:** `capi.ChatCommands.Create(name).WithDescription/.WithArgs/.HandleWith(...)`,
  parsers from `capi.ChatCommands.Parsers`, results via `TextCommandResult.Success/Error`.
  **Client commands are invoked with a leading `.`, not `/`.** Register the name without any
  prefix; the player types `.tallybook`. A `/` prefix routes to the server, which for a
  client-only mod has never heard of the command and replies "No such command exists" — a
  message that looks exactly like the mod failing to load. Before chasing that, check
  `client-debug.log` for `[tallybook] Loaded assembly` and `Starting system:
  TallybookModSystem`; if both are present, registration worked and the prefix is the problem.
  Document commands with the dot in README, CHANGELOG and `WithExamples`.
  Read parsed values as `args[0]`, and **never gate on `args.ArgCount`** — parsers consume the
  raw arguments while parsing, so `ArgCount` reads 0 inside a handler even when `args[0]`
  holds the value. Gating on it silently drops every argument and the command behaves as if
  it were called bare.
- **Lifecycle:** `capi.Event.PlayerJoin` (compare `PlayerUID` against
  `capi.World.Player.PlayerUID` — it fires for other players too) and `capi.Event.LeaveWorld`.

## Release flow

Stage `dist/tallybook_X.Y.Z.zip` into `%APPDATA%\VintagestoryData\Mods\` (remove older
tallybook zips) for local/friend testing first. Publish only on explicit go-ahead: dated
CHANGELOG entry, README version refs, commit, tag `vX.Y.Z`, push,
`gh release create vX.Y.Z dist\tallybook_X.Y.Z.zip --title "Tallybook X.Y.Z"`. ModDB upload
is manual. **Run `.\tools\compat-test.ps1` and `.\tools\version-sweep.ps1` before every
release.**
