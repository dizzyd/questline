using System;
using System.Collections.Generic;
using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace questline.Client;

/// <summary>
/// The quest book. The Quests tab has the chapter rail, the chapter's tree on the canvas and
/// the selected quest's details; the Reference tab hosts the vanilla handbook's item pages.
/// Everything is recomposed when the server sends new state, so each chapter's view is kept
/// here, not in the canvas.
/// </summary>
public partial class GuiDialogQuestBook : GuiDialog
{
    const double Gap = 10, ContentY = 70;
    const int QuestsTab = 0, ReferenceTab = 1;

    readonly QuestClient client;
    readonly QuestText text;
    readonly Dictionary<string, CanvasView> views = [];
    readonly Dictionary<string, int[]> picks = [];

    GuiElementQuestCanvas canvas;
    List<QuestDef> canvasQuests = [];
    List<QuestCanvasNode> spikeNodes;
    int tab;
    string questlineId, chapterId, questId;

    public override string ToggleKeyCombinationCode => "questline";

    // Sized to the screen, in unscaled GUI units: the rail and the detail panel take a share
    // within limits, and the canvas gets the rest.
    double ScreenWidth => capi.Render.FrameWidth / RuntimeEnv.GUIScale - 2 * GuiStyle.ElementToDialogPadding - 40;
    double ScreenHeight => capi.Render.FrameHeight / RuntimeEnv.GUIScale - 2 * GuiStyle.ElementToDialogPadding - 40;
    double RailWidth => Math.Clamp(ScreenWidth * 0.16, 150, 220);
    double DetailWidth => Math.Clamp(ScreenWidth * 0.27, 240, 340);
    double CanvasWidth => Math.Clamp(ScreenWidth - RailWidth - DetailWidth - 2 * Gap, 240, 1000);
    double Width => RailWidth + Gap + CanvasWidth + Gap + DetailWidth;
    double ContentHeight => Math.Clamp(ScreenHeight - ContentY, 300, 720);

    public GuiElementQuestCanvas Canvas => canvas;
    public string SelectedChapter => chapterId;
    public string SelectedQuest => questId;

    public GuiDialogQuestBook(ICoreClientAPI capi, QuestClient client) : base(capi)
    {
        this.client = client;
        text = new QuestText(capi, client);
        client.Changed += OnClientChanged;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Compose();
    }

    void OnClientChanged()
    {
        if (IsOpened()) Compose();
    }

