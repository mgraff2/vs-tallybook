# Vintage Story mod playbook

Transferable lessons from building Tallybook. Everything here is generalized: nothing depends
on what that mod does. Copy the relevant sections into a new mod's `CLAUDE.md`, adjust the
specifics, and delete what does not apply.

Each rule earned its place by costing something. Where a rule looks fussy, the failure it
prevents is stated — that is the part worth keeping, because a rule without its failure gets
"cleaned up" by the next person.

---

## 1. What to copy on day one

| From | To | Notes |
| --- | --- | --- |
| `tools/compat-test.ps1` | `tools/` | Portable as-is. Only the companion set needs editing. |
| `tools/version-sweep.ps1` | `tools/` | Portable as-is. Update the `-Versions` default. |
| `.gitignore` entries for `tools/compat-cache/`, `tools/server-cache/`, `dist/` | | Both caches are large and re-derivable. |
| Sections 2–9 below | `CLAUDE.md` | Trim to what the mod actually does. |

Both scripts discover the project as "the folder under the repo root containing a
`modinfo.json`" and read the modid, version and assembly name from there. They do not name a
mod anywhere.

---

## 2. Build

The system `dotnet` may be an older SDK that refuses the game's `net10.0` references. Build
with the user-scoped SDK:

```
& "$env:USERPROFILE\.dotnet\dotnet.exe" build <Project>\<Project>.csproj -c Release
```

Game references resolve from `%APPDATA%\Vintagestory`.

---

## 3. Testing — two gates, both mandatory

### Gate 1: compat matrix, after any code change, before any commit

```
.\tools\compat-test.ps1
```

Builds the zip, then boots a headless dedicated server once per mod combination (solo, +each
companion, all together) and fails on any `[Error]`/`[Warning]`, a wrong mod count or load
order, or a violated marker.

### Gate 2: game-version sweep, before every release

```
.\tools\version-sweep.ps1
```

`modinfo.json` declares a `game` version, and that is a promise to every player on every patch
release in that line. The sweep builds the zip **once** and runs the whole matrix against a
real server for each patch version, downloaded and cached. One artifact, N servers — that is
the claim being tested.

When a new patch ships, append it to the `-Versions` default. The CDN 404s on versions that do
not exist, which is how you find the current latest.

### Things that made these gates lie, and the fixes

- **`exit 0` at the end of `compat-test.ps1` is load-bearing.** The sweep reads
  `$LASTEXITCODE`, which only native commands and `exit` set. Without it, a `-SkipBuild` run
  that never invokes dotnet leaves a stale code behind and a fully passing matrix is reported
  as all-FAIL. (This happened: per-version logs said PASSED while the summary said FAIL.)
- **`SETUP` is not `FAIL`.** Distinguish "the mod failed" from "this version could not be
  tested". A half-extracted server package boots without its worldgen assets, floods the log
  with `[Error]`s that have nothing to do with you, and looks exactly like a broken mod.
- **Verify extraction against the archive's own entry count** and leave a completion stamp, so
  a partial or interrupted extract is never silently reused. Do not use `Expand-Archive` — it
  was caught truncating a ~9600-file archive to ~1400 files without raising an error.
- **Key temp data paths by `$PID`.** A hand-run test and a running sweep otherwise delete each
  other's directory mid-boot, which reports as "server did not start" and looks like a mod
  failure.
- **A transient failure must be re-run, not assumed.** If a combo fails with an empty log and
  passes on re-run, say so out loud rather than quietly re-rolling until green.

### Choosing companions

Derive the set from the mod's *real* interaction surface — hotkeys, HUD corners, dialog space,
per-world data files, the registries it reads — not from another project's list. A
recipe-adding content mod is the right companion for anything that reads recipes; a HUD mod is
the right companion for anything that draws in a screen corner.

Source companion zips from the live `Mods` folder first, then `ModsByServer/`, then the mod DB
API. `ModsByServer/` is where a modded server's own mods land and is usually the only place
they exist locally — it is the realistic companion pool.

### What the headless test cannot see

Everything client-side: registry reads, input events, GUI, rendering. For a client-side mod
that is nearly all of it. Keep a **manual checklist in README.md** and run it before any
release that touches those areas. A green matrix is necessary and nowhere near sufficient.

---

## 4. Compat invariants worth pinning

