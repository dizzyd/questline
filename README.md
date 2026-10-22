# Questline

A quest book for Vintage Story built to carry a long progression across a whole modpack:
chapters of quests laid out as dependency trees, tasks detected automatically, and rewards
claimed in the book. The design lives in the Questline design doc.

## Status

Every phase of the roadmap, minus the VS Quest importer:

- Chapters from any mod's `assets/<domain>/questline/chapters/`, merged and checked at load;
  a broken quest is disabled and reported, never its chapter
- `requires` as a tree of `all`, `any`, `atLeast`, quest ids and mod gates
- Tasks: obtain (liquids in a carried bucket count by the litre), kill, mount (rafts, boats,
  elk), place, break, craft (every shift-click batch, and Toolsmith's tools assembled in the
  hands or on its workbench), knap, clayform, smith, cook (firepit output), smelt (molds and
  bloomeries), trade (bought from a trader), visit (standing inside a structure worldgen placed,
  each structure once), custom, checkbox. A visit names the structure by its code in the
  worldgen config, with that file's domain (`game:trader-*`,
  `betterruins:largeforlornhopetower`), and its place by the lang entry `<domain>:structure-<code>`;
  a village counts each of its buildings. `/questline structures` names whatever you are standing
  in, and a `discovered` gate sees places visited
- Rewards: items, pick-one choices and stages, claimed in the book. `GearRewards` in
  `ModConfig/questline.json` scales the rusty gears they give (2 for a trading server, 0 for none);
  `"ItemRewards": false` gives no items at all (stages still), so quests finish without a claim,
  for a server that only tracks progression. `/questline reload` re-reads these settings
- Player history: a quest that unlocks is credited with work done before it unlocked, since
  Questline was installed (`CreditEarlierWork` in `ModConfig/questline.json`). The game keeps no
  record of its own, so for a world played before then, `/questline catchup [player]` completes
  every quest whose items the player carries in the hotbar and backpack, with everything it needs.
  Where a quest needs one of several, it takes only one the items prove; it never completes a
  server quest. Those quests count as claimed, for the whole party: earlier work doesn't pay out
- `QuestlineApi` for other mods (custom tasks, stages, completion events), also over the
  event bus for mods that don't reference Questline
- The book (L): a chapter rail, a pannable and zoomable tree, and quest pages with text,
  images, item rows and recipes; a Reference tab that hosts the handbook's item pages
- Finding the way: a locked quest lists what it needs, with the chapter of each and a link to
  it, and the open quests to start with; prerequisites from other chapters stand beside their
  quest on the canvas; a chapter with nothing open says what opens it. Backspace (or the
  mouse's back button) steps back along the way the book came
- Ages: chapters marked `"age": true` (stone, clay and fire, copper, bronze, iron, steel in the
  default questline) label every quest elsewhere with the age its prerequisites lead back to,
  and a chapter's late branch carries that age's icon
- Quest modes: each player, the party (pooled, each member claims once) or the whole server
  (pooled; `"claim": "first"` lets the first claim take it). Parties are made in the book's
  Party tab and turned off with `"Parties": false`
- Up to three quests pinned under the minimap, from the book or with `.questline pin <quest>`
  (`.questline pins` lists them); `.questline toasts on|off` per player
- Text size per player: A− and A+ beside the book's tabs, or `.questline textsize <percent>`
  (90, 100, 115 (the default), 130 or 150)
- Chapter art: a background, images, labels and arrows; quests as squares, circles, hexagons
  or gears, at any size
- The in-game editor, for players with `questline.edit` (admins have it): add, move and link
  quests on the canvas, edit them in a form, and export the result as content-mod assets.
  Working copies live in `ModConfig/questline/working/`, never in installed mods. A Problems
  tab lists what the last load found
- The Reference tab replaces the handbook (H, and handbook links): one search across quests,
  guides and items, with an item's variants (every wood, every colour) in one row; `@modid`
  lists every item a mod adds (`@modid words` searches within it), and an item page lists its
  variants. The game's own item and guide pages, with each item page listing
  the quests that need it or give it. `.questline handbook off` gives H back to the handbook
- A recipe graph of every recipe and item property the handbook knows (about 20,600 edges for
  vanilla, built in under 400 ms): `/questline graph <item>` and `/questline graph export`
- A default questline covering all of vanilla: 322 quests in 25 chapters, from knapping
  through steel, mechanical power, kilns, farming, husbandry, beekeeping, cheese, brewing,
  textiles and leather, boats, riding, arms and armor. It links every one of the game's own
  guides, teaches the mechanics and never the story
- Quests for popular mods, melded into the base chapters when the mod is installed: Expanded
  Foods (with A Culinary Artillery and its modules), Hex's Mech, Seafarer, Bricklayers, Charcoal
  Kiln, Wool, Tailor's Delight, From Golden Combs and Better Ruins (its ruins, hidden places and the
  far story locations its maps lead to). 194 quests in all; a chapter file can
  `extend` a base chapter and replace a base quest whose mechanic the mod changes
- A goal planner: "Plan how to make this" on an item page works it back through the recipe
  graph to what the player carries. `RevealLevel` in `ModConfig/questline.json` caps what it
  shows: `guide` (every step and recipe), `hints` (the default: what to get next, not how) or
  `discovery` (off). Players can choose less
- Checks against the recipe graph: tasks for things nothing makes, the wrong station, items the
  questline only asks for later, and story items in the default questline without a
  `{"discovered": ...}` gate. The editor suggests prerequisites from the same graph
- The book takes 80% of the screen, and opens each chapter on all of it or on twenty quests
  around the player
- `/questline reload | complete <quest> [player] | unlock <quest> [player] |
  catchup [player] | reset [quest] [player] | resetserver | export | graph <item> | structures | snapshot`
- A quest's id is at the foot of its page in the book; commands take it without its domain
  (`cast-anvil`), the default questline's quest winning where two domains share one

## Translations

Questline is in every language the game ships (`questline/assets/questline/lang/`), translated
by machine with the game's own word for every item, and not yet checked by native speakers.
Corrections are welcome. A new or changed line in `en.json` needs the same in every language;
`python3 tools/lang/check.py` checks each file's keys, arguments and handbook links.

## Checking a quest pack

`tools/packcheck` runs every check the server runs (missing items and text, broken requirements,
cycles, the recipe graph's checks, spoilers) on a quest pack outside the game, like a test suite
before a release.

1. In a game with the pack's dependencies installed, run `/questline snapshot`. It writes
   `ModConfig/questline/questline-snapshot.json`: the game's items, text and recipe graph.
2. Run the checker on the pack's folder or zip:

```bash
dotnet build tools/packcheck -c Release
dotnet tools/packcheck/bin/Release/net10.0/questline-packcheck.dll \
  --snapshot questline-snapshot.json --game "$VINTAGE_STORY" mypack/
```

It prints each problem and exits 1 if any quest would be disabled (`--strict`: any problem at
all). `--game` lets it find textures a pack borrows from the game.

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip lands in `Releases/`.

## License

MIT; see [LICENSE](LICENSE).
