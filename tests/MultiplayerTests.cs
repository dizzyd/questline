using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The phase 2 gate: station tasks on a dedicated server, with the client in its own process.
    /// The server side runs through Remote.Eval; this process is the client, and should hold no
    /// patches at all, only the progress the server sends it.
    /// </summary>
    [RequiresMultiplayer]
    public class MultiplayerTests
    {
        static QuestClient Client => Capi.ModLoader.GetModSystem<QuestlineModSystem>().Client;

        // Shared by every snippet: the engine and the one player on the server.
        const string Prelude = @"
            var engine = sapi.ModLoader.GetModSystem<questline.QuestlineModSystem>().Engine;
            var player = (Vintagestory.API.Server.IServerPlayer)sapi.World.AllOnlinePlayers[0];
            int Progress(string type) => engine.ProgressOf(player.PlayerUID).Find(""qltest:station-"" + type)?.Tasks[0] ?? 0;
        ";

        [VsTest(TimeoutMs = 60000)]
        public async Task PatchesLiveOnTheServerOnly()
        {
            string serverPatches = await Remote.Eval("return questline.Server.StationPatches.Attached().Count;");
            Log($"server: {serverPatches} patched methods; client: {StationPatches.Attached().Count}");
            Assert.Equal("8", serverPatches.Trim());
            Assert.Equal(0, StationPatches.Attached().Count, "the client process patches nothing");
        }

        /// <summary>The client builds the recipe graph from the recipes the server sent it; it should be the server's graph.</summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task TheClientsRecipeGraphMatchesTheServers()
        {
            string server = await Remote.Eval(@"
                var g = sapi.ModLoader.GetModSystem<questline.QuestlineModSystem>().Engine.Graph;
                return string.Join("","", g.CountByKind().Where(k => !k.Key.StartsWith(""drop"") && k.Key != ""entity-drop"" && k.Key != ""butcher"" && k.Key != ""trade"").Select(k => k.Key + ""="" + k.Value));");
            await OnClient();
            var graph = Client.Graph;
            string client = string.Join(",", graph.CountByKind().Where(k => !k.Key.StartsWith("drop") && k.Key != "entity-drop" && k.Key != "butcher" && k.Key != "trade").Select(k => k.Key + "=" + k.Value));
            Log($"server: {server.Trim()}");
            Log($"client: {client}");
            Assert.Equal(server.Trim(), client, "the same recipes on both sides");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task SmithingOnTheServerReachesTheClient()
        {
            await OnClient();
            await Until(() => Client.Ready, 200, "definitions on the client");
            if (Client.Registry.Get("qltest:station-smith") == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");

            string credited = await Remote.Eval(Prelude + @"
                engine.Reset(player);
                var pos = player.Entity.Pos.AsBlockPos.AddCopy(2, 0, 0);
                sapi.World.BlockAccessor.SetBlock(sapi.World.GetBlock(new AssetLocation(""game:anvil-copper"")).BlockId, pos);
                var anvil = (Vintagestory.GameContent.BlockEntityAnvil)sapi.World.BlockAccessor.GetBlockEntity(pos);
                var recipe = Vintagestory.GameContent.ApiAdditions.GetSmithingRecipes(sapi).First(r => r.Output.ResolvedItemstack != null);
                anvil.SelectedRecipeId = recipe.RecipeId;
                typeof(Vintagestory.GameContent.BlockEntityAnvil)
                    .GetField(""workItemStack"", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(anvil, new ItemStack(sapi.World.GetItem(new AssetLocation(""game:ingot-copper""))));
                var target = anvil.recipeVoxels;
                int layers = System.Math.Min(6, recipe.QuantityLayers);
                for (int x = 0; x < 16; x++)
                    for (int y = 0; y < 6; y++)
                        for (int z = 0; z < 16; z++)
                            anvil.Voxels[x, y, z] = (byte)(y < layers && target[x, y, z] ? Vintagestory.GameContent.EnumVoxelMaterial.Metal : Vintagestory.GameContent.EnumVoxelMaterial.Empty);
                anvil.CheckIfFinished(player);
                sapi.World.BlockAccessor.SetBlock(0, pos);
                return Progress(""smith"");
            ");
            Log($"server credited {credited.Trim()} for smithing");
            Assert.True(int.Parse(credited.Trim()) > 0, "the server credited the anvil finish");

            var quest = Client.Registry.Get("qltest:station-smith");
            await Until(() => Client.TasksOf(quest)[0] == int.Parse(credited.Trim()), 200, "the progress to reach the client");
        }
    }
}
