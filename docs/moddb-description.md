# ModDB description — Tallybook

ModDB renders HTML, not Markdown. Every section below that gets pasted into ModDB is raw
HTML; the short summary is plain text (it is a plain field).

## What the ModDB page actually supports (verified against the live site, 0.3.17)

Checked by fetching real mod pages and the site's own CSS/JS rather than guessing — the
failure mode of guessing here is a mangled *public* page, so re-verify rather than trust
this list if the site is redesigned.

- **Collapsible sections are the site's own widget**, not `<details>`. Its editor is TinyMCE
  with the `spoiler` plugin, and the markup it emits is exactly:

  ```html
  <div class="spoiler">
    <div class="spoiler-toggle">Caption</div>
    <div class="spoiler-text">…anything…</div>
  </div>
  ```

  `style.css` carries `.spoiler-toggle:not(.expanded) ~ * { display: none }`, so it is
  **collapsed by default with no attribute needed**, and `script.js` attaches the click
  handler page-wide at DOM ready (`attachSpoilerToggle($(".spoiler-toggle"))` — it only
  toggles the `expanded` class). `spoiler.css` supplies the ► / ▼ markers, the border and the
  bold caption; the caption is 12px, so bump it inline when using these as section headers.
  Plain `<details>`/`<summary>` does survive the sanitizer (the Extra Info mod page uses one),
  but the spoiler widget is the one that matches the site's own look and is guaranteed
  supported.
- **`class="stdtable"` is the site's table style** — 1px gray borders, a translucent header
  row, and parchment striping (`#e6dfd0` / `#fff8ea`) with a hover row. Use it; there is **no
  Bootstrap** on the site, so the `table-bordered table-hover` classes other mod pages carry
  are TinyMCE leftovers that do nothing.
- **`class` and `style` attributes both survive**, including CSS custom properties. That means
  inline styles can reference the site's own palette and stay right if it is retuned:
  `var(--color-text)` `#333`, `--color-text-weak` `#6f6f6f`, `--color-border` `#aaa`,
  `--color-link` `#3d6594`, `--color-highlight` `hsl(45 51% 54%)`,
  `--color-content-bg` `hsl(42 100% 98%)`.
- **`<style>` and `<script>` blocks are stripped** — no description on the site has one. All
  styling must therefore be inline; there are no hover states or media queries available.
- **`<h3>` is already styled as a section header** (120%, bottom rule, 40px top margin), so
  use it bare rather than faking headings with bold paragraphs.
- Also confirmed present in live description bodies: `table/thead/tbody/tr/th/td`, `dl/dt/dd`,
  `p ul li strong em code pre br hr a img iframe blockquote small span div abbr`.
- **The site is light-theme only** — no dark mode, no `prefers-color-scheme` anywhere in its
  CSS — so light backgrounds with dark text are safe.

Structure of the description below: a lead paragraph and two tables stay **open** (a page
whose every section is collapsed reads as an empty page), and the long feature prose lives in
spoilers, each captioned with a bold title plus a normal-weight teaser so the collapsed state
still says what is inside.

## Short summary (under 100 characters)

Crafting shopping list, quest tracker, story guide, and world/player reference. Fully client-side.

(98 characters)

---

## Description (HTML — paste as-is)

<p style="font-size: 1.1em;"><strong>Tallybook is a shopping list, a quest tracker, a story guide and a reference book &mdash; one window, seven tabs, all of it live off your own inventory.</strong> Pin what you mean to build and it tells you exactly what you still need; accept an errand and it picks it up by itself; play the story and it walks a step behind you, never a step ahead. Fully client-side: install it on your client and it works on any server, modded or not.</p>

<table class="stdtable" style="width: 100%;">
<tbody>
<tr><td style="width: 26%;"><strong>Where it installs</strong></td><td>Your client only. Nothing goes on the server, and nobody else needs it.</td></tr>
<tr><td><strong>Game version</strong></td><td>Vintage Story 1.22.x &mdash; tested against every patch release, 1.22.0 through 1.22.7.</td></tr>
<tr><td><strong>Content mods</strong></td><td>All of them, with no compat list to be on. Servers push their recipes and dialogue to connecting clients, and Tallybook reads those.</td></tr>
<tr><td><strong>What it writes</strong></td><td>Nothing, ever. It never crafts for you and never touches your inventory &mdash; the worst bug it can have is a wrong number.</td></tr>
<tr><td><strong>Opening it</strong></td><td><strong>L</strong> for the list, <strong>K</strong> for the HUD, and an <em>Add to Tallybook</em> button on every handbook page. All rebindable.</td></tr>
</tbody>
</table>

