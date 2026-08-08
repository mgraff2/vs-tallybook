# Changelog

## 0.3.4 — 2026-08-07

From an adversarial review pass (Fable) over the whole codebase:

- **A pin that had not yet resolved lost its expansion tree and recipe choice on the next
  save** — the pin survived (that was the wipe fix below), but saving wrote its empty
  in-memory state over the only copy of what it carried. Unresolved pins are now skipped when
  serializing derived state.
- **An errand asking for two things at once was read as offering a choice.** Conditions on one
  dialogue answer line must all hold — that is an AND — but they were pooled with the
  alternatives machinery, so "3 logs and 5 hides" tracked the logs and called the hides an
  acceptable substitute. Vanilla's quests are single-item, so only modded errands were
  affected.
- **Editing an errand's count could attach the wrong conversation to it, permanently.** The
  catalogue lookup fell back to "any quest for this item", and the pin's player-editable count
  was part of the key; the wrongly attached text then read as a finished transcript and
  blocked every future repair. The lookup now refuses to guess.
- **`.tallybook clearmarkers` un-did itself within seconds** — the cleared flags read as
  "marker missing" to the placement sync, which re-planted every one on the next recount.
- **Several performance sinks**: the full dialogue asset set was re-parsed on every dialog
  redraw and — for a quest whose text could not be recovered — once per second forever;
  briefings are now cached, including the not-found answer. A transiently unreadable quest
  variable also rewrote the save file every second until it read back.
- **Hardened recipe merging** against variant recipes authored with the same materials in a
  permuted grid, which could put one ingredient's variants into another's row; and the one
  unguarded tick handler now catches, so a modded item throwing from the game's own matcher
  degrades to a log line instead of a crash-per-second.

- **Fixed silent loss of your whole pin list.** A pin whose item the world could not identify
  at load was deleted, and the deletion was then saved over your file. But "this item does not
  exist" and "this item is not registered *yet*" look identical at that moment, so a load that
  ran a moment too early wiped everything — quietly, and with the quest history in the same
  file untouched, because nothing resolves that. Pins are now **kept** when they will not
  resolve and re-tried on every recount, so a registry that was not ready costs a tick instead
  of your list. Nothing is written before the file has been read at all, and if the list ever
  does go from full to empty, the previous version is kept beside it as `.bak`.
- **Fixed a crash: a quest marker with no title took the client down.** A waypoint whose title
  is blank makes the world map build hover text of zero width, and Cairo refuses to make a
  surface with no area — so the game died on mouse-move, nowhere near the mod that placed it.
  A quest giver's name comes from the entity, an entity can be nameless, and the guard before
  placing only ever checked for null. Names are now never blank at the source, the title is
  checked again at the moment of placing, and **`.tallybook blankmarkers`** lists any untitled
  waypoints already on your map (with `remove` to delete them) — whoever made them.

- **Alternative recipes are found automatically, whatever mod added them.** Whether two recipes
  count as a real choice is now decided by what they take, not by whether the mod's author
  filled in the optional `recipeGroup` field. Mods that re-add a vanilla item behind a
  schematic — Better Ruins, the airship mod — mostly leave that field alone, and their recipe
  was landing in the same bucket as vanilla's, losing the cheapest-recipe contest, and being
  dropped without a word. It now shows up as the second choice it always was, with no
  per-mod support code.
- **The choice says what you must be holding, not just what gets used up.** Two recipes can
  want identical materials and differ only in demanding a blueprint; without that line they
  read as identical twins. It also lists what each way is made of at all — before, only the
  recipe already in use knew, so every other option offered you a "?" to choose between.
- **A waypoint that names the quest giver counts as knowing where they are.** Reading "Map to
  Tobias' cave" drops the game's own waypoint at his cave, and a title carrying his name is
  the game saying whose place it is — so Tobias gets a Map button before you have ever stood
  next to him. Read live from your waypoints, so deleting one honestly returns the answer to
  "unknown".
- **Map goes to the person who wants the goods** — reliably, and nowhere else. An earlier
  attempt sent you to wherever a map the NPC hands out points, on the theory that Tobias
  giving you a map to the Devastation means that is where his errand takes you. Which maps an
  NPC hands out is known per dialogue *file*, and a file covers several unrelated quest
  threads: Agnieszka takes iron ingots at her forge and separately gives you the map to
  Tobias' cave, so her errand pointed across the world. Nothing in the game's files ties a
  particular map to a particular fetch request, so no tie is claimed. Reading a locator map
  still puts its own waypoint on your map, which is where that belongs.
