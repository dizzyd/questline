using System;
using System.Collections.Generic;
using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// The quest book. The Quests tab has the chapter rail, the chapter's tree on the canvas and
/// the selected quest's details; the Reference tab hosts the vanilla handbook's item pages;
/// the Party tab manages the player's party. Everything is recomposed when the server sends
/// new state, so each chapter's view is kept here, not in the canvas.
///
/// Every tab lays out on one grid: content starts under the tabs at <see cref="ContentY"/>,
/// columns are <see cref="BookStyle.Gap"/> apart, each panel is an inset, and a panel's
/// buttons sit in a row under it, aligned to its edges.
/// </summary>
public partial class GuiDialogQuestBook : GuiDialog
{
    const double TabsY = 30, TabsHeight = 26, ContentY = TabsY + TabsHeight + 10;
    const int QuestsTab = 0, ReferenceTab = 1, PartyTab = 2, ProblemsTab = 3;

    readonly QuestClient client;
    readonly QuestText text;
    readonly Dictionary<string, CanvasView> views = [];
    readonly Dictionary<string, int[]> picks = [];

    GuiElementQuestCanvas canvas;
    GuiElementChapterRail rail;
    List<QuestDef> canvasQuests = [];
    List<QuestCanvasNode> spikeNodes;
    int tab;
    string questlineId, chapterId, questId;
    // The chapter the canvas is showing, whose view is kept when the book recomposes.
    string canvasChapter;

    public override string ToggleKeyCombinationCode => "questline";

    /// <summary>The book's size and columns for the current window; see <see cref="BookLayout"/>.</summary>
    public BookLayout Layout => BookLayout.For(capi.Render.FrameWidth, capi.Render.FrameHeight, RuntimeEnv.GUIScale, ContentY);
    double RailWidth => Layout.RailWidth;
    // The editor's form needs more room than the quest panel; it takes it from the canvas.
    double DetailWidth => editing ? Math.Max(Layout.DetailWidth, Math.Min(380, Layout.Width * 0.36)) : Layout.DetailWidth;
    double CanvasWidth => Width - RailWidth - DetailWidth - 2 * Gap;
    double Width => Layout.Width;
    double ContentHeight => Layout.ContentHeight;
    /// <summary>The height of a panel with a row of buttons under it.</summary>
    double PanelHeight => ContentHeight - ButtonHeight - Gap / 2;
    double ButtonY => ContentY + ContentHeight - ButtonHeight;

    public GuiElementQuestCanvas Canvas => canvas;
    public GuiElementChapterRail Rail => rail;
    public string SelectedChapter => chapterId;
    public string SelectedQuest => questId;

    public GuiDialogQuestBook(ICoreClientAPI capi, QuestClient client) : base(capi)
    {
        this.client = client;
        text = new QuestText(capi, client);
        client.Changed += OnClientChanged;
        client.EditorChanged += OnEditorChanged;
    }

    // A save's reply replaces a draft with nothing unsaved in it. Only the editor's own packet
    // does: the definitions arrive first, with the editor's chapters still the old ones.
    void OnEditorChanged()
    {
        if (editing && !dirty) draft = null;
    }

    /// <summary>
    /// The handbook's layer, which chests, barrels and firepits share, so the book can come up
    /// over them; at the default it would always draw underneath.
    /// </summary>
    public override double DrawOrder => 0.2;

    /// <summary>The width of the A− and A+ buttons beside the tabs.</summary>
    const double TextSizeButton = 34;

    /// <summary>The front-most open dialog, the one a click or key goes to first.</summary>
    public bool OnTop => IsOpened() && capi.Gui.OpenedGuis.FirstOrDefault(d => d.DialogType == EnumDialogType.Dialog) == this;

    /// <summary>Shows the book as it now stands, opening it or, if it's open behind other windows, bringing it to the front.</summary>
    void Show()
    {
        if (IsOpened()) Compose();
        // Already open, this only brings the book to the front.
        TryOpen();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Compose();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        // The editor's 100% ends with the book; the pinned quests go on at the player's size.
        BookStyle.TextScale = client.Settings.TextSize / 100.0;
    }

    void OnClientChanged()
    {
        if (!IsOpened()) return;
        if (editing && InputFocused()) pendingCompose = true;
        else Compose();
    }