<h3>Seven tabs, seven questions</h3>

<table class="stdtable" style="width: 100%;">
<thead>
<tr><th style="width: 22%;">Tab</th><th>The question it answers</th></tr>
</thead>
<tbody>
<tr><td><strong>Items</strong></td><td>What do I still need to gather before I can build this?</td></tr>
<tr><td><strong>Side quests</strong></td><td>Who is waiting for what &mdash; and where are they?</td></tr>
<tr><td><strong>Explore</strong></td><td>Which places did I mean to come back to?</td></tr>
<tr><td><strong>Player</strong></td><td>Where do I respawn, and how many times can I still do it?</td></tr>
<tr><td><strong>World</strong></td><td>What rules is this server actually running?</td></tr>
<tr><td><strong>Lore</strong></td><td>What have I read, and how much is still out there?</td></tr>
<tr><td><strong>History</strong></td><td>What have I already finished &mdash; and what was said?</td></tr>
</tbody>
</table>

<p><small>Explore, Player, World and Lore can each be switched off in Options if you would rather have a shorter book.</small></p>

<h3>What each part does</h3>

<p><small>Click a heading to open it.</small></p>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">The shopping list <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; pin it, expand it, watch it fill in as you dig</span></div>
<div class="spoiler-text">
<p><strong>Pin an item from its handbook page.</strong> One click, and it is exactly the variant you were looking at, not a sibling that shares its name. Every pin tracks its own acquisition (Low-quality soil 12/64 fills in as you dig), so loot-only and gather-only items are first-class goals, not just craftables.</p>
<p><strong>Expand any craftable row</strong> to unfold its recipe beneath it, sized to what you still lack: need 4, carry 1, and the children ask for materials for 3. Nested as deep as you like, one deliberate click per level &mdash; never automatic, so recipe cycles and wrong guesses can't lie to you.</p>
<p><strong>Alternative recipes are found automatically, whatever mod added them.</strong> When an item has more than one genuinely different recipe &mdash; vanilla's way versus a mod's blueprint-gated way &mdash; Expand asks which you mean, listing each option by what it takes and what you'd have to be holding. No per-mod compat code: whether two recipes are a real choice is decided by what they consume, so it works for content mods that haven't been written yet.</p>
<p><strong>And the crafting grid is not the whole story.</strong> Tallybook follows every way the game actually makes things:</p>
<ul>
<li>cooking-pot products &mdash; acids, glue, potash</li>
<li>barrel recipes, distilling and fruit pressing</li>
<li>grinding, crushing and smelting</li>
<li>crucible alloying, counted in nuggets and bits &mdash; the units a crucible really accepts, at the ratios you'd pour</li>
<li>anvil smithing, so iron ingots finally decompose honestly: nuggets to bloom to hammered ingot</li>
</ul>
<p>Liquids are tracked as litres in whatever container the recipe accepts, an empty bowl never counts as a bowl of water, and pinned liquids get a volume calculator for "how many buckets is that".</p>
<p><strong>Boat construction sites are plannable too.</strong> The vanilla sailboat, Shipwright's Drakkar and friends are built in stages at a construction site &mdash; and those stages are data Tallybook reads, so no per-mod code. Pin the roller item and press <strong>Construct</strong>: the whole build joins your list as its own pin &mdash; rollers, planks, beams, rope, stone ballast, stage totals summed &mdash; with the starter item as an expandable first row, so the roller craft and the boat materials count at the same time. Holding rollers never marks the boat built.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">Side quests <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; errands picked up by themselves, counted, mapped and remembered</span></div>
<div class="spoiler-text">
<p><strong>Accept a fetch errand the way the game intends and Tallybook picks it up on its own</strong> &mdash; no button, no extra click. The row reads <em>Raw hide (Small) for Gerhardt 3/10</em> and goes green when you can deliver. What the villager actually said is kept as a transcript under the row, re-readable a week later.</p>
<p>Errands you were already on are recovered at login from the game's own dialogue files &mdash; <strong>including quests accepted before Tallybook was installed</strong> &mdash; and handed-in errands notice they're finished by themselves, moving to History instead of sitting at 0/10 forever because the goods left your bags.</p>
<p><strong>Quests from the VS Quest framework too</strong> &mdash; the one VS Village and other quest packs are built on. Accept a quest at its giver and anything it asks you to <em>bring</em> lands on the list as an errand, with the count, the giver, a map marker, a HUD row and the ready-shimmer once you carry it all. Quests that also count kills or blocks show how far along those are as of the last time you had the quest window open, because that is the only moment the game sends those numbers to your client &mdash; and the shimmer waits until everything it can verify is satisfied, so it never sends you on a wasted walk. It reads the framework's own quest files, so a quest pack needs no support of its own; walk back into range of a giver and any quest you already accepted from them is restored, which is how the list rebuilds itself on a computer that has never seen the world. Only quests you have already taken are ever restored &mdash; new ones still come from talking to the giver.</p>
<p><strong>Places worth walking to become side quests as well.</strong> Read a ruin map or treasure map &mdash; Better Ruins' artifacts, vanilla treasure maps, anything using the game's locator-map convention &mdash; and the destination joins the tab as a place to visit. Standing at the site marks it visited, and where a site hides writings that exist nowhere else in the world, the quest counts them ("5/17") from your journal and your bags, listing what you have found and keeping the rest the site's secret.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">The story guide <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; one step at a time, and never one step ahead</span></div>
<div class="spoiler-text">
<p>The vanilla storyline, one step at a time &mdash; starting from the very first one: a few rusty gears and a question for a wandering trader. A <em>story so far</em> block at the top of the Side quests tab shows the step you are on and nothing more, advancing by itself as you play: it watches the story's own progress flags, the maps and letters in your hands, and what NPCs tell you in conversation. Steps that need something fetched pin it automatically and retire it when the step is done.</p>
<p><strong>No spoilers by construction</strong> &mdash; a step only appears once the game itself has told you that much, and what comes next stays hidden everywhere, including in the <code>.tallybook story</code> command. Progress is per world and only moves forward, so a lost map or a handed-over item never un-completes a step.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">Explore &mdash; a place journal <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; the mine you half-dug, the ruin you spotted at dusk</span></div>
<div class="spoiler-text">
<p>Standing somewhere worth coming back to, save it with a name and a one-line "what it is" (or <code>.tallybook spot &lt;name&gt;</code> from chat). Each place gets an orange star on your map, a live distance, a Map button, and the side-quest checkbox contract: checked places ride the HUD (<em>Old copper mine &mdash; mine, half dug (1,240m)</em>), unchecked are parked.</p>
<p>Longer notes fold under the row &mdash; one free-text field where "- " lines draw as bullets and "[ ]" lines as checkboxes you tick off with a single click straight from the list. An Edit window changes everything (renaming moves the marker), removing takes the same hold-to-confirm as unpinning, and an optional hotkey opens straight to the tab.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">Lore <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; count what you've read, and print it as a book</span></div>
<div class="spoiler-text">
<p>The books, scrolls and tapestries you read land in your journal; this tab counts them against everything this world's content defines &mdash; volumes discovered, chapters collected, and how many volumes are still hidden, <strong>as counts only</strong>: unfound titles stay the world's secret.</p>
<p>Found volumes cluster by source (Vanilla first, then each lore-adding mod), filterable by status, by mod, and by story lore &mdash; writings only the story's own places can hold, recognised from the world's files, never a hand-kept list &mdash; versus world lore. Every volume has a Read button that opens the game's journal directly on that entry, side by side with the list.</p>
<p><strong>Export book</strong> writes your found lore as one printable HTML book &mdash; cover, contents, a section per volume &mdash; ready for your browser's print-to-PDF. All of it reads from the server-synced journal, so progress follows you between computers, and modded lore counts with zero compat work.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">World and Player <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; this server's rules, and your own numbers</span></div>
<div class="spoiler-text">
<p><strong>World tab.</strong> A reference card of this world's rules: every world-config setting the installed mods declare &mdash; world generation, survival challenges, temporal stability, all of it &mdash; grouped under the create-world screen's own headings, resolved to the labels that screen uses. Values the server changed from the game's defaults draw in colour with the default named on hover; a filter box finds settings by name, value, or description ("mobs" finds the grace timer). Below the settings: every mod this world runs, with versions &mdash; <strong>including server-side mods your client never loads</strong>. Handy on a server whose settings you didn't write.</p>
<p><strong>Player tab.</strong> Your spawn points, tracked: the world spawn and your temporal-gear returning point, each with coordinates, live distance, a Map button, and a maintained map marker that follows the point and leaves when it is used up or moved. <em>Respawns left there</em> counts your returning point's remaining uses against the server's budget &mdash; and drops as you die. Below that: your deaths in this world, lives left where the server grants a fixed number, character class, and temporal stability. A checkbox puts a <em>Spawn distance</em> line in the HUD &mdash; how far you are from wherever you would respawn right now &mdash; with an optional warning distance past which the line turns a colour you pick.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">The HUD and the map <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; pooled totals in the corner, markers on whoever is waiting</span></div>
<div class="spoiler-text">
<p><strong>HUD.</strong> A corner overlay in the minimap's style: side quests with distances, then pooled gathering totals &mdash; one <em>Boards 12/48</em> line even when three builds want boards &mdash; then required tools with carried/missing checks. Item icons on every line, with "any wood"-style rows cycling their icon through the accepted variants, handbook-style. It slots in directly under the minimap and coordinates like one more panel in the stack &mdash; sitting under compact corner elements, beside tall ones &mdash; and disappears when your list is empty. Hold <strong>Alt</strong> to free the cursor and click any row through to its handbook page.</p>
<p><strong>Map.</strong> Quest givers get a light-blue X for as long as their errand is live, and every errand row has a Map button that opens the world map centred on them. An errand that came with a locator map points at the map's destination while you're still fetching &mdash; the lens is in the Devastation, and that's the walk you're making &mdash; then back at the giver once you have the goods. Locations are learned by talking to the NPC (never by proximity scanning), from your own waypoints when one names them, or told directly: stand at the forge and type <code>.tallybook here Agnieszka</code>.</p>
</div>
</div>

