# The 1.22 story, in order — authoritative event list

Reconstructed from the game's own files (1.22.6 server assets: `config/dialogue/*.json`,
`worldgen/storystructures.json`, `itemtypes/utility/locatormap.json`, `itemtypes/lore/letter.json`,
`config/tradelists/*.json`, story schematics), not from prose or memory. Every step below is
pinned to the variable, item, or structure that the game itself uses; nothing here is inferred
from dialogue flavour text alone. Where a tie exists only by world placement (a map leading
somewhere) rather than by a recorded variable, that is said explicitly.

This is the reference for any Tallybook feature that steps players through the story —
`Tallybook/StoryProgress.cs` is authored directly from it (see "Story stepping" in
CLAUDE.md). **Spoiler rule still applies:** a surface built from this list must reveal only
the player's *next* step, never the whole table; in code that rule is each step's reveal
gate.

## The chain at a glance

```
spawn
 └─ 1. any wandering trader ──(4 rusty gears)──> Map to the treasure hunter
 └─ 2. treasure hunter camp (≤ ~1 km of spawn)
        ──(1 tin-bronze pickaxe)──> Map to the resonance archive
 └─ 3. Resonance Archives (underground, 4–6 km from spawn)
        talk to the library resonator ──> heardalchemist, heardalchemistlocations
        (also here: Eidolon boss → rustypart-eidolon2tr; translocators; archives lore)
 └─ 4. back to the treasure hunter
        ask about the place names ──> letter-lazaret (locator to the Lazaret)
        keep asking ──> elk offer ──(saddle + bridle + 100 gears, or Eidolon part + 50)──> tamed elk
 └─ 5. the Lazaret (7–8 km from spawn — the elk is the intended transport)
        inspect the skeleton, then its severed arm ──> foundmissive
        take the missive ──> letter-faded (locator to the Village)
 └─ 6. the Village of Nadiya (letter-faded leads here)
        show the letter: Liga/Sedna ──> heardoftobias;  Agnieszka ──> sawletteragnieszka
        Agnieszka ──> Map to Tobias' cave + readnote ("go to the Devastation first, take The Lens")
        Gerhardt (once readnote) ──> Map to the devastation
        side chains: Agnieszka's iron, Gerhardt's hides, Beata→Kat's bread, Wall→Gerhardt's daisy
 └─ 7. the Devastation — climb the tower (jonaslenstower-aged), take The Lens (jonaslens)
 └─ 8. Tobias' cave — give the lens ──> gavelens
        unlocks: his answers, food, trade, storage door,
        translocator schematic + Illuminal divergence manifold ──> receivedtranslocator  (current end)
```

## World layout (worldgen/storystructures.json)

Distances are block ranges **relative to the structure each depends on**; every story
structure is placed by `dependsOnStructure` + a distance box + a landform — there is no other
gating.

| structure | depends on | X range | Z range | note |
|---|---|---|---|---|
| `treasurehunter` | spawn | −900…900 | −1100…1000 | the only story structure near spawn |
| `resonancearchive` | spawn | −1000…1000 | 4000…6000 | underground; entrance is a hook structure |
| `lazaret` | spawn | 7000…8000 | 1000…2000 | surface ruin |
| `village` | lazaret | 6000…8000 | 0…2000 | Nadiya; 18 named villagers |
| `tobiascave` | village | 2000…3000 | 1000…2000 | Tobias spawns here |
| `devastationarea` | village | 1000…2000 | 7000…8000 | holds `jonaslenstower-aged` |

The treasure hunter trader exists **only** at the story camp (a `meta-spawner` in
`story/treasurehunter` spawning `trader-{male,female}-treasurehunter-temperate`); the type has
no natural worldgen spawn.

## The steps, with the game's own bookkeeping

Milestone variables are player scope unless marked entity. "Detection" is what a client-side
mod can actually read to know the step happened.

### 1. Learn where the treasure hunter is
Any generic wandering trader: the `dialogue-trader-know` line takes **4× `gear-rusty`** and
hands **`locatormap-treasurehunter`** ("Map to the treasure hunter") — trader.json.
Lang flavour: "Best find yourself a treasure hunter. They're always sticking their noses into
dangerous places."
**Detection:** `locatormap-treasurehunter` in inventory (no variable is set).

### 2. The treasure hunter: bronze for the archive map
First contact sets `player.hasmet` (the global "met a trader" flag) and initializes
`entity.bronzereceived="false"`. He asks for a **tin-bronze pickaxe** (`entity.requestbronze`);
handing it over (`pickaxe-tinbronze` taken) sets `entity.bronzereceived=true` and gives
**`locatormap-resonancearchive`** ("Map to the resonance archive"). Lost maps are re-issued
free once `bronzereceived` is true.
He also **sells** `locatormap-treasures` (~12 gears) — ordinary treasure, not story.
**Detection:** map in inventory; `entity.*` only readable while standing with him.

### 3. The Resonance Archives: the Resonator
The **library resonator** (`libraryresonator` entity, dialogue `resonator.json`) narrates the
alchemist's story: `player.heardalchemist`, then `player.heardalchemistlocations` at the end
of the telling — the variable the rest of the story keys on (the place names: Nadiya, the
Spoils, the Lazaret, the Cardinals, the Quiet).
Also in the archives: the **Eidolon** boss (drops `rustypart-eidolon2tr`, the elk discount
token), unrepairable/normal static translocators, and the `archives` lore bookshelves.
**Detection:** `heardalchemist`, `heardalchemistlocations`.

