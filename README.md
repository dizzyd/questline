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
  elk), place, break, craft (every shift-click batch), knap, clayform, smith, cook (firepit
  output), smelt (molds and bloomeries), trade (bought from a trader), custom, checkbox
- Rewards: items, pick-one choices and stages, claimed in the book
- Player history: a quest that unlocks is credited with work done before
  (`CreditEarlierWork` in `ModConfig/questline.json`)
- `QuestlineApi` for other mods (custom tasks, stages, completion events), also over the
  event bus for mods that don't reference Questline
- The book (L): a chapter rail, a pannable and zoomable tree, and quest pages with text,
  images, item rows and recipes; a Reference tab that hosts the handbook's item pages
- Quest modes: each player, the party (pooled, each member claims once) or the whole server
  (pooled; `"claim": "first"` lets the first claim take it). Parties are made in the book's
  Party tab and turned off with `"Parties": false`
- Up to three quests pinned under the minimap; `.questline toasts on|off` per player
- Text size per player: A− and A+ beside the book's tabs, or `.questline textsize <percent>`
  (90, 100, 115 (the default), 130 or 150)
- Chapter art: a background, images, labels and arrows; quests as squares, circles, hexagons
  or gears, at any size
- The in-game editor, for players with `questline.edit` (admins have it): add, move and link
  quests on the canvas, edit them in a form, and export the result as content-mod assets.
  Working copies live in `ModConfig/questline/working/`, never in installed mods. A Problems
  tab lists what the last load found
- The Reference tab replaces the handbook (H, and handbook links): one search across quests,
  guides and items; the game's own item and guide pages, with each item page listing the
  quests that need it or give it. `.questline handbook off` gives H back to the handbook
- A recipe graph of every recipe and item property the handbook knows (about 20,600 edges for
  vanilla, built in under 400 ms): `/questline graph <item>` and `/questline graph export`
- A default questline covering all of vanilla: 322 quests in 24 chapters, from knapping
  through steel, mechanical power, kilns, farming, husbandry, beekeeping, cheese, brewing,
  textiles and leather, boats, riding, arms and armor. It links every one of the game's own
  guides, teaches the mechanics and never the story
- Quests for popular mods, melded into the base chapters when the mod is installed: Expanded
  Foods (with A Culinary Artillery and its modules), Hex's Mech, Seafarer, Bricklayers, Charcoal
  Kiln, Wool, Tailor's Delight and From Golden Combs. 172 quests in all; a chapter file can
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
  reset [quest] [player] | resetserver | export | graph <item> | snapshot`

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