<div class="spoiler">
<div class="spoiler-toggle" style="font-size: 15px;">History <span style="font-weight: normal; color: var(--color-text-weak);">&mdash; everything finished, with what was said</span></div>
<div class="spoiler-text">
<p>Everything you've finished, including quests completed before Tallybook was installed, with the conversation transcripts preserved and story milestones (villages found, lore heard) recorded alongside. Undated finishes are ordered by story prerequisites rather than guessed dates. One click away from your journal for the lore you collected en route.</p>
</div>
</div>

<h3>Keys</h3>

<table class="stdtable" style="width: 100%;">
<thead>
<tr><th style="width: 22%;">Key</th><th>Does</th></tr>
</thead>
<tbody>
<tr><td><strong>L</strong></td><td>Open the list</td></tr>
<tr><td><strong>K</strong></td><td>Toggle the HUD</td></tr>
<tr><td><strong>Alt</strong> + click</td><td>Free the cursor and click a HUD row through to its handbook page</td></tr>
<tr><td><em>unbound</em></td><td>Optional: open straight to the Explore tab</td></tr>
</tbody>
</table>

<p><small>All rebindable under Settings &rarr; Controls. Tallybook's hotkeys stand down while you are typing in any mod's text field, and its own fields return the courtesy.</small></p>

