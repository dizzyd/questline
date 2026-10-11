# Questline

A quest book for Vintage Story built to carry a long progression across a whole modpack:
chapters of quests laid out as dependency trees, tasks detected automatically, and rewards
claimed in the book. The design lives in the Questline design doc.

## Status

Phases 1 to 4 of the roadmap, minus the VS Quest importer:

- Chapters from any mod's `assets/<domain>/questline/chapters/`, merged and checked at load;
  a broken quest is disabled and reported, never its chapter
- `requires` as a tree of `all`, `any`, `atLeast`, quest ids and mod gates
- Tasks: obtain, kill, place, break, craft (every shift-click batch), knap, clayform,
  smith, cook (firepit output), smelt (molds and bloomeries), custom, checkbox
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
- The book takes 80% of the screen, and opens each chapter on all of it or on twenty quests
  around the player
- `/questline reload | complete <quest> [player] | unlock <quest> [player] |
  reset [quest] [player] | resetserver | export | graph <item>|export`

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip lands in `Releases/`.

## License

MIT License
