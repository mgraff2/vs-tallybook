# Changelog

## 0.3.14 — 2026-08-14

- New: **Lore tab** (on by default; Options can hide it) — your journal against everything
  this world's content defines. The books, scrolls and tapestries you read land in the
  game's journal; this tab counts them: volumes discovered, chapters collected, volumes
  still hidden (counts only — unfound titles stay the world's secret). Everything is read
  from the server-synced journal and the world's own lore files, so progress is identical
  on every computer you play from and modded lore counts with zero compat work.
- Found volumes list two to a row, clustered by source — Vanilla first, then each
  lore-adding mod — with unfinished volumes ahead of complete ones. Slice the view by
  status (All / In progress / Complete), by source mod (a dropdown, one entry per mod
  with counts), or toggle **story lore** — writings only the story's own places can hold,
  recognised from the world's worldgen files rather than any hand-kept list — separately
  from world lore. The intro numbers re-count to whatever scope you pick, named so a
  smaller total never reads as lore going missing.
- Every found volume has a **Read** button that opens the game's own journal directly on
  that entry — with the two windows arranged side by side instead of stacked, for as long
  as both are open. An **Open journal** button sits next to **Export book**.
- **Export book** writes everything you have found as one printable HTML file (path told
  in chat): cover page, contents, one section per volume in exactly the tab's order, with
  per-volume "chapters still undiscovered" counts. Open it in a browser and print to PDF.
- The World and Player tabs are now **on by default** (the Options switches remain). A
  config that recorded the old off-default is brought forward once; a deliberate off needs
  one revisit to Options.

## 0.3.13 — 2026-08-13

- New: **spawn distance in the HUD** (checkbox on the Player tab). A "Spawn distance:
  1,250 blocks" line at the top of the HUD, measuring to wherever you would respawn right
  now — your temporal-gear returning point when one is set, otherwise the world spawn.
  The exact same number the Player tab shows (it refreshes in 5-block steps while you
  walk, and settles to the block when you stop). With the line enabled the HUD stays up
  even when your list is empty.
- An optional warning distance under it: past that many blocks from spawn, the line turns
  a colour you pick from a dropdown (red by default — or orange, yellow, green, blue,
  purple, white; any other hex written into the config file is honoured as Custom). A
  leash length for how far you are comfortable ranging from a respawn. Leave it off and
  the line never changes colour.

## 0.3.12 — 2026-08-13

- New: **World tab** (opt-in — switch it on in Options) — a fourth tab in the L window: a
  reference card of this world's rules. Every world-config setting the installed mods declare (world generation, survival
  challenges, temporal stability, spawn and death, multiplayer), grouped under the
  create-world screen's own category headings, showing the value this world actually runs
  with — resolved to the same labels the create screen uses, not raw codes. Values that
  differ from the game's defaults draw in colour, with the default named on hover alongside
  the setting's description; settings a content mod added say which mod on hover. The top
  line carries the seed and world size. Settings the world carries that no installed mod
  declares (a server-side mod's, or a removed mod's) are listed too, under "Other settings" —
  the list never pretends to be complete when it isn't.
- The World tab has a freeform filter box: matches against a setting's label, value, raw
  code and description, so "monsters", "graceTimer" and "grace" all find the grace timer.
  A matching category name keeps its whole section. Filters as you type.
- New: **Player tab** (opt-in — switch it on in Options): your spawn points and your
  numbers. The world spawn and your temporal-gear returning point each get coordinates
  (spawn-relative, as the coordinate HUD shows them), a live distance, a Map button, and a
  maintained map marker — the marker appears when a point is set, follows it when it
  moves, and is removed when the point is used up, cleared, or the feature is switched
  off. "Respawns left there" counts the returning point's remaining uses against the
  server's temporalGearRespawnUses budget; the server keeps the real number to itself
  between logins, so this is Tallybook's own death-count arithmetic from the moment the
  point was set — a point that predates the mod honestly says "not known" rather than
  guessing. Below that: deaths in this world (the server's per-world count — kept live by
  watching your own deaths, since the game only re-syncs the counter at login), lives
  left when the server sets playerlives, character class, temporal stability, and today's
  date. `.tallybook spawn` prints everything the tracker can see, layer by layer, for
  when a number looks wrong.