<h3>Commands</h3>

<p>Client commands, so they start with a dot.</p>

<table class="stdtable" style="width: 100%;">
<thead>
<tr><th style="width: 34%;">Everyday</th><th>Prints</th></tr>
</thead>
<tbody>
<tr><td><code>.tallybook story</code></td><td>Where you are in the storyline &mdash; never what comes next</td></tr>
<tr><td><code>.tallybook quests</code></td><td>Every fetch errand your world's content describes, with your status</td></tr>
<tr><td><code>.tallybook sites</code></td><td>Every locator-map destination and tracked site</td></tr>
<tr><td><code>.tallybook recipes</code></td><td>Every item craftable more than one way</td></tr>
<tr><td><code>.tallybook spot &lt;name&gt;</code></td><td>Save the place you are standing on to Explore</td></tr>
<tr><td><code>.tallybook here &lt;name&gt;</code></td><td>Set a quest giver's location to where you stand</td></tr>
</tbody>
</table>

<table class="stdtable" style="width: 100%;">
<thead>
<tr><th style="width: 34%;">If something looks wrong</th><th>Prints</th></tr>
</thead>
<tbody>
<tr><td><code>.tallybook version</code></td><td>Which build is actually running &mdash; start here</td></tr>
<tr><td><code>.tallybook spawn</code></td><td>Everything the spawn tracker can see, layer by layer</td></tr>
<tr><td><code>.tallybook vsquest</code></td><td>What the VS Quest integration can see. <code>track &lt;quest id&gt;</code> asserts a quest you took on another computer</td></tr>
<tr><td><code>.tallybook pages</code></td><td>Diagnose a misbehaving Handbook button</td></tr>
<tr><td><code>.tallybook npcs</code></td><td>Who Tallybook knows about, and where</td></tr>
<tr><td><code>.tallybook relearn</code></td><td>Forget learned positions and relearn them</td></tr>
<tr><td><code>.tallybook blankmarkers</code></td><td>Finds untitled waypoints &mdash; which crash the vanilla map on hover, whoever made them</td></tr>
</tbody>
</table>