- **Fixed a map that could not be closed.** Opening the full map while the corner minimap was
  showing left the map manager holding two dialogs over one slot; the result rendered oversized
  and its close button did nothing, so only Escape got out.
- **Errands are read from the game's own dialogue files, not from what was noticed at the
  time.** Every fetch errand in the world — who asks, for what, how many, which maps come with
  it — is now known from the content itself, so an errand fills itself in whether or not
  Tallybook was watching when you accepted it, and whether or not anything about it survived.
  Recovering the map names is what makes re-reading a locator map put the destination back:
  before, an errand that never recorded the name could not be pointed anywhere, however many
  times you read the map. Walking past a quest giver also gives their errand a location
  immediately rather than only at the moment it was taken on, so an errand with no location is
  one stroll from having one.
- **`.tallybook quests`** ties it all out: every errand the world describes, with its item,
  giver, maps, whether it is open to you and how far along you are. A command rather than a
  screen, because the full list includes quests you have not been offered.
- **An errand remembers where its map pointed.** A locator map's destination only existed for
  as long as its waypoint did, so deleting that waypoint by hand cost the errand its
  destination as well as its marker. Reading the map is still what establishes the place —
  nothing is claimed before you have read it — but once read it is kept. Markers themselves
  are unchanged: nothing puts one back on its own, and `.tallybook markers` is still how you
  ask for them by hand.
- **Errand quotes on the Side quests tab read as a transcript**, the way the History tab
  already showed them: `Gerhardt: "…"` and your own name against the line that prompted it,
  full width and wrapped over as many lines as they need. Squeezed into the item column they
  were cut off after a few words — which is exactly where the name and the thing being asked
  for live, so a quotation came out as a caption.
- **Quotes captured without a speaker get one back.** Errands from before quotes carried names
  are re-derived at login, but only for NPCs whose dialogue file is named after them — every
  villager, no trader. So Gerhardt and Agnieszka read correctly while a trader's errand stayed
  as bare paragraphs. Those are now attributed to the giver as they are drawn, which is honest:
  only the NPC's own words were ever kept.
- **Pages are measured, not counted.** Thirteen rows per page was fine when every row was the
  same height and wrong the moment a quoted conversation could be a dozen lines tall — the
  History tab in particular just grew until it ran off the bottom of the screen with no way to
  reach the rest. Both the list and the archive now fill a page to the height actually
  available and break there.
- **Talking to an NPC again now repairs an old quote, not just a missing one.** Errands
  captured before quotes carried speakers could only be re-derived at login for villagers
  whose dialogue file is named after them — which is every villager and no trader. Standing
  in front of a trader is the only chance their words get their speaker back, so it is taken.
- **`.tallybook recipes`** lists everything your world can make more than one way. Which items
  offer a choice depends on the recipes your server sent, so it is not a question that can be
  answered by reading mod files — and it doubles as the check that recipe grouping has not
  quietly fallen apart, since a healthy world lists dozens of items here, not thousands.

## 0.3.3 — 2026-08-07

- **Expand asks which recipe you mean**, when there is more than one way to make the thing.
  Each way is listed with what it yields and what it would have you fetch, and you pick one —
  instead of Tallybook choosing for you and leaving a "1 of 4" cycler to argue with, which
  answers a different question from the one you had.
- **Materials now carry their quantities.** The hunter backpack's four recipes differ by *how
  many* pelts and whether you get one backpack or two; listing bare ingredient names made four
  distinct recipes read as four identical ones.
- Items with only one recipe are unaffected — a chooser with a single entry is a dialog that
  wastes a click.

## 0.3.2 — 2026-08-07

- **Ingredients the recipe does not consume are no longer counted as materials.** They want
  *one*, present, however many times you craft, exactly as a tool does, so they sit with the
  tools now. Ingredients that are consumed but hand something back (`returnedStack`) are
  **not** in that group, though they look as if they should be: what comes back is often a
  lesser item — the hunter backpack takes a huge pelt and returns a small one — and treating
  that as a tool would claim one huge pelt makes three backpacks.
