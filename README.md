# Questline

A quest book for Vintage Story built to carry a long progression across a whole modpack:
chapters of quests laid out as dependency trees, tasks detected automatically, and rewards
claimed in the book. The design lives in the Questline design doc.

## Status

Phase 1 of the roadmap, minus the VS Quest importer:

- Chapters from any mod's `assets/<domain>/questline/chapters/`, merged and checked at load;
  a broken quest is disabled and reported, never its chapter
- `requires` as a tree of `all`, `any`, `atLeast`, quest ids and mod gates
- Tasks: obtain, kill, place, break, craft, knap, clayform, checkbox
- Rewards: items and pick-one choices, claimed in the book
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
