using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Pictures, not assertions: the book as a player sees it, for the ModDB page. Run with
    /// --filter Gallery by scripts/screenshots.sh, which boots the client at a desktop size and
    /// copies the PNGs back into screenshots/moddb/. Shots land in the game install as
    /// questline-gallery-*.png.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class Gallery
    {
        static QuestlineModSystem ClientSystem => Capi.ModLoader.GetModSystem<QuestlineModSystem>();
        static QuestClient Client => ClientSystem.Client;
        static GuiDialogQuestBook Book => ClientSystem.Book;
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static IServerPlayer Me => (IServerPlayer)Player.Me;

        // Early stone age, done and claimed, so the tree shows every state at once.
        static readonly string[] Done = ["gather-flint", "gather-sticks", "forage", "knap-axe-head", "knap-knife", "make-axe", "make-knife", "chop-wood"];

        [BeforeEach]
        public async Task SomeProgress()
        {
            // The setup's completions would each pop up "Quest complete" over the pictures.
            await OnClient();
            Client.SetToasts(false);
            await OnServer();
            await World.SetCalendarTo(500 * 24 + 12);
            Engine.Reset(Me);
            foreach (string id in Done)
            {
                Engine.ForceComplete(Me, "questline:" + id);
                Engine.Claim(Me, "questline:" + id, null);
            }
            await Player.Hold("game:flint", 3);
            await OnClient();
            foreach (var id in Client.Pins.ToList()) Client.Unpin(id);
            foreach (var plan in Client.Plans.ToList()) Client.RemovePlan(plan);
            await Until(() => Client.StateOf("questline:chop-wood") == questline.Model.QuestState.Done, 120, "the progress on the client");
            Client.SetToasts(true);
        }

        static async Task Snap(string name, bool minimap = false)
        {
            // The mouse off to the side, so no tooltip; the minimap only where the pins hang from it.
            // Opening a dialog warps the cursor to the middle of the screen, and the warp's event
            // can land after a move made straight away; so it's let land first, then moved.
            await Frames.Wait(10);
            await Input.MouseMove(Capi.Render.FrameWidth - 4, Capi.Render.FrameHeight / 2);
            foreach (var map in Capi.Gui.OpenedGuis.OfType<Vintagestory.GameContent.GuiDialogWorldMap>().ToList())
            {
                if (!minimap) map.TryClose();
            }
            if (minimap && !Capi.Gui.OpenedGuis.OfType<Vintagestory.GameContent.GuiDialogWorldMap>().Any())
            {
                Capi.ModLoader.GetModSystem<Vintagestory.GameContent.WorldMapManager>().ToggleMap(EnumDialogType.HUD);
            }
            await Frames.Wait(30);
            // With no dialog open the game holds the mouse in the middle, to look around with.
            if (Book.IsOpened()) Assert.True(Capi.Input.MouseX > Capi.Render.FrameWidth - 10, $"the mouse off to the side, not at {Capi.Input.MouseX},{Capi.Input.MouseY}");
            Log(await Shot.Take($"questline-gallery-{name}.png"));
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task StoneAge()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("questline:stone-age");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("1-stone-age");
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task QuestPage()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("questline:stone-age");
            Book.SelectQuest("questline:make-spear");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("2-quest");
            Book.TryClose();
        }

        /// <summary>
        /// Farming with the quern picked: the late branch's Copper age badges, the quests it needs
        /// from other chapters beside it, and the page's Needs and Start with.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task FindingTheWay()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("questline:farming");
            Book.SelectQuest("questline:make-quern");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("3-finding-the-way");
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task Milestone()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("questline:bronze-age");
            Book.SelectQuest("questline:smith-bronze");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("4-milestone");
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task Reference()
        {
            await OnClient();
            Assert.True(Book.ShowReference(new ItemStack(Capi.World.GetItem(new AssetLocation("game:ingot-copper")))));
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("5-reference");
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task Plan()
        {
            await OnServer();
            Engine.Config.RevealLevel = "guide";
            Engine.Reload();
            await OnClient();
            Client.SetPlannerLevel(RevealLevel.Guide);
            await Until(() => Client.GraphReady && Client.Reveal == RevealLevel.Guide, 1200, "the graph, at guide");
            Book.ShowPlan("game:pickaxe-copper");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("6-plan");
            Book.TryClose();
            await OnServer();
            Engine.Config.RevealLevel = "hints";
            Engine.Reload();
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task Pins()
        {
            await OnClient();
            Book.TryClose();
            Client.Pin("questline:make-spear");
            Client.Pin("questline:cut-grass");
            Client.Pin("questline:make-club");
            await Until(() => ClientSystem.Pins.IsOpened(), 60, "the HUD to open");
            await Snap("7-pins", minimap: true);
            foreach (var id in Client.Pins.ToList()) Client.Unpin(id);
        }

        /// <summary>With Expanded Foods installed, its quests inside the vanilla Kitchen chapter.</summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task ExpandedFoodsKitchen()
        {
            await OnServer();
            if (!Engine.Registry.Quests.ContainsKey("questline:make-mixing-bowl")) { Log("Expanded Foods isn't installed"); return; }
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("questline:kitchen");
            Book.SelectQuest("questline:make-mixing-bowl");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Snap("8-expanded-foods");
            Book.TryClose();
        }
    }
}
