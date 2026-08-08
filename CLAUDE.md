# Tallybook — working notes for Claude

Client-side-only Vintage Story mod (`"side": "Client"`): Satisfactory-style crafting
shopping list. Pin an item, see live inventory-tracked ingredient requirements. No assets,
no server code — the zip is `modinfo.json` + `Tallybook.dll`.

Full design in `tallybook-mod-spec.md`. Read it before implementing; the sections below are
the parts that are easy to get wrong twice.

`docs/vs-mod-playbook.md` is the **generalized** version of those lessons, written to be
copied into a new Vintage Story mod. When a lesson here turns out not to be about Tallybook
specifically, move it there too — that file is the one that outlives this project. Both
scripts in `tools/` are portable verbatim: they discover the project as "the folder holding a
modinfo.json" and name no mod anywhere, so keep them that way.

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
zip. Companion zips are cached in `tools/compat-cache/` (gitignored; sourced live-Mods-folder
first, then `ModsByServer/` newest-first, else mod DB API) — delete the cache to re-source.
`ModsByServer/` is where a modded server's own mods land, which is the realistic companion
pool and usually the only place they exist locally. Server data paths are keyed by
PID, so a hand-run test and a running sweep no longer delete each other's directory mid-boot
(that collision reports as "server did not start" and looks like a mod failure).

**Companion set: `betterruins`.** A recipe-adding content mod — the category the registry
reads care about. It adds hundreds of grid recipes for items vanilla already crafts, gated
behind schematics, so it is the mod that proves a server can push a large alternate recipe
set and Tallybook still says nothing server-side. Grow this set as Tallybook's surface grows —
add client-side GUI mods that compete for hotkeys, HUD corners, and dialog space, and
HUD-corner mods (e.g. `statushudcont`) once the §5 overlay ships. Derive additions from
Tallybook's *real* interaction surface; do not copy another project's list wholesale.

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

`compat-test.ps1` ends with an explicit `exit 0` — do not remove it. The sweep reads
`$LASTEXITCODE`, which only native commands and `exit` set; without it, a `-SkipBuild` run
that never invokes dotnet leaves a stale code behind and a fully passing matrix gets reported
as seven FAILs (this happened — the per-version log said PASSED while the summary said FAIL).

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

  Consequence: **never present a raw registry recipe to the player.** Group by output page
  code (see next bullet; plus `RecipeGroup`), then collapse the variants inside a group into
  one requirement that accepts them all and counts them collectively. Watching a single raw
  recipe reports "Board (Aged oak) 0/7" while the player carries twenty birch boards —
  accurate about that registry entry, and a lie about the question being asked.
- **Outputs sharing a code can be different items (found in 0.1.0 testing, Mark).** The four
  bookshelf grid recipes all output block code `bookshelf` and differ only in output
  *attributes* (`type: 2row1col…`, `material: {wood}`) — and each is its own handbook page
  with its own plank count. Grouping by bare output code merged them, so pinning the 8-plank
  page produced a 5-plank list. Group identity is therefore the handbook's own page identity
  (`GuiHandbookItemStackPage.PageCodeForStack`, static, verified): pinning must yield **the
  exact page the player clicked**, and pins persist their stack attributes (JSON token via
  `TreeAttribute.ToJsonToken`/`FromJson`) so they resolve back to the same variant after
  relog. Two corollaries: pin identity/dedup/prefs key on the page code, never the bare code
  (`Pin.Key`); and where the wildcard substitutes into output attributes, each wood is its
  own page, so such a pin honestly demands *that* wood's planks — "any wood" collapsing
  applies only to recipes whose outputs are identical. When no group matches the page's
  attributes, all groups for the code are the fallback (some recipe beats "no recipe known").

  **Grid layout is deliberately not part of the grouping key** (product decision, Mark): an
  item craftable four ways is still one thing to shop for, and the handbook is where layouts
  belong — link to it with `handbook://block-<path>` / `handbook://item-<path>` (chat renders
  `<a href="...">`). Because layouts want different amounts, a group is represented by its
  **cheapest** layout, and the row says so. Cheapest is the honest floor: gather that much and
  you can definitely build one, whereas showing a larger layout's numbers sends the player
  after materials they may not need. `BuildRequirements` only merges variants whose shape and
  quantities match the representative, so a 5-plank layout never absorbs an 8-plank one.
