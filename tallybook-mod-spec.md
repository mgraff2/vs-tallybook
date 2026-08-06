# Tallybook — Client-Side Crafting Shopping List for Vintage Story

**Spec v1.0 — final for implementation**
Target: Vintage Story 1.22.x. **Client-side only** (`Side = EnumAppSide.Client`) — installable per-player, works on any server, zero server component.
Proposed modid: `tallybook`

---

## 1. Design intent

Satisfactory-style crafting checklist: browse any item, pin it to a shopping list, and see live inventory-tracked ingredient requirements with at-a-glance status. VS's handbook already answers "how do I make X" — Tallybook answers "what do I still need to gather, and am I done?"

**Free compatibility guarantee (a selling point — put it in the mod description):** servers push their content mods to clients, so every modded recipe on any server is automatically present in the client's recipe registries. Tallybook reads those registries; it supports every content mod's recipes with zero compat patches, forever.

## 2. Core principle: flat by default, MANUAL expansion, auto-recursion rejected

A pinned item's list shows its **direct recipe ingredients**. Pin a Herty cup → "1 steel spile, 1 resin pot." The mod **never auto-expands** ingredients into sub-recipes.

Rationale (write into code comments so future contributors don't "fix" it): automatic recursion hits recipe cycles, per-level recipe-choice explosions (six woods × three grids × smith-vs-cast), and silent wrong guesses — every failure mode makes the list lie. Expansion must always be a **deliberate player action** with a recipe choice attached.

### 2a. Manual expansion (the tree)

Any ingredient row that is itself craftable carries an **EXPAND** affordance. Expanding unfolds that ingredient's own recipe as child rows beneath it, scaled to the parent's needs. Collapse re-folds (children hidden, state kept). Expansion nests to any depth — each level is one more deliberate click.

- **Recipe choice per expansion:** expansion never blocks on a prompt. It defaults to the first registered recipe (or the player's remembered preference — see below), displays which recipe was chosen on the node, and offers an inline swap control listing the alternatives. Swapping recomputes children immediately. **Wildcard ingredients are not alternate recipes:** one recipe accepting "any log" stays one recipe — the child row shows the wildcard's friendly name and inventory counts match any qualifying item collectively. The picker appears only for genuinely distinct recipes (different ingredient sets, different crafting systems, different ratios). **Preference memory:** choosing a recipe for an item records it per-world as that item's default for future pins/expansions; changeable anytime, cleared with the list.
- **Deficit-based scaling (the key math):** children are sized to what you *still lack*, not the gross requirement:
  `craftsNeeded = ceil(max(0, parentNeeded − parentHave) / recipeOutputQuantity)`
  `childNeeded = craftsNeeded × childQtyPerCraft`
  Need 4 spiles, carry 1 → children demand materials for 3. Craft a spile → children visibly shrink. Recipe output counts matter: 24 boards at 4/craft = 6 crafts = 6 logs.
- **Factor propagation:** the root pin count multiplies down through every level (2× the pins → 2× everything below, deficits recomputed). One factor at the root; no per-node multipliers.
- **Status semantics:** a parent's satisfied/craftable state considers only its **direct** rows — having all of a spile's ingredients does not mark the spile "have" (it isn't crafted yet). A fully-green expanded node reads as **"ready to craft"**, which is its own useful signal.
- **HUD = leaves only:** merged HUD totals draw from **unexpanded (leaf) rows only**. Expanding a node moves it from "gather this" to "craft this from the things below" — intermediates appear as craft-steps in the dialog's tree, not as gather-items in the HUD.
- **Cycle guard:** an item may not be expanded if it already appears as an ancestor in its own branch (disallow with tooltip). Guards degenerate recipe loops without any traversal logic.
- **Persistence:** expansion state (which nodes, chosen recipes) saves with the pin list per-world.

## 3. Pinning

- **Pin action in the handbook UI** on any craftable item's page (button or hotkey while viewing). If the handbook page can't be extended cleanly, fallback: pin-by-hotkey on hovered itemstack anywhere (inventory, creative menu, handbook).
- **Recipe choice:** items with multiple recipes get a small recipe picker at pin time (grid alternates, smithing, clay forming, etc.), defaulting to the first/most-common. The chosen recipe is stored with the pin and changeable later from the list dialog.
- **Pinning an already-pinned item increments its count** — never a duplicate row.
- **Pin count is mutable after pinning** (see §4). Defaults to 1.

## 4. The list — quantities and status

### Pinned item rows
Each pinned item shows: name, **count controls** (− / + steppers AND a direct-entry numeric field — a player pinning 20 taps should not click nineteen times), chosen recipe (changeable), and a **rollup status**:

- **CRAFTABLE** (all ingredient rows satisfied at current count) — bright/bold/check
- Otherwise normal display

Decrement to 0 unpins (subtle confirm so a misclick doesn't eat the list). Explicit unpin button too.

### Ingredient rows (under each pinned item)
Per ingredient: `name  have/needed`, where needed = per-recipe amount × pin count. Three states:

| State | Condition | Display |
|---|---|---|
| Satisfied | have ≥ needed | Bold/bright + check |
| Partial | 0 < have < needed | Amber/dim |
| None | have = 0 | Muted/grey |

### Tool rows
Recipes that **use but don't consume** a tool (saw for boards, hammer for smithing) render as presence-checked rows: `requires: saw ✓/✗` — checked by existence in inventory, never counted against quantity.

### Inventory counting
- Counts **carried inventory**: hotbar + backpack slots. **Not** nearby chests (needs world queries — scope creep, and dishonest to the "what do I have on me" question).
- **Mount inventory (config `includeMountInventory`, default true):** while the player is riding a mount with an accessible inventory (e.g., elk saddlebags via mount-gear mods), its contents count as carried — cargo on your mount is with you. Implement generically against the mounted entity's inventories, not any specific mod. **Prototype-verify sync behavior:** if the mount's inventory only syncs client-side when opened, degrade gracefully — use last-synced contents, refreshing on open, and mark mount-derived counts as potentially stale (subtle indicator) rather than lying confidently. Dismounting removes mount items from counts.
- Recomputed on **inventory-change events** (not per-frame): picking up the 24th board flips the row green immediately. This live feedback is the core loop — do not degrade it to polling.

## 5. HUD overlay (v1, not a stretch goal)

Compact always-on corner overlay (position/toggle configurable, hotkey to show/hide) showing the active list while playing:

- **Merged ingredient totals across all pins** — the HUD answers "what do I grab": one `Boards 12/48` line even if three pinned items want boards. Per-item breakdown lives in the management dialog, which answers "for what."
- Pinned item headers with craftable-state flip.
- Compact: name + have/needed + status color. No icons required for v1 if layout fights back.

## 6. Management dialog

Hotkey (default `L`, rebindable) opens the full dialog: pinned items with counts/steppers/recipe pickers, expandable ingredient detail per item, unpin, clear-all (confirm), and the HUD merged view mirrored. This is also where recipe choice is edited post-pin.

## 7. Persistence

Pinned list (item, chosen recipe, count) saves to per-world client-side JSON (`ModData/tallybook/<worldid>.json`). Log in tomorrow, the shopping list is still there. Corrupt/missing file → empty list, never a crash.

## 8. Data sources

- **Recipes:** client-side recipe registries — grid crafting recipes, smithing, clay forming, knapping, barrel, cooking, and mod-registered recipes in those systems. The handbook proves this data is client-resident; Tallybook reads the same registries. Normalize all recipe shapes to `(ingredients: [{itemstack-ish matcher, quantity}], tools: [...])`.
- **Wildcard ingredients** (e.g., "any plank"): count matching inventory items collectively; display the wildcard's friendly name ("planks (any)").
- **Inventory:** `capi.World.Player.InventoryManager` — hotbar + backpacks, event-driven.

## 9. Configuration (`ModConfig/tallybook.json`)

```jsonc
{
  "dialogHotkey": "L",
  "hudToggleHotkey": "K",
  "includeMountInventory": true,  // count saddlebags etc. while riding
  "hudPosition": "topright",      // topleft|topright|bottomleft|bottomright
  "hudMaxRows": 12,               // truncate with "+N more" beyond this
  "confirmOnUnpin": true,
  "colorSatisfied": "#80FF80",    // themeable status colors
  "colorPartial":   "#FFCC66",
  "colorNone":      "#909090"
}
```

## 10. Implementation architecture

- C# client-only `ModSystem`: hotkey registration, HUD element (`HudElement` subclass), management `GuiDialog`, handbook-page pin integration, per-world JSON persistence, inventory event subscription.
- **Build order (API-validation first):**
  1. Read-only prototype: enumerate grid recipes for a hardcoded item, print ingredients + live inventory counts to chat. Validates registry access + inventory events — the two API unknowns.
  2. Pin store + management dialog, flat lists only (grid recipes only).
  3. HUD overlay with merged totals + status colors.
  4. **Manual expansion tree** (§2a): deficit math, factor propagation, leaf-based HUD rule, cycle guard. Built after flat lists are solid — the tree is a strict superset of the flat row.
  5. Remaining recipe systems (smithing, clay forming, knapping, barrel, cooking) via the normalization layer.
  6. Handbook UI integration (or hotkey-on-hover fallback if the handbook resists extension).
- **API caveat (standing rule):** registry class names, handbook dialog extensibility, and inventory event signatures must be verified against 1.22 docs/source at implementation time — not trusted from memory. The step-1 prototype exists to surface exactly these.

## 11. Edge cases

- Item with no recipe (loot-only, trader-only) → pin allowed, list shows "no recipe known" row; still useful as a reminder entry.
- Recipe mods adding/removing recipes between sessions → stored recipe re-resolved by item code at load; if the chosen recipe no longer exists, fall back to first available + flag the row.
- Stack-size and container nesting (bags in bags) → count leaf itemstacks wherever carried.
- Server switch / world switch → per-world files keep lists separate by design.

## 12. Out of scope for v1

- **Automatic recursion / auto-expansion — permanently rejected (see §2). Manual per-node expansion is in scope (§2a); the machine deciding to expand is not.**
- Nearby-chest or storage-network counting
- Crafting execution from the list (it's a checklist, not an auto-crafter)
- Sharing lists between players (export/import could be v2 if wanted)