- **Side discipline.** A client-side mod must contribute *exactly one* line to
  `server-main.log`: its entry in the `Mods, sorted by dependency:` line. A second mention
  means server-side code started running — `ShouldLoad(EnumAppSide.Client)` or
  `"side": "Client"` was weakened. Pin this as an exact count, not a "should not appear",
  because path echoes will fool a naive match.
- **The assembly must still load server-side** even when it never runs there:
  `server-debug.log` must show `[<modid>] Loaded assembly` and
  `Instantiate mod systems for <modid>`. This is what catches an assembly that no longer loads
  against a new game version — the single most likely way a patch release breaks a mod.
- **Prefer dynamic, nameless cross-mod support.** Read whatever the server pushed into the
  client's registries and content mods work with zero compat patches. If you ever must add an
  `api.ModLoader.IsModEnabled(...)` branch, also add an **exact-count** log line at the
  registration site (`"[<modid>] X detected: N somethings registered"`) and pin it in the
  compat test as a `require` marker for combos with X and a `forbid` marker for combos
  without. An upstream change that silently breaks the integration then changes the count and
  fails the test.
- **Cross-mod grid recipes must not go in `recipes/grid/`** — the vanilla loader logs an
  `[Error]` when an ingredient's mod is missing. Register them from code, gated on
  `IsModEnabled`, with a count marker as above.

---

## 5. API discipline

**Verify against the real assemblies at implementation time. Never trust memory, and never
trust notes like these, for API shape.**

`VintagestoryAPI.xml` next to the game DLLs carries doc comments but only member *names*, and
only for documented members — absence there proves nothing. To get full signatures, use a
throwaway `net10.0` console app referencing `VintagestoryAPI.dll` / `VSSurvivalMod.dll` that
calls `Assembly.LoadFrom` and `GetMembers`. Note:

- `Assembly.Load` by simple name does not work; use `LoadFrom` with a full path.
- PowerShell cannot do this — no `MetadataLoadContext`, and it cannot load the assemblies.
- Use `BindingFlags.Public | Instance` **without** `DeclaredOnly` when you care about
  inherited members; a base class often holds the property you are looking for.

### Durable API facts (re-verify, but these held across 1.22.0–1.22.6)

- **Client commands are invoked with a leading `.`, not `/`.** Register the name with no
  prefix. A `/` prefix routes to the server, which for a client-only mod replies "No such
  command exists" — a message that looks exactly like the mod failing to load. Before chasing
  that, check `client-debug.log` for `Loaded assembly` and `Starting system:`.
- **Never gate on `args.ArgCount`.** Parsers consume the raw arguments while parsing, so
  `ArgCount` reads 0 inside a handler even when `args[0]` holds a value. Gating on it silently
  drops every argument.
- **`capi.Event.PlayerJoin` fires for other players too** — compare `PlayerUID` against
  `capi.World.Player.PlayerUID`.
- **Harmony ships with both the game and the dedicated server**, so a patch is available even
  where there is no registration hook. Prefer a postfix that only *appends* to what the game
  returned, catch and log a failure to apply, and never patch something whose failure costs
  the player progress that cannot be recovered.
- **Grid-recipe ingredient matching is two-step, and liquids live entirely in step two.**
  `SatisfiesAsIngredient` is only half the grid's check: `RecipeBase.MatchStackToIngredient`
  then calls `inputStack.Collectible.MatchesForCrafting(stack, recipe, ingredient)`, a
  virtual any collectible may override. `BlockLiquidContainerBase` uses it for liquid
  recipes, whose JSON names the *vessel* as the ingredient and hides the liquid in
  attributes: per-ingredient `recipeAttributes.requiresContent`+`requiresLitres`, or
  recipe-level `attributes.liquidContainerProps`. Anything that reproduces "would this
  craft?" from `SatisfiesAsIngredient` alone will call an empty bucket a bucket of water.
  Both attribute channels are round-tripped in `ToBytes`/`FromBytes` (verified by decompile,
  1.22.6), so clients see them; litres convert to portion items via
  `WaterTightContainableProps.ItemsPerLitre` (`BlockLiquidContainerBase.GetContainableProps`,
  static), contents read via `GetContent(stack)` (instance), and the content check itself is
  `JsonItemStack.Matches(world, contentStack)` — delegate to it rather than reimplementing.