- **`RecipeGroup` is optional, so it cannot be the thing that decides what counts as a second
  recipe (found by Mark, 0.3.4).** Mods that re-add a vanilla item behind a schematic — Better
  Ruins does this hundreds of times, the airship mod too — mostly leave that field at 0, which
  put their recipe in the same group as vanilla's, where it lost the cheapest-representative
  contest and was then dropped by `BuildRequirements`' shape gate without a word. The grouping
  key now carries `MaterialSignature`: what the recipe takes, keyed on the *ingredient's
  `Name`* rather than its concrete code.

  That indirection is the whole trick, and it is safe for a non-obvious reason: carrying a
  name is exactly *why* the game expands a wildcard, so an expanded ingredient always has one
  and a nameless wildcard is never expanded. Keying on the name therefore collapses all thirty
  woods of one authored recipe into a single token, while two recipes that genuinely want
  different materials keep different tokens — it cannot regress into the per-variant explosion
  that keying on the code would cause. Kept ingredients (tools, `consume: false` schematics)
  are in the signature too, since "craftable only with a schematic you must find" versus
  "craftable outright" is precisely the choice a player must be offered rather than have made
  for them.

  **It belongs only to the non-collapsed key, and that asymmetry is the point (found by Mark,
  0.3.4 — "Chute Section" offered twenty identical ways to make it).** In the collapsed
  (expansion) path the outputs are themselves variants of one ingredient row — a chute section
  in twenty metals — and vanilla authors those per variant rather than with a named wildcard,
  so each is made of its own metal plate and every one scores a different signature. Pattern
  and size already separate genuinely different recipes there. The general rule: a signature
  that distinguishes materials is correct only where output identity is already pinned to one
  page; wherever grouping is deliberately collapsing across variants, it is the explosion.

  `.tallybook recipes` reports every multi-recipe item in the connected world, and doubles as
  the explosion alarm: a healthy modded world lists dozens, not thousands. Use it rather than
  reading mod zips — which recipes exist is a fact about what the *server* sent.
- **A group only knows its `Materials` once `BuildRequirements` has run on it**, and the only
  group that happens to is the one in use — so any screen listing *alternatives* must build
  them first or it prints "?" for every option but the current one. Materials are the entire
  basis for choosing between recipes; a chooser that cannot state them is worse than no
  chooser.
- **The registry holds pseudo-recipes that consume their own output (found by Mark, 0.1.0
  testing).** `slabmode/*.json` recipes exist only to flip a slab's placement attributes:
  1 glass slab → 1 glass slab. Same family: chiseled-block combining, armor repair. They
  share an output code with the real recipe, tie or win the cheapest-representative choice
  (slabmode loads before slabs alphabetically), and then the pin claims "to craft a glass
  slab you need a glass slab" with no saw in sight. `RecipeProbe.EnsureIndex` drops any
  recipe whose consumed ingredients include its own output code — exact code equality only,
  so variant conversions (dye white wool red) survive.
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

## Architecture (who talks to whom)

`TallybookModSystem` wires everything; gameplay code never touches the registry or save file
directly. One-way flow:

- `RecipeProbe` — registry index (built per world), variant-group collapsing, expansion
  lookups, `InventorySnapshot` (one pass over carried slots per recount).
- `TallyTree` — pure math: deficit scaling, factor propagation, cycle guard, leaf
  enumeration, expansion (de)serialization. No API types beyond the data model.
- `PinStore` — the list + per-world JSON persistence + recipe preference memory.
- `TallyService` — the only mutation funnel. Every store change triggers exactly one
  `RecountAll()`, which fires `OnCountsChanged` only when a **signature of all visible facts**
  (numbers AND structure: recipe choice, expansion shape) actually changed. The dialog and
  HUD subscribe to that single event — they can never observe a structural change with stale
  numbers, and they never redraw for a no-op.
- `GuiDialogTallybook` (L) / `HudTallybook` (K) — surfaces. The dialog uses a
  recompose-everything pattern plus a **typing grace period**: recomposes steal focus, so
  live recounts defer up to ~2s while a count field is being typed
  (`restoringInputs` guards the SetValue→callback feedback loop — without it every recompose
  looks like typing and defers the next update forever).

