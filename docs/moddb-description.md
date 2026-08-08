# ModDB description — Tallybook

## Short summary (under 100 characters)

Crafting shopping list with live counts + villager quest tracker with map pins. Client-side.

(92 characters)

---

## Description

Tallybook is two trackers in one: a Satisfactory-style crafting shopping list — pin any item,
see exactly what you still need to gather, with counts that update the instant your inventory
changes — and a side quest tracker that picks up villager and trader errands by itself,
counts them the same way, and keeps a map marker on whoever is waiting for the goods. Fully
client-side: works on any server, no server-side install needed.

**Features**

**Shopping list.** Pin an item from its handbook page — one click, and it's exactly the
variant you were looking at, not a sibling that shares its name. Every pin tracks its own
acquisition (Low-quality soil 12/64 fills in as you dig), so loot-only and gather-only items
are first-class goals, not just craftables. Expand any craftable row to unfold its recipe
beneath it, sized to what you still lack: need 4, carry 1, and the children ask for materials
for 3. Nested as deep as you like, one deliberate click per level — never automatic, so recipe
cycles and wrong guesses can't lie to you.

**Alternative recipes are found automatically, whatever mod added them.** When an item has
more than one genuinely different recipe — vanilla's way versus a mod's blueprint-gated way —
Expand asks which you mean, listing each option by what it takes and what you'd have to be
holding. No per-mod compat code: whether two recipes are a real choice is decided by what
they consume, so it works for content mods that haven't been written yet.

**Side quests.** Accept a fetch errand the way the game intends and Tallybook picks it up on
its own — no button, no extra click. The row reads Raw hide (Small) for Gerhardt 3/10 and
goes green when you can deliver. What the villager actually said is kept as a transcript
under the row, re-readable a week later. Errands you were already on are recovered at login
from the game's own dialogue files — including quests accepted before Tallybook was
installed — and handed-in errands notice they're finished by themselves, moving to History
instead of sitting at 0/10 forever because the goods left your bags.

**Map integration.** Quest givers get a light-blue X on your map for as long as their errand
is live, and every errand row has a Map button that opens the world map centred on them. An
errand that came with a locator map points at the map's destination while you're still
fetching — the lens is in the Devastation, and that's the walk you're making — then back at
the giver once you have the goods. Locations are learned by talking to the NPC (never by
proximity scanning), from your own waypoints when one names them, or told directly:
stand at the forge and type .tallybook here Agnieszka.

**HUD.** A corner overlay in the minimap's style: side quests with distances, then pooled
gathering totals — one Boards 12/48 line even when three builds want boards — then required
tools with carried/missing checks. Item icons on every line, with "any wood"-style rows
cycling their icon through the accepted variants, handbook-style. It dodges the minimap,
clock and coordinates rather than fighting them, and disappears when your list is empty.

**History.** Everything you've finished, including quests completed before Tallybook was
installed, with the conversation transcripts preserved and story milestones (villages found,
lore heard) recorded alongside. Undated finishes are ordered by story prerequisites rather
than guessed dates. One click away from your journal for the lore you collected en route.

**Everything updates live.** Counting is event-driven off your carried inventory — hotbar
and backpacks, with an opt-in for saddlebags on animals you own. No polling, no nearby-chest
scanning: the question is "what do I have on me", answered honestly.

**How to open it:** press L for the list (three tabs — Items, Side quests, History), K to
toggle the HUD, and pin from any handbook page via its "Add to Tallybook" button. All keys
rebindable under Settings → Controls.

**Good to know**

- Works with every content mod's recipes and trader errands for free: servers push their
  recipes and dialogue to connecting clients, and Tallybook reads those — there is no compat
  list to be on.
- Tallybook only ever reads. It never crafts for you, never moves your items, and never
  writes to your inventory — the worst bug it can have is a wrong number.
- Errands are counted, never decomposed: a fetch quest is a fetch, and its row won't sprout
  an anvil because the game technically has a recipe.
- One text-size slider (in Options) governs the HUD and the whole list window.
- Useful commands: .tallybook here <name> (set a quest giver's location to where you stand),
  .tallybook quests (every fetch errand your world's content describes, with your status),
  .tallybook recipes (every item craftable more than one way), .tallybook npcs,
  .tallybook relearn, .tallybook blankmarkers (finds untitled waypoints, which crash the
  vanilla map on hover — whoever made them).
- Config at VintagestoryData/ModConfig/tallybook.json; per-world data at
  VintagestoryData/ModData/tallybook/.
- For Vintage Story 1.22.x (tested against every patch release, 1.22.0 through 1.22.6).

---

## Changelog — first ModDB release

**Tallybook 0.3.5 — for Vintage Story 1.22.x**

Initial ModDB release.

**Features**

- Crafting shopping list: pin items from their handbook page (exact-variant), live
  inventory-tracked counts, deficit-scaled recipe expansion with per-level recipe choice,
  and automatic detection of alternative recipes — including modded blueprint-gated ones —
  with a chooser that lists each option by its materials.
- Side quest tracking: villager and trader fetch errands picked up automatically on accept,
  recovered retroactively at login from the game's own dialogue files, counted like any
  other goal, with conversation transcripts kept on the row and completion detected from
  your own quest state when you hand goods over.
- Map integration: light-blue X markers on quest givers while their errand is live, Map
  buttons that centre the world map on the giver — or on an errand's map destination while
  you're still fetching — and .tallybook here to set a giver's location by standing there.
- HUD overlay: side quests with distances, pooled gathering totals, and tool checks, with
  item icons and variant cycling; positions itself around the minimap, clock and coordinates.
- History tab: finished quests (including pre-install ones) with transcripts, plus story
  milestones, ordered by prerequisites where dates are unknowable.
- Options screen with live text-size slider governing HUD and window, mount-bag counting
  opt-in, quest marker and glow settings — each option explained by a hover ?.
- Status marks drawn only with glyphs the game's fonts actually carry, so checks are checks
  everywhere rather than empty boxes.
- Fully client-side; works with every content mod's recipes and errands with zero compat
  patches. Tested against Vintage Story 1.22.0–1.22.6.
