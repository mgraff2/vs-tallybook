# ModDB description — Tallybook

ModDB renders HTML, not Markdown. Every section below that gets pasted into ModDB is raw
HTML; the short summary is plain text (it is a plain field).

## Short summary (under 100 characters)

Crafting shopping list, villager quest tracker, and spoiler-free story guide. Fully client-side.

(96 characters)

---

## Description (HTML — paste as-is)

<p>Tallybook is three trackers in one: a Satisfactory-style crafting shopping list &mdash; pin any item, see exactly what you still need to gather, with counts that update the instant your inventory changes &mdash; a side quest tracker that picks up villager and trader errands by itself, counts them the same way, and keeps a map marker on whoever is waiting for the goods &mdash; and a spoiler-free story guide that walks you through the vanilla storyline one step at a time, advancing entirely from your own play. Fully client-side: works on any server, no server-side install needed.</p>

<p><strong>Shopping list.</strong> Pin an item from its handbook page &mdash; one click, and it's exactly the variant you were looking at, not a sibling that shares its name. Every pin tracks its own acquisition (Low-quality soil 12/64 fills in as you dig), so loot-only and gather-only items are first-class goals, not just craftables. Expand any craftable row to unfold its recipe beneath it, sized to what you still lack: need 4, carry 1, and the children ask for materials for 3. Nested as deep as you like, one deliberate click per level &mdash; never automatic, so recipe cycles and wrong guesses can't lie to you.</p>

<p><strong>Alternative recipes are found automatically, whatever mod added them.</strong> When an item has more than one genuinely different recipe &mdash; vanilla's way versus a mod's blueprint-gated way &mdash; Expand asks which you mean, listing each option by what it takes and what you'd have to be holding. No per-mod compat code: whether two recipes are a real choice is decided by what they consume, so it works for content mods that haven't been written yet.</p>

<p><strong>Side quests.</strong> Accept a fetch errand the way the game intends and Tallybook picks it up on its own &mdash; no button, no extra click. The row reads Raw hide (Small) for Gerhardt 3/10 and goes green when you can deliver. What the villager actually said is kept as a transcript under the row, re-readable a week later. Errands you were already on are recovered at login from the game's own dialogue files &mdash; including quests accepted before Tallybook was installed &mdash; and handed-in errands notice they're finished by themselves, moving to History instead of sitting at 0/10 forever because the goods left your bags.</p>

<p><strong>Story guide.</strong> The vanilla storyline, one step at a time &mdash; starting from the very first one: a few rusty gears and a question for a wandering trader. A "story so far" block at the top of the Side quests tab shows the step you are on and nothing more, advancing by itself as you play: it watches the story's own progress flags, the maps and letters in your hands, and what NPCs tell you in conversation. Steps that need something fetched pin it automatically and retire it when the step is done. No spoilers by construction &mdash; a step only appears once the game itself has told you that much, and what comes next stays hidden everywhere, including in the .tallybook story command. Progress is per world and only moves forward, so a lost map or a handed-over item never un-completes a step.</p>

<p><strong>Map integration.</strong> Quest givers get a light-blue X on your map for as long as their errand is live, and every errand row has a Map button that opens the world map centred on them. An errand that came with a locator map points at the map's destination while you're still fetching &mdash; the lens is in the Devastation, and that's the walk you're making &mdash; then back at the giver once you have the goods. Locations are learned by talking to the NPC (never by proximity scanning), from your own waypoints when one names them, or told directly: stand at the forge and type .tallybook here Agnieszka.</p>

<p><strong>HUD.</strong> A corner overlay in the minimap's style: side quests with distances, then pooled gathering totals &mdash; one Boards 12/48 line even when three builds want boards &mdash; then required tools with carried/missing checks. Item icons on every line, with "any wood"-style rows cycling their icon through the accepted variants, handbook-style. It slots in directly under the minimap and coordinates like one more panel in the stack &mdash; sitting under compact corner elements, beside tall ones &mdash; and disappears when your list is empty.</p>

<p><strong>History.</strong> Everything you've finished, including quests completed before Tallybook was installed, with the conversation transcripts preserved and story milestones (villages found, lore heard) recorded alongside. Undated finishes are ordered by story prerequisites rather than guessed dates. One click away from your journal for the lore you collected en route.</p>

<p><strong>Everything updates live.</strong> Counting is event-driven off your carried inventory &mdash; hotbar and backpacks, with an opt-in for saddlebags on animals you own. No polling, no nearby-chest scanning: the question is "what do I have on me", answered honestly.</p>

<p><strong>How to open it:</strong> press L for the list (three tabs &mdash; Items, Side quests, History), K to toggle the HUD, and pin from any handbook page via its "Add to Tallybook" button. All keys rebindable under Settings &rarr; Controls.</p>

<p><strong>Good to know</strong></p>

<ul>
<li>Works with every content mod's recipes and trader errands for free: servers push their recipes and dialogue to connecting clients, and Tallybook reads those &mdash; there is no compat list to be on.</li>
<li>Tallybook only ever reads. It never crafts for you, never moves your items, and never writes to your inventory &mdash; the worst bug it can have is a wrong number.</li>
<li>Errands are counted, never decomposed: a fetch quest is a fetch, and its row won't sprout an anvil because the game technically has a recipe.</li>
<li>One text-size slider (in Options) governs the HUD and the whole list window.</li>
<li>Useful commands: .tallybook story (where you are in the storyline &mdash; never what comes next), .tallybook here &lt;name&gt; (set a quest giver's location to where you stand), .tallybook quests (every fetch errand your world's content describes, with your status), .tallybook recipes (every item craftable more than one way), .tallybook pages (diagnose a misbehaving Handbook button), .tallybook npcs, .tallybook relearn, .tallybook blankmarkers (finds untitled waypoints, which crash the vanilla map on hover &mdash; whoever made them).</li>
<li>Config at VintagestoryData/ModConfig/tallybook.json; per-world data at VintagestoryData/ModData/tallybook/.</li>
<li>For Vintage Story 1.22.x (tested against every patch release, 1.22.0 through 1.22.6).</li>
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
