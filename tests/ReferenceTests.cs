using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The Reference tab and the handbook takeover: H and handbook links open the book, one
    /// search finds quests, guides and items, item pages say which quests need them, guides
    /// render, and turning the takeover off gives the vanilla handbook back.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class ReferenceTests
    {
        static QuestlineModSystem ClientSystem => Capi.ModLoader.GetModSystem<QuestlineModSystem>();
        static GuiDialogQuestBook Book => ClientSystem.Book;

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnClient();
            if (ClientSystem.Client.Registry?.Get("qltest:gather-flint") == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Book.SetEditing(false);
            Book.TryClose();
            foreach (var d in Capi.Gui.OpenedGuis.OfType<GuiDialogSurvivalHandbook>().ToList()) d.TryClose();
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task HAndHandbookLinksOpenTheBook()
        {
            await OnClient();
            Assert.True(ClientSystem.Takeover.Active, "the takeover is on by default");

            Assert.True(await Input.Hotkey("handbook"), "H handled");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            Assert.True(Book.OnReferenceTab, "on the Reference tab");
            Assert.False(Capi.Gui.OpenedGuis.OfType<GuiDialogSurvivalHandbook>().Any(), "the vanilla handbook stays shut");

            var flint = new ItemStack(Capi.World.GetItem(new AssetLocation("game:flint")));
            // As a click on a link in chat or another mod's text would: through the registered protocol.
            Capi.LinkProtocols["handbook"](new LinkTextComponent("handbook://" + GuiHandbookItemStackPage.PageCodeForStack(flint)));
            await Frames.Wait(5);
            Assert.Contains(Book.PageCode ?? "", "flint", "a handbook link opens the item in the book");

            Assert.True(await Input.Hotkey("handbook"), "H again");
            await Frames.Wait(5);
            Assert.False(Book.IsOpened(), "H closes the book from the Reference tab");
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task TurningTheTakeoverOffGivesTheHandbookBack()
        {
            await OnClient();
            ClientSystem.Takeover.Disable();
            try
            {
                Assert.True(await Input.Hotkey("handbook"), "H handled");
                await VsTestkit.Testing.Gui.WaitFor<GuiDialogSurvivalHandbook>();
                Assert.False(Book.IsOpened(), "the book stays shut");
                foreach (var d in Capi.Gui.OpenedGuis.OfType<GuiDialogSurvivalHandbook>().ToList()) d.TryClose();
            }
            finally
            {
                ClientSystem.Takeover.Enable();
            }
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task OneSearchFindsQuestsGuidesAndItems()
        {
            await OnClient();
            Book.SearchReference("");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);
            Assert.True(Book.Results.Rows.Count > 0 && Book.Results.Rows.Where(r => !r.Heading).All(r => r.Id.StartsWith("guide:")), "with no search, the guides");
            Assert.True(Book.Results.Rows.Any(r => r.Heading), "grouped under headings");
            Log(await Shot.Take("questline-reference-guides.png"));

            // Typed into the box: the list follows without recomposing, so the box keeps its focus.
            var results = Book.Results;
            Book.SingleComposer.GetTextInput("ref-search").SetValue("flint");
            await Frames.Wait(5);
            Assert.True(ReferenceEquals(results, Book.Results), "the same list, updated in place");
            var ids = Book.Results.Rows.Select(r => r.Id).ToList();
            Log(string.Join(", ", ids.Take(12)));
            Assert.True(ids.Contains("quest:qltest:gather-flint"), "the quest");
            Assert.True(ids.Any(i => i.StartsWith("item:") && i.Contains("flint")), "flint items");
            Assert.Equal("quest:qltest:gather-flint", ids.First(), "quests come first");
            Log(await Shot.Take("questline-reference-search.png"));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task AnItemPageSaysWhichQuestsNeedIt()
        {
            await OnClient();
            Assert.True(Book.ShowReference(new ItemStack(Capi.World.GetItem(new AssetLocation("game:flint")))));
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            var page = Book.SingleComposer.GetRichtext("richtext");
            var link = page.Components.OfType<LinkTextComponent>().FirstOrDefault(l => l.DisplayText == "Gather flint");
            Assert.NotNull(link, "a link to the quest that needs flint");
            Log(await Shot.Take("questline-reference-item-quests.png"));

            link.Trigger();
            await Frames.Wait(5);
            Assert.Equal("qltest:gather-flint", Book.SelectedQuest, "the link opens the quest");
            Assert.False(Book.OnReferenceTab);
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task GuidesRenderAndBackAndForwardWalkThePages()
        {
            await OnClient();
            Book.SearchReference("");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(5);
            var guides = Book.Results.Rows.Select((r, i) => (r, i)).Where(p => !p.r.Heading).Take(2).ToList();
            string first = guides[0].r.Id, second = guides[1].r.Id;

            var (x, y) = Book.Results.ScreenPosOf(guides[0].i);
            await Click(x, y);
            await Until(() => Book.PageCode == first, 60, "the first guide");
            var text = Book.SingleComposer.GetRichtext("richtext");
            Assert.Greater(text.Components.Length, 3, "the guide has its text");
            Log(await Shot.Take("questline-reference-guide.png"));

            (x, y) = Book.Results.ScreenPosOf(guides[1].i);
            await Click(x, y);
            await Until(() => Book.PageCode == second, 60, "the second guide");

            Book.GoBack();
            Assert.Equal(first, Book.PageCode, "back");
            Book.GoForward();
            Assert.Equal(second, Book.PageCode, "forward");
        }

        static async Task Click(int x, int y)
        {
            await Input.MouseMove(x, y);
            await Frames.Wait(1);
            Book.OnMouseDown(new MouseEvent(x, y, EnumMouseButton.Left, 0));
            await Frames.Wait(1);
            Book.OnMouseUp(new MouseEvent(x, y, EnumMouseButton.Left, 0));
            await Frames.Wait(1);
        }
    }
}
