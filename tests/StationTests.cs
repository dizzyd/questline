using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using questline;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Each crafting station's finish, credited through StationPatches. Every test sets a real
    /// block entity up so its own finish method completes a real recipe, then calls that method,
    /// so a patch aimed at a moved or renamed method fails here rather than in someone's world.
    /// The qltest "stations" chapter has one never-ending quest per task type to count into.
    /// </summary>
    [RequiresClient]
    public class StationTests
    {
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static IServerPlayer Me => (IServerPlayer)Player.Me;

        static int Progress(string type) => Engine.ProgressOf(Me.PlayerUID).Find("qltest:station-" + type)?.Tasks[0] ?? 0;

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get("qltest:station-craft") == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Engine.Reset(Me);
        }

        [VsTest]
        public void EveryPatchAttachesOnce()
        {
            // In singleplayer both sides share the assembly; patching from Start() would double up.
            int methods = 0;
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                int prefixes = info.Prefixes.Count(p => p.owner == StationPatches.HarmonyId);
                int postfixes = info.Postfixes.Count(p => p.owner == StationPatches.HarmonyId);
                if (prefixes + postfixes == 0) continue;

                methods++;
                Log($"{method.DeclaringType?.Name}.{method.Name}: {prefixes} prefix, {postfixes} postfix");
                Assert.True(prefixes <= 1 && postfixes <= 1, $"{method.DeclaringType?.Name}.{method.Name} is patched more than once");
            }
            Assert.Equal(8, methods, "every station patch attached");
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ShiftClickGridCraftingCountsEveryBatch()
        {
            var recipe = Sapi.World.GridRecipes.First(r =>
                r.Width == 1 && r.Height == 1 && r.ResolvedIngredients?.Length == 1
                && r.ResolvedIngredients[0] is { IsTool: false, IsWildCard: false, Quantity: 1, ResolvedItemStack: not null }
                && r.Output.ResolvedItemStack?.StackSize > 0);
            Log($"recipe: {recipe.ResolvedIngredients[0].Code} -> {recipe.Output.ResolvedItemStack.StackSize}x {recipe.Output.Code}");

            var grid = Me.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
            var output = grid[grid.Count - 1];
            var ingredient = recipe.ResolvedIngredients[0].ResolvedItemStack.Clone();
            ingredient.StackSize = 5;
            grid[0].Itemstack = ingredient;
            grid[0].MarkDirty();
            await Until(() => output.Itemstack != null, 20, "the grid to find the recipe");

            var sink = new DummySlot();
            var op = new ItemStackMoveOperation(Sapi.World, EnumMouseButton.Left, EnumModifierKey.SHIFT, EnumMergePriority.AutoMerge, output.StackSize)
            {
                ActingPlayer = Me
            };
            output.TryPutInto(sink, ref op);

            Log($"made {sink.StackSize}, credited {Progress("craft")}");
            Assert.Greater(sink.StackSize, recipe.Output.ResolvedItemStack.StackSize, "more than one batch");
            Assert.Equal(sink.StackSize, Progress("craft"));
            grid[0].Itemstack = null;
            grid[0].MarkDirty();
        }

        [VsTest]
        public void FinishingOnTheAnvilCountsAsSmithing()
        {
            World.SetBlock("game:anvil-copper", P(3, 1, 3));
            var anvil = World.BE<BlockEntityAnvil>(P(3, 1, 3));
            var recipe = Sapi.GetSmithingRecipes().First(r => r.Output.ResolvedItemstack != null);

            anvil.SelectedRecipeId = recipe.RecipeId;
            AccessTools.Field(typeof(BlockEntityAnvil), "workItemStack").SetValue(anvil, World.Stack("game:ingot-copper"));
            var target = anvil.recipeVoxels;
            int layers = Math.Min(6, recipe.QuantityLayers);
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 6; y++)
                    for (int z = 0; z < 16; z++)
                        anvil.Voxels[x, y, z] = (byte)(y < layers && target[x, y, z] ? EnumVoxelMaterial.Metal : EnumVoxelMaterial.Empty);

            anvil.CheckIfFinished(Me);

            Assert.Equal(-1, anvil.SelectedRecipeId, "the anvil finished the recipe");
            Assert.Equal(recipe.Output.ResolvedItemstack.StackSize, Progress("smith"));
        }

        [VsTest]
        public void FinishingAKnappingCounts()
        {
            World.SetBlock("game:knappingsurface", P(5, 1, 3));
            var surface = World.BE<BlockEntityKnappingSurface>(P(5, 1, 3));
            var recipe = Sapi.GetKnappingRecipes().First(r => r.Output.ResolvedItemstack?.Class == EnumItemClass.Item);

            AccessTools.Field(typeof(BlockEntityKnappingSurface), "selectedRecipeId").SetValue(surface, recipe.RecipeId);
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++)
                    surface.Voxels[x, z] = recipe.Voxels[x, 0, z];

            surface.CheckIfFinished(Me);

            Assert.Equal(recipe.Output.ResolvedItemstack.StackSize, Progress("knap"));
        }

        [VsTest]
        public void FinishingAClayFormCountsEvenForPottery()
        {
            World.SetBlock("game:clayform", P(7, 1, 3));
            var form = World.BE<BlockEntityClayForm>(P(7, 1, 3));
            // Ground-storable pottery is the case the game's own event misses.
            var recipe = Sapi.GetClayformingRecipes().First(r =>
                r.Output.ResolvedItemstack?.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>() != null);
            Log($"recipe: {recipe.Output.ResolvedItemstack.Collectible.Code}");

            AccessTools.Field(typeof(BlockEntityClayForm), "selectedRecipeId").SetValue(form, recipe.RecipeId);
            AccessTools.Field(typeof(BlockEntityClayForm), "selectedRecipe").SetValue(form, recipe);
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    for (int z = 0; z < 16; z++)
                        form.Voxels[x, y, z] = recipe.Voxels[x, y, z];

            form.CheckIfFinished(Me, 0);

            Assert.Equal(recipe.Output.ResolvedItemstack.StackSize, Progress("clayform"));
        }

        [VsTest]
        public void TakingFromAFirepitCountsAsCooking()
        {
            World.SetBlock("game:firepit-extinct", P(9, 1, 3));
            var firepit = World.BE<BlockEntityFirepit>(P(9, 1, 3));
            var outputSlot = firepit.Inventory[2];
            outputSlot.Itemstack = World.Stack("game:ingot-copper", 3);

            var op = new ItemStackMoveOperation(Sapi.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 3) { ActingPlayer = Me };
            outputSlot.TryPutInto(new DummySlot(), ref op);

            Assert.Equal(3, Progress("cook"));

            // Other slots don't count: putting into the fuel slot is not cooking.
            var fuel = new DummySlot(World.Stack("game:firewood", 2));
            var op2 = new ItemStackMoveOperation(Sapi.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 2) { ActingPlayer = Me };
            firepit.Inventory[0].TryPutInto(fuel, ref op2);
            Assert.Equal(3, Progress("cook"));
        }

        [VsTest]
        public void TakingFromMoldsCountsAsSmelting()
        {
            World.SetBlock("game:toolmold-blue-fired-axe", P(11, 1, 3));
            var toolMold = World.BE<BlockEntityToolMold>(P(11, 1, 3));
            toolMold.MetalContent = World.Stack("game:ingot-copper");
            toolMold.FillLevel = Traverse.Create(toolMold).Field("requiredUnits").GetValue<int>();
            AccessTools.Method(typeof(BlockEntityToolMold), "TryTakeContents").Invoke(toolMold, [Me]);
            Assert.Equal(0, toolMold.FillLevel, "the tool mold was emptied");
            Assert.Equal(1, Progress("smelt"));

            World.SetBlock("game:ingotmold-blue-fired", P(13, 1, 3));
            var ingotMold = World.BE<BlockEntityIngotMold>(P(13, 1, 3));
            ingotMold.ContentsLeft = World.Stack("game:ingot-copper");
            ingotMold.FillLevelLeft = ingotMold.RequiredUnits;
            AccessTools.Method(typeof(BlockEntityIngotMold), "TryTakeIngot").Invoke(ingotMold, [Me, new Vec3d(0.25, 0.1, 0.5)]);
            Assert.Equal(0, ingotMold.FillLevelLeft, "the left ingot was taken");
            Assert.Equal(2, Progress("smelt"));
        }

        [VsTest]
        public void BreakingABloomeryWithABloomCountsAsSmelting()
        {
            World.SetBlock("game:bloomerybase-north", P(3, 1, 8));
            var bloomery = World.BE<BlockEntityBloomery>(P(3, 1, 8));
            Traverse.Create(bloomery).Field("bloomeryInv").GetValue<InventoryGeneric>()[2].Itemstack = World.Stack("game:ironbloom");

            bloomery.OnBlockBroken(Me);

            Assert.Equal(1, Progress("smelt"));
        }
    }
}