GUI lessons learned the hard way — do not relearn these:

- **Never align a HUD dialog with EnumDialogArea corner alignments.** Vanilla's coordinate
  overlay re-stacks itself below the first other RightTop-aligned composer every 250ms and
  the two chase each other forever. Position absolutely (`EnumDialogArea.None` +
  `WithFixedPosition`) and re-anchor on frame/scale change (1s tick).
- Dispose a replaced `SingleComposer` via `RegisterCallback(..., 250)` — the old composer may
  still be mid-iteration in the event loop that triggered the recompose.
- Set `ignoreNextKeyPress = true` in `OnGuiOpened` — the opening hotkey's own char event
  otherwise lands in the first text input.

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

## The quest catalogue — read the dialogue files, don't rely on capture

**`QuestScanner.QuestCatalogue()` is the authority on what every fetch errand is, and it
should be reached for first.** Built once per world from every `config/dialogue/*.json`, it
holds one `QuestDef` per errand: the NPC who asks for the goods, the item, the quantity, the
maps that come with it, its gates, and the briefing. It is **static content**, so it is
available whether or not this client was watching when the errand was accepted, and whether or
not anything about it survived in the save.

This exists because the alternative kept failing. Everything an errand needs used to come from
what happened to be captured at accept time — map names, briefing, giver — and each of those
was lost independently: traders' dialogue files are not named after the trader so the
name-based lookup missed them; a save that lost its pins lost the map names with them, after
which re-reading the locator map could never restore the destination because there was nothing
left to recognise the waypoint by (found by Mark). Derived data belongs to the files. Only
things the files genuinely cannot know should live on the pin.

- **`DefFor(giver, itemCode, quantity)`** matches a pin to its definition, falling back from
  giver+item+quantity to item+quantity to item. The giver's name is the strongest signal and
  the weakest link — it comes from a live entity, so it can be blank, renamed or lost.
- **The catalogue must never be used to *offer* anything.** It deliberately does not filter by
  gate satisfaction, so half of it has not been offered to this player and surfacing it would
  spoil content the game is withholding. Use it to *fill in* an errand the player already has.
  Deciding what is live stays `LiveVillageQuests()`, which checks gates properly (and requires
  a positive one — see below). `DefIsLive` reads player scope only and fails toward "not live".
- **What the files cannot give you: world coordinates.** Dialogue describes *which map* leads
  somewhere, never where anything is. Positions come from standing next to an NPC
  (`SaveFile.NpcPlaces`, applied to any position-less quest pin as you walk past) or from the
  waypoint a read locator map creates, matched by the map's name — which is why the names are
  worth recovering.
- **`.tallybook quests`** ties the whole thing out: every errand in the world's dialogue with
  its item, giver, maps, gate state and tracked status. A command rather than a screen, for the
  spoiler reason above.
- **Maps are known per dialogue *file*, never per errand — do not tie them to one (found by
  Mark, twice).** `MapsGivenBy` reads a file's `triggerdata` handouts, and a file covers
  several unrelated quest threads: Agnieszka takes iron ingots at her forge and separately
  gives out the map to Tobias' cave. Attaching a file's maps to its fetch errands sent her
  errand across the world, and then sent *every* errand to the Devastation. `MapTargetFor`
  therefore returns **the quest giver, always**, or null when we have never stood next to
  them. The destination is not lost — reading a locator map puts vanilla's own waypoint on the
  map, which is where it belongs. The general rule: the catalogue is authoritative about what
  it actually records; a relationship it does not record must not be inferred because it would
  be convenient.

## Villager errands (quest tracking)

There is **no quest system** in the game to query — quests *are* dialogue. A fetch request is
a condition on the answer that hands the goods over, in the NPC's `config/dialogue/<name>.json`:

```
{ variable: "player.inventory", isValue: "{type:'item', code:'hide-raw-small', stacksize:10}" }
```

so nothing parses prose. `QuestScanner` reads it: find the conversable entity whose public
`Dialog` field is open, read its resolved `dialogueLoc` (private field, via Harmony
`AccessTools`) because villagers map entity code → file through `dialogueByType`, load that
asset, and collect `player.inventory` conditions.

