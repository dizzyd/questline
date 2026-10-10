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