### 4. Back to the treasure hunter: the Lazaret letter and the elk
With `heardalchemistlocations`: asking him about the names opens `nameslist`; asking about
**the Lazaret** sets `entity.heardlazaret` and gives **`letter-lazaret`** — itself a locator
item pointing at the `lazaret` structure ("It'll be a long journey, friend."). Continuing
("anything else") reaches `offerelk` → `entity.offeredelk`. Buying the elk requires a
**saddle** (`hoovedwearables-middleback-saddle*`) and **bridle** (`hoovedwearables-head-bridle*`)
in inventory, and costs **100× gear-rusty**, or **1× rustypart-eidolon2tr + 50** (75 on
repeat); success sets `player.boughtelk` and spawns `tameddeer-elk-male-adult` (2% albino).
**Detection:** `boughtelk`; `letter-lazaret` in inventory.

### 5. The Lazaret: the skeleton and the faded letter
The Lazaret schematic holds the two story props: `skeletonwithloot` (inspecting sets
`player.inspectskeleton`) points at the **severed arm** (`skeletonarm`), whose inspection sets
`player.foundmissive`; reading the missive hands **`letter-faded`** ("Faded and crumpled
letter") — a locator item pointing at the **village**. Also here: `lazaret` lore ("In
Memoriam") and the lazaret stack randomizers.
**Detection:** `inspectskeleton`, `foundmissive`; `letter-faded` in inventory.

### 6. The Village of Nadiya: the letter finds its reader
Seventeen villagers acknowledge `letter-faded`; three matter mechanically:
- **Liga** and **Sedna** → `player.heardoftobias` (Saint Tobias, the statue in the square).
- **Agnieszka** → `player.sawletteragnieszka`; asking about the old man also sets
  `heardoftobias`; asking for the map runs `mapnote`: gives **`locatormap-cavetobias`**
  ("Map to Tobias' cave") and sets `gavemapagnieszka` + **`readnote`**. The map's own text is
  the marching order: *"Before you come to meet me, you must make your way to the Devastation.
  There is a tower there. You must climb it and take The Lens that rests upon its peak."*
- **Gerhardt**, once `readnote` is true, hands **`locatormap-devastationarea`**
  ("Map to the devastation") → `gavemapgerhardt`. (Tobias himself hands the same map on
  first meeting if the player arrives without `readnote`.)

The four village fetch chains hang here, independent of the spine (all re-issue lost maps and
gate their own rewards):
| chain | fetch | taken by | reward |
|---|---|---|---|
| `agnieszkaquest` | 8× ingot-iron | Agnieszka | 32 gears + permanent repair discount |
| `gerhardtquest` | 10× hide-raw-small | Gerhardt | 12 gears + 6 iron arrows |
| `beataquest` | 1× bread-rye-perfect (Beata gives it) | **Kat** completes | 6 clean bandages (Kat), bread (Beata) |
| `wallquest` | 1× flower-wilddaisy-free (Wall gives it) | **Gerhardt** completes | armlet (Gerhardt), 2 gears (Wall) |
**Detection:** `sawletteragnieszka`, `heardoftobias`, `readnote`, `gavemapagnieszka`,
`gavemapgerhardt`, the `*queststarted/completed/rewarded` flags.

### 7. The Devastation: The Lens
The devastation schematic contains **`jonaslenstower-aged`**; the takeable lens block
(`jonaslens`, lang "The Lens", inventory form `jonaslens-north`) is produced by the tower's
own code (`BlockJonasLensTower`) — it appears in no schematic and no loot table.
**Detection:** `jonaslens-north` in inventory. No variable marks "visited the devastation".

### 8. Tobias' cave: hand over the lens
Tobias (nametag "Old Man") initializes `gavelens="false"` on first meeting; the hand-over
takes the lens block and sets **`player.gavelens=true`** — which gates *everything else he
offers*: his Q&A, charred bread (`entity.gavebread`), his trade list, `unlockdoor
tobiasstorage`, and — via the "my efforts" line only — the **customized translocator
schematic** and the **Illuminal divergence manifold** (`tobtlocatorpart`), both setting
`player.receivedtranslocator`. That is where the 1.22 storyline currently ends.
**Detection:** `gavelens`, `heardtimeswitchtobias`, `receivedtranslocator`.

## Facts that matter for implementation

- **Two locator items are letters:** `letter-faded` → village, `letter-lazaret` → lazaret.
  Anything treating "locatormap-*" as the only map family misses both story letters.
- **Entity-scope steps are invisible away from the NPC** (`requestbronze`, `bronzereceived`,
  `heardlazaret`, `offeredelk`). The durable signals for those steps are the items they
  produce (the maps, the letter, the elk) and `player.hasmet` / `boughtelk`.
- **The lens turn-in has no positive gate** (`gavelens isNotValue true` + inventory only) —
  the reason pre-0.3.0 Tallybook offered it on fresh worlds. Any story surface must gate the
  lens step on the player's *arrival* in the story (e.g. `readnote` or `heardoftobias`),
  because the dialogue file alone cannot.
- **Steps with no variable at all:** owning each map, reaching each structure, taking the
  lens. Progress detection for those is inventory + (optionally) the maps' waypoints; the
  game records nothing else client-readable.
- **`locatormap-dungeon` and `locatormap-treasures`** are non-story (dungeon map is granted
  purely from code; treasure map is shop stock).
- Vanilla quirks found on the way (do not "fix", just know): Tad checks 1 gear for healing
  but never takes it; Tobias' `whathappensnow` branch loops without reaching the translocator
  handout (only "my efforts" reaches it); `trader.json` and `example.json` are identical;
  treasure hunter's saddle answer lists the 100-gear condition twice.