- **Grid recipes are not the whole crafting surface.** Vanilla 1.22 produces real items in
  the cooking pot (`recipes/cooking/*.json` with a `cooksInto` output: acids, glue, potash,
  leather), and that registry is separately client-readable and fully resolved:
  `capi.GetCookingRecipes()` (`ApiAdditions` → `RecipeRegistrySystem.CookingRecipes`).
  `CookingRecipeIngredient.Matches(stack)` is the game's own matcher, `PortionSizeLitres`
  marks liquid ingredients. Anything reasoning about "how is this item made" from
  `capi.World.GridRecipes` alone silently misses these.
- **A synced field is only as good as EVERY packet variant that writes it.** The game's
  network packets are protobuf-shaped: a builder that omits a field sends ZERO, and client
  `UpdateFromPacket` methods copy unconditionally. `Packet_PlayerData` alone has three
  builders — full (`ToPacket`), sparse (`ToPacketForOtherPlayers`: no Deaths, no spawn), and
  a deletion stub — so `ClientPlayer.SpawnPosition` can genuinely read `BlockPos(0,0,0)` (the
  world corner) and `WorldData.Deaths` can read 0, at any time, meaning nothing. Vanilla
  never notices, because vanilla never reads those fields client-side; a mod that does must
  (a) decompile every `new Packet_X` construction site before trusting a field, and
  (b) guard reads with a credibility test and treat a non-credible value exactly like a
  failed read — it proves nothing, and the last known good state stands. Where a counter
  must survive clobbering, persist it monotonically (ratchet up on credible reads, bump on
  observed events, never lower). Also: the client handler DROPS player-data packets that
  arrive before blocks are loaded ("Startup sequence wrong" in the log), so "the server
  sent it" never implies "the client has it".

---

## 6. GUI lessons

- **Never align a HUD dialog with `EnumDialogArea` corner alignments.** Vanilla's overlays
  re-stack themselves below the first other corner-aligned composer on a timer, and the two
  chase each other forever. Position absolutely (`EnumDialogArea.None` + `WithFixedPosition`)
  and re-anchor on frame/scale change.
- **Dodge every open HUD-type dialog in your column, not just the one you know about.**
  Vanilla stacks several.
- **Dispose a replaced `SingleComposer` on a short delayed callback**, not inline — the old
  composer may still be mid-iteration in the event loop that triggered the recompose.
- **Set `ignoreNextKeyPress = true` in `OnGuiOpened`**, or the opening hotkey's own char event
  lands in the first text input.
- **A hotkey must not fire while the player is typing — in ANY mod's window.**
  `HotkeyType.GUIOrOtherControls` means "always available", which is what makes a hotkey work
  from inside the inventory and equally what delivers the press while a text field somewhere
  has focus. Someone naming a route in another mod's planner typed an L and got our window.
  Do not argue about whose dialog should have swallowed it first: the mod that *reacted* is the
  one in the wrong. Guard every handler with a screen-wide check —
  walk `capi.Gui.OpenedGuis` (then `LoadedGuis`), ask each composer for `CurrentTabIndexElement`
  ("the currently tabbed index element, if there is one currently focused") and test it for
  `GuiElementEditableTextBase`, which every editable field derives from. Return **false**, not
  true: the press is not yours, so leave it unhandled for whoever it belongs to.
  Then pay it back the other way: override `CaptureAllInputs()` to return true **while one of
  your own fields has focus** — the documented purpose of that override, Escape still works —
  so your text boxes do not fire other mods' hotkeys. Only while focused, never for as long as
  the window is open, or you have simply reversed the rudeness.
  Add a client-side GUI mod with a text field to the companion set when this comes up. It will
  not catch the bug (no headless server can), but it puts the case on the manual list where
  someone will actually type into it.
- **A recompose steals focus.** If the dialog rebuilds on live data, defer recomposes briefly
  while a field is being typed in — and guard the `SetValue`→callback feedback loop, or every
  recompose looks like typing and defers the next update forever.
- **Item icons need `new DummySlot(stack, new DummyInventory(capi))`.** A bare `DummySlot`
  crashes the client on a *perishable* item: drawing it makes the renderer ask for transition
  state, which dereferences `slot.Inventory`. Non-perishable items never hit that path, so it
  is invisible until someone tracks food. Anything thrown from a render override kills the
  client — wrap the draw in try/catch and latch it off. Cosmetics are never worth a crash.