    void Compose()
    {
        if (canvas != null && chapterId != null && spikeNodes == null) views[chapterId] = canvas.View;
        SingleComposer?.Dispose();
        canvas = null;

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        GuiTab[] tabs =
        [
            new() { Name = Lang.Get("questline:tab-quests"), DataInt = QuestsTab },
            new() { Name = Lang.Get("questline:tab-reference"), DataInt = ReferenceTab },
        ];

        var composer = capi.Gui.CreateCompo("questline-book", dialogBounds)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(Lang.Get("questline:book-title"), () => TryClose())
            .BeginChildElements(bg)
            .AddHorizontalTabs(tabs, ElementBounds.Fixed(0, 30, 400, 24), OnTabClicked,
                CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");

        if (tab == QuestsTab) ComposeQuests(composer);
        else ComposeReference(composer);

        SingleComposer = composer.EndChildElements().Compose();
        SingleComposer.GetHorizontalTabs("tabs").SetValue(tab, false);

        if (tab == ReferenceTab) InitReferenceScroll();
        else InitDetailScroll();
    }

    void OnTabClicked(int dataInt)
    {
        tab = dataInt;
        Compose();
    }

    #region Quests tab

    List<ChapterDef> VisibleChapters() => client.Registry.Chapters
        .Where(c => c.Questline == questlineId && client.IsVisible(c))
        .ToList();

    void ComposeQuests(GuiComposer composer)
    {
        var railBounds = ElementBounds.Fixed(0, ContentY, RailWidth, ContentHeight);
        var canvasBounds = ElementBounds.Fixed(RailWidth + Gap, ContentY, CanvasWidth, ContentHeight);

        if (spikeNodes != null)
        {
            canvas = new GuiElementQuestCanvas(capi, canvasBounds, spikeNodes);
            composer
                .AddStaticText("Benchmark tree", CairoFont.WhiteSmallText(), railBounds)
                .AddInteractiveElement(canvas, "canvas");
            ComposeDetail(composer, null);
            return;
        }

        if (!client.Ready || client.Registry.Chapters.Count == 0)
        {
            composer.AddStaticText(Lang.Get("questline:no-quests"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, ContentY, Width, 60));
            return;
        }

        var questlines = client.Registry.Questlines.Where(ql => client.Registry.Chapters.Any(c => c.Questline == ql.Id)).ToList();
        if (questlineId == null || questlines.All(ql => ql.Id != questlineId)) questlineId = questlines.FirstOrDefault()?.Id;

        double railTop = ContentY;
        if (questlines.Count > 1)
        {
            composer.AddDropDown(
                questlines.Select(ql => ql.Id).ToArray(),
                questlines.Select(ql => text.QuestlineTitle(ql.Id)).ToArray(),
                questlines.FindIndex(ql => ql.Id == questlineId),
                (code, _) => { questlineId = code; chapterId = questId = null; Compose(); },
                ElementBounds.Fixed(0, ContentY, RailWidth, 26), "questlines");
            railTop += 34;
        }

        var chapters = VisibleChapters();
        if (chapterId == null || chapters.All(c => c.Id != chapterId)) chapterId = chapters.FirstOrDefault()?.Id;

        composer.AddRichtext(RailComponents(chapters),
            ElementBounds.Fixed(0, railTop, RailWidth, ContentHeight - (railTop - ContentY)), "rail");

        var chapter = chapterId == null ? null : client.Registry.ChaptersById[chapterId];
        canvasQuests = chapter?.Quests.Where(client.IsVisible).ToList() ?? [];
        if (questId != null && canvasQuests.All(q => q.Id != questId)) questId = null;

        canvas = new GuiElementQuestCanvas(capi, canvasBounds, BuildNodes(canvasQuests))
        {
            SelectedIndex = canvasQuests.FindIndex(q => q.Id == questId),
            OnNodeClicked = i => SelectQuest(canvasQuests[i].Id),
        };
        if (chapterId != null && views.TryGetValue(chapterId, out var view)) canvas.View = view;
        composer.AddInteractiveElement(canvas, "canvas");

        ComposeDetail(composer, client.Registry.Get(questId));
    }

    RichTextComponentBase[] RailComponents(List<ChapterDef> chapters)
    {
        var comps = new List<RichTextComponentBase>();
        foreach (var chapter in chapters)
        {
            var quests = chapter.Quests.Where(client.IsVisible).ToList();
            int done = quests.Count(q => client.StateOf(q.Id) >= QuestState.Claimable);
            var font = CairoFont.WhiteSmallText();
            if (chapter.Id == chapterId) font = font.WithWeight(Cairo.FontWeight.Bold).WithColor(GuiStyle.ActiveButtonTextColor);

            if (text.StackFor(chapter.Icon) is { } icon)
            {
                comps.Add(new ItemstackTextComponent(capi, icon, 28, 4, EnumFloat.Inline) { ShowStacksize = false, VerticalAlign = EnumVerticalAlign.Middle });
            }
            string id = chapter.Id;
            comps.Add(new LinkTextComponent(capi, text.ChapterTitle(id), font, _ => SelectChapter(id)) { VerticalAlign = EnumVerticalAlign.Middle });
            comps.Add(new RichTextComponent(capi, $"  {done}/{quests.Count}\n", CairoFont.WhiteDetailText()) { VerticalAlign = EnumVerticalAlign.Middle });
        }
        return comps.ToArray();
    }

    List<QuestCanvasNode> BuildNodes(List<QuestDef> quests)
    {
        var index = quests.Select((q, i) => (q.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return quests.Select(q => new QuestCanvasNode
        {
            Id = q.Id,
            Title = text.Title(q.Id),
            X = q.Pos.Length > 0 ? q.Pos[0] : 0,
            Y = q.Pos.Length > 1 ? q.Pos[1] : 0,
            Icon = text.StackFor(q.Icon ?? q.Tasks.FirstOrDefault()?.Target) is { } stack ? new DummySlot(stack) : null,
            State = client.StateOf(q.Id),
            // Only prerequisites drawn in this chapter get a line.
            Prereqs = q.Requires?.QuestIds().Where(index.ContainsKey).Select(id => index[id]).Distinct().ToArray() ?? [],
        }).ToList();
    }

    public void SelectChapter(string id)
    {
        chapterId = id;
        questId = null;
        tab = QuestsTab;
        if (IsOpened()) Compose();
        else TryOpen();
    }

    public void SelectQuest(string id)
    {
        questId = id;
        if (client.Registry?.Get(id) is { } quest) chapterId = quest.ChapterId;
        tab = QuestsTab;
        if (IsOpened()) Compose();
        else TryOpen();
    }

    #endregion

    #region Detail panel

    void ComposeDetail(GuiComposer composer, QuestDef quest)
    {
        double x = RailWidth + Gap + CanvasWidth + Gap;
        var textBounds = ElementBounds.Fixed(x + 3, ContentY + 3, DetailWidth - 30, ContentHeight - 46);
        var clipBounds = textBounds.ForkBoundingParent();
        var insetBounds = textBounds.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
        var scrollbarBounds = clipBounds.CopyOffsetedSibling(textBounds.fixedWidth + 6, -3, 0, 6).WithFixedWidth(16);

        composer
            .BeginClip(clipBounds)
                .AddInset(insetBounds, 3)
                .AddRichtext(DetailComponents(quest), textBounds, "detail")
            .EndClip()
            .AddVerticalScrollbar(OnDetailScroll, scrollbarBounds, "detailscroll");

        if (quest != null && client.StateOf(quest.Id) == QuestState.Claimable)
        {
            composer.AddSmallButton(Lang.Get("questline:claim"), OnClaim, ElementBounds.Fixed(x + DetailWidth - 110, ContentY + ContentHeight - 30, 110, 26), EnumButtonStyle.Normal, "claim");
        }
    }

    RichTextComponentBase[] DetailComponents(QuestDef quest)
    {
        var small = CairoFont.WhiteSmallText();
        if (quest == null)
        {
            return [new RichTextComponent(capi, Lang.Get("questline:pick-a-quest"), small)];
        }

        var state = client.StateOf(quest.Id);
        var tasks = client.TasksOf(quest);
        var comps = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, text.Title(quest.Id) + "\n", CairoFont.WhiteMediumText()),
            new RichTextComponent(capi, text.State(state) + "\n", CairoFont.WhiteDetailText().WithColor(StateColor(state))),
        };
        if (state == QuestState.Locked && quest.Requires != null)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:requires", text.Describe(quest.Requires)) + "\n", CairoFont.WhiteDetailText()));
        }
        comps.Add(new ClearFloatTextComponent(capi, 6));
        if (quest.Page.Count > 0)
        {
            foreach (var block in quest.Page) AddPageBlock(comps, block, small);
        }
        else if (text.Description(quest.Id) is { } desc)
        {
            comps.AddRange(VtmlUtil.Richtextify(capi, desc + "\n", small, OnLinkClicked));
        }

        comps.Add(new ClearFloatTextComponent(capi, 8));
        comps.Add(new RichTextComponent(capi, Lang.Get("questline:tasks") + "\n", small.Clone().WithWeight(Cairo.FontWeight.Bold)));
        for (int i = 0; i < quest.Tasks.Count; i++)
        {
            var task = quest.Tasks[i];
            if (task.Type != "checkbox" && task.Type != "kill" && text.StackFor(task.Target) is { } stack)
            {
                comps.Add(new ItemstackTextComponent(capi, stack, 30, 6, EnumFloat.Inline, OnStackClicked) { ShowStacksize = false, VerticalAlign = EnumVerticalAlign.Middle });
            }
            bool done = tasks[i] >= task.Count;
            string progress = task.Type == "checkbox" ? (done ? " ✓" : "") : $"  {tasks[i]}/{task.Count}";
            comps.Add(new RichTextComponent(capi, text.TaskLine(task) + progress, done ? small.Clone().WithColor(StateColor(QuestState.Done)) : small) { VerticalAlign = EnumVerticalAlign.Middle });

            int taskIndex = i;
            if (task.Type == "checkbox" && !done && state is QuestState.Available or QuestState.InProgress)
            {
                comps.Add(new LinkTextComponent(capi, " " + Lang.Get("questline:mark-done"), small, _ => client.Check(quest.Id, taskIndex)));
            }
            comps.Add(new ClearFloatTextComponent(capi, 2));
        }

        if (quest.Rewards.Count > 0)
        {
            comps.Add(new ClearFloatTextComponent(capi, 8));
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:rewards") + "\n", small.Clone().WithWeight(Cairo.FontWeight.Bold)));
            int choiceIndex = 0;
            foreach (var reward in quest.Rewards)
            {
                if (reward.Type == "choice") AddChoice(comps, quest, reward, choiceIndex++);
                else AddReward(comps, reward, small);
            }
        }

        return comps.ToArray();
    }

