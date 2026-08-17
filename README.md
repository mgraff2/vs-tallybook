# Tallybook

A client-side crafting shopping list for [Vintage Story](https://www.vintagestory.at/).
Pin any item, and Tallybook tells you what you still need to gather — with live
inventory tracking and at-a-glance status. The handbook already answers "how do I make X";
Tallybook answers "what do I still need, and am I done?"

**Version 0.3.16**, for Vintage Story 1.22.0–1.22.7. Client-side only: it works on any server,
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
   **Journal** button for the lore you collected along the way. A **World** tab
   (on by default, switchable off in Options): a reference card of this world's rules — every
   world-config setting the installed mods declare (world generation, survival challenges,
   temporal stability, all of it), grouped under the create-world screen's own headings, with
   values that differ from the game's defaults drawn in colour and each setting's description
   on hover — plus the full list of mods this world runs, with versions, including server-side
   mods your client never loads. A filter box narrows the list as you type, matching names,
   values, codes and descriptions alike. Handy on a server where you didn't write the
   settings yourself. A **Player** tab (also on by default) tracks your
   spawn points — the world spawn and your temporal-gear returning point, each with a Map
   button and a maintained map marker that follows the point and leaves when it is used up
   or moved — plus respawns left at the returning point, deaths in this world, lives left
   where the server grants a fixed number, character class, and temporal stability. A
   checkbox at the tab's foot puts a "Spawn distance: 1,250 blocks" line in the HUD,
   measuring to wherever you would respawn right now, with an optional warning distance
   past which the line turns a colour you pick. An **Explore** tab (on by default) is a
   place journal: save the spot you are standing on — a mine, a ruin, a cave — with a name
   and a one-line "what it is" (`.tallybook spot <name>` does it from chat). Each place
   gets an orange star map marker, a live distance, a Map button, and the side-quest
   checkbox contract: checked places show on the HUD with the distance back; unchecked are
   parked. Longer notes sit behind a +/− fold — one free-text field where "- " lines draw
   as bullets and "[ ]" lines as clickable checkboxes — with an Edit window for the name,
   description and notes. Removing a place is the same hold-to-confirm as unpinning, and
   an optional hotkey (unbound by default) opens straight to the tab. A **Lore** tab (on by default too),
   tracks the writings you collect — books, scrolls, tapestries land in your journal, and
   this tab counts them against everything this world's content defines: volumes discovered,
   chapters collected, and how many volumes are still hidden (counts only — titles stay the
   world's secret). Found volumes list two to a row with a **Read** button that opens the
   game's journal directly on that entry, arranged side by side with the list; filter by
   status (All / In progress / Complete), by source mod (Vanilla / each lore-adding mod), and
   toggle story lore — writings only the story's own places hold, recognised from the world's
   files — separately from world lore. An **Export book** button writes everything you have
   found as a single printable HTML book, ordered like the tab, ready for a browser's
   print-to-PDF. The first two tabs are a table of icon,
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

### Quests from the VS Quest framework

Servers running the **VS Quest** framework (VS Village and others build on it) get the same
treatment. Accept a quest at its giver and anything it asks you to *bring* lands on the list
as an errand — count, giver, map marker, HUD row, and the gold ready-shimmer once you are
carrying it all. Quests that also count kills or blocks show how far along those are as of the
last time you had the quest window open, because that is the only place the game sends those
numbers to your client; the shimmer waits until everything it can verify is satisfied, so it
under-promises rather than sending you on a wasted walk.

Everything comes from the framework's own quest files, so no per-pack support is needed. Walk
back into range of a giver and any quest you already accepted from them is restored — which is
how the list rebuilds itself on a machine that has never seen the world before. Only quests you
have already taken are ever restored; new ones still come from talking to the giver, so nothing
is spoiled. Finished quests move to the History tab with what you handed over and what you got
back. `.tallybook vsquest` explains what it can see, and `.tallybook vsquest track <quest id>`
lets you assert a quest you accepted on another machine before you get back to its giver.
`TrackVsQuests` turns the whole thing off.

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

**Map artifacts become side quests** too. Read a locator map — a treasure map, one of Better
Ruins' ruin maps, any mod's map that marks your world map — and the place it marks joins the
Side quests tab: *Visit the Abandoned Mine*. Standing at the site marks it visited, and where
a site hides writings found nowhere else in the world — the Sunrift Experiment hides
seventeen — the quest keeps counting after you arrive (`5/17`), read from your journal and
your bags. The row's toggle lists what you have recovered so far; what you haven't stays the
site's secret, only the count shows. Complete the set and the whole hunt moves to History.
Story maps (the Devastation, Tobias' cave…) stay with the story block rather than doubling
up here. Site quests carry the same checkbox as everything else — checked ones show on the
K HUD with a live distance to the site, unchecked ones park quietly on the tab.

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
| `TrackVsQuests` | `true` | track quests from the VS Quest framework (the one VS Village and others build on) as errands |
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

`.tallybook sites` prints every destination this world's locator maps can mark, which of
them hide provable writings, and where each tracked site quest stands; `.tallybook sites
track <name>` brings back a site you dismissed.

`.tallybook vsquest` reports what the VS Quest integration can see, layer by layer: how many
quests this world's quest files hold, which ones you are tracked on and on what evidence, what
each nearby giver's own record says about you, and whether the quest window has been read yet.
`.tallybook vsquest track <quest id>` asserts a quest you accepted on another machine, before
you are back in range of its giver.

`.tallybook spot <name>` saves where you are standing as an Explore-tab place — the same
act as the tab's "Save this spot" button, without opening the window.

`.tallybook screenshots` photographs every Tallybook surface — the HUD and each tab — into
stable, feature-named PNGs in `ModData/tallybook/screenshots/`, for refreshing the mod page.
Each shot is cropped to the Tallybook window rather than the whole screen, held inside
ModDB's 1920×1080 ceiling, with the HUD shot always exactly 480×320. It only
navigates: nothing is pinned, unpinned or expanded, so the shots are of your real world. It
counts down a few seconds first, so move the mouse away and let the chat fade. Optional
arguments: `wait <seconds>` for a longer pre-roll, `pad <px>` for the margin around the
window, `full` for whole-screen shots, `noflip` if the images come out upside down, and
`stage final` if they come back blank (the run detects that and says so). What each shot is
for and how to stage it is in [docs/moddb-screenshots.md](docs/moddb-screenshots.md).

## Install

Drop `tallybook_0.3.16.zip` into
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
or load order, or a violated marker. Companions cover both halves of the surface: a
recipe-adding content mod, the VS Quest framework, and a client-side GUI mod with its own text
field. Because Tallybook is client-side only, the pinned
markers are: the server must still *load* the assembly and instantiate its mod systems
(proves the DLL works against that game version), and the mod must stay completely silent
otherwise — exactly one `tallybook` mention in `server-main.log` (its load-order entry) in
every combo. Companion zips are cached in `tools/compat-cache/` (gitignored), pulled from
the live Mods folder or the mod DB on first use. `-SkipBuild` reuses the packaged zip.

**`.\tools\version-sweep.ps1`** — run at the end of every version, before the release commit.
Builds the zip once, then runs that same artifact through the full compat matrix against real
dedicated servers for **1.22.0 through 1.22.7**, downloaded from the official CDN and cached
in `tools/server-cache/` (gitignored). This is what backs the `"game": "1.22.0"` dependency
declaration. `-Versions 1.22.0,1.22.7` checks just the endpoints; `-KeepGoing` reports every
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
2b. **Liquid ingredients** — pin dough: the water row must read as water with litres
   ("Water (in …) 0/1 L"), never as a bare bucket/bowl/jug, and the three vessel recipes
   must be one recipe, not three identical chooser entries. An **empty** accepted container
   must count 0; filling it with the right liquid flips the row; the wrong liquid in the
   right container stays 0. Pin a liquid itself (a bucket of milk in the bag): its Have
   must come from container contents. An errand pin's count must *not* rise from liquid in
   containers — hand-over checks want slot stacks.
2c. **Cooking-pot recipes** — pin sulfuric acid from its handbook page: it arrives folded
   (no pin ever auto-expands); Expand shows 1 L water + 2 powdered sulfur + 1 saltpeter
   (chooser says "cooked in a pot, up to 6 L per pot"), the water counting from *any*
   carried container. Porridge and other meals
   must still say "no recipe known" when gather-only is toggled — meals are deliberately
   not decomposed. In a grid recipe that takes acid in a jug, expanding the acid row must
   offer the cooking recipe.
2d. **Liquid units & pot loads** — a pinned liquid's count field means litres (hover says
   so; header reads "0/12 L"), and setting 12 L of acid must show "2 pot loads" on the row
   (12 servings, vanilla pot cooks 6) with ingredients scaled ×12 (12 L water, 24 sulfur,
   12 saltpeter). Carrying some acid must shrink both the ingredient demands and the pot
   loads; at 12 L the row reads "got it" and loads disappear.
2e. **Volume calculator** — the acid pin row shows "Volume Calc" in its own column,
   alongside the normal Expand/Collapse and (once expanded) the recipe cycler — liquid
   rows keep every standard control. The screen lists containers largest-first (Barrel
   50 L on top); picking Barrel ×5 shows "5 × Barrel = 250 L — about 42 pot loads to cook",
   and Set lands back on the list with the pin at 0/250 L, expanded, ingredients ×250.
   Typing a count updates the summary after a typing pause without stealing focus
   mid-number. A liquid pin with no recipe (milk) gets the calculator but no pot-load line
   and no recipe toggle. Container variants must collapse to one row per family and size —
   Eternal Stew's metal cauldrons are ONE "Cauldron" row, not one per metal — and the small
   containers (bowl, jug) stay visible in the two-column list, never hidden behind a cap.
2f. **The spirit chain (barrel/distill/press)** — pin Apple brandy, Volume Calc → 1 Barrel
   (50 L), Set. Expanding walks the whole chain by hand: the pin shows ~1000 L of apple
   cider (distillation, ratio 0.05); expanding cider shows apple juice with a barrel-seals
   suffix ("...barrel seals (~7 days each)"); expanding juice shows apples (fruit press).
   Mead spirit walks to honey, grain spirits to flour + water. Barrel products beyond
   drink — tannin, lime water, dyes — must expand too. Carrying cider in any container
   counts toward the cider row (barrels and boilers are filled by pouring — no vessel
   constraint, unlike grid recipes).
2g. **Multi-path liquids** — pin Aqua Vitae (it distills from every spirit): the pin must
   arrive **folded** (counting only, chat says "N ways to make it"), with Expand, Volume
   Calc and Handbook all on the row. Expand must open a chooser with the paths **grouped
   by origin** — "Fruit" (all juices and mead), "Grain" (the mashes) — each category
   sorted, two columns, one line per path; picking one plans the tree down that path and
   shows the 1/N cycler. The choice is remembered as a *preselection only*: re-pinning the
   same item later still arrives folded, and expanding lands on the remembered path. No
   pin auto-expands, single-recipe ones included; a small chooser (bandages) keeps the
   two-line uncategorized layout.
2h. **Grinding & crushing** — under an expanded sulfuric acid, "Powdered sulfur" must
   offer Expand, unfolding to sulfur chunks ("ground in a quern"); a flour row under a
   whiskey mash expands to its grain. Counting the input works like any solid row.
2i. **Smelting & smithing** — pin a copper ingot: Expand offers the smelting paths (20 ×
   nuggets, "smelted in a crucible", one entry per smeltable source) listed before the
   anvil+chisel grid recycler. The nugget row expands to crushed ore. Bread must expand to
   dough ("baked"), cooked meat to raw ("cooked over fire"). No item may list itself as
   its own smelting source (ingots cast from ingots). Pin an IRON ingot: it expands to
   1 × Iron bloom ("smithed on an anvil"), and the bloom to 20 × iron nuggets — the
   bloomery chain, not a crucible.
2j. **Alloys & method sections** — pin ONE bismuth bronze ingot: the chooser leads with
   "Alloyed in a crucible" and the rows read 60/25/15 units (linear — 3 ingots read
   180/75/45; never whole-batch multiples) as "Copper — bits, nuggets or any meltable
   (50–70%)" (the metal named bare, never "Copper ingot") and sections the rest by method — the melt-your-chain-down entries under
   "Smelted in a crucible", the anvil chisel under "Crafting grid" — never one section per
   input item. The copper row must count nuggets and bits at 5 units each — 20 copper
   nuggets reads 100 u — and a whole copper ingot must count **nothing** (the crucible
   refuses ingots; chisel it into bits and the 20 bits read 100 u).
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
6a. **Typing must never trigger a hotkey** — with another mod that has a text field in its own
   window (Boat Autopilot's route planner in the map screen is the case this was found on),
   type a name containing **l** and **k**: the letters must land in the box and neither the
   Tallybook window nor the HUD may react. Then the reverse: with a Tallybook count field, the
   world filter, or a place's name focused, type letters bound to other mods' hotkeys and
   confirm nothing of theirs fires. Escape must still close the Tallybook window from a focused
   field, and pressing L with *nothing* focused must still open and close it as always.
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
    first page sharing its code. Ingredient and tool rows under an expanded pin carry the
    button too — it opens the page for the item the row's icon shows (a liquid row opens
    the liquid's page, not a container's). A "← Back to Tallybook" button must
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
13a. **VS Quest errands** (needs `vsquest` plus a quest pack — the framework alone ships one
    quest, which asks for nothing to be gathered and so must correctly add *nothing*). Accept
    a quest with a "bring me N of these" objective: it must appear under **Side quests** as a
    `(for <giver>)` pin at the right count, announced in chat, with the giver's marker.
    `.tallybook vsquest` must name the quest, the giver's entity id, and where the knowledge
    came from. Then, in order:
    - **Counting follows the framework, not us**: for an objective with several valid codes or
      a `code-*` wildcard, carrying *any* accepted variant must count, and the row must say
      "any of N". Compare against the framework's own Complete button — the two must agree.
    - **Unpin mid-window**: it must stay gone while the quest window is open, and come back the
      next time you open it. Uncheck instead and it must stay unchecked.
    - **Restore on approach**: unpin the errand, log out, delete the world's Tallybook save
      (`ModData/tallybook/<world>.json`), log back in — the list must be empty until you walk
      into range of the giver, then the errand must return by itself with its count and
      position. Walk past a giver whose quest you have *not* accepted: nothing may be added,
      ever. That is the whole spoiler rule — no quest may appear that you were not already on.
    - **Kill/block objectives**: a quest that also counts kills must show its progress with
      "as of your last talk with them"; before the window has ever been opened it must say the
      counters are not known rather than showing zero.
    - **Completion**: hand the quest in at the giver — the pins must leave the list and a
      record must appear on the History tab naming what you handed over and what you received.
      A repeatable quest taken again after completing must come *back* onto the list, not stay
      archived.
    - **No cross-talk with villager errands**: on a world with both, a vanilla errand and a VS
      Quest errand for the same item must both exist as separate rows, and finishing one must
      not remove the other.
14. **Quest-ready glow** — with a tracked errand incomplete, the NPC looks normal; collect
    the last item and a gold shimmer must appear over them (colour correct, sitting just
    above the head on both a villager and a taller trader), and stop once the items leave
    your inventory. An NPC with two tracked requests must not glow until *both* are met.
    For a VS Quest giver the glow must follow the *entity*, not the name: with two identically
    named villagers, only the one holding your quest may shimmer. A quest whose kill counter
    was last seen short of its demand must not glow even when the items are all carried.
15. **World tab** — on by default; "Show the World tab" in Options turns it off
    (flipping it on must add the tab on Back without reopening, flipping it off
    while *on* the tab must land you back on Items, and the choice must survive a
    restart). Once shown, the settings match what the world was created with: check a handful
    against the create-world screen (or the server's serverconfig) including one you
    deliberately changed — the changed one must draw in colour with its default named in
    the hover, defaults must draw plain, and dropdown values must read as the create
    screen's labels ("5 days before monsters appear"), not raw codes. Long values
    (temporal storms) truncate with "…" and show in full on hover. The seed/size line must
    match the coordinate HUD's world. On a world with a config-declaring content mod, its
    settings must appear (hover naming the mod) with no per-mod code; paging must work on
    a heavily modded world where the list runs past one screen. The Mods section must list
    every mod with its version: on a dedicated server that includes server-side-only mods
    (hover says so), and Tallybook itself must appear marked client-side — it is never in
    the server's announcement, so its presence proves the union of both lists works.
    The filter must narrow as you type *without losing the cursor* (type a whole word in
    one go — every keystroke rebuilds the screen, so a dropped cursor truncates the word),
    match by description ("mobs" finds the grace timer), keep section headings on hits,
    clear via the × button, reset when the dialog is reopened, and say so when nothing
    matches. While typing in it, pins changing in the background must not steal the box.
16. **Player tab** — on by default; "Show the Player tab" in Options turns it off. While on:
    the world spawn row's coordinates must match the coordinate HUD's numbers for that
    spot, its Map button must centre the map there, and a green "home" marker must appear
    on the map. Use a temporal gear: within a second the returning point row must appear
    with coordinates, distance, Map button, a second marker, and "Respawns left there"
    showing the full temporalGearRespawnUses budget. Die once: the count must drop by one
    (compare against the game's own "will vanish after N more uses" respawn message —
    they must agree). Spend the last use: the row must revert to "none", the marker must
    leave the map, and no marker may be re-planted afterwards (the stale synced packet is
    latched out until relog). Deaths must match `/player` bookkeeping expectations and
    rise by one per death. Switching the tab off must remove both markers; switching the
    "quest map markers" master option off must too. A returning point that existed before
    Tallybook was installed must show "not known" for respawns left, never a guess.
    The "Show my distance from spawn in the HUD" checkbox at the tab's foot must add a
    "Spawn distance: N blocks" line to the top of the HUD whose number matches the Player
    tab's exactly when standing still, and must keep the HUD visible with an empty list.
    Enabling it must reveal the warning sub-row; with a warning distance below your
    current distance the line must draw in the dropdown's colour, and raising the distance
    above it must return the line to white. Typing in the blocks field must not lose
    focus or fight the cursor, and the choices must survive a relog.
17. **Lore tab** — on by default; the intro numbers must match your journal (count an
    entry's chapters by hand against its volume's row). Read something new: the tab must
    update within a second — a tapestry too, which moves no inventory slot. Read on a
    volume must open the game's journal on that exact entry, with the two windows side by
    side, not overlapping; clicking around inside the journal must not re-centre it, and
    closing either window must return the other to centre. The status chips, source
    dropdown (on a world with lore-adding mods) and story/world toggles must each
    re-count the intro line to their scope, with the source named in it; filters must
    reset on reopen. Export book must write the HTML file, say so in chat with the path,
    order it exactly as the tab does (Vanilla part first, then each mod), and contain
    only found chapters — with per-volume "still undiscovered" counts and never an
    unfound title. All of it must read identically from a second computer on the same
    server (the journal is server-side).
18. **Explore tab** — save a spot: the row must appear checked with a star marker on the
    map, distance live, and a HUD line "Name — what it is (Nm)". Notes: the +/− fold only
    appears once notes exist; "[ ]" lines must tick/untick with one click and survive a
    relog; the Edit window must change name (marker follows), description and notes, and
    Cancel must change nothing. Remove must demand the same hold-countdown as Unpin.
    Unchecking must park the row (dimmed, off the HUD) without losing anything.
19. **Side-quest ordering** — with a mix of errands AND map-site quests: every sort mode
    must reorder the whole list identically on the tab and the HUD, and under Custom every
    row (sites included) must move with ^ / v, the arrangement surviving a relog.
20. **Construction (needs vanilla rollers or a Shipwright roller)** — pin the roller:
    Expand must show only its own recipe; Construct must add a separate "… construction"
    pin whose tree lists the starter (expandable to its recipe) plus every stage total.
    Carrying the starter must NOT mark the construction pin complete or shrink its
    demands; both pins must coexist and count independently.

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
