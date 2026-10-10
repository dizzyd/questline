using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The phase 1 rendering spike's two gates: a 500-node tree pans and zooms smoothly, and a
    /// vanilla handbook page renders inside the book without patches.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class BookSpikeTests
    {
        static GuiDialogQuestBook Book => Capi.ModLoader.GetModSystem<QuestlineModSystem>().Book;

        [VsTest(TimeoutMs = 30000)]
        public async Task HotkeyOpensTheBook()
        {
            await OnClient();
            Assert.True(await Input.Hotkey("questline"), "hotkey not handled");
            var book = await Gui.WaitFor<GuiDialogQuestBook>();

            await Frames.Wait(10);
            Log($"chapter {book.SelectedChapter}, {book.Canvas?.VisibleNodes ?? 0} nodes in view");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task FiveHundredNodeTreePansSmoothly()
        {
            await OnClient();
            Book.ShowBenchmarkTree();
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(60);
            Log(await Shot.Take("questline-canvas-default.png"));
            Book.Canvas.ZoomAt(0, 0, 0.01f);   // clamps to fully zoomed out
            await Frames.Wait(30);
            Log($"{Book.Canvas.VisibleNodes} nodes in view at zoom {Book.Canvas.Zoom:0.00}");
            Log(await Shot.Take("questline-canvas-zoomed-out.png"));

            CanvasFrameStats result = null;
            Book.StartTour(15, stats => result = stats);
            await Until(() => result != null, 2000, "the canvas tour");

            Log($"tour: {result}");
            Assert.Greater(result.Count, 100);
            // 30 fps is the floor for "smooth"; the canvas's own share should be a small slice of it.
            Assert.Less(result.P95FrameMs, 33.4f);
            Assert.Less(result.AvgCanvasMs, 4f);
            await OnClient();
            Book.LeaveBenchmarkTree();
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task ReferenceTabHostsAHandbookPage()
        {
            await OnClient();
            await Until(() => ObjectCacheUtil.TryGet<ItemStack[]>(Capi, "handbookallstacks") != null, 600, "the handbook's stack list");

            var anvil = new ItemStack(Capi.World.GetBlock(new AssetLocation("anvil-copper")));
            var ingot = new ItemStack(Capi.World.GetItem(new AssetLocation("ingot-copper")));

            Assert.True(Book.ShowReference(anvil));
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(30);

            var text = Book.SingleComposer.GetRichtext("richtext");
            Assert.NotNull(text);
            Log($"anvil page: {text.Components.Length} components, {text.Bounds.fixedHeight:0} px tall");
            Assert.Greater(text.Components.Length, 3);
            Log(await Shot.Take("questline-reference-anvil.png"));

            // Navigation stays inside the book, and Back returns to the page before.
            Assert.True(Book.ShowReference(ingot));
            await Frames.Wait(10);
            Assert.Contains(Book.PageCode, "ingot-copper");
            Assert.True(Book.GoBack());
            Assert.Contains(Book.PageCode, "anvil-copper");
        }
    }
}
