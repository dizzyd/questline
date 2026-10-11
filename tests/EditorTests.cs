using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
    /// The in-game editor, driven through the book the way an author would: mouse gestures on
    /// the canvas, the form, the buttons. Chapters are made in the domain qleditor, whose working
    /// copies are cleared before each test. The phase 3 gate is the fifty-quest chapter.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class EditorTests
    {
        const string Domain = "qleditor";
        const string Chapter = Domain + ":chapter-1";

        static QuestlineModSystem ClientSystem => Capi.ModLoader.GetModSystem<QuestlineModSystem>();
        static QuestClient Client => ClientSystem.Client;
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static GuiDialogQuestBook Book => ClientSystem.Book;
        static IServerPlayer Me => (IServerPlayer)Player.Me;

        static ChapterDef Raw(string id) => Client.Editor != null && Client.Editor.Chapters.TryGetValue(id, out var c) ? c : null;

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get("qltest:gather-flint") == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Engine.Config.EditorDomain = Domain;
            string mine = Path.Combine(Engine.Editor.WorkingDir, Domain);
            if (Directory.Exists(mine)) Directory.Delete(mine, true);
            string basics = Path.Combine(Engine.Editor.WorkingDir, "qltest");
            if (Directory.Exists(basics)) Directory.Delete(basics, true);
            Engine.Reload();

            await OnClient();
            await Until(() => Client.CanEdit && Client.Editor.Domain == Domain && Raw(Chapter) == null && Client.Editor.Edited.Count == 0, 100, "a clean editor on the client");
            Book.SetEditing(false);
            Book.ForgetViews();
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task SavingNeedsThePrivilege()
        {
            await OnServer();
            string chapter = "{\"id\":\"qleditor:locked-out\",\"quests\":[]}";
            Sapi.Permissions.DenyPrivilege(Me.PlayerUID, QuestEditor.Privilege);
            try
            {
                Assert.NotNull(Engine.Editor.Save(Me, chapter, null), "a save without the privilege");
                Assert.False(Engine.Registry.ChaptersById.ContainsKey("qleditor:locked-out"));
            }
            finally
            {
                Sapi.Permissions.RemovePrivilegeDenial(Me.PlayerUID, QuestEditor.Privilege);
            }
            Assert.Equal(null, Engine.Editor.Save(Me, chapter, null));
            Assert.True(Engine.Registry.ChaptersById.ContainsKey("qleditor:locked-out"), "saved once allowed");

            // Ids become folder and file names, so anything but plain names is refused.
            Assert.NotNull(Engine.Editor.Save(Me, "{\"id\":\"qleditor:../escape\",\"quests\":[]}", null), "a path in the id");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task BrokenQuestsStayInTheEditorAndShowAsProblems()
        {
            await OnServer();
            string json = "{\"id\":\"qleditor:fixme\",\"quests\":[{\"id\":\"qleditor:bad\",\"pos\":[0,0],\"tasks\":[{\"type\":\"obtain\",\"item\":\"game:no-such-thing\"}]}]}";
            Assert.Equal(null, Engine.Editor.Save(Me, json, "{\"qleditor:bad-title\":\"Bad quest\"}"));
            Assert.True(Engine.Registry.Get("qleditor:bad") == null, "the book leaves a broken quest out");

            await OnClient();
            await Until(() => Raw("qleditor:fixme")?.Quests.Count == 1, 100, "the editor's copy keeps it");
            Assert.True(Client.Editor.ProblemsOf("qleditor:bad").Any(), "its problem reaches the editor");

            Book.ShowInEditor("qleditor:fixme", "qleditor:bad");
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);
            Assert.True(Book.Canvas.Nodes.Single().Problem, "drawn as a problem");
            Log(await Shot.Take("questline-editor-problem.png"));

            Book.SelectTab(3);
            await Frames.Wait(20);
            Log(await Shot.Take("questline-editor-problems-tab.png"));
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task EditingAnInstalledChapterThenRevertingIt()
        {
            await OnClient();
            Book.ShowInEditor("qltest:basics", null);
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            int flint = Book.Canvas.Nodes.ToList().FindIndex(n => n.Id == "qltest:gather-flint");
            var (x, y) = Book.Canvas.ScreenPosOf(flint);
            await Drag(x, y, x + 120, y, EnumMouseButton.Left);
            await Until(() => Client.Editor.Edited.Contains("qltest:basics"), 100, "a working copy of basics");
            float moved = Raw("qltest:basics").Quests.Single(q => q.Id == "qltest:gather-flint").Pos[0];
            Assert.Greater(moved, 0.9f, "moved right");

            await OnServer();
            Assert.Equal(moved, Engine.Registry.Get("qltest:gather-flint").Pos[0], "the book uses the working copy");

            await OnClient();
            await Until(() => Book.SingleComposer.GetButton("edit-revert") != null, 60, "a Revert button on the chapter form");
            await ClickButton("edit-revert");
            await Until(() => !Client.Editor.Edited.Contains("qltest:basics"), 100, "the working copy gone");
            await OnServer();
            Assert.Equal(0f, Engine.Registry.Get("qltest:gather-flint").Pos[0], "back to the installed position");
        }

        /// <summary>
        /// The phase 3 gate: a fifty-quest chapter built in the editor, with links, a moved quest,
        /// a title typed in the form and a task filled in from an item on the cursor, then
        /// exported as content-mod assets.
        /// </summary>
        [VsTest(TimeoutMs = 600000)]
        public async Task GateAFiftyQuestChapterBuiltInTheEditor()
        {
            await OnClient();
            Book.SelectChapter("qltest:basics");
            await Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(10);
            await ClickButton("edit-start");
            await Until(() => Book.Editing, 60, "edit mode");
            await ClickButton("edit-new-chapter");
            await Until(() => Raw(Chapter) != null && Book.SelectedChapter == Chapter, 100, "the new chapter");

            // Zoomed out far enough for a ten-by-five grid, a cell and a half apart.
            await Frames.Wait(5);
            Book.Canvas.SetView(new CanvasView(-GuiElementQuestCanvas.CellSize, -GuiElementQuestCanvas.CellSize, 0.42f));
            await Frames.Wait(2);

            for (int i = 0; i < 50; i++)
            {
                var (x, y) = Book.Canvas.ScreenPosOfGrid(i % 10 * 1.5f, i / 10 * 1.5f);
                await Click(x, y, EnumMouseButton.Right);
                int want = i + 1;
                await Until(() => Raw(Chapter)?.Quests.Count == want, 100, $"quest {want} saved");
                await Frames.Wait(2);
            }
            Log(await Shot.Take("questline-editor-fifty.png"));

            // Chain the first row: each quest needs the one before it.
            for (int i = 1; i < 10; i++)
            {
                string pre = $"{Domain}:quest-{i}", next = $"{Domain}:quest-{i + 1}";
                var (fx, fy) = Book.Canvas.ScreenPosOf(Index(pre));
                var (tx, ty) = Book.Canvas.ScreenPosOf(Index(next));
                await Drag(fx, fy, tx, ty, EnumMouseButton.Right);
                await Until(() => Raw(Chapter).Quests.Single(q => q.Id == next).Requires?.QuestIds().Contains(pre) == true, 100, $"{next} needs {pre}");
            }

            // Move the last quest down a row.
            string last = $"{Domain}:quest-50";
            var (lx, ly) = Book.Canvas.ScreenPosOf(Index(last));
            int cell = (int)(GuiElementQuestCanvas.CellSize * Book.Canvas.Zoom * Vintagestory.API.Config.RuntimeEnv.GUIScale * 1.5f);
            await Drag(lx, ly, lx, ly + cell, EnumMouseButton.Left);
            await Until(() => Raw(Chapter).Quests.Single(q => q.Id == last).Pos[1] > 6.4f, 100, "the last quest moved down");

            // Title the first quest in the form.
            var (qx, qy) = Book.Canvas.ScreenPosOf(Index($"{Domain}:quest-1"));
            await Click(qx, qy, EnumMouseButton.Left);
            await Until(() => Book.SelectedQuest == $"{Domain}:quest-1", 60, "quest-1 selected");
            await Frames.Wait(5);
            Book.SingleComposer.GetTextInput("edit-title").SetValue("First steps");
            Assert.True(Book.Dirty, "typing marks the draft unsaved");
            await ClickButton("edit-save");

            // Give its task a target by clicking the pick slot with flint on the cursor.
            Book.SelectEditPage(1);
            await Frames.Wait(5);
            var mouse = Capi.World.Player.InventoryManager.MouseItemSlot;
            mouse.Itemstack = new ItemStack(Capi.World.GetItem(new AssetLocation("game:flint")));
            try
            {
                await ClickElement("edit-task-0-pick");
            }
            finally
            {
                mouse.Itemstack = null;
            }
            await Until(() => Raw(Chapter).Quests.Single(q => q.Id == $"{Domain}:quest-1").Tasks[0] is { Type: "obtain", Item: "game:flint" }, 100, "the task took the flint");
            await Frames.Wait(10);
            Log(await Shot.Take("questline-editor-tasks.png"));

            Book.SelectEditPage(3);
            await Frames.Wait(10);
            Log(await Shot.Take("questline-editor-options.png"));

            Book.SelectEditPage(0);
            await Frames.Wait(10);
            Log(await Shot.Take("questline-editor-quest.png"));

            Book.SelectQuest($"{Domain}:quest-2");
            Book.SelectChapter(Chapter);
            await Frames.Wait(10);
            Log(await Shot.Take("questline-editor-chapter.png"));

            await OnServer();
            var built = Engine.Registry.ChaptersById[Chapter];
            Assert.Equal(50, built.Quests.Count, "fifty quests in the book");
            var problems = Engine.Problems.Where(p => p.StartsWith(Domain + ":")).ToList();
            foreach (var p in problems) Log(p);
            Assert.Equal(0, problems.Count, "no problems in the new chapter");
            Assert.Equal("First steps", Engine.Registry.Text[$"{Domain}:quest-1-title"]);

            // Export, and the files read back as a pack would write them.
            var (folder, chapters) = Engine.Editor.Export();
            string file = Path.Combine(folder, Domain, "assets", Domain, "questline", "chapters", "chapter-1.json");
            Assert.True(File.Exists(file), "the chapter was exported");
            var exported = JObject.Parse(File.ReadAllText(file));
            Assert.Equal("chapter-1", (string)exported["id"]);
            Assert.Equal(50, exported["quests"].Count());
            Assert.Equal("quest-1", (string)exported["quests"].First(q => (string)q["id"] == "quest-2")["requires"], "ids written short");
            Assert.True(File.Exists(Path.Combine(folder, Domain, "assets", Domain, "lang", "en.json")), "the text was exported");
            Log($"exported {chapters} chapters to {folder}");
        }

        static int Index(string questId) => Book.Canvas.Nodes.ToList().FindIndex(n => n.Id == questId);

        static async Task Click(int x, int y, EnumMouseButton button)
        {
            await OnClient();
            await Input.MouseMove(x, y);
            await Frames.Wait(1);
            Book.OnMouseDown(new MouseEvent(x, y, button, 0));
            await Frames.Wait(1);
            Book.OnMouseUp(new MouseEvent(x, y, button, 0));
            await Frames.Wait(1);
        }

        static async Task Drag(int x1, int y1, int x2, int y2, EnumMouseButton button)
        {
            await OnClient();
            Book.OnMouseDown(new MouseEvent(x1, y1, button, 0));
            await Frames.Wait(1);
            for (int step = 1; step <= 5; step++)
            {
                int x = x1 + (x2 - x1) * step / 5, y = y1 + (y2 - y1) * step / 5;
                Book.OnMouseMove(new MouseEvent(x, y, button, 0));
                await Frames.Wait(1);
            }
            Book.OnMouseUp(new MouseEvent(x2, y2, button, 0));
            await Frames.Wait(1);
        }

        static async Task ClickButton(string key)
        {
            await OnClient();
            var button = Book.SingleComposer.GetButton(key);
            Assert.NotNull(button, $"a {key} button");
            await Click((int)(button.Bounds.absX + button.Bounds.OuterWidth / 2), (int)(button.Bounds.absY + button.Bounds.OuterHeight / 2), EnumMouseButton.Left);
        }

        static async Task ClickElement(string key)
        {
            await OnClient();
            var element = Book.SingleComposer.GetElement(key);
            Assert.NotNull(element, key);
            await Click((int)(element.Bounds.absX + element.Bounds.OuterWidth / 2), (int)(element.Bounds.absY + element.Bounds.OuterHeight / 2), EnumMouseButton.Left);
        }
    }
}
