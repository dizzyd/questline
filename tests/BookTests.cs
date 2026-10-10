using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Model;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The quest book against the qltest fixture: the chapter rail, the tree, picking a quest by
    /// clicking it, and claiming through the button. Run with --mods tests/fixtures/Mods.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class BookTests
    {
        const string GatherFlint = "qltest:gather-flint";

        static QuestlineModSystem ClientSystem => Capi.ModLoader.GetModSystem<QuestlineModSystem>();
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static GuiDialogQuestBook Book => ClientSystem.Book;
        static IServerPlayer Me => (IServerPlayer)Player.Me;

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get(GatherFlint) == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Engine.Reset(Me);
            await OnClient();
            await Until(() => ClientSystem.Client.StateOf(GatherFlint) == QuestState.Available, 60, "the reset to reach the client");
            // The book reopens where it was left; start every test on the first chapter, closed.
            Book.SelectChapter("qltest:basics");
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task OpensOnTheFirstChapterWithItsVisibleQuests()
        {
            await OnClient();
            Assert.True(await Input.Hotkey("questline"), "hotkey not handled");
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            Assert.Equal("qltest:basics", Book.SelectedChapter);
            // Eight quests, less the secret one and the one hidden until unlocked.
            Assert.Equal(6, Book.Canvas.VisibleNodes);
            Log(await Shot.Take("questline-book-chapter.png"));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ClickingAQuestShowsItAndClaimingGivesTheReward()
        {
            // The book composes textures, so it is only ever touched from the client thread.
            await OnClient();
            Book.TryOpen();
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);

            var (x, y) = Book.Canvas.ScreenPosOf(0);
            await ClickAt(x, y);
            await Until(() => Book.SelectedQuest == GatherFlint, 60, "the click to select gather-flint");
            Assert.True(Book.SingleComposer.GetButton("claim") == null, "no claim button before the tasks are done");

            await OnServer();
            Engine.ForceComplete(Me, GatherFlint);
            await OnClient();
            await Until(() => Book.SingleComposer.GetButton("claim") != null, 60, "the claim button");
            await Frames.Wait(20);
            Log(await Shot.Take("questline-book-claimable.png"));

            var button = Book.SingleComposer.GetButton("claim");
            await ClickAt((int)(button.Bounds.absX + button.Bounds.OuterWidth / 2), (int)(button.Bounds.absY + button.Bounds.OuterHeight / 2));

            await OnServer();
            await Until(() => Engine.StateOf(Me.PlayerUID, GatherFlint) == QuestState.Done, 60, "the claim to reach the server");
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task QuestPageShowsTextImagesItemsAndARecipe()
        {
            await OnClient();
            Book.SelectQuest("qltest:read-the-book");
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            var comps = Book.SingleComposer.GetRichtext("detail").Components;
            var image = comps.OfType<ImageTextComponent>().SingleOrDefault();
            Assert.NotNull(image, "an image component");
            Assert.True(image.Loaded, "the image texture loaded");
            Assert.Greater(image.BoundsPerLine[0].Height, 10.0);
            Assert.Equal(1, comps.OfType<SlideshowGridRecipeTextComponent>().Count());
            Assert.Greater(comps.OfType<ItemstackTextComponent>().Count(), 2);
            Log(await Shot.Take("questline-book-page.png"));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ChapterArtAndShapesDraw()
        {
            await OnServer();
            Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:gallery:") && p.Contains("nowhere.png")), "a missing art image is reported");

            await OnClient();
            Book.SelectChapter("qltest:gallery");
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(30);

            var canvas = Book.Canvas;
            Assert.True(canvas.HasBackground, "the chapter background loaded");
            // An image, a label and two arrows; the image whose file is missing is skipped.
            Assert.Equal(4, canvas.ArtReady);
            Assert.Equal(NodeShape.Gear, canvas.Nodes.Single(n => n.Id == "qltest:shape-gear").Shape);

            // A bigger node is a bigger target: just inside its edge still hits it.
            int gear = canvas.Nodes.ToList().FindIndex(n => n.Id == "qltest:shape-gear");
            var (gx, gy) = canvas.ScreenPosOf(gear);
            float edge = GuiElementQuestCanvas.NodeSize * 1.6f / 2 * canvas.Zoom * Vintagestory.API.Config.RuntimeEnv.GUIScale;
            Assert.Equal(gear, canvas.NodeAt(gx + edge - 2, gy));
            Log(await Shot.Take("questline-gallery.png"));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task PinnedQuestsShowOnTheHudUntilClaimed()
        {
            await OnClient();
            var client = ClientSystem.Client;
            foreach (var id in client.Pins.ToList()) client.Unpin(id);

            Assert.True(client.Pin(GatherFlint), "first pin");
            Assert.True(client.Pin("qltest:dig-soil"), "second pin");
            Assert.True(client.Pin("qltest:read-the-book"), "third pin");
            Assert.False(client.Pin("qltest:lay-cobble"), "a fourth pin is refused");

            await Until(() => ClientSystem.Pins.IsOpened(), 60, "the HUD to open");
            Assert.Equal(3, ClientSystem.Pins.Shown.Count);

            await OnServer();
            await Player.Hold("game:flint", 2);
            await OnClient();
            await Until(() => client.TasksOf(client.Registry.Get(GatherFlint))[0] == 2, 60, "progress on the client");
            await Frames.Wait(10);
            Log(await Shot.Take("questline-pins.png"));

            await OnServer();
            Engine.ForceComplete(Me, GatherFlint);
            Assert.Equal(null, Engine.Claim(Me, GatherFlint, null));
            await OnClient();
            await Until(() => !client.IsPinned(GatherFlint), 60, "a claimed quest to drop off the HUD");
            Assert.Equal(2, ClientSystem.Pins.Shown.Count);

            foreach (var id in client.Pins.ToList()) client.Unpin(id);
            await Until(() => !ClientSystem.Pins.IsOpened(), 60, "the HUD to close with nothing pinned");
            await OnServer();
            await Player.Hold("game:stick", 1);
        }

        /// <summary>
        /// A press and release handed to the book, as the game's GUI system does it. Input.Click
        /// goes in through the world's mouse state and doesn't reach dialog elements; configkit's
        /// suite clicks its dialogs the same way.
        /// </summary>
        static async Task ClickAt(int x, int y)
        {
            await Input.MouseMove(x, y);
            await Frames.Wait(1);
            Book.OnMouseDown(new MouseEvent(x, y, EnumMouseButton.Left, 0));
            await Frames.Wait(2);
            Book.OnMouseUp(new MouseEvent(x, y, EnumMouseButton.Left, 0));
            await Frames.Wait(1);
        }
    }
}
