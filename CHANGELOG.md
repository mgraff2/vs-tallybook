# Changelog

## Unreleased — 0.1.0

The full v1 feature set from the spec.

- **Pin from the handbook.** Every item's handbook page gains an "Add to Tallybook" link,
  plus "Go to Tallybook" to close the handbook and open the list. Arriving from a list row's
  Book button also shows a "← Back to Tallybook" button beneath the handbook's own Back
  button (a floating dialog anchored to the handbook's live bounds, not an injected element) — a separate floating dialog anchored to the handbook's live bounds rather than an
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
  configurable) plus tallied pins labelled with who asked. The marker's existence is
  reconciled from the list rather than bolted onto each action, so it appears while an errand
  is pinned and checked and goes when it is unchecked or unpinned — five separate paths could
  otherwise leave a marker outliving its errand. A **Map** button on each errand row opens
  the world map centred on the giver. Requests are read from the game's
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
