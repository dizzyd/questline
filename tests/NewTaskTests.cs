using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using questline;
using questline.Model;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The tasks the vanilla questline needed beyond the first ones: liquids carried in a bucket,
    /// riding something, buying from a trader. The qltest fixture's vehicles chapter.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class NewTaskTests
    {
        const string CarryMilk = "qltest:carry-milk";
        const string RideRaft = "qltest:ride-raft";
        const string BuySomething = "qltest:buy-something";

        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static IServerPlayer Me => (IServerPlayer)Player.Me;
        static QuestState StateOf(string id) => Engine.StateOf(Me.PlayerUID, id);

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get(CarryMilk) == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
            {
                foreach (var slot in Me.InventoryManager.GetOwnInventory(name))
                {
                    if (slot.Itemstack == null) continue;
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            Engine.Reset(Me);
            await Ticks(2);
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task MilkInABucketCountsByTheLitre()
        {
            var bucket = (BlockLiquidContainerBase)World.Stack("game:woodbucket").Collectible;
            var milk = World.Stack("game:milkportion");
            float perLitre = BlockLiquidContainerBase.GetContainableProps(milk).ItemsPerLitre;

            var stack = new ItemStack(bucket);
            bucket.SetContent(stack, new ItemStack(milk.Collectible, (int)(perLitre * 1)));
            var slot = Me.InventoryManager.GetHotbarInventory()[0];
            slot.Itemstack = stack;
            slot.MarkDirty();
            await Until(() => StateOf(CarryMilk) == QuestState.InProgress, 60, "a litre of milk counted");
            Assert.Equal(1, Engine.ProgressOf(Me.PlayerUID).Find(CarryMilk).Tasks[0]);

            bucket.SetContent(stack, new ItemStack(milk.Collectible, (int)(perLitre * 3)));
            slot.MarkDirty();
            await Until(() => StateOf(CarryMilk) == QuestState.Done, 60, "two litres complete it");
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task GettingOnARaftCounts()
        {
            await Player.StandNear(P(4, 1, 4));
            var raft = World.SpawnEntity("game:boat-raft-oak", P(4, 1, 4));
            await Ticks(5);
            var seat = raft.GetBehavior<EntityBehaviorSeatable>()?.Seats.FirstOrDefault();
            Assert.NotNull(seat, "the raft has a seat");

            Assert.True(Me.Entity.TryMount(seat), "mounted the raft");
            await Until(() => StateOf(RideRaft) == QuestState.Done, 60, "riding the raft credited");
            Me.Entity.TryUnmount();
            raft.Die(EnumDespawnReason.Removed);
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task BuyingFromATraderCounts()
        {
            var trader = (EntityTradingHumanoid)World.SpawnEntity("game:trader-male-commodities-temperate", P(6, 1, 6));
            await Until(() => trader.Inventory?.SellingSlots.Any(s => s.TradeItem?.Stock > 0) == true, 100, "the trader to stock up");

            var inventory = trader.Inventory;
            int index = System.Array.FindIndex(inventory.SellingSlots, s => s.TradeItem?.Stock > 0);
            Log($"buying {inventory.SellingSlots[index].Itemstack?.Collectible.Code} for {inventory.SellingSlots[index].TradeItem.Price}");

            // Enough rusty gears for anything a trader sells.
            await Player.Hold("game:gear-rusty", 64);

            trader.WatchedAttributes.SetString("tradingPlayerUID", Me.PlayerUID);
            var op = new ItemStackMoveOperation(Sapi.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge) { ActingPlayer = Me };
            inventory.ActivateSlot(index, new DummySlot(), ref op);
            Assert.NotNull(inventory.GetBuyingCartSlot(0).Itemstack, "the item is in the cart");

            var result = typeof(InventoryTrader).GetMethod("TryBuySell", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(inventory, [Me]);
            Log($"sale: {result}");
            Assert.Equal("Success", result.ToString());
            await Until(() => StateOf(BuySomething) == QuestState.Done, 60, "the purchase credited");

            trader.WatchedAttributes.RemoveAttribute("tradingPlayerUID");
            trader.Die(EnumDespawnReason.Removed);
        }
    }
}