- **Paginate by measured height, not by row count.** The moment any row can wrap to a variable
  number of lines, "N rows per page" silently runs off the bottom of the screen with no way to
  reach the rest. Walk the list against a height budget derived from
  `capi.Render.FrameHeight / RuntimeEnv.GUIScale`, and never break before a page's first row
  or one oversized row produces an endless list of empty pages.
- **One definition of "what is on this page".** Composing and any later input-restore pass must
  agree exactly; asking the composer for a control it never composed is unhealthy whether or
  not it throws.
- **The game can be paused during any of your GUI events — never call bare 2-arg
  `RegisterCallback` from one.** The handbook pauses singleplayer while it is open, and
  inventory dialogs, your dialogs and links inside the handbook all stay clickable
  underneath — so `SlotModified` and your own click handlers run while `IsGamePaused`. The
  2-arg `RegisterCallback` then logs an engine warning, and on a client with developer mode
  + extended debug it *throws on purpose* — a crash report with your mod at the top of the
  stack. Two replacements, chosen by what the defer is for (both present since 1.22.0):
  - A "next tick" defer that should still respond while paused (the click was the player's
    explicit act; a recount that keeps numbers live): `capi.Event.EnqueueMainThreadTask(
    action, code)` — dispatched every frame after the render loop, no pause gate.
  - Time-based housekeeping (delayed disposes, debounce timers): `capi.Event.
    RegisterCallback(handler, ms, permittedWhilePaused: true)` — the flag only suppresses
    the trap; delayed callbacks tick while unpaused only, so it fires at unpause, which is
    the point of housekeeping. `capi.World.RegisterCallback` has no such overload — route
    GUI callbacks through `capi.Event`.
- **The handbook's page index loads on a background thread — and is wiped and rebuilt every
  time any mod registers a hotkey.** `GuiDialogHandbook.loadEntries` (constructor, at level
  finalize) sets `loadingPagesAsync` and queues a thread-pool load; the survival handbook
  also wires `capi.Event.HotkeysChanged += loadEntries`, so rebuilds happen mid-session too,
  and heavy content mods make one take tens of seconds. Until the flag clears,
  `OpenDetailPageFor` misses and every `FilterItems` — including the one in `OnGuiOpened` —
  filters an empty list and renders a blank handbook that *stays* blank; nothing re-filters
  on completion. Anything that opens the handbook onto a page must first poll the
  (reflected) `loadingPagesAsync` flag — per frame, since the open handbook pauses
  singleplayer and a delayed callback would wait for unpause. Bound the wait by **wall
  clock** (`Environment.TickCount64`): a frame count expires in seconds at high FPS, and
  the in-world clock freezes while paused. On timeout, tell the player instead of
  half-acting; if they close the handbook mid-wait, stop. There is nothing to warm up: the
  game already starts loading as early as possible; the only correct move is waiting.

---

## 7. Persistence — the save file is the player's work

This section exists because ignoring it destroyed a user's data.

- **Never delete a saved entry because it failed to resolve.** "The world does not know this
  item" and "does not know it *yet*" are indistinguishable at load time. Deleting on that basis
  wiped an entire list, and the next save made it permanent — silently, because the failure
  path returned `false` rather than throwing. Keep unresolved entries, show what you can
  (a bare code is fine), and retry on a later tick.
- **Refuse to write before the file has been read.** Any number of code paths write; if one
  fires early it persists an empty in-memory state over a real file. A single `loaded` flag
  prevents the whole class.
- **Back up on the full→empty transition.** Emptying really is something a user does, so it
  cannot be forbidden — but it is also exactly what a failed load looks like. One file copy is
  the difference between an annoyance and unrecoverable loss.
- **Mark every derived member `[JsonIgnore]`.** Adding a computed property without it silently
  changes the save format.
- **Diagnostic signature of this failure class:** one collection empty while the others in the
  *same file* are intact. The intact ones are the ones nothing "resolves".

---

## 8. Outward actions

Anything that leaves the mod — a chat command, a marker, a message, a write — is an outward
action and needs different rules from an internal computation.

- **Never drive a repeatable outward action from a check that can fail quietly, on a
  schedule.** A reconcile loop asked the map which markers existed and added the missing ones
  on a timer; the read came back empty, so "missing" was always true and it planted a marker
  every few seconds until there were fifty. Drive outward actions from **transitions**, and
  record that they happened in persisted state. A flag that flips once cannot spam even when
  every read fails.
