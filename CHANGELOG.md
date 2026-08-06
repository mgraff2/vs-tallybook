# Changelog

## Unreleased — 0.1.0

The full v1 feature set from the spec.

- **Pin from the handbook.** Every item's handbook page gains an "Add to Tallybook" link.
  Pinning again increments the count rather than duplicating the row. Implemented as a
  Harmony postfix on `CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo` that only
  appends to the returned components — if the patch ever fails to apply, the handbook is
  unaffected and the mod logs why.
- **Management dialog (L).** Pinned items with − / + steppers and direct numeric count entry,
  colour-coded ingredient rows, tool presence rows, recipe switcher for items with several
  recipes, unpin and clear-all behind confirmation screens.
- **Manual expansion tree.** Craftable ingredient rows carry an Expand button that unfolds
  that ingredient's recipe beneath it, sized to the parent's *deficit*
  (`ceil(max(0, needed − have) / recipeOutput)`), nested to any depth, with per-node recipe
  choice, per-world recipe preference memory, and a cycle guard that refuses to expand an
  item into its own ancestor. Expansion is always a deliberate click — auto-recursion is
  rejected by design.
- **HUD overlay (K).** Corner readout with pinned-item headers that flip colour when
  craftable, plus merged gather totals across all pins from unexpanded (leaf) rows only.
  Corner, row cap and colours configurable; hides itself when the list is empty. Positioned
  absolutely rather than corner-aligned — vanilla's coordinate overlay re-stacks itself
  against aligned dialogs, a lesson inherited from Pin Matrix.
- **Per-world persistence** (`ModData/tallybook/<savegameid>.json`): pins, counts, recipe
  choices, expansion state, and recipe preferences. Itemstacks and recipes are re-resolved on
  load so a world with different mods degrades honestly; a corrupt or missing file yields an
  empty list, never a crash.
- **Live counting** via a single inventory pass per change, coalesced across slot events.
  Wildcard-expanded recipes are collapsed back into one row ("Board (any wood) 20/7") and
  counted collectively.
- Items with no crafting recipe can still be pinned as reminders.
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
  every mod combination, with Pin Matrix as the companion mod. Fails on server-log errors or
  warnings, wrong mod count/load order, a missing assembly-load marker, or any loss of
  server-side silence.
- Game-version sweep (`tools/version-sweep.ps1`): builds one artifact and runs the full
  compat matrix against real dedicated servers for 1.22.0 through 1.22.6. Server packages are
  extracted with verification against the archive's entry count and a completion stamp, and
  setup problems are reported as `SETUP` rather than being misattributed as a mod failure.