- **The recipe switcher says what each recipe is made of.** "1 of 2" tells you nothing about
  which one you mean to gather; hovering it now lists every alternative by its materials, with
  the current one marked.

## 0.3.1 — 2026-08-07 (superseded)
- Rolled into 0.3.2, which corrects the handling of returned ingredients.

## 0.3.0 — 2026-08-07

First public release. A crafting shopping list that tracks what you are gathering, the
errands villagers give you, and the story you have already finished.

- **HUD text size is a slider in Options**, and the HUD redraws as you drag it — the right
  size is the one that looks right, not a number you work out. Row height and icons follow
  the text, so shrinking it genuinely fits more on screen. Left alone it uses the game's own
  small-text size, read at runtime so it cannot drift from the rest of the interface.
- **Errands read as a transcript** — `Gerhardt: "…"` with your own name against the line that
  prompted it, since every villager line answers something you said. Closed by default, opened
  with a **+** beside the name, and remembered either way.
- **An errand says which map came with it** ("came with Map to the Devastation"), and once you
  have read that map, its **Map** button goes to the destination rather than the giver —
  reading a locator map is what puts its place on your map, so no extra bookkeeping is needed
  to know you have it.
- **Options** gathers the settings that change what the list shows or counts, one line each
  with a **?** carrying the explanation on hover.
- **Pin from the handbook.** Every item's handbook page gains an "Add to Tallybook" link,
  plus "Go to Tallybook" to close the handbook and open the list. Arriving from a list row's
  Handbook button also shows a "← Back to Tallybook" button beneath the handbook's own Back
  button — a separate floating dialog anchored to the handbook's live bounds rather than an
  element injected into its chrome, so the handbook's layout is never patched.
  Pinning again increments the count rather than duplicating the row. What gets pinned is
  exactly the page being viewed: recipe outputs that share an item code but differ by
  attributes (the four bookshelf shapes, each its own page and plank count) are treated as
  the distinct items they are — pin identity is the handbook's own page identity, and the
  pinned variant's attributes persist across relogs. Implemented as a Harmony postfix on
  `CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo` that only appends to the
  returned components — if the patch ever fails to apply, the handbook is unaffected and the
  mod logs why.
- **Management dialog (L), laid out as a table.** Fixed columns — icon, name (indented by
  tree depth), have/need, wanted count, actions — shared by every row type and under a
  column header, so a long list stays scannable down a column instead of being re-read row
  by row. Names too long for their column are truncated with "…" and carry the full text as
  hover help, so a cut-off row is never a dead end. Item icons on ingredient and tool rows as well as pins, matching
  the HUD. − / + steppers with direct numeric count entry, colour-coded status, tool presence
  rows, recipe switcher for items with several recipes. Unpinning is hold-to-confirm — hold the button for a second, through a visible
  countdown, release to cancel — never a dialog (stepping to 0 unpins instantly; clear-all
  keeps its confirmation screen). Long item names truncate with "…" instead of wrapping over
  the next row, in the dialog and the HUD both. A Handbook button on every pin row jumps
  back to the pinned variant's own handbook page. Per-pin checkboxes park items
  without losing them (state saved, excluded from HUD and counting; confirm-free because
  nothing is lost), with an Uncheck all / Check all bulk toggle; pinning a parked item from
  the handbook re-checks it.
- **Manual expansion tree.** Craftable ingredient rows carry an Expand button that unfolds
  that ingredient's recipe beneath it, sized to the parent's *deficit*
  (`ceil(max(0, needed − have) / recipeOutput)`), nested to any depth, with per-node recipe
  choice, per-world recipe preference memory, and a cycle guard that refuses to expand an
  item into its own ancestor. Expansion is always a deliberate click — auto-recursion is
  rejected by design.
- **HUD overlay.** Corner readout with pinned-item headers that flip colour when craftable,
  merged gather totals across all pins from unexpanded (leaf) rows only, and a tools section
  (presence-checked, never counted: ✓ when carried, flagged when missing) covering pinned
  recipes and expanded craft-steps alike. Every line carries its item icon; rows accepting
  several variants cycle the icon through them once a second, handbook-style
  (`HudCycleVariants`, also toggleable in the list window, applied live at render time). Behaves like a second minimap panel: present
  whenever the list has pins (K hides it as a persistent preference), gone when the list
  empties, and slotted in underneath whatever HUD elements already occupy its corner —
  minimap, coordinates, clock, other mods' overlays — measured from their real on-screen
  bounds, so anything toggled off costs no space. Corner, row cap and colours configurable.
  Positioned absolutely rather than corner-aligned — vanilla's coordinate overlay re-stacks
  itself against aligned dialogs.