    /// <summary>One block of a quest's page. A block the client can't show is skipped; the server log says why.</summary>
    void AddPageBlock(List<RichTextComponentBase> comps, PageBlock block, CairoFont font)
    {
        switch (block.Type)
        {
            case "text" when block.Lang != null:
                comps.AddRange(VtmlUtil.Richtextify(capi, Lang.Get(block.Lang) + "\n", font, OnLinkClicked));
                break;

            case "image" when block.Src != null:
                var image = new ImageTextComponent(capi, new AssetLocation(PageBlock.TexturePath(block.Src)), block.Width) { UnscaledMarginTop = 4 };
                if (!image.Loaded) break;
                comps.Add(image);
                if (block.Caption != null)
                {
                    comps.Add(new RichTextComponent(capi, Lang.Get(block.Caption) + "\n", CairoFont.WhiteDetailText().WithSlant(Cairo.FontSlant.Italic)));
                }
                break;

            case "items":
                foreach (var code in block.Items)
                {
                    if (text.StackFor(code) is { } stack)
                    {
                        comps.Add(new ItemstackTextComponent(capi, stack, 36, 4, EnumFloat.Inline, OnStackClicked));
                    }
                }
                comps.Add(new ClearFloatTextComponent(capi, 4));
                break;

            case "recipe" when text.StackFor(block.Output) is { } output:
                var recipes = capi.World.GridRecipes
                    .Where(r => r.Output.ResolvedItemStack?.Collectible == output.Collectible)
                    .ToArray();
                if (recipes.Length == 0) break;
                comps.Add(new SlideshowGridRecipeTextComponent(capi, recipes, 40, EnumFloat.None, OnStackClicked, AllStacks));
                comps.Add(new ClearFloatTextComponent(capi, 4));
                break;
        }
    }

