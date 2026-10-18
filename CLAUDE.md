# Questline

A quest book for Vintage Story 1.22: chapters of dependency trees, tasks detected
automatically, rewards claimed in the book. The design is a Claude Doc (see the
`questline-design-doc` memory); `docs/phase1-findings.md` records what the 1.22 source
and the rendering spike settled.

## Layout

| | |
|---|---|
| `questline/Model/` | Definitions (chapters, quests, tasks, rewards, page blocks), the `requires` tree and its JSON converter, progress and quest states. Shared by both sides |
| `questline/Server/` | `QuestLoader` (assets and the editor's working copies → registry, validation), `QuestEngine` (progress, parties, claims, sync, save game), `QuestEditor` (working copies, export), `Trackers` and `StationPatches` (game events → task reports) |
| `questline/Graph/` | `RecipeGraph` (edges between item codes, saved and loaded as JSON) and `RecipeGraphBuilder` (scans the running game), built on first use on either side |
| `questline/assets/questline/questline/chapters/` | The default questline: 25 chapters, all of vanilla. `VanillaQuestlineTests` holds it to zero problems, every quest reachable, every vanilla guide linked and a clean layout |
| `…/chapters/<modid>-<chapter>.json` | Addons for other mods: each `extends` a base chapter (or, for Hex's Mech's industry and Seafarer's provisions, is a chapter of its own) and loads only with its `requiresMod`. A quest with a base quest's id replaces it, its text in a page block keyed `<id>-<modid>` |
| `tools/packcheck/` | The pack checker: the loader and checks compiled outside the game (PACKCHECK leaves out the builder), run against `/questline snapshot` |
| `questline/Network/` | The packets. Channel `questline`; message types must be registered in the same order on both sides |
| `questline/Client/` | `QuestClient` (the client's copy), the book dialog (Quests, Reference, Party and, for editors, Problems tabs; edit mode in `GuiDialogQuestBook.Editor.cs`), the canvas, the chapter rail, the HUD pins. `BookStyle` and `BookLayout` hold the type scale, colours and sizing every panel uses |
| `tests/` | vstestkit suites; `tests/fixtures/Mods/qltest` is a content mod of test chapters, committed (it's ours, not downloaded) |

The server owns every quest. The client gets the whole registry as JSON when a player is
playing, then state changes; it only ever sends claim and checkbox requests.

## Testing

On vsclient.home, client tier, with the fixture mod passed by **absolute** path:

```bash
cd ~/src/anego-1.22/vstestkit
bash scripts/sync-linux.sh dizzyd@vsclient.home --mod ../questline/questline
ssh dizzyd@vsclient.home 'cd vstestkit-questline && bash scripts/run.sh mods/questline/tests \
  --mod mods/questline/questline \
  --mods "$HOME/vstestkit-questline/mods/questline/tests/fixtures/Mods" --client'
```

Without the fixture the engine and book tests skip, saying so. Swap `--client` for
`--multiplayer` to run `MultiplayerTests` (dedicated server plus client, the phase 2 gate);
every other suite is `[SingleplayerOnly]`, because it reaches the server with `OnServer()`,
which only works when both sides share a process. Screenshots land in the
game install on the box (`~/.cairn/games/1.22.6/questline-*.png`).

## Traps

- **The `questline/` asset folder only loads because `StartPre` registers it** as an
  `AssetCategory`. The asset manager skips folders that aren't categories, silently.
- **`AssetsFinalize` runs before `StartServerSide`.** The registry loads in
  `StartServerSide`, where every asset is already there.
- **Touch the book only from the client thread.** In singleplayer a test body can be on
  the server thread, which has no GL context: composing the dialog there throws on the first
  texture upload, and in one run took the whole process down with nothing in the logs.
  `await OnClient()` first.
- **`Input.Click` doesn't reach dialog elements**; it goes in through the world's mouse
  state. Hand the press to the dialog, as `BookTests.ClickAt` does (configkit does the same).
- **The book reopens on the tab it was left on.** Tests start from a known chapter.
- **Station patches attach one at a time** (`StationPatches.Apply`). The game's API moves
  even between 1.22 point releases — `InventoryCraftingGrid.ConsumeIngredients` returns its
  recipe in 1.22.7 and nothing in 1.22.6 — and one failed patch used to stop the whole mod.
  CI builds against 1.22.7; the test box runs 1.22.6.
- **Tests can't name HarmonyLib on a dedicated server**: the in-game compiler there doesn't
  see 0Harmony. Use plain reflection, and `StationPatches.Attached()` for patch counts.
- In multiplayer, a test reaches the server only through `Remote.Eval` snippets; the
  default usings include `Vintagestory.GameContent`.
- The test window is 960×600, so the book sizes itself to the screen. Check new layout in a
  screenshot there, not just at a desktop size.
- **Every panel goes on the book's grid.** Panels are insets the same height, buttons sit in
  the row under them (`AddRowButton` sizes them to their label), text uses `BookStyle`. Check
  new layout in a screenshot at 960×600, where the book is at its minimum.
- **A form in a dialog can't scroll.** Text inputs and dropdowns draw their frames into the
  dialog's static texture, so moving their bounds leaves the frames behind. The editor splits
  its form into pages instead.
- **The definitions packet arrives before the editor packet** after every reload. Only the
  editor packet (`QuestClient.EditorChanged`) may replace the editor's draft; on definitions
  alone the editor still holds the old chapters.
- **Editor tests work in the domain `qleditor`** and clear its working copies (and `qltest`'s)
  before each test, so a failed run doesn't leave edits loaded into the next.
- **Text rendered at a z below 50 is hidden** behind the dialog's own texture, which draws at
  50. The canvas draws its art just above that.
- **The graph and Reference tests reach the server directly**, so they're `[SingleplayerOnly]`;
  `MultiplayerTests` checks the client builds the same graph as a dedicated server.
- **A LinkTextComponent made without the client API can't handle its link.** To follow one in a
  test, call `Capi.LinkProtocols["handbook"](link)` as a click does.
- **The loader and checks must not touch the running game directly.** They get everything through
  `IGameFacts` (`ServerFacts` in game, `PackFacts` in the checker); `PackCheckTests` requires
  both to find the same problems on the fixture.
- **Ask `Translations.Has`, not `Lang.HasTranslation`.** The game's check looks only in the
  player's language, while `Lang.Get` falls back to English; on a German game every key German
  lacked counted as missing (364 problems, all of them false).
- **Every line is in 33 languages.** `TranslationTests` fails when a key in `en.json` is missing
  from any of them, so a new or reworded line means translating it into every `lang/*.json`, with
  the game's own word for each item; `tools/lang/check.py` checks keys, `{0}` arguments and
  handbook links without the game.
- **Look item codes up, don't guess them.** Variant codes differ from the file names: keep a
  headless session (`run.sh ... --keep`) and ask `bash scripts/vstk eval` what exists.
- **The graph stands an ingot in for the metal.** Casting takes anything that melts into it;
  a check that treats an ingot as only an ingot gets ordering wrong.
- **Test runs:** `--filter` is a plain substring, not a pattern. A stuck run is better caught
  with `timeout` around `run.sh` than by waiting.
- **The client builds its own recipe graph and sees less.** A dedicated client has no server-side
  entity behaviour config, so butchering drops are server-only, and milking and picking up fall
  back to the game's own defaults. `MultiplayerTests` holds the two graphs to the same counts.
- **Visit tasks read what worldgen recorded, not the configs.** Each placed structure is in its map
  region's `GeneratedStructures` under a code the game makes up (`<schematic>/<structure>` for ruins and each building of a village,
  `<structure>:<schematic>` for story locations, a dungeon's pieces by group), without a domain.
  `StructureCodes` turns that back into the config's code and domain. Tests add records of their own
  (`VisitTests.Place`) and must take them out again.
- **Some things fire no event to credit.** Putting things in ground storage never calls
  `DidPlaceBlock`; every pot meal is the same `claypot-*-cooked` (the recipe is an attribute);
  grafting and planting place the same cutting; quenching, kiln firing and anvil welding change
  state without a player action the trackers see. The default questline uses checkboxes there.
- **The canvas holds more nodes than the chapter has quests.** In reading mode, each unmet
  prerequisite from another chapter is a stub (`QuestCanvasNode.Stub`) appended after the chapter's
  quests, so a node index past `canvasQuests.Count` is a stub, not a quest. The editor and plans have none.
- **The book has one history, across tabs.** `SelectChapter`, `SelectQuest`, `OpenRef` and tab
  switches call `Remember()` first; Back (the key, the mouse button, the Reference tab's button)
  restores tab, chapter, quest and page together. Anything new that moves the book should remember too.
- **A quest's item may have no handbook page of its own.** The handbook keys pages by particular
  stacks (a lantern's materials, a meal) and leaves some things out (pies), so `ShowReference` falls
  back to the item's page, then its kind's, then a search. `EveryQuestItemOpensAPage` holds every
  default quest item to that.
- **The back key is a hotkey with no handler.** `questline-back` (Backspace) is registered so
  players can rebind it, but the book reads it in its own `OnKeyDown`, after `base.OnKeyDown` has
  given a focused text field the key. A handler would fire while typing in chat or a form.
- **Gear rewards are scaled on the server's registry, not in the files.** `GearRewards` multiplies
  them after loading, so claims and the book agree; the editor's raw chapters keep the pack's amounts.
  An ordinary quest gives one gear at most (`GearRewardsAreSparingAndScale`); milestones offer more.
- **Edit the default questline with the layout in mind.** Quests sit on columns 2 apart and rows
  1.5 apart; a quest's in-chapter prerequisites sit to its left; no line may pass behind a quest.
  Entry quests (prerequisites only in other chapters) sit beside what they unlock, not in column 0.
- **Addons are tested against the real mods.** The box keeps them in `~/questline-addon-mods`
  (the pack's zips plus their libraries); pass that as `--mods` instead of the fixtures to run the
  questline tests with every addon loaded. Take a snapshot there too (`/questline snapshot`) for
  the pack checker. Without the mods the addons must load to nothing, quietly.
- **The graph can't see a mod's own machines** (Hex's Mech's furnaces, Expanded Foods' mixing
  bowl, Seafarer's prep table). A task for their output carries `"unchecked": true`, which skips
  the "how is it made" checks; the code must still exist. Use it only for that, and for loot
  only worldgen's schematics hold (Better Ruins' blueprints), which the graph can't read either.
- **A target can be a regular expression,** as the game's matcher allows: `game:@(redmeat|poultry)-cooked`.
  Use it where a wildcard would take in things the quest doesn't mean.
- **The whole of Dizzy's Best Pack runs on the box** for a release check. The pack targets 1.22.7,
  which lives at `~/vs-installs/1.22.7` there, outside Cairn's games folder so other slots keep
  defaulting to 1.22.6. Its zips are in `~/questline-pack-mods`. Point the run at both, give the
  boot time for a big pack's worldgen, and keep the fixtures:
  ```bash
  env VINTAGE_STORY=$HOME/vs-installs/1.22.7 DOTNET_ROOT=$HOME/.cairn/runtimes/10.0.11-linux-x64 \
      PATH=$HOME/.cairn/runtimes/10.0.11-linux-x64:$PATH VSTK_BOOT_TIMEOUT=900 \
    bash scripts/run.sh mods/questline/tests --mod mods/questline/questline \
      --mods "$HOME/questline-pack-mods" --mods "$HOME/vstestkit-questline/mods/questline/tests/fixtures/Mods" --client
  ```
  On 1.22.6 two of its mods refuse to load and the client gives up before the world opens.
- **Tests write `VsTestkit.Testing.Gui` in full.** A pack mod (`gui`) has a top-level `Gui`
  namespace, which hides the testkit's class, and an alias can't share its name.
- **The book draws in the handbook's layer** (`DrawOrder` 0.2, with chests, barrels and firepits),
  so it can come up over them, and every way of showing it brings it to the front (`Show()`).
  L and H close it only when it's on top (`OnTop`); a buried book comes forward instead.
- **Text sizes come from `BookStyle.TextScale`**, the player's `TextSize` setting. The editor sets it
  back to 1 while editing, because its forms are laid out for the game's own sizes; check new layout
  at 150% as well as 115%.
- **Nothing heavy runs in one go on the main thread.** The recipe graph is built a few
  milliseconds a tick (`GraphBuild`, from a tick listener, on both sides): its builder reads the
  game through mods' code, which isn't safe off the main thread, so it yields in tiny steps
  instead (`GraphTests.TheGraphIsBuiltInSmallSteps`). The checks' index and first run are pure
  work over Questline's own data and go to a worker thread (`GraphIndex`, about 15 s on the whole
  pack), built once per graph; a reload's checks then take milliseconds. Tests that read
  graph-check problems wait for `Engine.GraphChecksDone`; the planner waits for
  `QuestClient.GraphReady`. Asking for `Graph` early still works: it finishes the build at once.