    void Compose()
    {
        // The editor's forms are laid out for the game's own sizes; reading uses the player's.
        BookStyle.TextScale = editing ? 1 : client.Settings.TextSize / 100.0;
        // Saved under the chapter the canvas was showing, which may not be the one about to be shown.
        if (canvas != null && canvasChapter != null) views[canvasChapter] = canvas.View;
        canvasChapter = null;
        float detailScroll = tab == QuestsTab ? PanelScroll("detail") : 0;
        SingleComposer?.Dispose();
        canvas = null;
        rail = null;
        panels.Clear();
        afterCompose.Clear();
        inputKeys.Clear();
        if (tab == ProblemsTab && !client.CanEdit) tab = QuestsTab;

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        GuiTab[] tabs =
        [
            new() { Name = Lang.Get("questline:tab-quests"), DataInt = QuestsTab },
            new() { Name = Lang.Get("questline:tab-reference"), DataInt = ReferenceTab },
            new() { Name = Lang.Get("questline:tab-party"), DataInt = PartyTab },
        ];
        if (client.CanEdit) tabs = [.. tabs, new() { Name = Lang.Get("questline:tab-problems", client.Editor.Problems.Count), DataInt = ProblemsTab }];

        var composer = capi.Gui.CreateCompo("questline-book", dialogBounds)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(Lang.Get("questline:book-title"), () => TryClose())
            .BeginChildElements(bg)
            .AddHorizontalTabs(tabs, ElementBounds.Fixed(0, TabsY, Width - 2 * TextSizeButton - 4, TabsHeight), OnTabClicked,
                CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");
        // Text size, at the right end of the tab row; not while editing, which keeps the game's sizes.
        if (!editing)
        {
            composer.AddSmallButton("A−", () => client.StepTextSize(-1), ElementBounds.Fixed(Width - 2 * TextSizeButton - 4, TabsY, TextSizeButton, TabsHeight), EnumButtonStyle.Small, "text-smaller");
            composer.AddSmallButton("A+", () => client.StepTextSize(1), ElementBounds.Fixed(Width - TextSizeButton, TabsY, TextSizeButton, TabsHeight), EnumButtonStyle.Small, "text-larger");
        }
        // The tabs are measured in the regular weight; room for the bold one on the active tab.
        composer.GetHorizontalTabs("tabs").unscaledTabPadding = 12;

        // Every tab keeps the row of buttons at the bottom, so the book is the same size on all of them.
        composer.AddStaticText("", Small(), ElementBounds.Fixed(0, ButtonY, Width, ButtonHeight));

        if (tab == QuestsTab) ComposeQuests(composer);
        else if (tab == PartyTab) ComposeParty(composer);
        else if (tab == ProblemsTab) ComposeProblems(composer);
        else ComposeReference(composer);

        SingleComposer = composer.EndChildElements().Compose();
        SingleComposer.GetHorizontalTabs("tabs").SetValue(tab, false);
        loadingForm = true;
        try
        {
            foreach (var setup in afterCompose) setup();
        }
        finally
        {
            loadingForm = false;
        }
        InitPanels();
        if (detailScroll > 0) RestorePanelScroll("detail", detailScroll);
    }

    void OnTabClicked(int dataInt)
    {
        tab = dataInt;
        Compose();
    }

    /// <summary>Switches to a tab (0 quests, 1 reference, 2 party), opening the book if it's closed.</summary>
    public void SelectTab(int index)
    {
        tab = index;
        Show();
    }

    #region Quests tab

    List<ChapterDef> VisibleChapters() => client.Registry.Chapters
        .Where(c => c.Questline == questlineId && client.IsVisible(c))
        .ToList();

    void ComposeQuests(GuiComposer composer)
    {
        if (editing && spikeNodes == null && client.Ready)
        {
            ComposeEditing(composer);
            return;
        }

        // Editors get a row of buttons under the canvas, starting with Edit.
        var canvasBounds = ElementBounds.Fixed(RailWidth + Gap, ContentY, CanvasWidth, client.CanEdit ? PanelHeight : ContentHeight);

        if (spikeNodes != null)
        {
            canvas = new GuiElementQuestCanvas(capi, canvasBounds, spikeNodes);
            rail = new GuiElementChapterRail(capi, ElementBounds.Fixed(0, ContentY, RailWidth, ContentHeight),
                [new("spike", "Benchmark tree", null, 0, spikeNodes.Count)], "spike", _ => { });
            composer.AddInteractiveElement(rail, "rail").AddInteractiveElement(canvas, "canvas");
            ComposeDetail(composer, null);
            return;
        }

        if (!client.Ready || client.Registry.Chapters.Count == 0)
        {
            composer.AddInset(ElementBounds.Fixed(0, ContentY, Width, ContentHeight), 3);
            composer.AddStaticText(Lang.Get("questline:no-quests"), Body().WithColor(Muted),
                ElementBounds.Fixed(Pad * 2, ContentY + Pad * 2, Math.Min(560, Width - Pad * 4), 80));
            return;
        }

        // The questlines with chapters, and the player's own plans as one more when there are any.
        var questlines = client.Registry.Questlines.Where(ql => client.Registry.Chapters.Any(c => c.Questline == ql.Id)).Select(ql => ql.Id).ToList();
        if (PlansAvailable) questlines.Add(PlansQuestline);
        if (questlineId == null || !questlines.Contains(questlineId)) questlineId = questlines.FirstOrDefault();

        double railTop = ContentY;
        if (questlines.Count > 1)
        {
            composer.AddDropDown(
                questlines.ToArray(),
                questlines.Select(id => id == PlansQuestline ? Lang.Get("questline:plans-title") : text.QuestlineTitle(id)).ToArray(),
                questlines.IndexOf(questlineId),
                (code, _) => { questlineId = code; chapterId = questId = null; Compose(); },
                ElementBounds.Fixed(0, ContentY, RailWidth, ButtonHeight), "questlines");
            railTop += ButtonHeight + Gap / 2;
        }

        if (questlineId == PlansQuestline)
        {
            ComposePlans(composer, railTop);
            return;
        }

        if (client.CanEdit)
        {
            AddRowButton(composer, Lang.Get("questline:edit-start"), RailWidth + Gap, false, () => { SetEditing(true); return true; }, "edit-start");
        }

        var chapters = VisibleChapters();
        if (chapterId == null || chapters.All(c => c.Id != chapterId)) chapterId = chapters.FirstOrDefault()?.Id;

        rail = new GuiElementChapterRail(capi, ElementBounds.Fixed(0, railTop, RailWidth, ContentHeight - (railTop - ContentY)),
            chapters.Select(RailRow).ToList(), chapterId, SelectChapter);
        composer.AddInteractiveElement(rail, "rail");

        var chapter = chapterId == null ? null : client.Registry.ChaptersById[chapterId];
        canvasQuests = chapter?.Quests.Where(client.IsVisible).ToList() ?? [];
        if (questId != null && canvasQuests.All(q => q.Id != questId)) questId = null;

        var background = chapter?.Background == null ? null : new AssetLocation(PageBlock.TexturePath(chapter.Background));
        var nodes = BuildNodes(canvasQuests);
        MarkAges(nodes, canvasQuests, chapter);
        var stubs = AddStubs(nodes, canvasQuests);
        canvas = new GuiElementQuestCanvas(capi, canvasBounds, nodes, BuildArt(chapter), background)
        {
            SelectedIndex = canvasQuests.FindIndex(q => q.Id == questId),
            // A stub takes the book to its quest, in its own chapter.
            OnNodeClicked = i => SelectQuest(i < canvasQuests.Count ? canvasQuests[i].Id : stubs[i - canvasQuests.Count]),
        };
        if (chapterId != null && views.TryGetValue(chapterId, out var view)) canvas.View = view;
        canvasChapter = chapterId;
        composer.AddInteractiveElement(canvas, "canvas");

        ComposeDetail(composer, client.Registry.Get(questId));
    }

    GuiElementChapterRail.Row RailRow(ChapterDef chapter)
    {
        var quests = chapter.Quests.Where(client.IsVisible).ToList();
        int done = quests.Count(q => client.StateOf(q.Id) >= QuestState.Claimable);
        return new(chapter.Id, text.ChapterTitle(chapter.Id), ChapterIcon(chapter), done, quests.Count) { Closed = client.IsClosed(chapter) };
    }

    /// <summary>A chapter's icon; one without an icon of its own shows its first quest's.</summary>
    ItemSlot ChapterIcon(ChapterDef chapter)
    {
        var icon = text.StackFor(chapter.Icon) ?? text.StackFor(chapter.Quests.Select(q => q.Icon ?? q.Tasks.FirstOrDefault()?.Target).FirstOrDefault(c => c != null));
        return icon == null ? null : new DummySlot(icon);
    }

    /// <summary>
    /// Badges the quests that come from a later age than most of their chapter with that age, so
    /// a chapter's late branch stands out. An age's own chapter has none: its quests are that age.
    /// </summary>
    void MarkAges(List<QuestCanvasNode> nodes, List<QuestDef> quests, ChapterDef chapter)
    {
        if (chapter == null || chapter.Age || quests.Count == 0) return;
        var ages = quests.Select(client.Registry.AgeOf).ToList();
        // The chapter's own age: the one most of its quests have, the earlier of two as common.
        int usual = ages.GroupBy(a => a?.Order ?? int.MinValue)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        for (int i = 0; i < quests.Count; i++)
        {
            if (ages[i] is not { } age || age.Order <= usual) continue;
            nodes[i].AgeIcon = ChapterIcon(age);
            nodes[i].AgeTitle = text.ChapterTitle(age.Id);
        }
    }

    List<QuestCanvasNode> BuildNodes(List<QuestDef> quests, System.Func<string, string> title = null)
    {
        title ??= text.Title;
        var index = quests.Select((q, i) => (q.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return quests.Select(q => new QuestCanvasNode
        {
            Id = q.Id,
            Title = title(q.Id),
            X = q.Pos.Length > 0 ? q.Pos[0] : 0,
            Y = q.Pos.Length > 1 ? q.Pos[1] : 0,
            Icon = IconFor(q),
            State = client.StateOf(q.Id),
            Shape = q.Shape,
            Size = Math.Clamp(q.Size, 0.5f, 3f),
            // Only prerequisites drawn in this chapter get a line.
            Prereqs = q.Requires?.QuestIds().Where(index.ContainsKey).Select(id => index[id]).Distinct().ToArray() ?? [],
        }).ToList();
    }

    ItemSlot IconFor(QuestDef q) => text.StackFor(q.Icon ?? q.Tasks.FirstOrDefault()?.Target) is { } stack ? new DummySlot(stack) : null;

    List<CanvasArt> BuildArt(ChapterDef chapter)
    {
        var list = new List<CanvasArt>();
        foreach (var a in chapter?.Art ?? [])
        {
            int color = ArtColor(a.Color, a.Type == "arrow" ? ColorUtil.ColorFromRgba(200, 180, 140, 255) : ColorUtil.ColorFromRgba(235, 225, 200, 255));
            var item = new CanvasArt
            {
                Type = a.Type,
                X = a.Pos.ElementAtOrDefault(0),
                Y = a.Pos.ElementAtOrDefault(1),
                ToX = a.To?.ElementAtOrDefault(0) ?? 0,
                ToY = a.To?.ElementAtOrDefault(1) ?? 0,
                Width = a.Size.ElementAtOrDefault(0),
                Height = a.Size.ElementAtOrDefault(1),
                Src = a.Src == null ? null : new AssetLocation(PageBlock.TexturePath(a.Src)),
                Text = a.Text == null ? null : text.Get(a.Text),
                Scale = Math.Clamp(a.Scale, 0.5f, 4f),
                Color = color,
                TextColor = ArtRgba(a.Color, [0.92, 0.88, 0.78, 1]),
                LineWidth = Math.Clamp(a.Width, 1, 20),
            };
            if (a.Type is "image" or "text" || a.To != null) list.Add(item);
        }
        return list;
    }

    /// <summary>#rrggbb or #rrggbbaa as the mesh colour the canvas wants, or <paramref name="fallback"/>.</summary>
    static int ArtColor(string hex, int fallback) => ArtRgba(hex, null) is { } c
        ? ColorUtil.ColorFromRgba((int)(c[0] * 255), (int)(c[1] * 255), (int)(c[2] * 255), (int)(c[3] * 255))
        : fallback;

    /// <summary>#rrggbb or #rrggbbaa as RGBA from 0 to 1, or <paramref name="fallback"/>.</summary>
    static double[] ArtRgba(string hex, double[] fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length is not (7 or 9)) return fallback;
        try
        {
            var c = ColorUtil.Hex2Doubles(hex);
            return [c[0], c[1], c[2], c.Length > 3 ? c[3] : 1];
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Forgets where each chapter was panned and zoomed to, so each opens on its fitted view again.</summary>
    public void ForgetViews()
    {
        views.Clear();
        canvasChapter = null;
    }

    public void SelectChapter(string id)
    {
        Remember();
        chapterId = id;
        questId = null;
        tab = QuestsTab;
        Show();
    }

    public void SelectQuest(string id)
    {
        Remember();
        questId = id;
        // A prerequisite can be in another questline's chapter; the book follows it there.
        if (client.Registry?.Get(id) is { } quest)
        {
            chapterId = quest.ChapterId;
            questlineId = client.Registry.ChaptersById[quest.ChapterId].Questline;
        }
        tab = QuestsTab;
        Show();
    }

    #endregion

    #region History

    /// <summary>Hotkey code for stepping back, Backspace unless the player rebinds it.</summary>
    public const string BackHotkey = "questline-back";

    /// <summary>A place on the Quests tab: the questline, the chapter and the quest selected in it.</summary>
    record Place(string Questline, string Chapter, string Quest);

    /// <summary>Where the Quests tab has been and, after stepping back, where it was going; as a browser keeps them.</summary>
    readonly Stack<Place> placesBack = new(), placesForward = new();

    Place Here => new(questlineId, chapterId, questId);

    public int PlacesBehind => placesBack.Count;

    /// <summary>Keeps where the Quests tab is, before the book goes somewhere else.</summary>
    void Remember()
    {
        if (tab != QuestsTab || chapterId == null) return;
        if (placesBack.Count == 0 || placesBack.Peek() != Here) placesBack.Push(Here);
        placesForward.Clear();
    }

    /// <summary>Goes back to the place before this one on the Quests tab; false when there's none.</summary>
    public bool StepBack() => Step(placesBack, placesForward);

    public bool StepForward() => Step(placesForward, placesBack);

    bool Step(Stack<Place> from, Stack<Place> to)
    {
        // A place can stop existing (a reload, a quest gone hidden); skip past it.
        while (from.Count > 0)
        {
            var place = from.Pop();
            if (place == Here || place.Chapter == null || !client.Registry.ChaptersById.ContainsKey(place.Chapter)) continue;
            to.Push(Here);
            (questlineId, chapterId, questId) = (place.Questline, place.Chapter, place.Quest);
            tab = QuestsTab;
            Show();
            return true;
        }
        return false;
    }

    /// <summary>The back key: back a place on the Quests tab, back a page on the Reference tab. A focused text field has it first.</summary>
    public override void OnKeyDown(KeyEvent args)
    {
        base.OnKeyDown(args);
        if (args.Handled || capi.Input.GetHotKeyByCode(BackHotkey)?.DidPress(args, capi.World, capi.World.Player, true) != true) return;
        args.Handled = Back();
    }

    /// <summary>The mouse's back button does the same, over the book.</summary>
    public override void OnMouseDown(MouseEvent args)
    {
        if (args.Button == EnumMouseButton.Button4 && SingleComposer?.Bounds.PointInside(args.X, args.Y) == true)
        {
            args.Handled = Back();
            return;
        }
        base.OnMouseDown(args);
    }

    bool Back() => tab switch
    {
        QuestsTab => StepBack(),
        ReferenceTab => CanGoBack && GoBack(),
        _ => false,
    };

    #endregion

    #region Detail panel

    void ComposeDetail(GuiComposer composer, QuestDef quest)
    {
        double x = RailWidth + Gap + CanvasWidth + Gap;
        AddTextPanel(composer, "detail", x, ContentY, DetailWidth, PanelHeight, DetailComponents(quest));
        if (quest == null) return;

        var state = client.StateOf(quest.Id);
        if (state != QuestState.Done)
        {
            bool pinned = client.IsPinned(quest.Id);
            AddRowButton(composer, Lang.Get(pinned ? "questline:unpin" : "questline:pin"), x, false, () =>
            {
                if (pinned) client.Unpin(quest.Id);
                else if (!client.Pin(quest.Id)) capi.TriggerIngameError(this, "questline-pins", Lang.Get("questline:pins-full", ClientSettings.MaxPins));
                return true;
            }, "pin");
        }
        if (state == QuestState.Claimable)
        {
            AddRowButton(composer, Lang.Get("questline:claim"), x + DetailWidth, true, OnClaim, "claim", primary: true);
        }
    }

    RichTextComponentBase[] DetailComponents(QuestDef quest)
    {
        if (quest == null)
        {
            var pick = new RichTextComponent(capi, Lang.Get("questline:pick-a-quest"), Body().WithColor(Muted));
            return spikeNodes == null && chapterId != null && client.Registry.ChaptersById.TryGetValue(chapterId, out var chapter) && client.IsClosed(chapter)
                ? [.. ChapterNeeds(chapter), new RuleTextComponent(capi), pick]
                : [pick];
        }

        var state = client.StateOf(quest.Id);
        var tasks = client.TasksOf(quest);
        var comps = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, text.Title(quest.Id) + "\n", Title()),
            new RichTextComponent(capi, text.State(state), Small().WithColor(StateColor(state)).WithWeight(Cairo.FontWeight.Bold)),
        };
        if (quest.Mode != QuestMode.Individual)
        {
            string shared = quest.Mode == QuestMode.Server ? (quest.Claim == ClaimMode.First ? "questline:mode-server-first" : "questline:mode-server")
                : client.InParty ? "questline:mode-party" : "questline:mode-party-alone";
            comps.Add(new RichTextComponent(capi, "   " + Lang.Get(shared), MutedSmall()));
        }
        // In a chapter on one theme, the age the quest really belongs to.
        if (client.Registry.AgeOf(quest) is { } age && age.Id != quest.ChapterId)
        {
            comps.Add(new RichTextComponent(capi, "  · " + text.ChapterTitle(age.Id), MutedSmall()));
        }
        comps.Add(new RichTextComponent(capi, "\n", Small()));
        if (state == QuestState.Locked && quest.Requires != null) AddNeeds(comps, quest);

        var page = new List<RichTextComponentBase>();
        if (quest.Page.Count > 0)
        {
            foreach (var block in quest.Page) AddPageBlock(page, block, Body());
        }
        else if (text.Description(quest.Id) is { } desc)
        {
            page.AddRange(VtmlUtil.Richtextify(capi, desc + "\n", Body(), OnLinkClicked));
        }
        if (page.Count > 0)
        {
            comps.Add(new RuleTextComponent(capi));
            comps.AddRange(page);
        }

        comps.Add(new RuleTextComponent(capi));
        comps.Add(new RichTextComponent(capi, Lang.Get("questline:tasks").ToUpperInvariant() + "\n", Heading()));
        for (int i = 0; i < quest.Tasks.Count; i++)
        {
            var task = quest.Tasks[i];
            bool done = tasks[i] >= task.Count;
            if (task.Type == "checkbox")
            {
                // The box stands where an item's icon would, and ticking it is the task.
                int taskIndex = i;
                bool open = state is QuestState.Available or QuestState.InProgress;
                comps.Add(new TickBoxComponent(capi, IconRowSize, 8, done, open ? () => client.Check(quest.Id, taskIndex) : null) { VerticalAlign = EnumVerticalAlign.Middle });
                comps.Add(new RichTextComponent(capi, text.TaskLine(task), done ? Body().WithColor(Good) : Body()) { VerticalAlign = EnumVerticalAlign.Middle });
            }
            else
            {
                AddIconRow(comps, task.OnCollectible ? text.StackFor(task.Target) : null);
                comps.Add(new RichTextComponent(capi, text.TaskLine(task), done ? Body().WithColor(Good) : Body()) { VerticalAlign = EnumVerticalAlign.Middle });
                comps.Add(new RichTextComponent(capi, $"   {tasks[i]}\u00a0/\u00a0{task.Count}", done ? Small().WithColor(Good) : MutedSmall()) { VerticalAlign = EnumVerticalAlign.Middle });
            }
            comps.Add(new ClearFloatTextComponent(capi, 4));
        }

        if (quest.Rewards.Count > 0)
        {
            comps.Add(new RuleTextComponent(capi));
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:rewards").ToUpperInvariant() + "\n", Heading()));
            int choiceIndex = 0;
            foreach (var reward in quest.Rewards)
            {
                if (reward.Type == "choice") AddChoice(comps, quest, reward, choiceIndex++);
                else AddReward(comps, reward);
            }
        }

        return comps.ToArray();
    }

    const double IconRowSize = 26;

    /// <summary>A row's leading icon, or the same space left blank, so every row's text lines up.</summary>
    void AddIconRow(List<RichTextComponentBase> comps, ItemStack stack)
    {
        if (stack != null)
        {
            comps.Add(new ItemstackTextComponent(capi, stack, IconRowSize, 8, EnumFloat.Inline, OnStackClicked) { ShowStacksize = false, VerticalAlign = EnumVerticalAlign.Middle });
        }
        else
        {
            comps.Add(new RichTextComponent(capi, " ", Body()) { PaddingLeft = IconRowSize + 4, VerticalAlign = EnumVerticalAlign.Middle });
        }
    }

    /// <summary>One block of a quest's page. A block the client can't show is skipped; the server log says why.</summary>
    void AddPageBlock(List<RichTextComponentBase> comps, PageBlock block, CairoFont font)
    {
        switch (block.Type)
        {
            case "text" when block.Lang != null:
                comps.AddRange(VtmlUtil.Richtextify(capi, text.Get(block.Lang) + "\n", font, OnLinkClicked));
                comps.Add(new ClearFloatTextComponent(capi, 6));
                break;

            case "image" when block.Src != null:
                var image = new ImageTextComponent(capi, new AssetLocation(PageBlock.TexturePath(block.Src)), block.Width) { UnscaledMarginTop = 4 };
                if (!image.Loaded) break;
                comps.Add(image);
                if (block.Caption != null)
                {
                    comps.Add(new RichTextComponent(capi, text.Get(block.Caption) + "\n", MutedSmall().WithSlant(Cairo.FontSlant.Italic).WithOrientation(EnumTextOrientation.Center)));
                }
                comps.Add(new ClearFloatTextComponent(capi, 6));
                break;

            case "items":
                foreach (var code in block.Items)
                {
                    if (text.StackFor(code) is { } stack)
                    {
                        comps.Add(new ItemstackTextComponent(capi, stack, 36, 4, EnumFloat.Inline, OnStackClicked));
                    }
                }
                comps.Add(new ClearFloatTextComponent(capi, 6));
                break;

            case "recipe" when text.StackFor(block.Output) is { } output:
                var recipes = capi.World.GridRecipes
                    .Where(r => r.Output.ResolvedItemStack?.Collectible == output.Collectible)
                    .ToArray();
                if (recipes.Length == 0) break;
                comps.Add(new SlideshowGridRecipeTextComponent(capi, recipes, 40, EnumFloat.None, OnStackClicked, AllStacks));
                comps.Add(new ClearFloatTextComponent(capi, 6));
                break;
        }
    }

    void AddReward(List<RichTextComponentBase> comps, RewardDef reward)
    {
        AddIconRow(comps, reward.Type == "item" ? text.StackFor(reward.Item, reward.Count) : null);
        comps.Add(new RichTextComponent(capi, text.RewardLine(reward), Body()) { VerticalAlign = EnumVerticalAlign.Middle });
        comps.Add(new ClearFloatTextComponent(capi, 4));
    }

    void AddChoice(List<RichTextComponentBase> comps, QuestDef quest, RewardDef choice, int choiceIndex)
    {
        comps.Add(new RichTextComponent(capi, Lang.Get("questline:pick-one") + "\n", MutedSmall()));
        int picked = Picks(quest)[choiceIndex];
        for (int j = 0; j < choice.Options.Count; j++)
        {
            var option = choice.Options[j];
            int optionIndex = j;
            AddIconRow(comps, text.StackFor(option.Item, option.Count));
            var font = picked == j ? Body().WithWeight(Cairo.FontWeight.Bold).WithColor(Accent) : Body();
            comps.Add(new LinkTextComponent(capi, (picked == j ? "● " : "○ ") + text.RewardLine(option), font, _ =>
            {
                Picks(quest)[choiceIndex] = optionIndex;
                Compose();
            }) { VerticalAlign = EnumVerticalAlign.Middle });
            comps.Add(new ClearFloatTextComponent(capi, 4));
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

    #endregion

    #region Benchmark

    /// <summary>Shows the 500-node benchmark tree instead of a chapter, for the canvas tests.</summary>
    public void ShowBenchmarkTree()
    {
        spikeNodes ??= SpikeTree.Build(capi);
        tab = QuestsTab;
        Show();
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
        client.EditorChanged -= OnEditorChanged;
        base.Dispose();
    }
}
