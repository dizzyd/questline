using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Model;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The quest engine against the qltest fixture chapters: loading and validation, each task
    /// type through the real game events, claiming, and the client's copy of the state.
    /// Run with --mods tests/fixtures/Mods.
    /// </summary>
    [RequiresClient]
    public class EngineTests
    {
        const string GatherFlint = "qltest:gather-flint";
        const string DigSoil = "qltest:dig-soil";
        const string LayCobble = "qltest:lay-cobble";
        const string Hunt = "qltest:hunt";
        const string ReadTheBook = "qltest:read-the-book";
        const string TradeSticks = "qltest:trade-sticks";
        const string TwoOfThree = "qltest:two-of-three";
        const string Secret = "qltest:secret";

        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static QuestClient Client => Capi.ModLoader.GetModSystem<QuestlineModSystem>().Client;
        static IServerPlayer Me => (IServerPlayer)Player.Me;
        static QuestState StateOf(string id) => Engine.StateOf(Me.PlayerUID, id);

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get(GatherFlint) == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            foreach (var slot in Carried())
            {
                if (slot.Itemstack == null) continue;
                slot.Itemstack = null;
                slot.MarkDirty();
            }
            Engine.Reset(Me);
            await Ticks(2);
        }

        [VsTest]
        public void LoadsChaptersAndDisablesBrokenQuests()
        {
            var registry = Engine.Registry;
            foreach (var problem in Engine.Problems) Log(problem);

            Assert.Equal(8, registry.ChaptersById["qltest:basics"].Quests.Count);
            Assert.NotNull(registry.Get("qltest:fine"), "a valid quest beside broken ones");
            Assert.False(registry.ChaptersById.ContainsKey("qltest:gated"), "chapter gated on a missing mod");

            foreach (var id in new[] { "unknown-prereq", "unknown-task", "unknown-item", "cycle-a", "cycle-b" })
            {
                Assert.True(registry.Get("qltest:" + id) == null, $"{id} should be disabled");
                Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:" + id + ":")), $"{id} should be reported");
            }

            // A missing page image is only a warning: the quest still loads.
            Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:fine:") && p.Contains("missing.png")), "missing page image reported");

            // Loads, but is reported as never able to unlock.
            Assert.NotNull(registry.Get("qltest:needs-missing-mod"));
            Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:needs-missing-mod:") && p.Contains("never unlock")));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ObtainTaskFollowsWhatThePlayerCarries()
        {
            Assert.Equal(QuestState.Available, StateOf(GatherFlint));

            await Player.Hold("game:flint", 2);
            await Until(() => StateOf(GatherFlint) == QuestState.InProgress, 60, "two flint counted");
            Assert.Equal(2, Engine.ProgressOf(Me.PlayerUID).Find(GatherFlint).Tasks[0]);

            await Player.Hold("game:flint", 3);
            await Until(() => StateOf(GatherFlint) == QuestState.Claimable, 60, "three flint completes the quest");

            // Dropping the flint afterwards doesn't undo it.
            await Player.Hold("game:stick", 1);
            await Ticks(30);
            Assert.Equal(QuestState.Claimable, StateOf(GatherFlint));

            await Until(() => Client.StateOf(GatherFlint) == QuestState.Claimable, 60, "the client to hear about it");
        }

        [VsTest]
        public void ClaimingGivesTheRewardOnce()
        {
            Engine.ForceComplete(Me, GatherFlint);
            Assert.Equal(QuestState.Claimable, StateOf(GatherFlint));

            Assert.Equal(null, Engine.Claim(Me, GatherFlint, null));
            Assert.Equal(QuestState.Done, StateOf(GatherFlint));
            Assert.Equal(4, CountCarried("game:stick"));

            Assert.NotNull(Engine.Claim(Me, GatherFlint, null), "a second claim");
            Assert.Equal(4, CountCarried("game:stick"));
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task LockedQuestsDontCountProgress()
        {
            // Free-standing blocks a few apart, so each aim has a clear line.
            BlockPos[] soil = [P(2, 1, 4), P(6, 1, 4), P(10, 1, 4)];
            foreach (var pos in soil) World.SetBlock("game:soil-medium-normal", pos);

            Assert.Equal(QuestState.Locked, StateOf(DigSoil));
            await Break(soil[0]);
            await Ticks(5);
            Assert.True(Engine.ProgressOf(Me.PlayerUID).Find(DigSoil) == null, "a locked quest took progress");

            Engine.ForceComplete(Me, GatherFlint);
            Assert.Equal(QuestState.Available, StateOf(DigSoil));
            await Break(soil[1]);
            await Break(soil[2]);
            // No rewards, so it goes straight to done.
            await Until(() => StateOf(DigSoil) == QuestState.Done, 60, "two soil broken");
        }

        static async Task Break(BlockPos pos)
        {
            await Player.StandNear(pos, 2.5);
            await Interact.BreakBlock(pos);
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task PlacingABlockCounts()
        {
            Engine.ForceComplete(Me, GatherFlint);
            await Player.StandNear(P(3, 1, 3));
            await Player.Hold("game:cobblestone-granite", 4);

            await Interact.UseBlock(P(3, 0, 3), BlockFacing.UP);
            await Until(() => StateOf(LayCobble) == QuestState.Done, 60, "a cobblestone placed");
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task KillingCreditsThePlayerWhoDidIt()
        {
            Engine.ForceComplete(Me, DigSoil);
            Assert.Equal(QuestState.Available, StateOf(Hunt), "hunt needs any of dig-soil or lay-cobble");

            var chicken = World.SpawnEntity("game:chicken-hen", P(6, 1, 6));
            await Ticks(2);
            chicken.ReceiveDamage(new DamageSource
            {
                Source = EnumDamageSource.Player,
                SourceEntity = Me.Entity,
                CauseEntity = Me.Entity,
                Type = EnumDamageType.BluntAttack,
            }, 1000);

            await Until(() => StateOf(Hunt) == QuestState.Done, 60, "the chicken's death credited");
        }

        [VsTest]
        public void AtLeastNeedsTwoOfThree()
        {
            Engine.ForceComplete(Me, DigSoil);
            Assert.Equal(QuestState.Locked, StateOf(TwoOfThree));
            Engine.ForceComplete(Me, LayCobble);
            Assert.Equal(QuestState.Available, StateOf(TwoOfThree));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task CheckboxTicksFromTheClient()
        {
            await OnClient();
            Client.Check(ReadTheBook, 0);
            await OnServer();
            await Until(() => StateOf(ReadTheBook) == QuestState.Done, 60, "the server to accept the tick");

            // A checkbox request for a task that isn't a checkbox is ignored.
            Engine.ForceComplete(Me, GatherFlint);
            Engine.Check(Me, DigSoil, 0);
            Assert.Equal(QuestState.Available, StateOf(DigSoil));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ChoiceRewardNeedsAPickAndTakesTheItems()
        {
            await Player.Hold("game:stick", 2);
            await Until(() => StateOf(TradeSticks) == QuestState.Claimable, 60, "two sticks carried");

            Assert.NotNull(Engine.Claim(Me, TradeSticks, null), "a claim with no pick");
            Assert.Equal(QuestState.Claimable, StateOf(TradeSticks));

            Assert.Equal(null, Engine.Claim(Me, TradeSticks, [1]));
            Assert.Equal(QuestState.Done, StateOf(TradeSticks));
            Assert.Equal(0, CountCarried("game:stick"));
            Assert.Equal(5, CountCarried("game:clay-blue"));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ClientHidesSecretAndLockedQuests()
        {
            await Until(() => Client.Ready, 60, "definitions on the client");
            var registry = Client.Registry;

            Assert.False(Client.IsVisible(registry.Get(Secret)), "secret before completion");
            Assert.False(Client.IsVisible(registry.Get(TwoOfThree)), "whenUnlocked while locked");
            Assert.True(Client.IsVisible(registry.Get(DigSoil)), "an ordinary locked quest");

            Engine.Check(Me, Secret, 0);
            Engine.ForceComplete(Me, DigSoil);
            Engine.ForceComplete(Me, LayCobble);
            await Until(() => Client.StateOf(Secret) == QuestState.Done && Client.StateOf(TwoOfThree) == QuestState.Available, 60, "states on the client");

            Assert.True(Client.IsVisible(registry.Get(Secret)), "secret once done");
            Assert.True(Client.IsVisible(registry.Get(TwoOfThree)), "whenUnlocked once unlocked");
        }

        [VsTest]
        public void ProgressSurvivesASaveAndLoad()
        {
            Engine.ForceComplete(Me, GatherFlint);
            Engine.SaveProgress();

            Engine.Reset(Me);
            Assert.Equal(QuestState.Available, StateOf(GatherFlint));

            Engine.LoadProgress();
            Assert.Equal(QuestState.Claimable, StateOf(GatherFlint));
            Assert.Equal(3, Engine.ProgressOf(Me.PlayerUID).Find(GatherFlint).Tasks[0]);
        }

        static ItemSlot[] Carried() => new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName }
            .Select(name => Me.InventoryManager.GetOwnInventory(name))
            .Where(inv => inv != null)
            .SelectMany(inv => inv)
            .ToArray();

        static int CountCarried(string code) => Carried()
            .Where(s => s.Itemstack?.Collectible.Code.ToString() == code)
            .Sum(s => s.StackSize);
    }
}
