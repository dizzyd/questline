using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Model;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
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
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            Assert.Equal("qltest:basics", Book.SelectedChapter);
            // Eight quests, less the secret one and the one hidden until unlocked.
            Assert.Equal(6, Book.Canvas.VisibleNodes);
            Log(await Shot.Take("questline-book-chapter.png"));
        }

        /// <summary>The rail lists the ages first under a heading of their own, in order, and every other chapter under another.</summary>
        [VsTest(TimeoutMs = 30000)]
        public async Task TheRailPutsTheAgesFirst()
        {
            await OnClient();
            Book.SelectChapter("qltest:basics");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);

            var rows = Book.Rail.Rows.ToList();
            var chapters = ClientSystem.Client.Registry.ChaptersById;
            int topics = rows.FindIndex(r => r.Id == "#topics");
            Assert.Equal("#ages", rows[0].Id);
            Assert.Greater(topics, 1, "a Topics heading after the ages");
            Assert.True(rows.Skip(1).Take(topics - 1).All(r => chapters[r.Id].Age), "only ages above it");
            Assert.True(rows.Skip(topics + 1).All(r => !r.Heading && !chapters[r.Id].Age), "the rest below");
            Assert.Equal("qltest:basics, questline:stone-age, questline:clay-and-fire", string.Join(", ", rows.Skip(1).Take(3).Select(r => r.Id)));
            Log(await Shot.Take("questline-rail-ages.png"));
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ClickingAQuestShowsItAndClaimingGivesTheReward()
        {
            // The book composes textures, so it is only ever touched from the client thread.
            await OnClient();
            Book.TryOpen();
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
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
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
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
        /// The book comes up over a chest, as the handbook does; and with the book open behind the
        /// chest, H and L bring it to the front rather than closing it unseen.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task TheBookComesUpOverAChest()
        {
            await OnServer();
            World.SetBlock("game:chest-east", P(3, 1, 3));
            await Ticks(2);
            await Player.StandNear(P(3, 1, 3), 2.5);
            await Player.Hold("game:stick", 1);
            await OnClient();
            await Interact.UseBlock(P(3, 1, 3));
            var chest = await VsTestkit.Testing.Gui.WaitFor<GuiDialogBlockEntityInventory>();

            Assert.True(await Input.Hotkey("handbook"), "H handled");
            await Until(() => Book.OnTop, 60, "the book over the chest");
            Assert.True(chest.IsOpened(), "the chest stays open behind it");
            Log(await Shot.Take("questline-book-over-chest.png"));

            chest.TryOpen();   // a click on the chest brings it forward
            await Frames.Wait(2);
            Assert.False(Book.OnTop, "the chest in front");
            Assert.True(await Input.Hotkey("handbook"), "H again");
            await Frames.Wait(2);
            Assert.True(Book.OnTop, "H brings the buried book forward instead of closing it");
            Assert.True(await Input.Hotkey("handbook"), "H once more");
            await Until(() => !Book.IsOpened(), 30, "H closes the book once it's in front");

            Assert.True(await Input.Hotkey("questline"), "L handled");
            await Until(() => Book.OnTop, 60, "L opens the book over the chest");
            chest.TryOpen();
            await Frames.Wait(2);
            Assert.True(await Input.Hotkey("questline"), "L again");
            await Frames.Wait(2);
            Assert.True(Book.OnTop, "L brings it forward too");

            Book.TryClose();
            chest.TryClose();
        }

        /// <summary>
        /// A− and A+ step the book's text through its sizes, and the page follows; photographed at
        /// each size at the test window's 960×600, where the book is at its minimum.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task TextSizeStepsAndTheBookFollows()
        {
            await OnClient();
            var client = ClientSystem.Client;
            try
            {
                client.SetTextSize(90);
                Book.ForgetViews();
                Book.SelectQuest("questline:make-spear");
                await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
                float BodySize() => Book.SingleComposer.GetRichtext("detail").Components.OfType<RichTextComponent>()
                    .Max(c => c.Font?.UnscaledFontsize ?? 0) is var max ? (float)max : 0;
                float smallest = BodySize();
                Log(await Shot.Take("questline-text-90.png"));

                var larger = Book.SingleComposer.GetButton("text-larger");
                Assert.NotNull(larger, "an A+ button beside the tabs");
                await ClickAt((int)(larger.Bounds.absX + larger.Bounds.OuterWidth / 2), (int)(larger.Bounds.absY + larger.Bounds.OuterHeight / 2));
                await Until(() => client.Settings.TextSize == 100, 30, "A+ to step up to 100%");
                await Frames.Wait(5);
                Assert.Greater(BodySize(), smallest, "the page's text grew");

                foreach (int size in new[] { 115, 130, 150 })
                {
                    Assert.True(client.StepTextSize(1), $"a step up to {size}");
                    Assert.Equal(size, client.Settings.TextSize);
                    await Frames.Wait(10);
                    Log(await Shot.Take($"questline-text-{size}.png"));
                }
                Assert.False(client.StepTextSize(1), "150% is the largest");
                Assert.Greater(BodySize(), smallest * 1.5f, "half as big again as at 90%");
            }
            finally
            {
                client.SetTextSize(115);
                Book.TryClose();
            }
        }

        /// <summary>A checkbox task is a box on the quest's page; clicking it ticks the task.</summary>
        [VsTest(TimeoutMs = 30000)]
        public async Task ClickingTheBoxTicksACheckboxTask()
        {
            const string Quest = "questline:ground-storage";
            await OnServer();
            Engine.Unlock(Me.PlayerUID, Quest);
            await OnClient();
            await Until(() => ClientSystem.Client.StateOf(Quest) == QuestState.Available, 60, "the quest open on the client");
            Book.SelectQuest(Quest);
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);

            var detail = Book.SingleComposer.GetRichtext("detail");
            var box = detail.Components.OfType<TickBoxComponent>().SingleOrDefault();
            Assert.NotNull(box, "a box to tick");
            Assert.False(box.Ticked);
            Log(await Shot.Take("questline-book-tickbox.png"));

            var b = box.BoundsPerLine[0];
            await ClickAt((int)(detail.Bounds.absX + b.X + GuiElement.scaled(13)), (int)(detail.Bounds.absY + b.Y + b.Height / 2));
            await Until(() => ClientSystem.Client.StateOf(Quest) is QuestState.Claimable or QuestState.Done, 60, "the tick to reach the server and back");
            await Frames.Wait(10);
            box = Book.SingleComposer.GetRichtext("detail").Components.OfType<TickBoxComponent>().Single();
            Assert.True(box.Ticked, "the box shows ticked");
            Log(await Shot.Take("questline-book-tickbox-ticked.png"));
            Book.TryClose();
        }

        /// <summary>
        /// The book is 80% of the screen each way on ordinary monitors, and on a small window
        /// it grows towards the whole screen rather than below its minimum, but never off it.
        /// </summary>
        [VsTest]
        public async Task BookTakesEightyPercentOfTheScreen()
        {
            await OnClient();
            double pad = 2 * GuiStyle.ElementToDialogPadding, chrome = 66;
            foreach (var (w, h, scale) in new[] { (1920.0, 1080.0, 1.0), (2560.0, 1440.0, 1.0), (3840.0, 2160.0, 2.0), (1600.0, 900.0, 1.0) })
            {
                var layout = BookLayout.For(w, h, scale, chrome);
                double outerW = layout.Width + pad, outerH = layout.ContentHeight + chrome + pad + GuiStyle.TitleBarHeight;
                double shareW = outerW * scale / w, shareH = outerH * scale / h;
                Log($"{w}×{h} at {scale}: book {outerW:0}×{outerH:0}, {shareW:P0} × {shareH:P0}; rail {layout.RailWidth:0}, canvas {layout.CanvasWidth:0}, detail {layout.DetailWidth:0}");
                Assert.True(System.Math.Abs(shareW - 0.8) < 0.01 && System.Math.Abs(shareH - 0.8) < 0.01, $"{w}×{h}: 80% each way");
                Assert.Greater(layout.CanvasWidth, layout.DetailWidth, "the canvas gets the most room");
            }

            // The window the test runs in: 80% of it, or more on a window too small for that, and inside it.
            var small = Book.Layout;
            double screenW = Capi.Render.FrameWidth / RuntimeEnv.GUIScale, screenH = Capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            Log($"{Capi.Render.FrameWidth}×{Capi.Render.FrameHeight}: book {small.Width + pad:0}×{small.ContentHeight + chrome + pad + GuiStyle.TitleBarHeight:0}");
            Assert.True(small.Width + pad <= screenW && small.ContentHeight + chrome + pad + GuiStyle.TitleBarHeight <= screenH, "fits the window");
            Assert.Greater(small.Width + pad, screenW * 0.79, "at least 80% of the window");
        }

        /// <summary>
        /// A chapter opens fitted to the canvas when it can be read that way; a big one opens on
        /// the twenty quests nearest the player instead of shrinking past legibility.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task OpeningViewShowsTheChapterOrTwentyQuests()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("qltest:basics");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);
            Log($"basics: {Book.Canvas.VisibleNodes} of {Book.Canvas.Nodes.Count} in view at zoom {Book.Canvas.Zoom:0.00}");
            Assert.Equal(Book.Canvas.Nodes.Count, Book.Canvas.VisibleNodes, "the whole small chapter");
            Assert.Greater(Book.Canvas.Zoom, 0.9f, "zoomed in to fill the canvas, not left small");

            Book.ShowBenchmarkTree();
            await Frames.Wait(10);
            Log($"benchmark: {Book.Canvas.VisibleNodes} of {Book.Canvas.Nodes.Count} in view at zoom {Book.Canvas.Zoom:0.00}");
            Assert.True(Book.Canvas.VisibleNodes >= GuiElementQuestCanvas.FocusCount, "at least twenty quests in view");
            Assert.True(Book.Canvas.Zoom >= 0.75f, "still readable");
            Book.LeaveBenchmarkTree();
            Book.TryClose();
        }

        /// <summary>
        /// Zooming out stops once the whole chapter is in view, and the tree is centred there
        /// rather than left wherever the mouse was. Editors keep the full range.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task ZoomingOutStopsAtTheWholeChapter()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("qltest:basics");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);
            var canvas = Book.Canvas;
            var fitted = canvas.View;

            canvas.ZoomAt(0, 0, 3f);
            canvas.ZoomAt(0, 0, 3f);
            Assert.Greater(canvas.Zoom, fitted.Zoom, "zoomed in");
            for (int i = 0; i < 20; i++) canvas.ZoomAt(0, 0, 1 / 1.15f);
            await Frames.Wait(5);
            Log($"zoomed out: {canvas.VisibleNodes} of {canvas.Nodes.Count} in view at zoom {canvas.Zoom:0.00} (fitted {fitted.Zoom:0.00})");
            Assert.True(System.Math.Abs(canvas.Zoom - fitted.Zoom) < 0.001f, "stops where the chapter fits");
            Assert.Equal(canvas.Nodes.Count, canvas.VisibleNodes, "the whole chapter in view");
            Assert.True(System.Math.Abs(canvas.View.PanX - fitted.PanX) < 0.5f && System.Math.Abs(canvas.View.PanY - fitted.PanY) < 0.5f, "centred, as it opened");

            // A view saved further out than the floor comes back as the whole tree.
            canvas.SetView(new CanvasView(-5000, -5000, 0.2f));
            Assert.True(System.Math.Abs(canvas.Zoom - fitted.Zoom) < 0.001f, "a saved view can't get past the floor either");
            Book.TryClose();
        }

        /// <summary>Photographs the book's screens in the states a layout has to cope with, for review.</summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task Screens()
        {
            await OnClient();
            Book.SelectQuest("qltest:hunt");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);
            Log(await Shot.Take("questline-screen-locked.png"));

            Book.SelectQuest("qltest:trade-sticks");
            await Frames.Wait(20);
            Log(await Shot.Take("questline-screen-choice.png"));

            Book.SelectChapter("qltest:together");
            await Frames.Wait(20);
            Log(await Shot.Take("questline-screen-nothing-selected.png"));

            foreach (string chapter in new[] { "questline:stone-age", "questline:copper-age" })
            {
                Book.ForgetViews();
                Book.SelectChapter(chapter);
                await Frames.Wait(20);
                Log(await Shot.Take($"questline-screen-{chapter[10..]}.png"));
            }

            Book.SelectTab(1);
            await Frames.Wait(20);
            Log(await Shot.Take("questline-screen-reference-empty.png"));

            Book.SelectTab(2);
            await Frames.Wait(20);
            Log(await Shot.Take("questline-screen-no-party.png"));
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task ChapterArtAndShapesDraw()
        {
            await OnServer();
            Assert.True(Engine.Problems.Any(p => p.StartsWith("qltest:gallery:") && p.Contains("nowhere.png")), "a missing art image is reported");

            await OnClient();
            Book.SelectChapter("qltest:gallery");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
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

        [VsTest(TimeoutMs = 30000)]
        public async Task PinCommandTogglesAQuestByItsId()
        {
            await OnClient();
            var client = ClientSystem.Client;
            foreach (var id in client.Pins.ToList()) client.Unpin(id);

            Assert.Equal(EnumCommandStatus.Success, Run(".questline pin station-craft").Status, "pinned by the id without its domain");
            Assert.True(client.IsPinned("qltest:station-craft"));
            Assert.True(Run(".questline pins").StatusMessage.Contains("qltest:station-craft"), "the list names the pin by its id");
            Assert.Equal(EnumCommandStatus.Success, Run(".questline pin station-craft").Status, "unpinned");
            Assert.False(client.IsPinned("qltest:station-craft"));
            Assert.Equal(EnumCommandStatus.Error, Run(".questline pin no-such-quest").Status);

            await OnServer();
            Engine.ForceComplete(Me, GatherFlint);
            Assert.Equal(null, Engine.Claim(Me, GatherFlint, null));
            await OnClient();
            await Until(() => client.StateOf(GatherFlint) == QuestState.Done, 60, "the claim to reach the client");
            Assert.Equal(EnumCommandStatus.Error, Run(".questline pin " + GatherFlint).Status, "a claimed quest isn't pinned, as in the book");
            Assert.False(client.IsPinned(GatherFlint));
        }

        // A client command, run as the player typing it in chat would: the game gives client
        // commands every privilege, since they act only on the player's own game.
        static TextCommandResult Run(string command)
        {
            TextCommandResult result = null;
            var caller = new Caller { Type = EnumCallerType.Player, Player = Capi.World.Player, CallerPrivileges = ["*"] };
            Capi.ChatCommands.ExecuteUnparsed(command, new TextCommandCallingArgs { Caller = caller }, r => result = r);
            return result;
        }

        static string Title(string questId) => ClientSystem.Client.TextOf(questId + "-title");

        /// <summary>
        /// A locked quest lists each prerequisite on its own row, naming the chapter of one that's
        /// elsewhere; under them, the open quests that lead there. Each is a link to its quest.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task ALockedQuestSaysWhatItNeedsAndWhereToStart()
        {
            await OnClient();
            Book.SelectQuest("qltest:cross-hunt");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);

            var detail = Book.SingleComposer.GetRichtext("detail");
            var comps = detail.Components.ToList();
            string startWith = Lang.Get("questline:start-with").ToUpperInvariant();
            int start = comps.FindIndex(c => c is RichTextComponent r && r.DisplayText.StartsWith(startWith));
            Assert.True(start > 0, "a Start with section");
            var needs = comps.Take(start).OfType<LinkTextComponent>().Select(l => l.DisplayText).ToList();
            var steps = comps.Skip(start).TakeWhile(c => c is not RuleTextComponent).OfType<LinkTextComponent>().Select(l => l.DisplayText).ToList();
            Assert.Equal($"{Title("qltest:hunt")}, {Title("qltest:shape-square")}", string.Join(", ", needs));
            Assert.True(comps.Take(start).OfType<RichTextComponent>().Any(r => r.DisplayText.Contains(ClientSystem.Client.TextOf("qltest:gallery-title"))), "the other chapter named");
            // The hunt waits on quests that wait on flint; the square is open already.
            Assert.Equal($"{Title(GatherFlint)}, {Title("qltest:shape-square")}", string.Join(", ", steps));
            Log(await Shot.Take("questline-needs.png"));

            var link = comps.OfType<LinkTextComponent>().First(l => l.DisplayText == Title("qltest:hunt"));
            var b = link.BoundsPerLine[0];
            await ClickAt((int)(detail.Bounds.absX + b.X + b.Width / 2), (int)(detail.Bounds.absY + b.Y + b.Height / 2));
            await Until(() => Book.SelectedQuest == "qltest:hunt", 60, "the link to go to the hunt");
            Assert.Equal("qltest:basics", Book.SelectedChapter);
            Book.TryClose();
        }

        /// <summary>
        /// A prerequisite in another chapter is a stub beside the quest it holds up, named by its
        /// chapter and clear of the chapter's own lines; clicking it goes there, and once it's met it's gone.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task PrerequisitesFromOtherChaptersAreStubsOnTheCanvas()
        {
            await OnClient();
            Book.ForgetViews();
            Book.SelectChapter("qltest:crossroads");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            var nodes = Book.Canvas.Nodes.ToList();
            var stubs = nodes.Where(n => n.Stub).ToList();
            Assert.Equal("qltest:hunt, qltest:shape-square, qltest:lay-cobble, qltest:read-the-book, qltest:gather-flint", string.Join(", ", stubs.Select(s => s.Id)));
            Assert.Equal(Title("qltest:hunt"), stubs[0].Title);
            Assert.Equal(ClientSystem.Client.TextOf("qltest:basics-title"), stubs[0].Note);
            // Straight left of Follow on is the line to it from After the hunt; the stub goes elsewhere.
            var cobble = stubs[2];
            Assert.False(cobble.X == 3 && cobble.Y == 0, $"the stub clear of the chapter's own line, at {cobble.X}, {cobble.Y}");
            Assert.True(nodes.Single(n => n.Id == "qltest:cross-follow").Prereqs.Contains(nodes.IndexOf(cobble)), "a line from the stub to its quest");
            // Hovering a stub names it and its chapter.
            var (x, y) = Book.Canvas.ScreenPosOf(nodes.IndexOf(stubs[1]));
            await Input.MouseMove(x, y);
            await Until(() => Book.Canvas.Tooltip == $"{Title("qltest:shape-square")}\n{ClientSystem.Client.TextOf("qltest:gallery-title")}", 30, "the tooltip for the gallery's stub");
            await Frames.Wait(2);
            Log(await Shot.Take("questline-stubs.png"));

            await ClickAt(x, y);
            await Until(() => Book.SelectedQuest == "qltest:shape-square", 60, "the stub to go to its quest");
            Assert.Equal("qltest:gallery", Book.SelectedChapter);

            await OnServer();
            Engine.ForceComplete(Me, "qltest:lay-cobble");
            await OnClient();
            await Until(() => ClientSystem.Client.IsMet("qltest:lay-cobble"), 60, "lay-cobble met on the client");
            Book.SelectChapter("qltest:crossroads");
            await Frames.Wait(5);
            Assert.False(Book.Canvas.Nodes.Any(n => n.Stub && n.Id == "qltest:lay-cobble"), "a met prerequisite has no stub");
            Book.TryClose();
        }

        /// <summary>
        /// A chapter with nothing open is muted on the rail, and picking it says what its first
        /// quests need and where to start; it opens as they're met.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task AChapterWithNothingOpenSaysWhatOpensIt()
        {
            await OnClient();
            Book.SelectChapter("qltest:crossroads");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);

            Assert.True(Book.Rail.Rows.Single(r => r.Id == "qltest:crossroads").Closed, "crossroads muted on the rail");
            Assert.False(Book.Rail.Rows.Single(r => r.Id == "qltest:basics").Closed, "basics is open");
            var comps = Book.SingleComposer.GetRichtext("detail").Components.ToList();
            Assert.True(comps.OfType<RichTextComponent>().Any(r => r.DisplayText.StartsWith(Lang.Get("questline:chapter-closed"))), "says nothing is open");
            var links = comps.OfType<LinkTextComponent>().Select(l => l.DisplayText).ToList();
            // Early start is the chapter's earliest way in, and needs only flint; After the hunt,
            // which waits on the later gallery, isn't what opens it.
            Assert.Equal(Title(GatherFlint), string.Join(", ", links));
            Log(await Shot.Take("questline-chapter-closed.png"));

            await OnServer();
            Engine.ForceComplete(Me, "qltest:hunt");
            Engine.ForceComplete(Me, "qltest:shape-square");
            await OnClient();
            await Until(() => ClientSystem.Client.StateOf("qltest:cross-hunt") == QuestState.Available, 60, "after-the-hunt to open");
            await Frames.Wait(5);
            Assert.False(Book.Rail.Rows.Single(r => r.Id == "qltest:crossroads").Closed, "open once its first quest is");
            Book.TryClose();
        }

        static ItemStack[] HandbookStacks => ObjectCacheUtil.TryGet<ItemStack[]>(Capi, "handbookallstacks");

        /// <summary>Whether the handbook has a page for exactly this stack, as a quest builds it.</summary>
        static bool HasOwnPage(ItemStack stack)
        {
            string code = GuiHandbookItemStackPage.PageCodeForStack(stack);
            return HandbookStacks.Any(s => GuiHandbookItemStackPage.PageCodeForStack(s) == code);
        }

        /// <summary>
        /// Every item a default quest shows, as a task or a reward, opens a page: its own, its
        /// item's when the handbook keys the page by a particular stack (a pie, a meal), or a search
        /// for it by name. Never the empty page.
        /// </summary>
        [VsTest(TimeoutMs = 180000)]
        public async Task EveryQuestItemOpensAPage()
        {
            await OnClient();
            await Until(() => HandbookStacks != null, 600, "the handbook's stacks");
            var registry = ClientSystem.Client.Registry;
            var text = new QuestText(Capi, ClientSystem.Client);
            var codes = registry.Quests.Values.Where(q => q.Id.StartsWith("questline:"))
                .SelectMany(q => q.Tasks.Where(t => t.OnCollectible).Select(t => t.Target)
                    .Concat(q.Rewards.SelectMany(r => r.Type == "choice" ? r.Options : [r]).Where(r => r.Type == "item").Select(r => r.Item)))
                .Distinct().ToList();
            int own = 0, kin = 0, searched = 0;
            foreach (string code in codes)
            {
                if (text.StackFor(code) is not { } stack) continue;
                bool paged = Book.ShowReference(stack);
                if (!paged)
                {
                    searched++;
                    Log($"{code}: searched for");
                    Assert.Null(Book.PageCode, $"{code}: no page left over from before");
                    Assert.True(Book.Results.Rows.Count > 0, $"{code}: the search finds something");
                    continue;
                }
                // The page shown is this item, or the same kind of thing from the same mod.
                var shown = HandbookStacks.First(s => "item:" + GuiHandbookItemStackPage.PageCodeForStack(s) == Book.PageCode).Collectible;
                Assert.True(shown.Code.Domain == stack.Collectible.Code.Domain && shown.FirstCodePart() == stack.Collectible.FirstCodePart(), $"{code} opened {Book.PageCode}");
                if (HasOwnPage(stack)) own++;
                else
                {
                    kin++;
                    Log($"{code}: {Book.PageCode}");
                }
            }
            Log($"{codes.Count} codes: {own} with a page of their own, {kin} on their kind's page, {searched} searched for");
            Book.TryClose();
        }

        /// <summary>Clicking a quest's item opens its page; Backspace comes back to the quest.</summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task AQuestsItemOpensItsPageAndBackspaceReturns()
        {
            await OnClient();
            await Until(() => HandbookStacks != null, 600, "the handbook's stacks");
            var registry = ClientSystem.Client.Registry;
            var text = new QuestText(Capi, ClientSystem.Client);
            // A quest whose first item has no handbook page of its own: the case that used to open an empty page.
            var quest = registry.Quests.Values.Where(q => q.Id.StartsWith("questline:"))
                .FirstOrDefault(q => q.Tasks.FirstOrDefault(t => t.OnCollectible) is { } t && text.StackFor(t.Target) is { } s && !HasOwnPage(s))
                ?? registry.Get("questline:bake-pie");
            Log($"clicking the item in {quest.Id}");
            Book.SelectQuest(quest.Id);
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);

            var detail = Book.SingleComposer.GetRichtext("detail");
            var icon = detail.Components.OfType<ItemstackTextComponent>().First();
            var b = icon.BoundsPerLine[0];
            await ClickAt((int)(detail.Bounds.absX + b.X + b.Width / 2), (int)(detail.Bounds.absY + b.Y + b.Height / 2));
            await Until(() => Book.OnReferenceTab, 30, "the Reference tab");
            Assert.True(Book.PageCode?.StartsWith("item:") == true, $"an item page, not an empty one: {Book.PageCode ?? "none"}");
            Assert.NotNull(Book.SingleComposer.GetButton("back"), "a Back button");
            Log(await Shot.Take("questline-quest-item-page.png"));

            await Input.Press(GlKeys.BackSpace);
            await Until(() => !Book.OnReferenceTab, 30, "Backspace back to the Quests tab");
            Assert.Equal(quest.Id, Book.SelectedQuest);
            Book.TryClose();
        }

        /// <summary>
        /// Backspace steps back along the way the book came, across chapters, as a browser's back
        /// does; but a text field with the focus keeps it, for deleting.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task BackspaceStepsBackThroughAChain()
        {
            await OnClient();
            Book.SelectChapter("qltest:crossroads");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            Book.SelectQuest("qltest:cross-hunt");
            await Frames.Wait(10);

            // Follow the hunt's link out to Basics, by a click.
            var detail = Book.SingleComposer.GetRichtext("detail");
            var link = detail.Components.OfType<LinkTextComponent>().First(l => l.DisplayText == Title("qltest:hunt"));
            var b = link.BoundsPerLine[0];
            await ClickAt((int)(detail.Bounds.absX + b.X + b.Width / 2), (int)(detail.Bounds.absY + b.Y + b.Height / 2));
            await Until(() => Book.SelectedQuest == "qltest:hunt", 60, "the link followed");

            await Input.Press(GlKeys.BackSpace);
            await Until(() => Book.SelectedQuest == "qltest:cross-hunt", 30, "Backspace back to the quest the link was on");
            Assert.Equal("qltest:crossroads", Book.SelectedChapter);
            await Input.Press(GlKeys.BackSpace);
            await Until(() => Book.SelectedQuest == null, 30, "and back again to the chapter with nothing picked");
            Assert.Equal("qltest:crossroads", Book.SelectedChapter);
            Assert.True(Book.StepForward(), "forward again");
            Assert.Equal("qltest:cross-hunt", Book.SelectedQuest);

            // In the Reference tab's search box, Backspace deletes.
            Book.SelectTab(1);
            await Frames.Wait(10);
            var search = Book.SingleComposer.GetTextInput("ref-search");
            await ClickAt((int)(search.Bounds.absX + 20), (int)(search.Bounds.absY + search.Bounds.OuterHeight / 2));
            await Input.Type("ab");
            await Until(() => Book.SingleComposer.GetTextInput("ref-search").GetText() == "ab", 30, "typed into the search");
            await Input.Press(GlKeys.BackSpace);
            await Until(() => Book.SingleComposer.GetTextInput("ref-search").GetText() == "a", 30, "Backspace deleted a letter");
            Book.SearchReference("");
            Book.TryClose();
        }

        /// <summary>
        /// A quest outside an age is labelled with the latest age its prerequisites lead back to
        /// (the earliest way through an "any"). On the canvas, the quests later than most of their
        /// chapter carry that age's icon, which names it on hover; the page says it for every quest.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task QuestsAreLabelledWithTheirAge()
        {
            await OnClient();
            var registry = ClientSystem.Client.Registry;
            string AgeOf(string id) => registry.AgeOf(registry.Get(id))?.Id;
            Assert.Equal("qltest:basics", AgeOf("qltest:hunt"), "an age's own quest is that age");
            Assert.Equal("qltest:gallery", AgeOf("qltest:cross-hunt"), "the later of the two it needs");
            Assert.Equal("qltest:gallery", AgeOf("qltest:cross-follow"), "through a quest in its own chapter");
            Assert.Equal("qltest:basics", AgeOf("qltest:cross-either"), "the earlier way through an any");

            Book.ForgetViews();
            Book.SelectQuest("qltest:cross-follow");
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);
            var nodes = Book.Canvas.Nodes.ToList();
            // Two quests of each age: the earlier is the chapter's own, the later is badged.
            Assert.Equal("qltest:cross-hunt, qltest:cross-follow", string.Join(", ", nodes.Where(n => n.AgeIcon != null).Select(n => n.Id)));
            Assert.False(nodes.Any(n => n.Stub && n.AgeIcon != null), "stubs carry no badge");
            string gallery = ClientSystem.Client.TextOf("qltest:gallery-title");
            var detail = Book.SingleComposer.GetRichtext("detail").Components.OfType<RichTextComponent>().ToList();
            Assert.True(detail.Take(4).Any(c => c.DisplayText.Contains(gallery)), "the page names the age beside the state");

            // The badge sits at the frame's top right corner.
            int follow = nodes.FindIndex(n => n.Id == "qltest:cross-follow");
            var (x, y) = Book.Canvas.ScreenPosOf(follow);
            int corner = (int)(GuiElementQuestCanvas.NodeSize / 2 * 0.9f * Book.Canvas.Zoom * RuntimeEnv.GUIScale);
            await Input.MouseMove(x + corner, y - corner);
            await Until(() => Book.Canvas.Tooltip == gallery, 30, "the badge's tooltip");
            await Frames.Wait(2);
            Log(await Shot.Take("questline-ages.png"));
            await Input.MouseMove(x - corner, y + corner);
            await Until(() => Book.Canvas.Tooltip == null, 30, "no tooltip away from the badge");
            Book.TryClose();
        }

        /// <summary>
        /// The default questline's Kitchen and Baking, where this started: their stubs, and a locked
        /// quest's needs, at 960×600.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task KitchenAndBakingScreens()
        {
            await OnClient();
            foreach (string chapter in new[] { "questline:kitchen", "questline:baking" })
            {
                Book.ForgetViews();
                Book.SelectChapter(chapter);
                await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
                await Frames.Wait(30);
                Log($"{chapter}: {Book.Canvas.Nodes.Count(n => n.Stub)} stubs");
                Log(await Shot.Take($"questline-{chapter[10..]}-closed.png"));
            }
            Book.SelectQuest("questline:bake-pie");
            await Frames.Wait(30);
            Log(await Shot.Take("questline-baking-bake-pie.png"));
            Book.TryClose();
        }

        /// <summary>
        /// The book at 960×600 in languages whose words run long (German, Russian) and in other
        /// scripts (Chinese, Arabic), to look at for text that overflows its panel or button.
        /// </summary>
        [VsTest(TimeoutMs = 90000)]
        public async Task TheBookInOtherLanguages()
        {
            await OnClient();
            string language = Lang.CurrentLocale;
            try
            {
                foreach (string code in new[] { "de", "ru", "zh-cn", "ar" })
                {
                    Book.TryClose();
                    await Frames.Wait(5);
                    Lang.ChangeLanguage(code);
                    for (int open = 0; open < 2; open++)
                    {
                        Book.TryClose();
                        await Frames.Wait(5);
                        Book.ForgetViews();
                        Book.SelectChapter("questline:stone-age");
                        Book.SelectQuest("questline:make-firestarter");
                        await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
                        await Frames.Wait(30);
                    }
                    Assert.Equal("questline:make-firestarter", Book.SelectedQuest);
                    Log(await Shot.Take($"questline-lang-{code}.png"));
                }
            }
            finally
            {
                Book.TryClose();
                Lang.ChangeLanguage(language);
                Book.ForgetViews();
            }
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