- **Per-world persistence** (`ModData/tallybook/<savegameid>.json`): pins, counts, recipe
  choices, expansion state, and recipe preferences. Itemstacks and recipes are re-resolved on
  load so a world with different mods degrades honestly; a corrupt or missing file yields an
  empty list, never a crash.
- **Live counting** via a single inventory pass per change, coalesced across slot events.
  Wildcard-expanded recipes are collapsed back into one row ("Board (any wood) 20/7") and
  counted collectively. Pseudo-recipes that consume their own output (slab placement-mode
  recipes, chiseled-block combining, armor repair) are excluded from recipe lookups — they
  convert an item, they don't create one, and left in they could hijack an item's recipe and
  hide its real ingredients and tools.
- **Quest-ready glow.** A gold shimmer appears over a tracked quest giver once you carry
  everything they asked for — all-or-nothing per NPC, so a two-item errand does not flag as
  ready when only one is met. Game particles on a 400ms timer rather than a custom renderer
  or per-frame work; purely cosmetic and local, and it disables itself rather than
  interrupting play if anything throws (`QuestReadyGlow` / `QuestReadyGlowColor`).
- **The HUD groups materials under the item that needs them** — "Resonator" and what it
  needs, then "Wooden table" and what that needs — rather than listing every pin and then one
  pooled list. Pooling still merges an item two builds both want into a single line, so it
  stays available as `HudGroupByItem` / the Options screen; grouping answers "what does this
  one need", pooling answers "what do I fetch in total".
- **Rows for unexpanded wildcards name and draw themselves properly.** An ingredient written
  `plank-*` with no `name` field is not expanded by the game into per-wood recipes the way a
  named one is, so such a row had nothing concrete behind it and read "Any suitable item 0/7"
  with no icon. The accepted items are now resolved from the world, so it reads
  "Board (any, N variants)" and cycles a board icon through the woods like any other.
- **Long HUD lines scroll instead of ending in "…"**: a line too wide for its column holds
  for 15 seconds, slides left to reveal the rest, then returns. The dialog can offer hover
  text for a truncated row; a HUD cannot, since during play the mouse belongs to the world.
  Drawn as a text texture blitted at an offset and clipped to the line, so animating one row
  does not re-compose the whole overlay several times a second (`HudScrollLongLines`).
- **"Nothing yet" rows are white**, level with the coordinates readout the HUD sits under —
  the old grey read fine on a dialog background and murky over the world. A config still
  carrying that grey is migrated, since it was the old default rather than a choice; HUD
  section headings move to the game's parchment tone so they still read as headings.
- **Options screen** in the management dialog, holding the settings that change what the list
  shows or counts rather than spending a row above the table on every visit: icon cycling for
  "any" rows, and counting saddlebags on animals you own within range — ridden or standing
  beside you (off by default; "what do I have on me" is the question this mod answers, and
  counting the pack mule changes the answer). The test is the game's own **ownership**, not
  proximity: counting any container that happened to be near would be the nearby-chest
  scanning the design rejects, and on a shared server another player's animals are never
  counted.
- **Quest history.** A third tab records what you have finished, kept after the pins are
  gone, each with a **Read** button that opens what the villager actually said — recovered
  from the dialogue graph by the chain's own "started" variable, so it works for quests
  finished long before the mod existed and whose pins never existed at all. A **Journal**
  button opens the game's journal from the same page, for the lore collected alongside. Quests completed while Tallybook was running are dated by in-game day; ones already
  finished the first time it looked cannot be — inventing a date would be worse than saying
  so — and are listed last, ordered by how deep into the story they sit. That ordering is read
  from the content itself: a quest's opening is gated on variables other quests set, so the
  dialogue files describe a real partial order (the archives must precede what they unlock),
  and counting how many quests must precede each one sequences them without guesswork.