- **Sanitise anything that becomes a command argument.** A blank waypoint title crashed the
  client on hover (zero-width hover text → Cairo refuses a zero-area surface), from a stack
  trace containing nothing of the mod's, arbitrarily long after the marker was placed. Trim,
  strip newlines, require non-empty with a fallback.
- **A null check is not an emptiness check** for anything the game handed you.
  `Entity.GetName()` returns blank for a nameless entity.
- **Record the state before sending, not after.** If the command fails you would rather have
  no marker than retry forever.
- **Removal by current index, never a remembered one** — indices shift as other items come and
  go. Match on identity at the moment of removal, and remove highest-index-first so removals
  cannot shift each other.
- **"Did it work?" must not be answerable trivially.** A check of "does the view contain the
  target" passes instantly on a zoomed-out map, so every retry is skipped. Compare something
  that actually distinguishes success.

---

## 8a. Reading ANOTHER mod without depending on it

Sooner or later you want to see something a second mod owns. You can do it without referencing
its assembly, without patching it, and without breaking when it updates — if you take its
sources in this order:

1. **Its content files.** Anything a mod loads out of `assets/**` you can load too, through
   `capi.Assets`. This is the best source by a distance: it is the mod's own published contract,
   it survives its internal refactors, and it is there whether or not the player has done
   anything yet. Prefer `GetLocations(prefix)` + `TryGet(loc)` and filter yourself — the docs
   confirm prefix matching ("all asset locations that **begins with** given path"), which also
   means one read covers both `config/x.json` and `config/x/*.json` layouts.
2. **State the server already syncs.** `WatchedAttributes` on an entity reach every client that
   tracks it, so a mod storing per-player state there has effectively published it. Enumerate
   the tree for the keys you want rather than probing for names you guessed; the keys *are* the
   list. Watch for per-player keys: one without a uid suffix is shared by everyone on the
   server and cannot be attributed to your player.