- **No patch on the conversation UI, deliberately.** An earlier version injected a "track
  this" link via a Harmony patch on `GuiDialogueDialog.EmitDialogue`; it was removed in
  favour of `QuestWatcher` polling while a conversation is open. Villager dialogue is story
  content and a mod that can break a conversation can cost progress that cannot be
  recovered — and no detection of the *moment* of acceptance is needed anyway, because
  accepting is exactly what flips the gate, so the next poll simply sees a request that is
  live and was not before. Do not reintroduce the patch.
- **Re-adding must stay possible (Mark).** The gate stays true for the whole quest, so a
  guard is needed or unpinning mid-conversation is undone half a second later — but that
  guard lives in `QuestWatcher` for **one conversation only** and is never persisted. Walk
  away and come back and the errand is offered again; a permanent "already declined" memory
  makes an unpin unrecoverable. Setting an errand aside for good is what *unchecking* is
  for, which is why the auto path calls `Store.Add(..., activate: false)` — it must never
  re-check a parked pin.

- **The other conditions on the same answer are the quest's gates**, and **two scopes matter**:
  - `player.*` — village quest state (`gerhardtqueststarted`), via
    `VariablesModSystem.GetPlayerVariable` (client-side; the server syncs `VariableData`).
  - `entity.*` — state belonging to the NPC in front of you, via
    `GetVariable(EnumActivityVariableScope.Entity, name, npcEntity)`. **This is how traders
    hold a task**: the treasure hunter sets `entity.requestbronze` when he asks for a pickaxe
    and `entity.bronzereceived` when you hand it over. Reading only player scope made every
    trader errand invisible — the gate could never be met, so it was silently skipped.

  Strip the scope prefix. Anything else stays unmet.
- **Vanilla 1.22.6 fetch quests, for reference** (all four, found by scanning
  `config/dialogue/*.json` for answers carrying both a `*queststarted` gate and a
  `player.inventory` requirement): Agnieszka 8×`ingot-iron`; Gerhardt 10×`hide-raw-small`;
  Gerhardt 1×`flower-wilddaisy-free` (gated on **`wallqueststarted`**); Kat
  1×`bread-rye-perfect` (gated on **`beataqueststarted`**). Note the last two: **the NPC you
  hand items to is not always the one who asked.** Quest chains are agnieszka / gerhardt /
  beata / wall. Everything else that sets a variable is bookkeeping (`hasmet*`, `heard*`).
- **Requirements sharing a gate set are alternatives, not a list.** Better Ruins' salt trader
  accepts an andesite *or* basalt *or* peridotite quern for one errand, each written as its
  own answer line with identical gates. Adding them all demands three querns for a task that
  wants one. `QuestScanner.Scan` groups by gate signature, tracks the first, and records the
  rest as an "any of these will do" note. Content mods use exactly the vanilla trader
  mechanism (`entity.<name>_request<thing>` + `player.inventory`), so reading entity scope
  covers them with no per-mod work — which is the §1 promise holding up.
- **Retroactive village pickup:** `LiveVillageQuests` scans every `config/dialogue/*.json`,
  keeps requirements whose gates are **all player-scope and satisfied**, and skips anything
  gated on entity scope (that state lives on an NPC that may not be loaded — traders are
  conversation-only by nature, not by choice). Offered **once ever** per errand
  (`SaveFile.OfferedQuests`), because it runs at every login and an unpinned errand returning
  each time would be its own bug; talking to the NPC still re-adds, that being deliberate.
  Locations come from `SaveFile.NpcPlaces`, a directory built by noting conversable NPCs you
  walk past — at load we know who wants what, never where they live.
- **The player's journal is readable client-side** (not yet used): `ModJournal` keeps a
  private `ownJournal` (`Journal.Entries` → `JournalEntry{ EntryId, LoreCode, Title, Chapters }`)
  and `DidDiscoverLore(playerUid, code, chapterId)` is public. So showing collected lore
  beside a quest is possible; *relating* the two is the open problem — nothing links a lore
  code to a quest chain, so any connection is a heuristic over codes and titles.
- **Not yet handled:** `player.inventorywildcard` (a wildcard-coded inventory condition, e.g.
  `hoovedwearables-middleback-saddle*` in treasurehunter.json) and `triggerdata` hand-over
  quantities. Both are real requirement sources this scanner ignores.