    void AddReward(List<RichTextComponentBase> comps, RewardDef reward, CairoFont font)
    {
        if (text.StackFor(reward.Item, reward.Count) is { } stack)
        {
            comps.Add(new ItemstackTextComponent(capi, stack, 30, 6, EnumFloat.Inline, OnStackClicked) { VerticalAlign = EnumVerticalAlign.Middle });
        }
        comps.Add(new RichTextComponent(capi, text.RewardLine(reward), font) { VerticalAlign = EnumVerticalAlign.Middle });
        comps.Add(new ClearFloatTextComponent(capi, 2));
    }

    void AddChoice(List<RichTextComponentBase> comps, QuestDef quest, RewardDef choice, int choiceIndex)
    {
        comps.Add(new RichTextComponent(capi, Lang.Get("questline:pick-one") + "\n", CairoFont.WhiteDetailText()));
        int picked = Picks(quest)[choiceIndex];
        for (int j = 0; j < choice.Options.Count; j++)
        {
            var option = choice.Options[j];
            int optionIndex = j;
            if (text.StackFor(option.Item, option.Count) is { } stack)
            {
                comps.Add(new ItemstackTextComponent(capi, stack, 30, 6, EnumFloat.Inline, OnStackClicked) { VerticalAlign = EnumVerticalAlign.Middle });
            }
            var font = CairoFont.WhiteSmallText();
            if (picked == j) font = font.WithWeight(Cairo.FontWeight.Bold).WithColor(GuiStyle.ActiveButtonTextColor);
            comps.Add(new LinkTextComponent(capi, (picked == j ? "● " : "○ ") + text.RewardLine(option), font, _ =>
            {
                Picks(quest)[choiceIndex] = optionIndex;
                Compose();
            }) { VerticalAlign = EnumVerticalAlign.Middle });
            comps.Add(new ClearFloatTextComponent(capi, 2));
        }
    }