3. **Its GUI, by reflection, while it is open.** Last resort, and the only place a mod names
   another mod's *type*. Find the dialog by `GetType().FullName` in `capi.Gui.OpenedGuis` **and**
   `LoadedGuis` (a dialog's presence in either is not guaranteed), then read private fields with
   `AccessTools`. Distinguish a MISSING field from an EMPTY value: empty is an answer callers
   may act on, a field that has been renamed is not, and conflating them turns an update into
   silent data loss.

And two things not to do:

- **Do not send on its network channel.** Check what its handlers actually do first: if the
  messages a client can send *write* (accept, complete, purchase), sending one to harvest state
  is acting for the player. There is usually no read-only request to borrow.
- **Do not patch its UI.** Poll while it is open instead. A patch that breaks someone's quest or
  trade window can cost progress that cannot be recovered, and polling gets the same data.

Then verify the reflection targets against the **shipped assembly**, not the source on GitHub —
`MetadataLoadContext` over the DLL out of its release zip lists the real field and property
names (see §5). And note what this does to your compat gates: an integration living entirely in
`StartClientSide` cannot have its marker pinned by a headless server test, so log the count at
world join, print it from a diagnostic command, and add the other mod to the companion set so
the *silence* check proves the integration never runs server-side.

Finally, a rule that only shows up once two systems describe the same kind of thing:
**an identity that is exact must never be matched by an identity that is fuzzy.** If your own
feature matches records by name because that is all it has, exclude the other system's records
from those matchers explicitly — otherwise an NPC who happens to share a name will archive,
merge or overwrite rows belonging to something entirely unrelated.

---

## 9. Product principles that generalize

- **Never write to the player's inventory or act for them.** Read-only means the worst bug is a
  wrong number; a bug that writes scatters or loses items. Automated inventory manipulation is
  also what anti-cheat tooling looks for on someone else's server. Finding that something is
  *possible* is not a reason to do it.
- **Answer the question actually asked.** Answering a different question dishonestly is worse
  than not answering.
- **Fail toward saying nothing, never toward a spoiler.** If a condition cannot be evaluated,
  treat it as unmet. Surfacing content the game is deliberately withholding is a bug with no
  error message.
- **Prefer a deliberate action to a clever guess.** Automatic recursion, auto-expansion and
  auto-selection all hit cycles, explosions and silent wrong answers. Every one of those makes
  the output lie, and a tool that lies occasionally is worse than one that asks.
- **Never present a raw registry object to the player.** Group it into what a player would call
  one thing, and never render a raw wildcard, code or key as a name.
- **Event-driven, not polled.** Coalesce bursts into one deferred recompute — moving one stack
  raises several events, and recomputing per event both wastes work and briefly displays a
  number that was never true. Reserve polling for the one or two paths that genuinely have no
  event, and gate those on cheap preconditions.
- **A destructive-looking diagnostic should report first and act on request.** List what would
  be removed, with enough detail to undo it, and take a `remove` argument to actually do it.
- **Derive from the game's content files; do not rely on having been watching.** If the assets
  describe something fully — quests, recipes, prices, structures — build the model from them
  once per world and match existing records to it. Capture-time data has as many failure modes
  as there are ways to miss the moment, and every one of them is silent. Keep on your own
  records only what the files genuinely cannot know (world coordinates, player progress). Two
  guards: match on substance rather than only on a name, since names come from live entities
  and go missing; and never use such a catalogue to *offer* or display content the player has
  not reached, because it contains everything, including what the game is deliberately
  withholding. Expose the tie-out as a command, not a screen.

---

## 10. Release flow

1. Run both gates.
2. Stage `dist/<modid>_X.Y.Z.zip` into `%APPDATA%\VintagestoryData\Mods\` (removing older
   copies) for local testing.
3. Publish only on explicit go-ahead: dated CHANGELOG entry, README version refs, commit, tag
   `vX.Y.Z`, push, `gh release create`.
4. Mod DB upload is manual.

Keep the CHANGELOG in the user's vocabulary, not the code's — if the game calls the item a
"blueprint" and the code calls it a "schematic", the changelog says blueprint.

### Screenshots: automate the tour, or they go stale

Refreshing mod-page screenshots is the release chore that never gets done, and stale shots
misrepresent the mod more than missing ones do. A command that walks your own surfaces and
writes one **stable, feature-named** PNG per shot turns it into one command in a real world;
stable names are what make replacing them on the mod page mechanical, and let you diff the
folder against the last upload. Pair it with a manifest listing each shot, how to stage it,
and which version last invalidated it — knowing *which* shots are now lies is the expensive
part, not taking eight pictures.

Four things that cost a round each:

- **`capi.Render.GrabScreenshot(w, h, scale, flip, alpha)` + `BitmapRef.Save(path)` is the
  whole capture** (public API). Call it from a renderer registered at `EnumRenderStage.Done`.
- **Never write to `GamePaths.Screenshots`.** It resolves to the user's Pictures folder,
  which on Windows is routinely redirected into OneDrive — mod working files then land in
  someone's personal cloud. Use `GamePaths.DataPath/ModData/<modid>/…`.
- **Crop to the window, not the screen** — the window is the subject.
  `GuiDialog.SingleComposer.Bounds` gives `absX/absY/OuterWidth/OuterHeight` in real pixels;
  scale that rect by the captured image's width ÷ `Render.FrameWidth` or SSAA shifts the
  crop. Crop by re-reading the PNG the game's own writer produced (SkiaSharp
  `Decode`/`ExtractSubset`/`Encode`, and it ships with the game) rather than slicing
  `BitmapRef.Pixels` — that keeps you out of guessing the buffer's channel order, whose
  failure mode is red and blue swapped in every shot.
- **Enforce the mod DB's size ceiling in code** (1920×1080 for VS): a 4K screen or a
  supersampled framebuffer sails past it, and the upload is where you find out. Cap every
  shot, cropped or not. For a surface whose height depends on content — a HUD with a row per
  item — give that shot a **fixed frame** (a region of exactly the output size centred on the
  window) instead of a tight crop, or the mod page gets a differently shaped picture every
  release; centring a fixed region also avoids resampling entirely in the normal case.
- **Which framebuffer is bound at a given stage is not knowable from the public API** (the
  game binds one explicitly using platform internals). So check each grab for being a single
  flat colour and report a blank run with a retry-at-the-other-stage hint, instead of writing
  a folder of black PNGs that read as a rendering bug.

And the discipline that makes the shots trustworthy: the walker **navigates, never edits** —
it opens screens and selects tabs, and touches no user data. The screenshots are then of a
real world, and a showcase run can never damage what it is photographing.
