using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Graph;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The phase 4 gate: the recipe graph covers every vanilla recipe. Each recipe registry is
    /// counted against the graph's edges of that kind, and each item property the handbook reads
    /// (smelting, grinding, crushing, transitions) is checked against an edge from that item.
    /// Runs headless too: the server has every recipe.
    /// </summary>
    [SingleplayerOnly]
    public class GraphTests
    {
        static RecipeGraph Graph => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine.Graph;

        static int Edges(string kind) => Graph.Edges.Count(e => e.Kind == kind);

        [VsTest(TimeoutMs = 120000)]
        public async Task GateTheGraphCoversEveryVanillaRecipe()
        {
            await OnServer();
            var graph = Graph;
            Log($"{graph.Edges.Count} edges in {graph.BuildMs:0} ms");
            foreach (var (kind, count) in graph.CountByKind()) Log($"  {kind}: {count}");

            Assert.Equal(RecipeGraph.Countable(Sapi.World.GridRecipes).Count(), Edges("grid"), "every grid recipe");
            Assert.Equal(RecipeGraph.Countable(Sapi.GetSmithingRecipes()).Count(), Edges("smithing"), "every smithing recipe");
            Assert.Equal(RecipeGraph.Countable(Sapi.GetKnappingRecipes()).Count(), Edges("knapping"), "every knapping recipe");
            Assert.Equal(RecipeGraph.Countable(Sapi.GetClayformingRecipes()).Count(), Edges("clayforming"), "every clay forming recipe");
            Assert.Equal(RecipeGraph.Countable(Sapi.GetBarrelRecipes()).Count(), Edges("barrel"), "every barrel recipe");
            Assert.Equal(Sapi.GetCookingRecipes().Count(r => r.Enabled && r.Ingredients != null), Edges("cooking"), "every cooking pot recipe");
            Assert.Equal(Sapi.GetMetalAlloys().Count(a => a.Enabled && a.Output?.ResolvedItemstack != null), Edges("alloy"), "every alloy");
            Assert.Greater(Edges("grid"), 1000, "vanilla has thousands of grid recipes");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task EveryItemPropertyHasItsEdge()
        {
            await OnServer();
            var graph = Graph;
            var world = Sapi.World;
            int checkedCount = 0;
            foreach (var c in world.Collectibles.Where(c => c?.Code != null))
            {
                var stack = new ItemStack(c);
                string code = c.Code.ToString();

                if (c.GetCombustibleProperties(world, stack, null)?.SmeltedStack?.ResolvedItemstack is { } smelted)
                {
                    Has(graph, code, smelted, "heat");
                    checkedCount++;
                }
                if (c.GetGrindingProperties(world, stack)?.GroundStack?.ResolvedItemstack is { } ground) { Has(graph, code, ground, "grind"); checkedCount++; }
                if (c.GetCrushingProperties(world, stack)?.CrushedStack?.ResolvedItemstack is { } crushed) { Has(graph, code, crushed, "crush"); checkedCount++; }
                foreach (var t in c.GetTransitionableProperties(world, stack, null) ?? [])
                {
                    if (t?.TransitionedStack?.ResolvedItemstack is { } into) { Has(graph, code, into, "transition"); checkedCount++; }
                }
            }
            Log($"{checkedCount} item properties each have their edge");
            Assert.Greater(checkedCount, 500);
        }

        static void Has(RecipeGraph graph, string from, ItemStack to, string kind)
        {
            string target = to.Collectible.Code.ToString();
            Assert.True(graph.MadeBy(target).Any(e => e.Kind.StartsWith(kind) && e.Inputs.Any(i => i.Codes.Contains(from))), $"{from} → {target} by {kind}");
        }

        /// <summary>The qltest "checks" chapter breaks each graph rule once, and keeps one spoiler behind a discovered gate.</summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task GraphChecksFindWhatTheChaptersGetWrong()
        {
            await OnServer();
            var engine = Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
            await Until(() => engine.GraphChecksDone, 1200, "the graph checks, built in the background");
            var problems = engine.Problems.Where(p => p.StartsWith("qltest:check-")).ToList();
            foreach (var p in problems) Log(p);

            Assert.True(problems.Any(p => p.StartsWith("qltest:check-wrong-method:") && p.Contains("nothing makes that way")), "a smith task for flint");
            Assert.True(problems.Any(p => p.StartsWith("qltest:check-early-gizmo:") && p.Contains("gizmo-part") && p.Contains("later")), "a gizmo asked for before its part");
            Assert.True(problems.Any(p => p.StartsWith("qltest:check-spoiler:") && p.Contains("story item")), "a story item in the default questline");
            Assert.False(problems.Any(p => p.StartsWith("qltest:check-spoiler-found-first:")), "a story item behind a discovered gate is fine");
            Assert.False(problems.Any(p => p.StartsWith("qltest:check-late-part:")), "the part itself is fine");
            Assert.False(problems.Any(p => p.StartsWith("qltest:check-unchecked:")), "an unchecked task is the author's word");
        }

        /// <summary>
        /// The graph is built a few milliseconds a tick, so a large pack never stalls the server for
        /// it. What the build controls is how finely its work divides: nearly every step must be
        /// tiny. (Wall-clock spikes from the garbage collector or a busy machine hit any code.)
        /// </summary>
        [VsTest(TimeoutMs = 120000)]
        public async Task TheGraphIsBuiltInSmallSteps()
        {
            await OnServer();
            var builder = new RecipeGraphBuilder(Sapi);
            var steps = new System.Collections.Generic.List<double>();
            var watch = new System.Diagnostics.Stopwatch();
            using (var e = builder.Steps().GetEnumerator())
            {
                while (true)
                {
                    watch.Restart();
                    bool more = e.MoveNext();
                    steps.Add(watch.Elapsed.TotalMilliseconds);
                    if (!more) break;
                }
            }
            steps.Sort();
            double p99 = steps[(int)(steps.Count * 0.99)];
            Log($"{steps.Count} steps, {steps.Sum():0} ms in all: 99% under {p99:0.00} ms, the longest {steps[^1]:0.0} ms");
            Assert.Greater(steps.Count, 100, "the work comes in many steps");
            Assert.True(p99 < 2, $"steps stay small (99% under {p99:0.00} ms)");

            // Driven a tick's budget at a time, it makes the same graph as built at once.
            var build = new GraphBuild(Sapi);
            int slices = 1;
            while (!build.Advance(4)) slices++;
            Log($"{slices} slices of 4 ms");
            Assert.Equal(Graph.Edges.Count, build.Result.Edges.Count, "the same graph as built at once");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task KnownChainsAreThere()
        {
            await OnServer();
            var graph = Graph;
            var flint = Sapi.World.GetItem(new AssetLocation("game:flint"));
            Assert.True(graph.UsedIn(flint).Any(e => e.Kind == "knapping"), "flint is knapped");

            Assert.True(graph.MadeBy("game:ingot-copper").Any(e => e.Kind == "heat-smelt"), "copper ingots are smelted");
            Assert.True(graph.MadeBy("game:ingot-tinbronze").Any(e => e.Kind == "alloy" || e.Kind == "heat-smelt"), "tin bronze is an alloy");
            // A wildcard ingredient: recipes take "any plank", and an oak plank satisfies them.
            var plank = Sapi.World.GetBlock(new AssetLocation("game:planks-oak-hor"));
            if (plank != null) Assert.Greater(graph.UsedIn(plank).Count(), 0, "planks are used in something");

            // Butchering: a chicken gives meat.
            Assert.True(graph.Edges.Any(e => e.Kind == "butcher" && e.Source.StartsWith("game:chicken")), "chickens are butchered");
            Log(string.Join("\n", graph.MadeBy("game:ingot-copper").Take(5).Select(e => $"{e.Kind}: {string.Join(" + ", e.Inputs.Select(i => string.Join("|", i.Codes)))}")));
        }
    }
}
