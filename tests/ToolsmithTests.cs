using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using questline;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Toolsmith puts tools together outside the crafting grid, in the hands or on its workbench,
    /// and either way the tool counts as crafted. Needs the real mod: run tests/fixtures/fetch.sh
    /// and pass --mods tests/fixtures/Toolsmith as well as the fixture Mods.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class ToolsmithTests
    {
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static IServerPlayer Me => (IServerPlayer)Player.Me;

        // Plain reflection, as in StationTests: Toolsmith is optional, and the tests mustn't name it.
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static Type ToolsmithType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        static readonly Type Utility = ToolsmithType("Toolsmith.ToolTinkering.TinkeringUtility");

        const string Head = "game:axehead-copper", Handle = "toolsmith:handle", Bundle = "toolsmith:tinkertoolparts";

        static int Crafted() => Engine.ProgressOf(Me.PlayerUID).Find("qltest:station-craft")?.Tasks[0] ?? 0;

        // Every craft credited, whatever its code: the station quest counts only game:*, and a
        // wrongly credited bundle would be toolsmith:.
        static int CraftsRecorded() => Engine.ProgressOf(Me.PlayerUID).History.Where(h => h.Key.StartsWith("craft|")).Sum(h => h.Value);

        // The runner makes an instance per test and runs AfterEach even when BeforeEach skips,
        // so the hands are given back only if they were taken.
        ItemStack held, offheld;
        bool handsSaved;

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (!Sapi.ModLoader.IsModEnabled("toolsmith")) Skip("Toolsmith isn't loaded; run tests/fixtures/fetch.sh and pass --mods tests/fixtures/Toolsmith");
            if (Engine.Registry.Get("qltest:station-craft") == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Engine.Reset(Me);
            (held, offheld) = (Me.InventoryManager.ActiveHotbarSlot.Itemstack, Me.Entity.LeftHandItemSlot.Itemstack);
            handsSaved = true;
        }

        [AfterEach]
        public async Task GiveBackTheHands()
        {
            if (!handsSaved) return;
            await OnServer();
            (Me.InventoryManager.ActiveHotbarSlot.Itemstack, Me.Entity.LeftHandItemSlot.Itemstack) = (held, offheld);
        }

        /// <summary>Head in the hand and handle in the other make a bundle, which is no tool yet; the hand holds it after.</summary>
        static ItemSlot BundleInHand()
        {
            var hand = Me.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = World.Stack(Head);
            Me.Entity.LeftHandItemSlot.Itemstack = World.Stack(Handle);
            Utility.GetMethod("AssemblePartBundle", Any).Invoke(null, [hand, Me.Entity, null]);
            Assert.Equal(Bundle, hand.Itemstack?.Collectible.Code.ToString(), "a bundle in the hand");
            Me.Entity.LeftHandItemSlot.Itemstack = null;
            return hand;
        }

        static void AssembleFullTool(ItemSlot hand) => Utility.GetMethod("AssembleFullTool", Any).Invoke(null, [hand, Me.Entity, null]);

        [VsTest]
        public void AToolPutTogetherInTheHandsCountsAsCrafted()
        {
            var hand = BundleInHand();
            Assert.Equal(0, Crafted(), "a bundle isn't a craft");

            AssembleFullTool(hand);
            Assert.Equal("game:axe-felling-copper", hand.Itemstack?.Collectible.Code.ToString(), "the axe in the hand");
            Assert.Equal(1, Crafted());
        }

        [VsTest]
        public void ABundleWithNoToolToMakeCountsNothing()
        {
            var hand = BundleInHand();

            // Toolsmith maps each head to the tool it makes; without the head's entry it leaves the bundle be.
            var recipes = (IDictionary)ToolsmithType("Toolsmith.RecipeRegisterModSystem").GetField("TinkerToolGridRecipes", Any).GetValue(null);
            var tool = recipes[Head];
            recipes.Remove(Head);
            try { AssembleFullTool(hand); }
            finally { recipes[Head] = tool; }

            Assert.Equal(Bundle, hand.Itemstack?.Collectible.Code.ToString(), "the bundle still in the hand");
            Assert.Equal(0, CraftsRecorded());
        }

        [VsTest]
        public void AToolMadeOnTheWorkbenchCountsForWhoeverStruckIt()
        {
            var pos = P(3, 1, 12);
            World.SetBlock("toolsmith:workbench-north", pos);
            var bench = Sapi.World.BlockAccessor.GetBlockEntity(pos);
            var inventory = bench.GetType().GetProperty("Inventory", Any).GetValue(bench);
            ItemSlot Slot(int id) => (ItemSlot)inventory.GetType().GetMethod("GetSlotFromSelectionID", Any).Invoke(inventory, [id]);
            Slot(1).Itemstack = World.Stack(Head);
            Slot(2).Itemstack = World.Stack(Handle);

            // The bench reads the striking hand without a null check: strike it with a hammer, as a player would.
            Me.InventoryManager.ActiveHotbarSlot.Itemstack = World.Stack("game:hammer-copper");
            // It crafts on the fourth strike (three counted, then the craft); ten is room to spare,
            // stopping once the head is used.
            var strike = bench.GetType().GetMethod("AttemptToCraft", Any);
            for (int i = 0; i < 10 && !Slot(1).Empty; i++) strike.Invoke(bench, [Sapi.World, Me, null]);

            Assert.True(Slot(1).Empty, "the bench used the head");
            Assert.Equal(1, Crafted());
        }
    }
}