- **Village errands are picked up retroactively at login.** Their state lives on the player
  and is synced to the client, so quests you were already on — including from before the mod
  was installed — appear without going to find anyone. Each is offered once ever, so unpinning
  one sticks. Trader tasks stay conversation-based: their state lives on the NPC and only
  exists while that trader is loaded.
- **Items / Side quests tabs** in the management dialog: errands from villagers are a
  different kind of thing from what you decided to build, so they get their own tab with its
  own paging rather than being mixed in and told apart by a label. Errands are counted, not
  decomposed, and contribute nothing to the gathering list; a **Gather** button copies one
  across when you would rather craft the item than find it, and the two rows then track
  independently — an errand can never quietly rewrite the count on a goal you set yourself.
  Only pins added from the handbook start showing their recipe; everything else starts as
  plain counting with **Expand** / **Collapse** on the row, the same words an ingredient row
  uses for the same act. A recipe existing is no reason to assume the player meant to craft
  rather than gather — iron ingots exist as a grid recipe solely by chiselling an iron anvil
  back into ingots, while smelting, the real source, is not a grid recipe at all.
  The HUD mirrors the split: a **side quests** section reading
  `Iron ingot for Agnieszka (140m)  0/8` — distance rather than a place name, because the
  game has no notion of "the village" to ask for — and everything else under **gathering**.
  Counts sit in a reserved right-hand column so truncating a long label can never eat them.
- **Villager errand tracking.** Accepting a villager's or trader's fetch quest adds it to
  the list automatically — a light-blue `x` map marker on the NPC (colour/icon/pinned
  configurable) plus tallied pins labelled with who asked. The marker appears while an errand
  is pinned and checked and goes when it is unchecked or unpinned, driven by a flag on the pin
  rather than by re-reading the map on a timer — the latter placed a duplicate marker every
  few seconds whenever that read came back empty. A **Map** button on each errand row opens
  the full world map centred on the giver, and `.tallybook clearmarkers` removes every marker
  Tallybook has placed. The villager's own words are kept with the errand and shown under its
  row — recovered from the dialogue graph (the step that *sets* the quest variable is the
  accepting one; the speech leading to that choice is the briefing) rather than by reading the
  live conversation, so the conversation UI is still never touched. Requests are read from the game's
  own structured dialogue conditions rather than parsed from text, and only requests whose
  quest gates are currently satisfied are picked up — a gate that cannot be evaluated counts
  as unmet, so the failure direction is "nothing tracked", never a spoiler. Nothing patches
  the conversation UI, so villager dialogue is untouched. Unpinning holds for the current
  conversation and the errand is offered again next time; unchecking sets it aside for good,
  since auto-tracking never re-checks a parked pin (`AutoTrackQuests`).
- **Direct acquisition tracking.** Every pin counts the pinned item itself against its
  target, so items nothing crafts — ore, hides, soil, a villager's fetch request — are fully
  trackable rather than inert reminders, and a craftable item's ingredient requirements
  scale down as you acquire the item itself (the §2a deficit rule, now applied at the root
  as well as inside the tree). Counting matches the exact handbook page, so owning one
  bookshelf variant never marks a different variant as had.
- Config file `ModConfig/tallybook.json`: HUD corner, row cap, default visibility, unpin
  confirmation, status colours.

Foundations proven in game before the UI was built on them (the step-1 probe, since
retired): registry access (30k+ grid recipes on a modded client, content-mod recipes
included), event-driven inventory counting via `IInventory.SlotModified`, wildcard-variant
collapsing, and cheapest-layout selection where grid layouts disagree on quantity.

- Design spec finalised (`tallybook-mod-spec.md`).
- Client-only mod skeleton: `modinfo.json` (`"side": "Client"`, requires game 1.22.0) and a
  `ModSystem` gated to `EnumAppSide.Client`.
- Compat regression harness (`tools/compat-test.ps1`): headless dedicated-server boots for
  every mod combination in the companion set. Fails on server-log errors or warnings, wrong
  mod count/load order, a missing assembly-load marker, or any loss of server-side silence.
- Game-version sweep (`tools/version-sweep.ps1`): builds one artifact and runs the full
  compat matrix against real dedicated servers for 1.22.0 through 1.22.6. Server packages are
  extracted with verification against the archive's entry count and a completion stamp, and
  setup problems are reported as `SETUP` rather than being misattributed as a mod failure.