- **A bare `player.inventory` condition is NOT an errand — require ≥1 satisfied gate**
  (found by Mark, 0.1.0 testing). The game uses the identical condition for **prices** and
  for "do you have the thing" checks: Tad's healing costs one gear, expressed exactly like
  Gerhardt's ten hides but with no other conditions at all. Accepting nothing and refusing
  the service still left it on the list, because there was never an acceptance to detect —
  and `Enumerable.All()` over an **empty** gate set returns true, which is how it slipped
  through. A real fetch quest is tied to quest state; a shop price is not. Also note pins
  merge by item and quest adds use `setCount: true`, so one bad capture of "10 gears"
  elsewhere silently raises every other gear requirement to 10 — false positives here do not
  stay local.
- **Unevaluatable gate ⇒ not met.** The dialogue file also describes quests this player has
  never been offered; surfacing those spoils content the game is deliberately withholding.
  Fail toward "no link", never toward a spoiler. Same reason inverted `player.inventory`
  conditions are ignored — "must NOT be carrying" is a state check, not a shopping list.
- **A waypoint with a blank title crashes the client (found by Mark, 0.3.4).** Hovering it on
  the world map makes vanilla build hover text of zero width and hand that to Cairo, which
  throws `Image surface width and hight must be above 0` from `GuiElementHoverText.Recompose`
  — a stack trace containing nothing of ours, on mouse-move, arbitrarily long after the
  marker was placed. `NpcName` comes from `Entity.GetName()`, which can be blank, and the
  placing guard tested `!= null`. Two lessons: **a null check is not an emptiness check for
  anything that came from the game**, and text that leaves the mod as a *command argument* is
  an outward action — sanitise it (`QuestWaypoints.SafeTitle`, trimmed and newline-free,
  since the title is rest-of-line) the same way you would validate a write. `.tallybook
  blankmarkers [remove]` finds ones already planted, by us or anything else.
- **Waypoint syntax** (confirmed working, Mark): `/waypoint addati <icon> <x> <y> <z>
  <pinned> <color> <title>` — icon `x`, **decimal** coords, hex colour (`#ff3f33`), title is
  rest-of-line. Format coords with `InvariantCulture`: a locale writing `131,5` splits one
  argument into two and shifts every argument after it. A client-only mod cannot write
  waypoints directly (`WaypointMapLayer.AddWaypoint` needs an `IServerPlayer`), so the chat
  command is the route; the NPC position is also stored on the pin so losing the waypoint
  never loses the way back.
- **Quest pins are counted, never decomposed.** `TallyService.Resolve` skips recipe lookup
  entirely when `QuestGiver != null`. Agnieszka's 8 iron ingots pulled in the *chisel an
  iron anvil back into ingots* grid recipe, so the errand sprouted an "Iron anvil 0/1" row
  and a tags-only chisel row — which read as a demand for an anvil the player lacks and,
  worse, as a preview of an unoffered quest stage (Mark). An errand is a fetch; the real
  acquisition path for ingots is smelting, which is not a grid recipe and could not be shown
  anyway.
- **Never render a raw wildcard as a name.** Tag-matched ingredients (`tags: ["tool-chisel"]`)
  have no `ResolvedItemStack` and a `Code` that reads `*:*` — which is exactly what one row
  displayed. `IngredientName` falls back to the author's `Name` field ("any metal").