<h3>What Tallybook will never do</h3>

<ul>
<li><strong>Craft for you, or move a single item.</strong> Everything here only ever reads. That is a decision, not a limitation &mdash; automating the game into boringness is not the point, and a mod that only reads can never scatter your inventory.</li>
<li><strong>Count chests you are standing next to.</strong> The question is "what do I have on me", and answering a different one dishonestly is worse than not answering. The one opt-in extension is saddlebags on animals you own.</li>
<li><strong>Decompose an errand.</strong> A fetch quest is a fetch. Its row won't sprout an anvil because the game technically has a recipe that consumes one.</li>
<li><strong>Show you a step you haven't reached.</strong> Story steps, unfound lore titles and un-offered quests all stay hidden. Where Tallybook cannot tell, it says nothing rather than guessing.</li>
</ul>

<h3>Good to know</h3>

<ul>
<li>Counting is event-driven off your carried inventory &mdash; hotbar and backpacks &mdash; so rows flip the instant you pick something up. No polling.</li>
<li>One text-size slider, in Options, governs the HUD and the whole list window.</li>
<li>Config at <code>VintagestoryData/ModConfig/tallybook.json</code>; per-world data at <code>VintagestoryData/ModData/tallybook/</code>.</li>
</ul>

---

## Changelog — 0.3.17 (HTML — paste as-is)

<p><strong>Tallybook 0.3.17 &mdash; for Vintage Story 1.22.x</strong></p>

<ul>
<li><strong>Fixed: quest items that carry attributes now show their real name and picture.</strong> Better Ruins' Luxuries trader asks for a large intact globe, and the errand appeared as "game:clutter" under a question-mark icon. Blocks like clutter, banners and wall decorations all share one block code and keep their identity in attributes &mdash; the code really is just "clutter", and an attribute is what makes it a globe &mdash; and Tallybook was reading the code and the count out of the dialogue while dropping the rest. It now hands the whole request to the game's own reader, so the row reads "Large Intact Globe" with the globe's picture, and only an actual globe counts toward it rather than any scrap of clutter. Anything a quest hands <em>back</em> is read the same way. An errand already on your list from an older version repairs itself at the next world load.</li>
<li><strong>Fixed: the Handbook button on those errands opens the page directly instead of searching for it.</strong> The handbook builds a globe's page from a plain globe, while the one in your bag is marked as salvaged &mdash; close enough to look identical, different enough that the exact lookup missed and the button fell back to searching by name. It now asks the item which pages the handbook built from it and opens the closest one.</li>
<li><strong>Fetch errands now count exactly what the villager will accept.</strong> The have-count follows the game's own hand-over test instead of an approximation of it, which cuts both ways: an item of the right kind counts even if it reached you by an unusual route, and a worn tool or spoiled food reads as not-had, because the turn-in option will not appear for it either. A trip saved is worth more than a green number.</li>
<li>Tested against Vintage Story 1.22.0 through 1.22.7.</li>
</ul>