- The World tab also lists every mod this world runs, with versions — the server's own
  handshake announcement (which is the only place a server-side-only mod is visible to a
  client), plus anything client-side the server never sees, Tallybook included. Hover names
  the mod id and which side a mod lives on.

## 0.3.11 — 2026-08-11

- New: map artifacts become side quests. Reading a locator map (Better Ruins' ruin maps,
  vanilla treasure maps — anything using the game's locator-map convention) plants a titled
  waypoint; Tallybook adopts it on the Side quests tab as a place to go. Standing at the
  site (within ~64 blocks) marks it visited. Sites with nothing further to prove — the
  Abandoned Mine — complete on arrival and move to History.
- New: where a site hides writings that exist *nowhere else* in worldgen — the Sunrift
  Experiment's 17, the Coal Mine's miner's log — the quest continues past the visit and
  counts them ("5/17"), read from your journal and your bags. The row's toggle lists what
  you have found (never what you haven't — titles stay the site's secret, only the count
  shows), and completing the set archives the whole hunt to History. Derived per world by
  scanning the installed content's worldgen for site-exclusive lore, cached after the first
  look; no mod is special-cased.
- Story-map waypoints (the archives, the Devastation, Tobias' cave…) are deliberately not
  adopted — those walks already belong to the story block.
- New: `.tallybook sites` ties the whole thing out — every locator destination this world's
  items can mark, which have provable writings, and where each tracked site stands. A
  dismissed site comes back with `.tallybook sites track <name>`.
- Fixed: Better Ruins' trader barters were captured with the wrong amount — the Luxuries
  trader's "10 rusty gears for the Forlorn Hope map" tracked as a single gear, because BR
  writes `quantity:` where vanilla writes `stacksize:` and the game accepts both (they are
  documented aliases). Both keys are read now.
- Fixed: the same errand could appear twice — once "(for Luxuries)" from the login scan
  (named after the dialogue file) and once "(for Trader)" from the live conversation (named
  after the entity) — doubling what you owed. Player-scope errands are now recognised as one
  quest regardless of which name captured them: existing duplicates merge at login (keeping
  position, text and checkmarks), and both capture paths refuse to create a twin.
- Fixed: a completed trader barter could sit on the Side quests tab forever — a drifted
  giver name or amount meant the finished errand never matched its dialogue entry. While you
  are talking to an NPC, their own dialogue file now identifies the errand as a fallback, so
  the purchase archives to History on your next chat with them. The record also says what
  the hand-over gave back ("Received: Map to the Forlorn Hope Tower"), so a barter no longer
  reads as goods vanishing for nothing.
- Fixed: an errand's saved conversation could retell every thread its NPC carries — the
  gear pin narrating the iron-pickaxe story and vice versa. Briefings are now scoped to the
  thread that actually set each errand.
- Fixed: a finished trader barter came back forever — its dialogue gates never close, so
  every conversation re-offered the completed purchase as a fresh errand, and the sweep's
  "checked pins are deliberate" exception then refused to remove it (the immortal
  "Rusty gear √ 221/1"). A hand-over that already happened is no longer offered at all,
  and a pin over a recorded-done errand now leaves the list regardless of its checkmark.
- Changed: History names a barter for what it was **about** — the bought map's destination
  ("Forlorn Hope Tower", "The Sunrift Experiment") — with the items as sub-lines
  underneath: "Handed in: 10 x Rusty gear to Trader.", the conversation, "Received: Map to
  the Forlorn Hope Tower." Records written by earlier builds are reshaped on their own.
- New: `.tallybook version` prints the exact build stamp of the running DLL — the check for
  "I restaged the zip but nothing changed", which is usually the previous build still
  loaded (a restaged zip needs a full game restart).
- New: site quests carry the same checkbox as everything else (Mark) — unchecked parks one:
  off the HUD, no arrival announcements, row dimmed; nothing is lost and facts still latch
  quietly. "Uncheck all" covers them too.
- New: checked site quests show on the K HUD in the side-quests section, with live distance
  to the site ("The Sunrift Experiment 3/17 (140m)"), exactly like errands show their giver.
- Fixed: History regrouping skipped records whose item several errands ask for (rusty gears
  are wanted by half the world), leaving "Rusty gear for Trader" un-renamed. The record's
  own archived transcript now identifies its errand — the words that were said are unique
  where the item is not.

## 0.3.10 — 2026-08-10

- Fixed: a finished errand stayed on the Side quests tab, unchecked, until you unpinned it
  yourself. Handing the goods over now takes the pin off the list — the History tab is where
  a finished errand lives (and its map marker is cleaned up with it). Leftover parked pins
  from earlier versions are swept up too; re-pinning a finished errand yourself still works
  and is never fought.
- New: a finished quest's history now records the whole conversation, not just the ask —
  what you said when you brought the goods, what they said back (Agnieszka's "my usual
  rate" included), and, once you collect a reward, that exchange too (Beata's "so glad").
  Recovered from the dialogue files like the briefing is, so records written by earlier
  versions gain the missing parts on their own, and quests awaiting a reward ("go and
  collect") show the story so far. The payout lines join only after the reward is actually
  collected — never as a preview.
- Fixed: the gold shimmer over a quest giver you are ready to hand goods to never actually
  appeared — the entity query it relied on silently returns nothing (the same API already
  banned elsewhere in the mod for exactly that). It now scans loaded entities directly.
  The shimmer also gained a steady glowing orb at its heart, and the sparkles glow softly,
  so the flag reads in a dark room and from across a village square.
- New: a quest that is handed in but not yet paid out ("done — go and collect") now shows on
  the Side quests tab as "Beata — done, collect your reward", with a Map button to whoever
  pays it — which is not always who took the goods (Kat takes Beata's bread; Beata pays for
  it). The same NPC gets the gold shimmer until you collect.
- New: `.tallybook glow` diagnoses the shimmer end to end — option state, who should be
  glowing and why, every conversable NPC in range with whether their name matches — and
  fires a test burst over your own head so "does the particle render at all" has a visible
  answer. Name matching is case-insensitive now, and a glow error no longer writes the
  feature off into your saved config (it stops for the session and says so in the log).

## 0.3.9 — 2026-08-10

- Fixed: recipes that call for a liquid — dough, alcohol-soaked bandages, ink and quill,
  modded dyes and acids — listed the empty container ("Bowl") instead of what actually
  matters, and worse, counted an *empty* bowl as satisfying the row while the crafting grid
  would refuse it. Liquid ingredients now show the liquid itself with litres ("Water (in
  Bowl / Bucket) 0.5/1 L"), count only liquid sitting in a vessel the recipe accepts, and
  use the game's own content matcher so the row can never claim materials the grid rejects.
  Reported on ModDB. (The handbook itself linking a liquid ingredient's icon to the
  container's page is vanilla behaviour Tallybook cannot change.)
- Recipes that differ only in which vessel holds the liquid (dough via bucket, bowl or jug)
  are now one recipe with one liquid row that accepts any of the vessels, instead of three
  seemingly identical choices.
- Pinned liquids (milk, honey, a modded acid) are now counted from what your carried
  containers hold — a liquid can never sit in a bare inventory slot, so before this a
  pinned liquid could never be "had". Errand pins still count only slot stacks, since a
  hand-over check does not accept a jug of the goods.
- New: cooking-pot recipes. Vanilla 1.22 cooks sulfuric and nitric acid, glue, potash,
  sulfate, leather and more into existence in a cooking pot (`cooksInto`), and none of that
  was visible to Tallybook — pinning sulfuric acid said "no recipe known". Such items now
  expand like anything else: sulfuric acid shows 1 L of water (counted from any carried
  container — the pot takes a pour, no specific vessel), two powdered sulfur and one
  saltpeter, with the chooser noting "cooked in a pot". Meal recipes stay out deliberately;
  a meal's identity lives in its bowl's attributes and is a different product entirely.
- Expanding a liquid ingredient row keeps the chain walkable across recipe kinds: the acid
  inside a grid recipe's jug expands into its cooking recipe, whose own water row expands no
  further.
- Fixed: a liquid with many recipes — Aqua Vitae distills from every spirit in the game —
  silently expanded down the first path (apple) with no way to choose another. Two changes:
  a freshly pinned item with **several** recipes now starts as plain counting, and Expand
  opens the recipe chooser (single-recipe handbook pins still start expanded, and a
  remembered choice is honoured); and liquid rows got their standard Expand/Collapse and
  recipe-cycler buttons back — Volume Calc moved to a column of its own (the list is a
  little wider for it). The chooser states liquid outputs in litres ("Makes 1 L of Aqua
  Vitae") and compacts to one line per choice when there are many, so twenty brandy paths
  fit on screen.
- Big choosers now **group and sort their paths by origin**: Aqua Vitae's thirty-two ways
  arrange under "Fruit" (every juice path *and* mead) and "Grain" (the mashes), each
  category sorted, in two columns. Categories come from the game's own food classification
  met while walking each chain (cider declares Fruit or Grain per variant), falling back to
  ingredient code families — derived entirely from data, so any other many-path item
  (modded or vanilla) gets the same treatment automatically. Berries and orchard fruit
  share "Fruit" deliberately: the game records no distinction between them, and guessing by
  name is against house rules.
- New: **crucible alloys**. Bismuth bronze ingot's real recipe now leads its chooser —
  one row per metal, named bare ("Copper", never "Copper ingot"), in the game's own metal
  units: "1200 units Copper — bits, nuggets or any meltable (50–70%)" counts nuggets and
  bits at 5 units each, exactly as the crucible will. Whole ingots deliberately count
  nothing — a crucible refuses them; chisel them into bits and the bits count. Ratio
  windows come from the game's alloy registry; quantities use the midpoint at the smallest
  whole batch. The melt-something-back-down entries and the anvil chisel are still there,
  sectioned below it.
- Fixed: an ingot "made by smelting an ingot" no longer appears — an item that smelts into
  itself is the crucible's casting bookkeeping, not a recipe (same rule as the
  self-consuming grid pseudo-recipes).
- Choosers that mix recipe **kinds** now group by method — "Alloyed in a crucible" /
  "Smelted in a crucible" / "Crafting grid" — instead of by each input's name; origin
  grouping (Fruit / Grain) still applies when every path is the same kind.
- Fixed: alloy quantities were charged in whole 20-ingot batches — wanting one bismuth
  bronze ingot demanded 1200 units of copper (found against a reference alloy calculator).
  Alloying is continuous: one ingot now reads 60/25/15 units, scaling linearly with the
  pin count.
- New: **anvil smithing** as a recipe step, closing the iron chain: iron ingot → 1 iron
  bloom ("smithed on an anvil") → 20 iron nuggets (bloomery). Plates, chains, tools and
  everything else hammered on an anvil expands too, with input counts computed the way the
  handbook computes them (recipe voxels against what one workpiece provides).
- New: **smelting** as a recipe step. Copper ingots finally expand the way anyone actually
  makes them — twenty nuggets smelted in a crucible — instead of only offering "chisel a
  copper anvil back into ingots"; where both exist, smelting is listed first and the
  recycler last. The same data covers every metal and ore, plus baking (dough → bread),
  cooking over fire (raw → cooked meat) and kiln firing (raw → fired pottery), each labeled
  with its real method. Chains link up: ingot → nuggets → crushed ore.
- New: **quern grinding and pulverizer crushing** as recipe steps. Powdered sulfur expands
  to sulfur chunks ("ground in a quern"), grain grinds to flour — completing the whiskey
  chain down to the harvest — and ores crush to grits. Derived from the game's own
  grinding/crushing properties on each item, the same way distillation and pressing are.
- **No pin auto-expands anymore, full stop.** Every fresh pin — single recipe, many
  recipes, remembered choice or not — arrives as plain counting; Expand is always your
  act, the chat message says what it will show, and a remembered choice only preselects in
  the chooser.
- New: **barrel, distillation and fruit-press recipes** — the whole spirit chain now
  unrolls. Pin a barrel of brandy (via Volume Calc) and expand: the boiler step shows the
  litres of ferment it takes (at vanilla's ratio, 20 L of cider per litre of spirit — the
  math is brutal, which is why a planner helps), the ferment expands to fruit juice or
  honey or a grain mash (all the barrel alternatives the game defines), and juice expands
  to the fruit that gets pressed. Each step is a deliberate expand with honest litres, and
  rows say their batching: "3 barrel seals (~7 days each)" alongside cooking's pot loads,
  sized to the biggest barrel in the world. Barrel recipes cover far more than ferments —
  tannin, lime water, dyes, cheese and some thirty other products all decompose now.
- Every ingredient and tool row under an expanded pin now has its own **Handbook** button,
  opening the page for the item the row's icon shows (for a liquid row, the liquid itself).
  Same behaviour as the pin's button: waits out handbook rebuilds, falls back to the base
  item's page, then to a name search.
- A pinned liquid's count is now in **litres**: pin sulfuric acid and set 12 to mean 12 L
  (the count field says so on hover), and its header reads "3/12 L" instead of portion-item
  noise like "300/12". Errand pins keep counting items, since their numbers come from
  dialogue.
- Cooking pins plan in **pot loads**: the row says how many times the pot must be filled
  for what is still missing ("Sulfuric acid — 2 pot loads"), based on the best pot the
  world has (vanilla's clay pot cooks 6 servings at once; a modded bigger pot raises the
  math automatically). The recipe chooser also states the per-pot cap for liquids ("cooked
  in a pot, up to 6 L per pot").
- New: a **volume calculator** on every liquid pin's row (the "Volume Calc" button). Plan
  in containers instead of litres: pick a container — every liquid container in the world
  is listed, largest first, so barrels lead — choose how many, and the calculator shows the
  total ("5 × Barrel = 250 L — about 42 pot loads to cook") and sets it as the pin's goal,
  unfolding the recipe so the scaled ingredient list is right there. Capacities are read
  from the game's own blocks: a vanilla barrel holds 50 L, buckets 10 L, and modded
  containers report their real sizes. On liquid rows the calculator takes the place of the
  Expand/Collapse button; showing or hiding the recipe moved inside the calculator screen.

## 0.3.8 — 2026-08-09

- Fixed: a crash when clicking while the game was paused — the handbook pauses singleplayer
  while open, and the inventory, the Tallybook list and its buttons all stay clickable
  underneath. Any of those clicks (rearranging your backpack, a row's Book button) made
  Tallybook register a deferred callback the engine flags as "while game is paused"; on
  clients running developer mode with extended debug info the engine deliberately crashes
  there, and on everyone else it wrote a spurious warning to the log. Reported on ModDB and
  reproduced locally via both paths.
- With the same change, Tallybook now stays responsive while singleplayer is paused with the
  handbook open: counts keep updating as you rearrange bags, and the Book, list and journal
  buttons act immediately instead of waiting for the game to unpause.
- Fixed: the recipe chooser showed seemingly duplicate entries. Linen (normal stitches) has
  three vanilla shears recipes that differ only in which stitch type they convert — and the
  materials summary was trimming "Linen (Square stitches)" and friends down to "Linen",
  hiding exactly the part that told them apart. Real name parentheticals are now kept; only
  Tallybook's own "(any …)" variant bookkeeping is trimmed.
- Fixed: a tool that any variant satisfies no longer masquerades as one specific variant in
  the materials summary — "needs Copper shears" (with nine metals of shears qualifying) now
  reads "needs Shears", using the words all qualifying variants share.
- Fixed: clicking a row's Handbook button could open a blank handbook that stayed blank
  until closed and reopened. The game builds the handbook's page index on a background
  thread — at world join, and again from scratch whenever any mod registers a hotkey — and
  on a well-modded world one build takes long enough to click into; a click mid-build found
  no pages, and the handbook never refreshes itself when the build finishes. The button now
  waits out the build (however long it takes, bounded by a generous safety limit) and then
  lands on the right page; closing the handbook while it waits cancels quietly. When a wait
  is coming, a chat line says so up front — the blank handbook you may briefly see is the
  game still rebuilding, and the page opens by itself the moment it finishes.

## 0.3.7 — 2026-08-09

- **The story, one step at a time.** Tallybook now walks you through the vanilla storyline —
  from asking a wandering trader about a treasure hunter all the way to Tobias' cave — as a
  "story so far" block at the top of the Side quests tab. It advances entirely by itself,
  watching the story's own variables, the locator maps and letters in your hands, and what
  NPCs tell you in conversation. **No spoilers by construction:** every step has a reveal
  gate — proof the game has already told you that much — and only the step you are on is ever
  shown; nothing about later steps appears anywhere, including in the new `.tallybook story`
  command. Steps with things to fetch (the trader's gears, The Lens) pin them automatically,
  once, and park them when the step completes. Progress is remembered per world and only
  moves forward, so a lost map or a handed-over item never un-completes a step. On worlds
  without story content the whole feature stays silent.
- Fixed: on worlds created with 0.2.x, the lens errand could appear at world start (its only
  dialogue gate is negative, and the pre-0.3.0 login scan accepted that). The scan has
  refused it since 0.3.0; the story tracker now surfaces the lens at the right moment
  instead — after you have read the note that names the Devastation. A leftover lens pin
  from an old build can simply be unpinned; it will not return.

- **The Handbook button resolves pages the way the game itself does.** It now asks the
  collectible's own `IHandBookPageCodeProvider` first — the hop vanilla's open-handbook-for-
  held-item flow and every in-handbook item link take — before falling back to the stack's
  page code, so items whose class names a representative page (meals, modded classes) open
  the right entry instead of a code the handbook never indexed.
- **The wrong handbook can no longer answer.** The survival handbook was found via
  "first dialog deriving from GuiDialogHandbook", and vanilla's Command Handbook derives from
  the same class — whichever the player had opened first won, and an item page sent to the
  command handbook opened the wrong book at its root, silently. The survival handbook's own
  mod system is now asked first, and the fallback filters the command handbook out.
- **A pin whose page genuinely is not indexed now searches the handbook by name** instead of
  leaving it sitting at the root, and says so in chat.
- **New diagnostic: `.tallybook pages`** prints every pin's handbook page code and whether
  the live handbook index knows it — run this when a Handbook button misbehaves and report
  the line for the affected pin.

## 0.3.6 — 2026-08-08

- **The HUD hugs the minimap and coordinates again.** Its anchor sat it below the deepest
  HUD element in its column, so a tall side panel — prospecting readouts, which can also stay
  "open" with full bounds while visually collapsed — dragged it to mid-screen. Only compact
  corner residents count now: the minimap, coordinates and clock are things to sit under; a
  tall panel is a neighbour to sit beside.

## 0.3.5 — 2026-08-08

- **History reads at the same size as the other tabs.** Its record names were drawn at the
  secondary-text size while Items and Side quests use the larger title face, so the whole tab
  looked like fine print. Long quest names truncate with an ellipsis rather than wrapping into
  the date column.

## 0.3.4 — 2026-08-08

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
- **Checkmarks are checkmarks now, not little boxes.** The game's fonts (Montserrat, Lora,
  Almendra) carry none of ✓ ○ ◑ ✗ ⚒ ▸ — verified against the shipped font files — so every
  status mark drew as an empty rectangle. All marks now use glyphs the fonts actually have:
  √ for done, • for partial, · for none, × for a missing tool, ▶ for the chosen recipe.
- **An NPC's location is recorded only while talking to them** — villagers and traders alike;
  there is no passive radar (Mark). Talking is the backfill: the moment the conversation
  opens, the errand gets their position, and the blue X lands on the map in the same breath.
  The two deliberate assists remain: **`.tallybook here <name>`** sets a giver's location to
  where you stand, and a map waypoint whose title names them counts as knowing where they
  are. **`.tallybook npcs`** lists nearby conversable NPCs with their exact names, so a name
  mismatch is visible instead of guessed at.
- **One text-size slider for everything.** The HUD's size slider now governs the Tallybook
  window's table too — rows, quotes, history and the recipe chooser follow it, and row
  heights close up or open out with the text. One size that looks right, everywhere.
- **The Map button is always on an errand row.** With no location known it says what would
  teach us one — walk past the giver, or name a map waypoint after them — instead of silently
  not existing, which read as broken.
- **The Map button opens the world map through the game's own hotkey handler**, exactly as
  pressing M does, instead of driving the map manager by hand — hand-driving it produced a
  map that could not be closed once, and is the prime suspect for the map opening at the
  wrong size. A stray errand with a blank quest giver — "(for )" — is also repaired at load
  into an ordinary pin.
- **Handed-in errands notice they are finished.** Handing goods over sets variables in your
  own player state (`agnieszkaquestcompleted`, Tobias' `gavelens`, Better Ruins'
  `gaveironpickaxe`), and the catalogue now knows which belong to each errand — so a
  completed errand is archived into History and its pin parked, at login or the moment it
  happens, whether or not Tallybook was watching. Before this, a handed-in errand looked
  *less* finished afterwards: the goods left your inventory, so 8/8 fell back to 0/8 on a
  quest that was over. Vanilla traders keep completion on the NPC, so theirs is noticed while
  you stand with them; Better Ruins uses player state, so its errands settle at login. Parked
  once, never re-fought: re-check the pin and it stays.
- **A "not carrying the map" condition no longer ties every map to every errand.** Quest
  turn-in lines and map handout lines both routinely carry that condition, and the map tie
  saw `player.inventory` on both sides as a shared quest variable — Better Ruins' iron
  pickaxe fetch got the Sunrift Experiment map attached that way and sent you to a ruin
  instead of back to the trader. Ties now require a real state variable with the same
  expected value on both sides, so a reward map handed out after completion can never
  masquerade as the errand's destination. Tobias' lens → Devastation is unaffected — that
  tie is genuine and stays.
- **Side quest rows line up.** The transcript-toggle column is reserved on every errand row,
  so a quest with no captured conversation no longer starts its name further left than its
  neighbours; and fitted text keeps a few pixels of slack, because "fits exactly" sometimes
  lost to the renderer's own wrap decision and bled the last word into the row below. The
  HUD's lines had the same defect at the same boundary — "Raw hide (Small) for" with
  "Gerhardt" overprinting the next errand — and get the same slack.
- **Quest markers land on the quest givers now.** The waypoint command takes spawn-relative
  coordinates — the numbers your coordinate HUD shows — while entity positions are absolute
  world coordinates, so every marker was offset by the entire spawn position: a cluster of
  blue x's nowhere near the villagers. Worse, the position resolver then captured our own
  misplaced markers back as where the NPCs "are". Fixed both: coordinates are converted
  before sending, and the resolver never reads our own markers back. **`.tallybook relearn`**
  forgets all learned positions and markers so a poisoned map heals in one command —
  walk through the village and everything re-places itself correctly.
- **Map buttons no longer flicker out of existence.** They were driven by reading the client's
  waypoint list live, at draw time — and that read is known to come back empty at random (it
  is what once planted fifty duplicate markers). Successful reads are now captured into the
  pin and saved; the button draws from what the pin knows, so once a place is learned it stays
  learned, across relogs and regardless of what the waypoint list says today. `.tallybook
  waypoints` shows what the client can read right now, for when things look wrong.
- **An errand that comes with a map points at the map's destination while you are still
  fetching, then back to the giver.** The tie is the game's own: the dialogue line that hands
  Tobias' Devastation map is gated on the same quest variable as the lens turn-in, and a
  shared variable is what makes two conversation threads one quest. Maps whose handout shares
  no variable with any errand attach to nothing — which is why Agnieszka's ingots no longer
  go anywhere but her forge.
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