    int[] Picks(QuestDef quest)
    {
        int choices = quest.Rewards.Count(r => r.Type == "choice");
        if (!picks.TryGetValue(quest.Id, out var p) || p.Length != choices)
        {
            picks[quest.Id] = p = Enumerable.Repeat(-1, choices).ToArray();
        }
        return p;
    }

    bool OnClaim()
    {
        var quest = client.Registry.Get(questId);
        if (quest == null) return true;

        var p = Picks(quest);
        if (p.Any(i => i < 0))
        {
            capi.TriggerIngameError(this, "questline-pick", Lang.Get("questline:pick-first"));
            return true;
        }
        client.Claim(quest.Id, p);
        return true;
    }

    static double[] StateColor(QuestState state) => state switch
    {
        QuestState.Done => [0.45, 0.85, 0.45, 1],
        QuestState.Claimable => [1.0, 0.8, 0.25, 1],
        QuestState.InProgress => [0.5, 0.7, 1.0, 1],
        QuestState.Available => [0.9, 0.85, 0.7, 1],
        _ => [0.6, 0.6, 0.6, 1],
    };

    void InitDetailScroll()
    {
        var detail = SingleComposer.GetRichtext("detail");
        if (detail == null) return;
        SingleComposer.GetScrollbar("detailscroll").SetHeights((float)(ContentHeight - 46), (float)detail.Bounds.fixedHeight);
    }

    void OnDetailScroll(float value)
    {
        var detail = SingleComposer.GetRichtext("detail");
        detail.Bounds.fixedY = ContentY + 3 - value;
        detail.Bounds.CalcWorldBounds();
    }

    #endregion

    #region Benchmark

    /// <summary>Shows the 500-node benchmark tree instead of a chapter, for the canvas tests.</summary>
    public void ShowBenchmarkTree()
    {
        spikeNodes ??= SpikeTree.Build(capi);
        tab = QuestsTab;
        if (IsOpened()) Compose();
        else TryOpen();
    }

    /// <summary>Runs the benchmark tour on the canvas. Call after <see cref="ShowBenchmarkTree"/>.</summary>
    public void StartTour(float seconds, Action<CanvasFrameStats> done)
    {
        canvas.StartTour(seconds, stats =>
        {
            capi.Logger.Notification("[questline] canvas tour, {0} nodes: {1}", spikeNodes?.Count ?? 0, stats);
            done(stats);
        });
    }

    public void LeaveBenchmarkTree()
    {
        spikeNodes = null;
        if (IsOpened()) Compose();
    }

    #endregion

    public override void Dispose()
    {
        client.Changed -= OnClientChanged;
        base.Dispose();
    }
}
