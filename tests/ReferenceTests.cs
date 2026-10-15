using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
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
            Assert.True(Book.Results.Rows.Count > 0 && Book.Results.Rows.Where(r => !r.Heading).All(r => r.Id.StartsWith("guide:") || r.Id.StartsWith("mod:")), "with no search, the guides (and the mods)");
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
        public async Task VariantsShareARowUntilTheSearchNamesOne()
        {
            await OnClient();
            // A family of variants from the handbook's own list: "Chest (oak)", "Chest (pine)", ...
            var stacks = ObjectCacheUtil.TryGet<ItemStack[]>(Capi, "handbookallstacks");
            // Grouped within one mod, as the book does: two mods' families of the same name are two rows.
            var family = stacks.Select(s => (Name: s.GetName(), s.Collectible.Code.Domain)).Distinct()
                .Select(n => (n.Name, n.Domain, Open: n.Name.LastIndexOf(" (")))
                .Where(n => n.Open > 0 && n.Name.EndsWith(")") && n.Name.IndexOf('(') == n.Open + 1)
                .GroupBy(n => n.Name[..n.Open])
                .First(g => g.Count() >= 3 && g.Select(n => n.Domain).Distinct().Count() == 1);
            string variant = family.First().Name;
            Log($"{family.Key}: {family.Count()} kinds, e.g. {variant}");

            Book.SearchReference(family.Key);
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);
            var rows = Book.Results.Rows.Where(r => r.Id.StartsWith("item:")).ToList();
            Log(string.Join(", ", rows.Take(12).Select(r => $"{r.Title} [{r.Note}]")));
            Assert.Equal(1, rows.Count(r => r.Title == family.Key), "one row for the family");
            Assert.False(rows.Any(r => r.Title.StartsWith(family.Key + " (")), "no variants listed");
            Log(await Shot.Take("questline-reference-variants.png"));

            Book.SingleComposer.GetTextInput("ref-search").SetValue(variant);
            await Frames.Wait(5);
            Assert.True(Book.Results.Rows.Any(r => r.Title == variant), "the variant, when the search names it");

            // Without the brackets, as people type it
            Book.SingleComposer.GetTextInput("ref-search").SetValue(variant.Replace("(", "").Replace(")", ""));
            await Frames.Wait(5);
            Assert.True(Book.Results.Rows.Any(r => r.Title == variant), "the variant, searched without brackets");

            // The family's page lists every variant
            var row = Book.Results.Rows.First(r => r.Title == variant);
            Book.Results.Pick(row.Id);
            await Until(() => Book.PageCode == row.Id, 60, "the variant's page");
            await Frames.Wait(10);
            // The component keeps its stack to itself, so this counts them after the heading.
            var comps = Book.SingleComposer.GetRichtext("richtext").Components.ToList();
            int heading = comps.FindIndex(c => c is RichTextComponent t && t.DisplayText.Trim() == "VARIANTS");
            Assert.True(heading >= 0, "a Variants section");
            int shown = comps.Skip(heading + 1).TakeWhile(c => c is ItemstackTextComponent).Count();
            Assert.True(shown >= family.Count(), $"every variant on the page: {shown} of {family.Count()}");
            Log(await Shot.Take("questline-reference-variant-page.png"));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task AtAModListsEverythingItAdds()
        {
            await OnClient();
            // The game's own domain is the one every run has: far more than a list holds, so it ends
            // with a note to narrow the search.
            Book.SearchReference("@game");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);
            var rows = Book.Results.Rows;
            Log($"{rows.Count} rows: " + string.Join(", ", rows.Take(8).Select(r => $"{r.Title} [{r.Note}]")));
            Assert.True(rows.Count > 100 && rows.Take(rows.Count - 1).All(r => r.Id.StartsWith("item:")), "the game's items");
            Assert.True(rows[^1].Heading && rows[^1].Id == "#more", "a note that there's more");
            Assert.True(rows.Any(r => r.Note.EndsWith("kinds")), "variants share a row");
            Log(await Shot.Take("questline-reference-mod.png"));

            // Words after the mod search within it
            var input = Book.SingleComposer.GetTextInput("ref-search");
            input.SetValue("@game anvil");
            await Frames.Wait(5);
            Log(string.Join(", ", Book.Results.Rows.Select(r => r.Title)));
            Assert.True(Book.Results.Rows.Count > 0 && Book.Results.Rows.All(r => r.Title.ToLowerInvariant().Contains("anvil")), "the game's anvils");

            // The vanilla mods keep their items in "game"
            input.SetValue("@survival anvil");
            await Frames.Wait(5);
            Assert.True(Book.Results.Rows.Count > 0, "@survival means the game's items");

            // "@" alone lists the mods
            input.SetValue("@");
            await Frames.Wait(5);
            Assert.True(Book.Results.Rows.All(r => r.Heading || r.Id.StartsWith("mod:")), "the mods, while the name is typed");

            // A mod in the list with no search opens the same list for it
            Book.SearchReference("");
            await Frames.Wait(5);
            var mod = Book.Results.Rows.FirstOrDefault(r => r.Id.StartsWith("mod:"));
            if (mod == null) { Log("no mod with items of its own is loaded"); return; }
            Book.Results.Pick(mod.Id);
            await Frames.Wait(5);
            Assert.True(Book.Results.Rows.Count > 0 && Book.Results.Rows.All(r => r.Id.StartsWith("item:") || r.Heading), $"{mod.Title}'s items");
            Assert.Equal("@" + mod.Id[4..], Book.SingleComposer.GetTextInput("ref-search").GetText(), "the search box says what it shows");
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