- **A wildcard with no `name` field is NOT expanded by the game** (found by Mark: the wooden
  table read "Any suitable item 0/7" with no icon). Bookshelf's `{code: "plank-*", name:
  "wood"}` is a *named* wildcard and expands into one resolved recipe per wood, giving real
  codes to name and draw; table's bare `{code: "plank-*"}` stays one recipe holding a matcher
  and nothing concrete. Counting still worked — `SatisfiesAsIngredient` does not care — but
  there was nothing to display. `RecipeProbe.ResolveVariants` asks the world which
  collectibles the matcher accepts (wildcard-match the *code* first: the block list is tens of
  thousands and building each stack to ask properly is far slower), keeps up to 30 for icons
  and the true count for the label. Done once per row; the answer cannot change mid-session.
- **Waypoints act on transitions and are remembered on the pin — never reconciled, never on a
  tick.** `Pin.WaypointPlaced` (persisted) is the *only* thing that decides whether a marker
  gets placed. The first version instead asked the map which markers existed and added the
  missing ones on a timer; the client's waypoint list read back empty, so "missing" was always
  true and it planted a marker every few seconds until the player had **fifty** (Mark). The
  lesson generalises: **never drive an outward, repeatable action from a check that can fail
  quietly, on a schedule.** A flag that flips once cannot spam even when every read fails.
  Unpinning is caught via `PinStore.OnPinRemoved`, which fires while the pin can still say it
  had a marker. Removal is by **current index** into `WaypointMapLayer.ownWaypoints` (falling
  back to `Waypoints`), matched on title+position at the moment of removal — never a
  remembered index, which shifts as other waypoints come and go. `.tallybook clearmarkers`
  removes all of ours, highest index first so removals cannot shift each other.
- **Map centring — the working recipe:**
  ```csharp
  foreach (var compo in mapMgr.worldMapDlg.Composers.Values)
      if (compo?.GetElement("mapElem") is GuiElementMap m) m.CenterMapTo(pos);
  ```
  after `ToggleMap(EnumDialogType.Dialog)` and a ~250ms delay. The element **has a public key,
  `"mapElem"`** — use `GetElement`, and apply it to *every* composer rather than choosing one.
  Do not gate opening on `IsOpened` alone: an open minimap satisfies it, so also require
  `worldMapDlg.DialogType == EnumDialogType.Dialog`.
  Two dead ends, both of which cost a testing round each (Mark):
  - Reflecting into the dialog's private `fullDialog` composer and matching the element **by
    type** finds *an* element that is not the rendered one — the map opens and stays on the
    player, silently.
  - "Did it work?" cannot be answered with *does the view contain the target*: a zoomed-out
    map contains everything, so the check passes instantly and any retry is skipped. If a
    check is wanted at all, compare the view's **centre**.
- **The briefing text is recovered from the dialogue graph, not the live conversation.**
  `QuestScanner.Briefing`: the component whose `setVariables` sets the gate variable is the
  *accepting* step; the component with a `text[].jumpTo` pointing at it is the choice; the
  component whose `jumpTo` is that choice is the NPC's actual pitch ("Can you bring me some
  small raw hides? Say ten of them?"). Resolve `text[].value` through `Lang.Get`, skip
  player-owned components, and skip results equal to the key (that is Lang saying it has no
  translation). Kept on `Pin.QuestText`. Doing it this way means saving the conversation cost
  no patch on the conversation.
- Quest requirements become **ordinary pins** (`Pin.QuestGiver`), so tallying, the HUD, the
  table and persistence are all reused. `Store.Add(..., setCount: true)` raises an existing
  pin to the requested count instead of adding to it — an errand needs exactly 10, and
  pinning it twice must not ask for 20.

## Persistence — the list is the player's work, not our cache

- **Never delete a pin because it failed to resolve (found by Mark, 0.3.4 — the entire list
  vanished).** `Load` used to `RemoveAll` pins whose code the world did not know, reasoning
  that an unnameable row is a mystery rather than a reminder. That is right about the row and
  catastrophic about the file: **"the world does not know this item" and "does not know it
  *yet*" are indistinguishable at load time**, so one early load deleted everything and the
  next `Save()` made it permanent. Unresolved pins are now kept and retried from `RecountAll`
  via `PinStore.RetryUnresolved`; the cost is a row showing a bare item code for a tick.
  The diagnostic signature of this failure: `Pins: 0` while `QuestHistory`/`OfferedQuests` in
  the *same file* are intact — nothing resolves those, so only pins were destroyed.
- **`Save()` refuses to write before `Load()` has run** (`loaded` flag). Quest arrival, history
  updates and map-site discovery all write, and any of them firing early would persist an empty
  list over a real one.
- **Emptying the list keeps a `.bak`.** A player really can clear their list, so it cannot be
  forbidden — but it is also exactly what a failed load looks like, and one file copy on that
  rare transition is the difference between an annoyance and unrecoverable loss.
- **Everything on `Pin` that is derived carries `[JsonIgnore]`.** Adding a computed property
  without it silently changes the save format.

## Design invariants — do not "fix" these

These are deliberate and each one has a failure mode behind it (spec §2, §2a, §4):

- **A recipe existing does not mean it is how the item is obtained.** `Pin.GatherOnly`
  counts an item without decomposing it; Expand/Collapse on the pin row toggles it. Only
  handbook pins start expanded — pinning there is an act of reading a recipe (Mark) —
  everything else starts as counting. Iron ingots are the case that forced this: their sole *grid* recipe is
  chiselling an iron anvil back into ingots, so a decomposed ingot pin demands an anvil the
  player does not have, while smelting — the real source — is not a grid recipe and cannot
  be shown. Errand copies (`Gather`) start gather-only for the same reason.
- **Never craft for the player, and never write to their inventory.** Auto-crafting was
  investigated and rejected (Mark): "I don't want to automate the game into boringness." The
  mechanism exists — `InventoryBase.ActivateSlot` / `IInventoryNetworkUtil.GetFlipSlotsPacket`
  plus `SendPacketClient` drives the same moves dragging does — so this is a **product**
  decision, not a technical limit, and finding that it is possible is not a reason to revisit
  it. Two supporting reasons: everything here only ever *reads*, so the worst bug to date is a
  wrong number, whereas a bug that writes scatters or loses items and destroys the one promise
  the mod makes; and automated inventory manipulation is what anti-cheat tooling looks for on
  someone else's server. The handbook already shows the recipe, and a row's Handbook button
  goes straight there.
- **No automatic recursion.** A pinned item shows its *direct* ingredients only. Expansion
  is always a deliberate player action with a recipe choice attached. Auto-expansion hits
  recipe cycles, per-level recipe-choice explosions, and silent wrong guesses — every one of
  those makes the list lie. Permanently rejected, not a backlog item.
- **HUD shows leaves only.** Expanding a node moves it from "gather this" to "craft this
  from the things below"; intermediates belong in the dialog's tree, not the HUD totals.
- **Deficit-based scaling**, not gross requirement:
  `craftsNeeded = ceil(max(0, parentNeeded − parentHave) / recipeOutputQuantity)`. This
  applies at the **root** too: a pin's own carried count reduces its ingredient demand.
- **Every pin tracks the pinned item itself** (`Pin.SelfNode`, built from the stack in
  `TallyService.Resolve`). This is what makes recipe-less items — ore, hides, soil, a
  villager's fetch request — real trackable goals rather than inert reminders, and it is
  where a pin's `Have` comes from. Self-counting is page-code exact (`Requirement.SelfPageCode`
  → single lookup in `InventorySnapshot`, which is keyed by page code and carries the bare
  code alongside for code-level ingredient matching): owning a 5-plank bookshelf must never
  mark the 8-plank pin as had, since that would zero out its whole ingredient list.
  Self-nodes are deliberately **not** emitted as HUD gather rows — the pin header already
  draws the same have/needed, and emitting both printed the identical line twice.
- **Event-driven inventory counting, never per-frame polling.** The instant green-flip when
  you pick up the last board is the core loop; degrading it to a poll guts the mod.
- **Carried inventory only** — no nearby-chest scanning. The question is "what do I have on
  me", and answering a different question dishonestly is worse than not answering. The one
  extension is `IncludeMountBags` (**off** by default, opt-in in the Options screen): bags on
  an animal *you own* and are near are arguably still on you.
  - **Ownership, never proximity.** `EntityBehaviorOwnable.IsOwner(EntityAgent)` is public and
    ownership syncs to clients (`ModSystemEntityOwnership.StartClientSide`), so on a shared
    server a friend's elk is correctly excluded. Counting any nearby container instead would
    be exactly the nearby-chest scanning this section rejects.
  - **Two hops to the goods:** `GetEntitiesAround` filtered by ownership (or being ridden) →
    behaviors of type **`EntityBehaviorAttachable`** (which `EntityBehaviorRideableAccessories`
    extends) → `.Inventory` slots hold the *bags*, and each bag's contents come from
    `IHeldBag.GetContents(bagstack, world)`, because a bag stores its contents inside its own
    itemstack. Match `Attachable`, **not** its parent `EntityBehaviorContainer` — the parent
    also covers an animal's mouth inventory and `EntityBehaviorPlayerInventory`, and counting
    those would put items in the totals that the player cannot reach. Bags on the ground or in
    ground storage are unreachable from here by construction, which is the intent.
  - **The one path that cannot be event-driven.** Moving something inside a bag raises no slot
    event we can subscribe to, and an animal wandering in or out of range changes the answer
    with no event at all — so it recounts on the 1s tick, gated on the option being on *and*
    an owned animal actually being nearby. Everywhere else stays event-driven.
  - Whether the client is told the contents at all is not guaranteed; it counts what it can
    see and never errors.

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
- **Stack identity & attributes:** `GuiHandbookItemStackPage.PageCodeForStack(ItemStack)`
  (static, Vintagestory.GameContent) is the handbook's page identity — code plus
  distinguishing attributes, minus `GlobalConstants.IgnoredStackAttributes`. The reverse
  direction works too: `GuiDialogHandbook.OpenDetailPageFor(pageCode)` (instance found via
  `capi.Gui.LoadedGuis.OfType<GuiDialogHandbook>()`) opens the handbook on that page.
  **Derive that page code from the stack, never from `Pin.Key`** — a key carries the quest
  giver (`…|for:Agnieszka`) so an errand and a personal goal can coexist as separate rows,
  and passing it to the handbook silently opens nothing (found by Mark). `OpenDetailPageFor`
  returns a bool; honour it rather than assuming the page exists.
- **The handbook is missing from `Gui.LoadedGuis` until first opened** (found by Mark: Book
  failed with "handbook is not available" until H had been pressed once). That list holds
  dialogs registered with the GUI manager, and the handbook is not wired in until its first
  open — but `ModSystemSurvivalHandbook` has built the instance long before. `HandbookPin.FindDialog`
  falls back to that private `dialog` field, and `OpenLikeThePlayerWould` triggers the game's
  own handbook hotkey handler so first-open setup happens by the game's hand, not ours; the
  page is then selected a tick later, once registration has happened. Attribute
  persistence: `(TreeAttribute)stack.Attributes` → `.ToJsonToken()` (instance) and
  `TreeAttribute.FromJson(string)` (static, returns `IAttribute` — pattern-match to
  `ITreeAttribute`); `ItemStack.Attributes` has a public setter. `ITreeAttribute` itself has
  no `ToJsonToken` — cast to the concrete `TreeAttribute`.
- **Item icons in GUIs:** subclass `GuiElement`, override `RenderInteractiveElements(float)`,
  add via `composer.AddInteractiveElement(...)` (static elements only get cairo-composed
  once — no per-frame render). Draw with `capi.Render.RenderItemstackToGui(slot, x, y, z,
  size, color, ...)` — the `ItemStack`-direct overload is **obsolete** in 1.22.
  **The slot must have an inventory: `new DummySlot(stack, new DummyInventory(capi))`.**
  A bare `DummySlot` crashed the client outright on a raw hide (found by Mark): drawing a
  **perishable** item makes the renderer ask for its transition state, which dereferences
  `slot.Inventory`. Non-perishable items never hit that path, so this is invisible until
  someone tracks food or hides. Anything thrown from a render override kills the client, so
  wrap the draw in try/catch and latch it off — cosmetics are never worth a crash.
  `Bounds.renderX/Y` are the element's live screen coords.
- **HUD anchoring:** `capi.Gui.OpenedGuis` is a `List<GuiDialog>`; any dialog's on-screen
  rect comes from `dlg.Composers.Values` → `composer.Bounds`
  (`absX/absY/OuterWidth/OuterHeight`, in real pixels — divide by `RuntimeEnv.GUIScale` for
  GUI units). The Tallybook HUD dodges **every** open HUD-type dialog in its column (top
  half of screen only), not just the minimap (`GuiDialogWorldMap`, `DialogType == HUD` when
  it is the corner minimap) — vanilla stacks the coordinates and clock overlays below the
  minimap, so dodging only the minimap still overlapped them (found by Mark in 0.1.0
  testing).

## Release flow

Stage `dist/tallybook_X.Y.Z.zip` into `%APPDATA%\VintagestoryData\Mods\` (remove older
tallybook zips) for local/friend testing first. Publish only on explicit go-ahead: dated
CHANGELOG entry, README version refs, commit, tag `vX.Y.Z`, push,
`gh release create vX.Y.Z dist\tallybook_X.Y.Z.zip --title "Tallybook X.Y.Z"`. ModDB upload
is manual. **Run `.\tools\compat-test.ps1` and `.\tools\version-sweep.ps1` before every
release.**
