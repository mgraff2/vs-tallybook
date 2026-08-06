# Changelog

## Unreleased — 0.1.0

First working slice: pin items from the handbook and see what you still need.

- **Pin from the handbook.** Every item's handbook page gains an "Add to Tallybook" link.
  Clicking it pins that item; pinning again increments the count rather than duplicating the
  row. Implemented as a Harmony postfix on
  `CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo`, which only appends to the
  returned components — if the patch ever fails to apply, the handbook is unaffected and the
  mod says so.
- **`.tallybook`** lists your pins with live ingredient counts; `.tallybook unpin <name>` and
  `.tallybook clear` manage it. The chat command reports the list, it is not how things get
  onto it.
- **Per-world persistence** (`ModData/tallybook/<savegameid>.json`). Only item code and count
  are stored; itemstacks and recipes are re-resolved on load, so a world with different mods
  degrades honestly instead of restoring something that no longer exists. A corrupt or missing
  file yields an empty list, never a crash.
- Items with no crafting recipe can still be pinned, and say so (spec §11).
- Quantities scale with pin count, rounding up to whole crafts.

- **Read-only recipe probe** (`.tallybook <item code>`): finds grid recipes producing a
  matching item and prints ingredients with live carried-inventory counts and satisfied /
  partial / none status, plus non-consumed tool rows. `.tallybook off` stops watching. This
  is spec §10 step 1 — it exists to validate registry access and inventory events against the
  real 1.22 API before anything is built on top of them.
- Inventory counting is driven by `IInventory.SlotModified`, not polling, and reports only
  values that actually changed. **Confirmed working in game.**
- Wildcard recipes are collapsed back into a single requirement row. The registry expands
  `plank-*` into one recipe per wood, so variants are counted collectively — "Board (any wood)
  20/5" rather than naming one arbitrary wood and reporting 0/5.
- One entry per item, with a clickable `[handbook]` link for the grid layouts. Where layouts
  disagree on quantity, the cheapest is shown and labelled as such.
- Recipes are indexed once per world instead of rescanning ~30,000 entries per lookup, and
  inventory events are coalesced into a single recount.
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
