# Changelog

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
