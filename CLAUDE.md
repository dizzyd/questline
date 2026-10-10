# Questline

A quest book for Vintage Story 1.22: chapters of dependency trees, tasks detected
automatically, rewards claimed in the book. The design is a Claude Doc (see the
`questline-design-doc` memory); `docs/phase1-findings.md` records what the 1.22 source
and the rendering spike settled.

## Layout

| | |
|---|---|
| `questline/Model/` | Definitions (chapters, quests, tasks, rewards, page blocks), the `requires` tree and its JSON converter, progress and quest states. Shared by both sides |
| `questline/Server/` | `QuestLoader` (assets → registry, validation), `QuestEngine` (progress, claims, sync, save game), `Trackers` (game events → task reports) |
| `questline/Network/` | The four packets. Channel `questline`; message types must be registered in the same order on both sides |
| `questline/Client/` | `QuestClient` (the client's copy), the book dialog (Quests and Reference tabs), the canvas |
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

Without the fixture the engine and book tests skip, saying so. Screenshots land in the
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
- The test window is 960×600, so the book sizes itself to the screen. Check new layout in a
  screenshot there, not just at a desktop size.
