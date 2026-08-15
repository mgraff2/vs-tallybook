# ModDB screenshots — what each one is for

The screenshots on the mod page are taken by **`.tallybook screenshots`**, which walks every
surface and writes one stable, feature-named PNG per shot into
`VintagestoryData/ModData/tallybook/screenshots/`. Stable names are the point: replacing a
shot on ModDB is then mechanical, and diffing this folder against the last upload says which
shots actually changed.

Each shot is **cropped to the Tallybook window** (plus a small margin), not the whole screen
— the window is the subject anyway. `full` turns cropping off; `pad <px>` changes the margin.

Two size rules are enforced for you, so nothing needs resizing before upload:

- **Nothing is ever larger than 1920×1080** — ModDB's ceiling. A 4K screen or a
  supersampled framebuffer is scaled down to fit; anything already inside is left untouched.
- **`hud.png` is always exactly 480×320.** The HUD's height depends on how many rows your
  list has that day, so a tight crop would give the mod page a differently shaped picture
  every release; instead it takes a fixed 480×320 window centred on the HUD, with game
  visible around it. No resampling in the normal case, so it stays pixel-sharp.

Note the folder is deliberately *not* the game's own screenshots folder: that resolves to
your Pictures directory, which on Windows is often redirected into OneDrive, and these are
working files for a mod page rather than screenshots you took.

The command only ever *navigates* — it opens screens and selects tabs, and touches no pin,
count or expansion. **What is in the shots is therefore whatever is in your world**, which
is the honest way round: the staging notes below are things to have going before you run it,
not things the mod fakes.

## Taking them

1. Load the world you want photographed (see "the screenshot world" below).
2. Stand somewhere that looks like the game: daylight, a village or your base behind the
   window, not a dirt hole — and facing a villager who is ready, for the last shot.
3. Run `.tallybook screenshots`. It counts down ~6 seconds, then works through the list.
   Move the mouse off the game window and **don't touch the keyboard** until it reports back.
4. Check the folder, then upload the ones the table says are stale.

Arguments, all optional and only needed when something looks wrong:

| Argument | When |
| --- | --- |
| `wait <seconds>` | The chat log is still showing in the shots — a longer pre-roll lets it fade. |
| `pad <px>` | More or less of the game visible around the window (default 10). |
| `full` | You want whole-screen shots after all. |
| `stage final` | Every shot came back blank; grabs at `AfterFinalComposition` instead of `Done`. The command detects blank shots and tells you this. |
| `noflip` | The images are upside down. |

## The shots

Each row is one file in `Screenshots/tallybook/`. "Last invalidated" is the version whose
changes mean the shot must be re-taken; if the uploaded one predates it, it is a lie about
the current mod.

File names are numbered by **gallery order** — the order they are uploaded and shown in.
The HUD appears twice on purpose: the first image doubles as the mod page's thumbnail, so
it does the cover job and opens the gallery. They are two separate captures a moment apart,
not one file copied.

| File | Shows | Stage it with | Last invalidated |
| --- | --- | --- | --- |
| `01-hud.png` | The corner HUD (fixed 480×320 frame around it) | A few checked pins with partial progress (a mix of green/orange/white rows), at least one errand with a distance, an Explore place on the HUD, and the spawn-distance line on. The dialog is closed for this one. | 0.3.15 |
| `02-hud.png` | The same, second capture | As above — nothing extra to do. | 0.3.15 |
| `03-options.png` | The settings screen | Nothing to stage. | 0.3.15 |
| `04-items.png` | The shopping list | 3–5 pins, at least one expanded a level or two so the craft tree shows, one complete (green) and one barely started — a "construction" pin with its stage materials makes the best show. | 0.3.15 |
| `05-side-quests.png` | Errands, sites and the story block | An accepted villager errand mid-progress, ideally a map-artifact site quest too, with the story block showing a revealed step; the Sort dropdown is visible by default. | 0.3.15 |
| `06-explore.png` | Saved places with notes and distances | Two or three saved places — one unfolded showing bullet and checkbox notes (one ticked), one parked — with distances at a glance. | 0.3.15 |
| `07-player.png` | Spawn points and your numbers | A temporal-gear returning point set, so the second spawn row and "respawns left" are populated rather than "none". | 0.3.14 |
| `08-world.png` | This world's rules and mods | Nothing to stage — the accordion opens on "Changed on this server"; have a content mod installed so the counted section headings and Mods list have something to show. | 0.3.15 |
| `09-lore.png` | Lore volumes, filters and Read buttons | Lore found from **two or more sources** (vanilla + a lore-adding mod) so the source dropdown and the per-mod clustering are visible, and a mix of complete and in-progress volumes. | 0.3.14 |
| `10-history.png` | Finished quests by year, with transcripts | At least one completed errand, with one record expanded so the conversation transcript is visible; year headings show with the pre-install pile folded at the bottom. | 0.3.15 |
| `11-quest-glow.png` | A villager shimmering with an errand ready to hand in | **Stand facing a villager who is ready** — you carry everything they asked for, or they owe you an uncollected reward. This is the one shot of the world rather than a window, so it keeps the full screen (capped to 1920×1080). The walker will not fake a quest state: with nobody glowing it skips the shot and says so. | 0.3.10 |

### The screenshot world

The staging above is easiest in a **world kept for this purpose**: pins in every state, an
errand ready to hand in, a villager standing in front of you, lore found from two mods, a
returning point set. Load it, aim at the villager, run the command once — the whole gallery
comes out in order, correctly framed, ready to upload.

## Keeping this honest

- **A new shot needs a row here.** The shot list lives in `TallybookModSystem.RunShowcase`;
  a file with no row is a screenshot nobody knows the purpose of, and it will be the one
  that goes stale unnoticed.
- **Bump "last invalidated" when a release changes what a surface looks like** — that is the
  whole value of this table. The release checklist in README.md points here.
- A shot whose subject is not there — a tab switched off, no villager glowing — is
  **skipped**, and the run says which and why. A missing file means "there was nothing to
  photograph", never "the shot failed silently".
- **Numbers are the upload order**, so keep them contiguous: inserting a shot in the middle
  means renumbering the ones after it here and in `RunShowcase` together.