---

## Changelog — 0.3.16 (HTML — paste as-is)

<p><strong>Tallybook 0.3.16 &mdash; for Vintage Story 1.22.x</strong></p>

<ul>
<li><strong>New: VS Quest support</strong> &mdash; quests from the VS Quest framework (the one VS Village and other quest packs build on) are tracked as ordinary errands. Anything a quest asks you to <em>bring</em> becomes a row with its count, its giver, a map marker and a HUD line, and the gold ready-shimmer appears over the giver once you are carrying it all. It reads the framework's own quest files, so a quest pack needs no support of its own &mdash; the same promise Tallybook makes about recipes.</li>
<li>Quests that also count kills or blocks show that progress <strong>as of the last time you had the quest window open</strong>, which is the only moment the game sends those counters to your client. The shimmer holds off while any objective is unverifiable, so it under-promises rather than sending you on a wasted walk.</li>
<li><strong>Walk into range of a giver and any quest you already accepted from them is restored</strong> &mdash; how the list rebuilds itself on a computer that has never seen the world. Only quests you have already taken are ever restored; a new one still comes from talking to the giver, so nothing you have not been offered is revealed. Finished quests move to History with what you handed over and what you received.</li>
<li><strong>Fixed: typing in another mod's window no longer triggers Tallybook's hotkeys.</strong> Naming a route in Boat Autopilot's planner &mdash; or typing in any mod's text field &mdash; with an L or a K in it opened the shopping list or toggled the HUD instead of typing the letter. Tallybook now checks whether the keyboard belongs to a text field before acting, and stands down if it does. The courtesy runs both ways: while one of Tallybook's own fields has focus, other mods' hotkeys stop firing on those letters too.</li>
<li>New diagnostic: .tallybook vsquest, and .tallybook vsquest track &lt;quest id&gt; to assert a quest you accepted on another computer. TrackVsQuests in the config file turns the integration off.</li>
<li>Tested against Vintage Story 1.22.7, now part of the version sweep.</li>
</ul>

---

## Changelog — 0.3.15 (HTML — paste as-is)

  <p><strong>Tallybook 0.3.15 &mdash; for Vintage Story 1.22.x</strong></p>

  <ul>
  <li><strong>New: Explore tab</strong> (on by default) &mdash; a place journal. Save the spot you are standing on with a name and a one-line "what it is" (or <em>.tallybook spot &lt;name&gt;</em>). Each place gets an orange star map marker, a live distance, a Map button, and the side-quest checkbox contract: checked places show on the HUD with the distance back, unchecked are parked. Longer notes fold under the row &mdash; one text field where "- " lines draw as bullets and "[ ]" lines as checkboxes you tick off with a click straight from the list &mdash; with an Edit window for name, description and notes (renaming moves the marker), hold-to-confirm removal, and an optional go-straight-there hotkey (unbound by default).</li>
  <li><strong>New: boat construction sites are plannable</strong> &mdash; the vanilla sailboat, Shipwright's boats, anything built in stages at a construction site. The stages are data Tallybook reads, so it works with no per-mod code: pin the roller item and press <strong>Construct</strong> to add the whole build as its own pin &mdash; rollers, planks, beams, rope, stage totals summed, the starter item as an expandable first row. The roller craft and the boat materials count at the same time, and holding rollers never marks the boat built.</li>
  <li><strong>Side quests: sorting and hand-arranging now cover every row.</strong> Errands and map-artifact site quests are one ordered list on the tab and the HUD alike &mdash; the Sort dropdown reorders all of it, and under Custom every row moves with ^ / v, the arrangement saved per world.</li>
  <li><strong>HUD rows link to the handbook</strong>: hold Alt to free the cursor and click any item row to open its handbook page.</li>
  <li><strong>History is organized by game year</strong> &mdash; newest year first, still-going quests on top, pre-install finishes collapsed at the bottom, and your folds are remembered.</li>
  <li><strong>The World tab is an accordion</strong> &mdash; it opens on "Changed on this server" with every category folded to a counted heading; the filter still finds everything inside folded sections.</li>
  <li>Tab order is now Items, Side quests, Explore, Player, World, Lore, History.</li>
  </ul>

