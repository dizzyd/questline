# Questline

A quest book for Vintage Story built to carry a long progression across a whole modpack:
chapters of quests laid out as dependency trees, tasks detected automatically, and rewards
claimed in the book. The design lives in the Questline design doc.

## Status

Phases 1 and 2 of the roadmap, minus the VS Quest importer:

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
- `/questline reload | complete <quest> | reset [quest]`

## Building

```bash
export VINTAGE_STORY="$(ls -d ~/.cairn/games/1.22* | sort -V | tail -1)"
./build.sh
```

The zip lands in `Releases/`.

## License

MIT License
