# Phase 1 findings

What the rendering spike measured and what the 1.22 source says about the design doc's open
questions. Source paths are relative to the workspace root; checked against the 1.22.0 trees
and run on game 1.22.6.

## Rendering spike: both gates pass

`tests/BookSpikeTests.cs`, client tier on vsclient.home (GTX 1060):

| Gate | Result |
|---|---|
| 500-node tree pans and zooms smoothly | 15 s tour across the whole tree, zoom 0.2–2.3: frame 16.3 ms avg, 16.7 ms p95 (vsync-bound, 60 fps). The canvas's own share: 1.3 ms avg, 4.4 ms p95, 9.8 ms max over 921 frames |
| A handbook page renders inside the book without patches | The copper anvil's vanilla page (61 rich-text components, recipe slideshows and links) composes in the book's Reference tab; item links navigate inside the book and Back returns |

How the canvas does it (`questline/Client/GuiElementQuestCanvas.cs`): edges and node frames are
two meshes in canvas units, drawn with the engine's GUI shader, so pan and zoom only change the
model-view matrix. Item icons (`RenderItemstackToGui`) and labels are drawn per node, for the
nodes in view only. Labels are cached text textures, made at most 8 per frame.

The canvas's worst frames come from label generation on first zoom-in. Dummy titles are long
item names and overlap at zoom 1; real quest titles will need a width limit or show-on-hover.

## Hosting the handbook (Reference tab)

No Harmony needed for item pages:

- `new GuiHandbookItemStackPage(capi, stack).ComposePage(composer, bounds, allStacks, openDetailPageFor)`
  is public. `allStacks` is `ObjectCacheUtil.TryGet<ItemStack[]>(capi, "handbookallstacks")`, set
  at `LevelFinalize` (`VSSurvivalMod/Systems/Handbook/SurvivalHandbook.cs:74`).
- Our `openDetailPageFor` receives every item-icon click in the page, so navigation stays in the book.
- Pages are cheap to construct; `GetHandbookInfo` does the recipe scan at compose time, per page.
- Guide pages: re-parse `GuiHandbookTextPage.Text` with `VtmlUtil.Richtextify`; never reuse its
  cached components, which a disposed rich-text element disposes.
- The full page list (guides included) is reachable by subscribing to
  `ModSystemSurvivalHandbook.OnInitCustomPages`, which hands over the handbook's own list.
- H is the hotkey `"handbook"`; `capi.Input.SetHotKeyHandler("handbook", ...)` reroutes it. The
  `handbook://` link protocol is `capi.LinkProtocols["handbook"]`, also replaceable.

**The recipe graph is not free.** "Created by" and "used in" exist only as rich-text components;
no per-item recipe structure is kept (`CollectibleBehaviorHandbookTextAndExtraInfo`, the
`add*Info` methods). The lookups underneath are public (`World.GridRecipes`,
`FastSearchRecipesByIngredient`, `GetSmithingRecipes()` etc.), so the graph needs its own scan
over the same sources. That moves planner and validator work earlier than phase 4 assumed.

## Crafting hooks (phase 2's cost)

The engine pushes three event-bus events with no Harmony needed:
`"onitemcrafted"`, `"onitemknapped"`, `"onitemclayformed"`. Payload: a tree with `itemstack` and
`byentityid`. Subscribe with `sapi.Event.RegisterEventBusListener`; vanilla's tutorial system does
(`VSSurvivalMod/Systems/Handbook/Tutorial/ModSystemTutorial.cs:81`).

| Station | Hook | Gap |
|---|---|---|
| Grid crafting | `onitemcrafted` | Shift-click reports only the last batch's quantity |
| Knapping | `onitemknapped` | Not fired when the output is a placed block |
| Clay forming | `onitemclayformed` | Not fired for ground-storable outputs — most pottery |
| Anvil | Harmony: `BlockEntityAnvil.CheckIfFinished` | Prefix saves the recipe (cleared on finish); player is null for helve hammers |
| Firepit, cooking pot | Harmony: `ItemSlot.TryPutInto` filtered to the output slots | Credit on take, via `op.ActingPlayer` |
| Tool / ingot molds | Harmony: `TryTakeContents` / `TryTakeIngot` | Prefix saves the contents |
| Bloomery | Harmony: `BlockEntityBloomery.OnBlockBroken` | Its inventory is `internal` |
| Barrel | Harmony: remember the sealer from `OnReceivedClientPacket`, credit on unseal | No player at completion |

So the doc's "one patch per station" holds for anvil, molds, heat stations and barrel, and the
three events cover the rest except for the gaps above, which need a patch each as well.
`CollectibleObject.OnCreatedByCrafting` and `MatchesGridRecipe` fire on preview, not on take, and
can't credit anyone.

## Phase 2: what the station patches found

Every station is now patched (`questline/Server/StationPatches.cs`), and the event-bus
listeners are gone: the grid patch counts every shift-click batch, and the knapping and
clay-forming patches cover the cases their events skip.

The game's internals differ between 1.22 point releases: `InventoryCraftingGrid.ConsumeIngredients`
returns the recipe it consumed in 1.22.7 and `void` in 1.22.6. A postfix reading `__result`
failed to attach on 1.22.6 and, under `PatchAll`, took the whole mod down. Patches now attach
one by one, and the grid patch reads the public `MatchingRecipe` field in a prefix instead.

Gate result, two processes on vsclient.home (game 1.22.6): the dedicated server holds all
8 patched methods, the client process none, and a smithing finish on the server reaches the
client's book as progress.

## Phase 4: the recipe graph and the handbook

**The graph is built, not read.** 1.22 gives every recipe registry a common shape
(`RecipeBase.RecipeIngredients` and `RecipeOutput`, with `IRecipeIngredientBase.SatisfiesAsIngredient`
for wildcards and tags), which covers grid, smithing, knapping, clay forming and barrel recipes.
Cooking pot recipes and alloys are their own classes. Everything else the handbook shows under
"Created by" is an item property, found by walking every collectible the way
`CollectibleBehaviorHandbookTextAndExtraInfo.addCreatedByInfo` does: combustible (smelt, cook,
bake, fire), grinding, crushing, ore → nugget, transitions, juiceable, squeezable, distillation,
carburizing, beehive kiln, ground-stored processing, block drops, creature drops and the
harvestable behaviour's butchering drops. Vanilla 1.22.6: 20,622 edges in 366 ms.

Not yet in it: panning, charcoal pits, bloomery blooms (the graph has ore → ingot by heat, not
ore → bloom → ingot), quern and pulveriser chains beyond the item properties, trader stock, and
anything found only by world generation. The client's graph matches the server's for every
recipe kind; drops weren't compared.

**Hosting the handbook, for real.** Guide pages come from `config/handbook` assets plus the list
`ModSystemSurvivalHandbook.OnInitCustomPages` hands over. A plain `GuiHandbookTextPage` is
re-read from its `Text`; its subclasses (tutorial pages, meal recipe pages, mods') compose
themselves under the key "richtext" exactly as item pages do, so they're hosted the same way and
anything to go on top is prepended to that rich text's components before composing.

The H keys (`handbook`, `selectionhandbook`) and the `handbook`/`handbooksearch` link protocols
are replaced at `LevelFinalize`, after the handbook set them; `HotKey.Handler` and
`capi.LinkProtocols` hand back the originals, so the takeover turns off without a restart.