---

## Changelog — 0.3.14 (HTML — paste as-is)

<p><strong>Tallybook 0.3.14 &mdash; for Vintage Story 1.22.x</strong></p>

<ul>
<li><strong>New: Lore tab</strong> (on by default; Options can hide it). The books, scrolls and tapestries you read land in your journal &mdash; this tab counts them against everything this world's content defines: volumes discovered, chapters collected, volumes still hidden (counts only &mdash; unfound titles stay the world's secret). Read from the server-synced journal and the world's own lore files, so progress follows you between computers and modded lore counts with zero compat work.</li>
<li>Found volumes cluster by source &mdash; Vanilla first, then each lore-adding mod &mdash; and can be sliced by status (All / In progress / Complete), by source mod, or by story lore (writings only the story's own places can hold, recognised from the world's worldgen files, never a hand-kept list) versus world lore. The header numbers re-count to whatever scope you pick, named so a smaller total never reads as missing lore.</li>
<li>Every found volume has a <strong>Read</strong> button that opens the game's own journal directly on that entry, with the two windows arranged side by side instead of stacked for as long as both are open.</li>
<li><strong>Export book</strong> writes everything you have found as one printable HTML file: cover page, contents, a section per volume in exactly the tab's order, with per-volume "chapters still undiscovered" counts. Open it in a browser and print to PDF.</li>
<li>The World and Player tabs are now on by default (their Options switches remain).</li>
</ul>

---

## Changelog — 0.3.13 (HTML — paste as-is)

<p><strong>Tallybook 0.3.13 &mdash; for Vintage Story 1.22.x</strong></p>

<ul>
<li><strong>New: spawn distance in the HUD</strong> (checkbox on the Player tab). A "Spawn distance: 1,250 blocks" line at the top of the HUD, measuring to wherever you would respawn right now &mdash; your temporal-gear returning point when one is set, otherwise the world spawn. It is the exact same number the Player tab shows, refreshing in 5-block steps as you walk; with the line enabled the HUD stays up even when your list is empty.</li>
<li>An optional warning distance under it: past that many blocks from spawn, the line turns a colour you pick from a dropdown (red by default) &mdash; a leash length for how far you are comfortable ranging from a respawn. Leave it off and the line never changes colour.</li>
</ul>

---

## Changelog — 0.3.12 (HTML — paste as-is)

<p><strong>Tallybook 0.3.12 &mdash; for Vintage Story 1.22.x</strong></p>

<ul>
<li><strong>New: World tab</strong> (opt-in &mdash; switch it on in Options). A reference card of this world's rules: every world-config setting the installed mods declare &mdash; world generation, survival challenges, temporal stability, spawn and death, multiplayer &mdash; grouped under the create-world screen's own category headings, showing the value this world actually runs with, resolved to the same labels the create screen uses ("5 days before monsters appear", not raw codes). Values changed from the game's defaults draw in colour, with the default and the setting's description on hover; settings added by a content mod say which mod. A filter box narrows the list as you type, matching names, values, codes and descriptions alike. The tab also lists every mod this world runs, with versions &mdash; the server's own announcement, so server-side-only mods appear too, plus your client-side mods. The top line carries the seed and world size.</li>
<li><strong>New: Player tab</strong> (opt-in &mdash; switch it on in Options). Your spawn points and your numbers: the world spawn and your temporal-gear returning point, each with spawn-relative coordinates, live distance, a Map button, and a maintained map marker &mdash; the marker appears when a point is set, follows it when it moves, and leaves when the point is used up or cleared. "Respawns left there" counts your returning point's remaining uses against the server's temporalGearRespawnUses budget, dropping as you die; a point set before Tallybook was watching honestly says "not known" rather than guessing. Below that: deaths in this world, lives left when the server sets playerlives, character class, temporal stability, and today's date.</li>
<li>New diagnostic: .tallybook spawn prints everything the spawn tracker can see, layer by layer, for when a number looks wrong.</li>
<li>Under the hood, for the curious: the game syncs a player's own spawn point and death count to the client, but one packet variant leaves those fields empty and the death broadcast never updates the counter &mdash; so Tallybook treats non-credible values as meaning nothing, keeps its own forward-only death count (ratcheted by the server's announcements, bumped by deaths it watches happen), and computes returning-point expiry itself, since the server keeps the real remaining-uses number to itself between logins.</li>
</ul>

