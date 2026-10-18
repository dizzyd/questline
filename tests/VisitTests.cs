using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Model;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Visit tasks: a player inside a structure worldgen placed has visited it. The game records
    /// each placed structure in its map region; the tests add records of their own around the
    /// plot, as worldgen would, and take them out again. The qltest fixture's places chapter.
    /// </summary>
    [SingleplayerOnly]
    public class VisitTests
    {
        const string FindATrader = "qltest:find-a-trader";
        const string TwoRuins = "qltest:two-ruins";

        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static IServerPlayer Me => (IServerPlayer)Player.Me;
        static QuestState StateOf(string id) => Engine.StateOf(Me.PlayerUID, id);

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get(FindATrader) == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Engine.Reset(Me);
            await Ticks(2);
        }

        /// <summary>
        /// Records a structure where worldgen would: in the map region of its corner, with the code
        /// the game makes up for it. Returns a way to take it out again.
        /// </summary>
        static System.Action Place(string code, string group, BlockPos from, int size)
        {
            var placed = new GeneratedStructure
            {
                Code = code,
                Group = group,
                Location = new Cuboidi(from.X, from.Y, from.Z, from.X + size, from.Y + size, from.Z + size),
            };
            int regionSize = Sapi.WorldManager.RegionSize;
            var region = Sapi.WorldManager.GetMapRegion(from.X / regionSize, from.Z / regionSize);
            Assert.NotNull(region, "the plot's map region is loaded");
            region.AddGeneratedStructure(placed);
            return () => region.GeneratedStructures.Remove(placed);
        }

        [VsTest]
        public void StructuresAreNamedByTheirWorldgenConfig()
        {
            var codes = Engine.Structures;
            Assert.True(codes.All.Contains("game:undergroundruin"), "a ruin, from worldgen/structures.json");
            Assert.True(codes.All.Contains("game:tobiascave"), "a story location, from worldgen/storystructures.json");
            Assert.True(codes.All.Contains("game:refinerynew"), "a tiled dungeon, from worldgen/tileddungeons.json");

            // The codes the game records them under, as GenStructures, GenStoryStructures and GenDungeons make them.
            string Of(string code, string group = null) => string.Join(",", codes.Of(new GeneratedStructure { Code = code, Group = group }));
            Assert.Equal("game:undergroundruin", Of("game:underground/ruin-3/undergroundruin", "ruins"));
            Assert.Equal("game:tobiascave", Of("tobiascave:story/tobiascave"));
            Assert.Equal("game:refinerynew", Of("game:refinerynew/simple/shaft/bottom1", "refinerynew"));
            Assert.Equal("", Of("game:nowhere/nothing"));
        }

        [VsTest]
        public void AVisitNeedsAPlaceThatExistsAndAName()
        {
            Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:unknown-structure:") && p.Contains("matches nothing")), "an unknown structure reported");
            Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:unnamed-structure:") && p.Contains("no lang entry game:structure-gates")), "a structure with no name reported");
            Assert.True(Engine.Problems.All(p => !p.StartsWith("qltest:find-a-trader:") && !p.StartsWith("qltest:two-ruins:")), "the places chapter loads cleanly");
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task StandingInsideAStructureVisitsIt()
        {
            var remove = Place("game:trader/plains/trader-plains", "traders", P(10, 0, 10), 6);
            try
            {
                await Player.Teleport(P(2, 1, 2));
                await Ticks(60);
                Assert.Equal(QuestState.Available, StateOf(FindATrader), "standing outside counts for nothing");

                await Player.Teleport(P(12, 2, 12));
                await Until(() => StateOf(FindATrader) == QuestState.Done, 150, "the trader's camp visited");
                Assert.True(Engine.HasDiscovered(Me.PlayerUID, "game:trader-*"), "a discovered gate sees the visit");
            }
            finally
            {
                remove();
            }
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task EachStructureCountsOnce()
        {
            var first = Place("game:underground/ruin-1/undergroundruin", "ruins", P(10, 0, 10), 6);
            var second = Place("game:underground/ruin-2/undergroundruin", "ruins", P(20, 0, 10), 6);
            try
            {
                int Visits() => Engine.ProgressOf(Me.PlayerUID).Find(TwoRuins)?.Tasks[0] ?? 0;

                await Player.Teleport(P(12, 2, 12));
                await Until(() => Visits() == 1, 150, "the first ruin visited");
                await Player.Teleport(P(2, 1, 2));
                await Ticks(30);
                await Player.Teleport(P(12, 2, 12));
                await Ticks(60);
                Assert.Equal(1, Visits(), "going back in doesn't count again");

                await Player.Teleport(P(22, 2, 12));
                await Until(() => StateOf(TwoRuins) == QuestState.Done, 150, "the second ruin completes it");
            }
            finally
            {
                first();
                second();
            }
        }

        /// <summary>A quest that opens later is credited with places already visited.</summary>
        [VsTest(TimeoutMs = 30000)]
        public void VisitsAreKeptInTheHistory()
        {
            var placed = new GeneratedStructure { Code = "game:underground/ruin-1/undergroundruin", Location = new Cuboidi(0, 0, 0, 1, 1, 1) };
            Engine.Visit(Me.PlayerUID, placed);
            Engine.Visit(Me.PlayerUID, placed);
            Assert.Equal(1, Engine.ProgressOf(Me.PlayerUID).History.GetValueOrDefault(PlayerProgress.HistoryKey("visit", "game:undergroundruin")), "once, however long the player stays");
        }
    }
}
