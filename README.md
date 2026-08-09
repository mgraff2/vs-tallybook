# Tallybook

A client-side crafting shopping list for [Vintage Story](https://www.vintagestory.at/).
Pin any item, and Tallybook tells you what you still need to gather — with live
inventory tracking and at-a-glance status. The handbook already answers "how do I make X";
Tallybook answers "what do I still need, and am I done?"

**Version 0.3.6**, for Vintage Story 1.22.0–1.22.6. Client-side only: it works on any server,
and nobody else needs it installed. The design is in
[tallybook-mod-spec.md](tallybook-mod-spec.md).

### How it works

1. **Pin** — open the handbook (H), find the thing you want to build, click
   **"Add to Tallybook"** at the bottom of its page — or **"Go to Tallybook"** below it to
   close the handbook and open your list. Arrive from a list row's **Book** button and a
   **← Back to Tallybook** button also appears under the handbook's own Back button, so the
   return trip is where you'd look for it. Pinning again raises the count. What
   gets pinned is exactly the page you were on: variants that share an item code but are
   really different things (the four bookshelf shapes, each its own page and plank count)
   stay distinct.
2. **Manage** — press **L** (rebindable): three tabs — **Items** for what you decided to build
   or collect, **Side quests** for errands villagers gave you, and **History** for what you
   have finished — each with a **Read** button for what the villager said at the time, and a
   **Journal** button for the lore you collected along the way. The first two tabs are a table of icon,
   item (indented to show the craft tree), have/need, how many you want, actions — with
   − / + steppers and direct count entry, colour-coded status, and tool checks. Unpinning is
   hold-to-confirm, never a dialog: hold the Unpin button for a second, through its countdown;
   release early and nothing happens (stepping the count to 0 unpins instantly, and
   clear-all keeps its confirm). Every pin row has a **Handbook** button that jumps straight
   back to that item's handbook page — the exact variant you pinned — for when you want the
   recipe layouts. Each pin has a checkbox: unchecking parks it — kept and saved
   with its count, recipe choice and expansions, but out of the HUD and the counting — and
   "Uncheck all" parks the whole list in one click, no confirmation per item. Pinning a
   parked item from the handbook re-checks it.
3. **Expand** — any craftable ingredient row has an **Expand** button that unfolds its own
   recipe beneath it, sized to what you still *lack*: need 4 spiles, carry 1 → the children
   ask for materials for 3. Crafting shrinks them live. Nested to any depth, one deliberate
   click per level, with a recipe switcher where an ingredient has several recipes and a
   cycle guard so recipe loops can't unfold forever. Never automatic (see "Design notes").
4. **Gather** — the corner HUD shows merged totals across all pins: one `Boards 12/48` line
   even when three pinned items want boards, plus a tools section (a glass slab needs the saw
   as much as the glass — ✓ when carried, flagged when missing). Every line carries the
   item's icon; an "any wood" row cycles its icon through the accepted variants once a
   second, handbook-style (toggleable from the list window). Expanded intermediates move
   out of the gather list — they're craft-steps now, not shopping items. It behaves like a
   second minimap panel: there whenever the list has pins (K hides it if you'd rather not),
   gone when the list empties, and it slots in underneath whatever already occupies its
   corner — minimap, coordinates, clock — rising to the top when the corner is clear.

### Villager errands

Accept a villager's or trader's fetch quest the way the game intends and Tallybook picks it
up by itself — no button, no extra click. It drops a light-blue map marker on the NPC and
adds their request to your list, tallied like anything else: the row reads
`Small hide 3/10  (for Gerhardt)` and goes green when you can hand it over. Nothing is
injected into the conversation itself, so villager dialogue behaves exactly as it does
without the mod.

Errands are counted, not broken down — the row is what the villager asked for and nothing
else, and its ingredients stay out of your gathering list. **Gather** copies it to the Items
tab as a plain gathering goal — just the item and its count, because that's what you said
you were going to do. The errand keeps its own row and its own count.

Only pins added from the handbook start showing their recipe — pinning there is an act of
reading a recipe, so wanting to see it is a fair assumption. Everything else starts as plain
counting, and **Expand** on the row shows the recipe if you want it (**Collapse** puts it
back). A recipe existing is no reason to assume you meant to craft rather than gather: iron
ingots have exactly one grid recipe, chiselling an iron anvil back into ingots, when what
you really do is smelt them.

The errand keeps **what the villager actually said** — "Damned drifter trampled through my
traps this week… Can you bring me some small raw hides? Say ten of them?" — quoted under its
row and in full on hover, so you can re-read why you're carrying ten hides a week later.

Each errand row has a **Map** button that opens the full world map centred on the quest giver,
and a light-blue `x` marker is kept on the map for as long as the errand is pinned and
checked — uncheck or unpin it and the marker goes, re-check or re-accept and it returns.

The HUD lists everything you're building on one scrolling row, then the pooled totals — one
line per material, however many builds want it. Options can switch that to a per-item
breakdown instead.

On the HUD, errands get their own **side quests** section reading
`Iron ingot for Agnieszka (140m)  0/8`, with everything else below under **gathering**.

Unpin an errand and it's gone for that conversation; talk to them again and it comes back,
because re-accepting should re-add. To set one aside for good, **uncheck** it instead —
auto-tracking never re-checks a parked pin. (`AutoTrackQuests` turns the whole thing off.) Once you're carrying **everything** an NPC asked for, a gold
shimmer appears above them out in the world — the usual "ready to turn in" flag — so you
don't have to open a list to know the walk is worth it. Nothing is guessed from prose: the game stores fetch requests as
structured conditions, and only requests that are actually live for you are offered, so
quests you haven't been given stay unspoiled.

### The story, one step at a time

Tallybook also walks you through the vanilla storyline — starting from the very first step, a
few rusty gears and a question for a wandering trader. A **"story so far"** block at the top
of the Side quests tab shows the step you are on and nothing more; it advances by itself as
you play, watching the story's own progress flags, the maps and letters in your hands, and
what NPCs tell you in conversation. Steps that need something fetched pin it automatically
and retire it when the step is done.

No spoilers: a step is only ever shown once the game itself has told you that much — the
next step after it stays hidden everywhere, including in `.tallybook story`, which prints
where you are on request. Progress is per world and only moves forward, so losing a map or
handing an item over never un-completes a step. On worlds without story content the block
never appears.

Every pin tracks **its own acquisition**, not just its ingredients: the row reads
`Low-quality soil 12/64` and fills in as you dig. That means items nothing crafts are worth
pinning too — ores, hides, soil, an item a villager asked you to fetch — and a craftable
item's ingredient list shrinks as you acquire the item itself, the same deficit rule the
expansion tree already uses.

Everything updates the instant your inventory changes, and the list (counts, recipe choices,
expansion state) is saved per world.

Counting is your **carried** inventory — hotbar and backpacks. An **Options** button in the
L window can extend that to saddlebags on animals **you own** within 15 blocks, ridden or
just standing beside you, and holds the icon-cycling setting too. Ownership is the game's
own, so on a shared server your friend's elk is never counted toward your totals — and only
bags actually strapped to an animal count, never one lying on the ground or stored in a
chest.

## Why it works with every content mod, for free

Servers push their content mods to connecting clients, so every modded recipe on a server is
already present in the client's recipe registries. Tallybook reads those registries directly.
That means every content mod's recipes are supported with zero compatibility patches — not
as a maintenance promise, but as a property of where the data lives.

### Configuration (`VintagestoryData/ModConfig/tallybook.json`)

| Key | Default | Meaning |
|---|---|---|
| `HudPosition` | `"topright"` | `topleft` / `topright` / `bottomleft` / `bottomright` |
| `HudMaxRows` | `12` | gather rows before `+N more…` |
| `HudVisible` | `true` | HUD default; K toggles at runtime |
| `HudCycleVariants` | `true` | cycle "any"-row icons through their variants (also toggleable in the L window) |
| `HudScrollLongLines` | `true` | a HUD line too long for its column slides left every 15s to show the rest |
| `HudGroupByItem` | `true` | HUD lists each pinned item followed by what it needs; off pools everything into one merged list |
| `ConfirmOnUnpin` | `true` | unpin needs a 1s button hold; `false` = instant click |
| `IncludeMountBags` / `MountBagRange` | `false` / `15` | count saddlebags on animals you own within N blocks (also in the L window's Options) |
| `QuestWaypoints` | `true` | keep a map marker on tracked quest givers at all (off = Tallybook never touches your waypoints) |
| `QuestWaypointColor` / `QuestWaypointIcon` / `QuestWaypointPinned` | `#4fc3f7` / `x` / `true` | how that marker looks — light blue, to read differently from your own markers |
| `QuestReadyGlow` / `QuestReadyGlowColor` | `true` / `#FFBE3C` | gold shimmer over an NPC once you carry everything they asked for |
| `AutoTrackQuests` | `true` | pick up villager fetch quests automatically when you accept them |
| `ColorSatisfied` / `ColorPartial` / `ColorNone` | `#80FF80` / `#FFCC66` / `#FFFFFF` | status colours |

Hotkeys (L, K) are rebindable in Settings → Controls like any other key.

`.tallybook clearmarkers` removes every quest map marker Tallybook has placed, and
`.tallybook markers` puts one back on every tracked quest giver — useful if you deleted them
by hand and want them again.

`.tallybook recipes` lists every item your world can make more than one way, and what each way
would have you fetch. Which items those are depends on the mods your server runs, so it is
worth a look after adding one.

`.tallybook pages` prints, for every pin, the handbook page code its Handbook button will ask
for and whether the handbook actually has that page — if a Handbook button ever dumps you at
the handbook's front page instead of the item, run this and the line for that pin says why.

`.tallybook story` prints where you are in the storyline — completed and current steps only,
never what comes next.

## Install

Drop `tallybook_0.3.8.zip` into
`%APPDATA%\VintagestoryData\Mods\`.

## Building from source

The system `dotnet` is SDK 9 and refuses the net10.0 game references; build with the
user-scoped SDK:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" build Tallybook\Tallybook.csproj -c Release
```

Game assemblies resolve from `%APPDATA%\Vintagestory`; override with
`-p:VintageStoryPath=<dir>`.

## Compat regression testing

Two automated gates, both run from PowerShell at the repo root.

**`.\tools\compat-test.ps1`** — run after any code change and before every commit. Builds
the zip and boots a headless dedicated server for every mod combination (solo, +each
companion mod, and all together), failing on any `[Error]`/`[Warning]` in the server log, a wrong mod count
or load order, or a violated marker. Because Tallybook is client-side only, the pinned
markers are: the server must still *load* the assembly and instantiate its mod systems
(proves the DLL works against that game version), and the mod must stay completely silent
otherwise — exactly one `tallybook` mention in `server-main.log` (its load-order entry) in
every combo. Companion zips are cached in `tools/compat-cache/` (gitignored), pulled from
the live Mods folder or the mod DB on first use. `-SkipBuild` reuses the packaged zip.

**`.\tools\version-sweep.ps1`** — run at the end of every version, before the release commit.
Builds the zip once, then runs that same artifact through the full compat matrix against real
dedicated servers for **1.22.0 through 1.22.6**, downloaded from the official CDN and cached
in `tools/server-cache/` (gitignored). This is what backs the `"game": "1.22.0"` dependency
declaration. `-Versions 1.22.0,1.22.6` checks just the endpoints; `-KeepGoing` reports every
version rather than stopping at the first failure.

Headless boots validate zip packaging, modinfo/dependency declarations, assembly loading
across game versions, and the client-only gate — **not** client behaviour, which for this mod
is most of it. Manual pre-release checklist for what the server can't see:

1. **Pin flow** — handbook page shows "Add to Tallybook" and "Go to Tallybook" (the latter
   must close the handbook and open the list, with no flicker or stuck dialog); clicking pins, re-clicking
   increments, and the L dialog and K HUD both reflect it. Pin one of the larger bookshelf
   variants: the pinned recipe's plank count must match that page (attribute-distinct
   variants share a code; the 8-plank page must never pin the 5-plank shape), and pinning a
   second bookshelf shape must create a second row, not increment the first.
2a. **Animal bags** — with the Options toggle on, put items in an owned elk's saddlebags:
   they must count toward Have while riding *and* while standing near it, stop counting once
   you walk out of range, and never count when the toggle is off. On a server, another
   player's animal must never count toward yours.
2. **Counting** — pick up and drop ingredients; dialog rows and HUD lines must flip state
   immediately, not on a timer. Pin an uncraftable item (low-quality soil x64): its
   have/needed must climb as you collect, reach "got it" at the target, and — for a
   craftable pin — acquiring the item itself must shrink its ingredient requirements. "Any wood" rows must count all woods collectively. Recipe
   tools (pin a glass slab: saw) must appear in the HUD's tools section and flip between
   ✓ and missing as the tool is picked up and dropped — including tools introduced by an
   expanded node's recipe, not just the pinned item's own. The glass slab's ingredient row
   must be the glass *block* — if it reads "glass slab", the self-consuming placement-mode
   pseudo-recipe has leaked back into the index.
3. **Counts** — steppers and direct numeric entry; typing must not lose focus mid-number;
   stepping or typing to 0 unpins immediately, no dialog.
4. **Expansion math** — expand a node while partially stocked; children must size to the
   deficit, shrink as you craft, and scale with the root pin count. Confirm the cycle guard
   refuses with a visible reason, and the recipe switcher recomputes children.
5. **HUD leaves rule** — expanding a node must remove it from the HUD's merged gather totals
   and replace it with its children; collapsing restores it.
6. **HUD corner anchoring** — with the minimap and the coordinates/clock overlays visible in
   the HUD's corner, the HUD must sit below all of them, overlapping nothing (within ~1s of
   any of them toggling) and must stay clear after a window resize or GUI-scale change; with
   the corner empty it rises to the top margin.
7. **Alongside other client GUI mods** — open Tallybook's dialog and HUD with other mods'
   GUIs active: no hotkey collision, no overlapping/hidden GUI, all HUD elements readable,
   and the Tallybook HUD must not fight the vanilla coordinate overlay for its corner.
8. **Persistence** — relog and confirm pins, counts, chosen recipes, expansion state, and
   checked/unchecked state survive; corrupt the JSON by hand and confirm it degrades to an
   empty list, never a crash. A pinned bookshelf variant must come back as the same variant
   after relog.
9. **Park/unpark** — uncheck a pin: its tree disappears from the dialog, its rows leave the
   HUD immediately, and unchecking the last active pin hides the HUD entirely. "Uncheck all"
   must ask nothing; re-checking (checkbox, or re-pinning from the handbook) restores the
   tree with expansions intact.
10. **Hold-to-unpin & long names** — holding Unpin counts down on the button and
    removes the pin only when held to the end; releasing early or sliding off the button
    cancels, and a short tap just shows the hint. Pin something with a long ingredient name
    (crude shield: bundle of bamboo stakes): every dialog row and HUD line must stay on its
    own line, truncated with "…", never wrapping over the row below — and hovering a
    truncated dialog row must show the full name.
11. **Handbook button** — must work on a fresh login *before* the handbook has ever been
    opened (it is not registered with the GUI manager until then). On a pin row it must close
    the list and open the handbook on that item's page, and must work from **both** tabs: an errand and a goal copied from one are
    separate pins, and a pin's identity is not itself a handbook page code. For an attribute
    variant (bookshelf shapes) it must open the *same* variant that was pinned, not just the
    first page sharing its code. A "← Back to Tallybook" button must
    then sit under the handbook's own Back button, return you to the list, and follow the
    handbook if the window is resized or the GUI scale changed; close the handbook and press
    H afresh and it must not appear, since there is no journey to return from.
12. **HUD icons** — pin something **perishable** (raw hide, food) and confirm its icon draws
    in both the HUD and the L window: perishable items take a transition-state code path
    that a slotless icon renderer crashes on. Every HUD line (headers, gather, tools) shows the item's icon; an "any
    wood" row cycles through woods once a second. Flipping "Cycle icons" in the L window
    takes effect immediately, without reopening anything, and freezes rows on their first
    variant.
13. **Villager errands** — accept a fetch quest (Gerhardt, 10 small hides): it must appear
    in the list as a `(for <name>)` pin at the right count with a light-blue marker on the
    NPC, announced in chat. Unpin it mid-conversation: it must stay gone until the
    conversation ends, then reappear next time you talk. Uncheck it instead: it must stay
    unchecked across conversations. Accepting twice must not stack the count past what was
    asked, an NPC whose quest you have *not* accepted must add nothing, and conversations
    must behave exactly as they do without the mod. Errands must appear under **Side
    quests**, never in Items, with a light-blue `x` marker on the giver. Uncheck the errand
    and the marker must vanish; re-check it and it must come back; unpin it and it must go
    for good — and none of that may disturb any other waypoint you have placed. **Map** must
    open the world map centred on the giver. Critically, *paid services must not be mistaken
    for errands*:
    ask Tad to heal your wounds (one gear) and decline — nothing may be added, either way.
    An errand must show only what was asked for: take Agnieszka's 8 iron ingots and the row
    must be the ingots alone, with no ingredient or tool rows underneath.
14. **Quest-ready glow** — with a tracked errand incomplete, the NPC looks normal; collect
    the last item and a gold shimmer must appear over them (colour correct, sitting just
    above the head on both a villager and a taller trader), and stop once the items leave
    your inventory. An NPC with two tracked requests must not glow until *both* are met.

## Design notes

Two decisions that look like missing features and are not:

- **Ingredient expansion is never automatic.** Auto-recursion runs into recipe cycles,
  per-level recipe-choice explosions (six woods × three grids × smith-vs-cast), and silent
  wrong guesses — every one of those makes the list lie. Expansion is always a deliberate
  click with a recipe attached to it.
- **Only carried inventory counts** (hotbar, backpacks, and your mount's bags while riding) —
  not nearby chests. The question is "what do I still need on me", and answering a different
  question confidently is worse than not answering it.

## License

MIT — see [LICENSE](LICENSE).