---

## Changelog — 0.3.7 (HTML — paste as-is)

<p><strong>Tallybook 0.3.7 &mdash; for Vintage Story 1.22.x</strong></p>

<ul>
<li><strong>New: the story, one step at a time.</strong> A spoiler-free story guide at the top of the Side quests tab walks you through the vanilla storyline, from asking a wandering trader about a treasure hunter all the way to Tobias' cave. It advances entirely by itself &mdash; watching the story's own progress flags, the locator maps and letters in your hands, and what NPCs tell you in conversation &mdash; and only ever shows the step you are on: every step is gated on proof the game has already told you that much, so nothing is revealed early, anywhere. Steps with things to fetch pin them automatically and retire them when the step completes. Progress is per world, only moves forward (a lost map or handed-over item never un-completes a step), and the guide stays completely silent on worlds without story content. New command: .tallybook story.</li>
<li>The Handbook button resolves pages the way the game itself does (via the collectible's own page-code provider), so meals and modded items open their real handbook entry instead of the handbook root &mdash; and it can no longer be answered by the Command Handbook by mistake. A pin whose page genuinely isn't indexed now searches the handbook by name and says so. New diagnostic: .tallybook pages.</li>
<li>Fixed: on worlds created with 0.2.x builds, the lens errand could appear at world start. The scan has refused it since 0.3.0, and the story guide now surfaces the lens at the right moment instead &mdash; after you've read the note that names the Devastation. A leftover lens pin from an old build can simply be unpinned; it will not return.</li>
</ul>

---

## Changelog — first ModDB release (0.3.6, HTML — already published)

<p><strong>Tallybook 0.3.6 &mdash; for Vintage Story 1.22.x</strong></p>

<p>Initial ModDB release.</p>

<p><strong>Features</strong></p>

<ul>
<li>Crafting shopping list: pin items from their handbook page (exact-variant), live inventory-tracked counts, deficit-scaled recipe expansion with per-level recipe choice, and automatic detection of alternative recipes &mdash; including modded blueprint-gated ones &mdash; with a chooser that lists each option by its materials.</li>
<li>Side quest tracking: villager and trader fetch errands picked up automatically on accept, recovered retroactively at login from the game's own dialogue files, counted like any other goal, with conversation transcripts kept on the row and completion detected from your own quest state when you hand goods over.</li>
<li>Map integration: light-blue X markers on quest givers while their errand is live, Map buttons that centre the world map on the giver &mdash; or on an errand's map destination while you're still fetching &mdash; and .tallybook here to set a giver's location by standing there.</li>
<li>HUD overlay: side quests with distances, pooled gathering totals, and tool checks, with item icons and variant cycling; slots in directly under the minimap and coordinates.</li>
<li>History tab: finished quests (including pre-install ones) with transcripts, plus story milestones, ordered by prerequisites where dates are unknowable.</li>
<li>Options screen with live text-size slider governing HUD and window, mount-bag counting opt-in, quest marker and glow settings &mdash; each option explained by a hover ?.</li>
<li>Status marks drawn only with glyphs the game's fonts actually carry, so checks are checks everywhere rather than empty boxes.</li>
<li>Fully client-side; works with every content mod's recipes and errands with zero compat patches. Tested against Vintage Story 1.22.0&ndash;1.22.6.</li>
</ul>
