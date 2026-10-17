using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// Edit mode, for players with questline.edit. The canvas takes the gestures (drag to move,
/// right-drag from one quest to another to toggle a prerequisite, right-click empty canvas to
/// add a quest) and the detail column becomes a form with Quest, Tasks and Rewards pages.
///
/// The book edits a draft of the shown chapter, cloned from the editor's raw copy so broken
/// quests are there to fix. Canvas gestures save at once; form changes save with the Save
/// button, or when another quest is picked. A save sends the whole chapter and its text; the
/// server writes the working copy, reloads, and the reply replaces the draft.
/// </summary>
public partial class GuiDialogQuestBook
{
    const int QuestPage = 0, TasksPage = 1, RewardsPage = 2, OptionsPage = 3;
    const double FieldHeight = 28, LabelHeight = 18, RowGap = 6;

    static readonly string[] TaskTypes = ["obtain", "kill", "mount", "place", "break", "craft", "knap", "clayform", "smith", "cook", "smelt", "trade", "custom", "checkbox"];
    static readonly string[] RewardTypes = ["item", "stage"];
    static readonly string[] SizeValues = ["1", "1.25", "1.5", "2"];

    bool editing, dirty, pendingCompose, loadingForm;
    ChapterDef draft;
    readonly Dictionary<string, string> draftText = [];
    int editPage;
    string armedDelete;
    // Set up once the composer exists: text inputs only take a value after composing.
    readonly List<Action> afterCompose = [];
    readonly List<string> inputKeys = [];

    public bool Editing => editing;
    /// <summary>The chapter being edited, as it stands in the book; null when not editing.</summary>
    public ChapterDef Draft => editing ? draft : null;
    public bool Dirty => dirty;

    /// <summary>Turns edit mode on or off. Leaving saves anything unsaved.</summary>
    public void SetEditing(bool on)
    {
        if (!on && dirty) SaveDraft();
        editing = on && client.CanEdit;
        draft = null;
        tab = QuestsTab;
        Show();
    }

    /// <summary>Shows a page of the quest form: 0 quest, 1 tasks, 2 rewards, 3 options.</summary>
    public void SelectEditPage(int page)
    {
        editPage = page;
        Compose();
    }

    ChapterDef EnsureDraft()
    {
        if (draft != null && draft.Id == chapterId) return draft;
        draftText.Clear();
        dirty = false;
        armedDelete = null;
        draft = chapterId != null && client.Editor.Chapters.TryGetValue(chapterId, out var raw) ? Clone(raw) : null;
        return draft;
    }

    static T Clone<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));

    static string DomainOf(string id) => id[..id.IndexOf(':')];

    /// <summary>The text for a lang key as the editor has it: typed here, else the server's.</summary>
    string EditText(string key) => draftText.TryGetValue(key, out var value) ? value : text.Has(key) ? text.Get(key) : "";

    void SetEditText(string key, string value)
    {
        if (loadingForm) return;
        draftText[key] = value;
        dirty = true;
    }

    void Changed()
    {
        if (!loadingForm) dirty = true;
    }

    /// <summary>Sends the draft to be saved. The server's reply brings it back as the new draft.</summary>
    public void SaveDraft()
    {
        if (draft == null) return;
        client.SaveChapter(draft, new Dictionary<string, string>(draftText));
        dirty = false;
    }

    bool InputFocused() => SingleComposer != null && inputKeys.Any(k =>
        SingleComposer.GetElement(k) is { HasFocus: true });

    public override void OnRenderGUI(float deltaTime)
    {
        // A change from the server while someone is typing waits until they stop, so the field keeps its focus.
        if (pendingCompose && !InputFocused())
        {
            pendingCompose = false;
            Compose();
        }
        base.OnRenderGUI(deltaTime);
    }

    #region Layout

    List<ChapterDef> EditableChapters()
    {
        var list = client.Editor.Chapters.Values.Where(c => c.Questline == questlineId).ToList();
        if (draft != null && list.All(c => c.Id != draft.Id)) list.Add(draft);
        return list.OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
    }

    void ComposeEditing(GuiComposer composer)
    {
        questlineId ??= client.Registry.Questlines.FirstOrDefault()?.Id ?? QuestRegistry.DefaultQuestline;
        var chapters = EditableChapters();
        if (chapterId == null || chapters.All(c => c.Id != chapterId)) chapterId = chapters.FirstOrDefault()?.Id;
        var chapter = EnsureDraft();

        rail = new GuiElementChapterRail(capi, ElementBounds.Fixed(0, ContentY, RailWidth, ContentHeight),
            chapters.Select(c =>
            {
                int problems = client.Editor.ProblemsOf(c.Id).Count() + c.Quests.Sum(q => client.Editor.ProblemsOf(q.Id).Count());
                var icon = text.StackFor(c.Icon) ?? text.StackFor(c.Quests.Select(q => q.Icon ?? q.Tasks.FirstOrDefault()?.Target).FirstOrDefault(x => x != null));
                return new GuiElementChapterRail.Row(c.Id, NonEmpty(EditText(c.Id + "-title"), c.Id[(c.Id.IndexOf(':') + 1)..]), icon == null ? null : new DummySlot(icon), 0, c.Quests.Count)
                {
                    Note = problems > 0 ? Lang.Get("questline:edit-problems-short", problems) : c.Quests.Count.ToString(),
                    Warn = problems > 0,
                };
            }).ToList(), chapterId, id => { if (dirty) SaveDraft(); SelectChapter(id); });
        composer.AddInteractiveElement(rail, "rail");

        canvasQuests = chapter?.Quests ?? [];
        if (questId != null && canvasQuests.All(q => q.Id != questId)) questId = null;
        var nodes = BuildNodes(canvasQuests, id => NonEmpty(EditText(id + "-title"), id));
        foreach (var n in nodes) n.Problem = client.Editor.ProblemsOf(n.Id).Any();

        var background = chapter?.Background == null ? null : new AssetLocation(PageBlock.TexturePath(chapter.Background));
        double canvasX = RailWidth + Gap;
        canvas = new GuiElementQuestCanvas(capi, ElementBounds.Fixed(canvasX, ContentY, CanvasWidth, PanelHeight), nodes, BuildArt(chapter), background)
        {
            EditMode = true,
            SelectedIndex = canvasQuests.FindIndex(q => q.Id == questId),
            OnNodeClicked = i => { if (dirty) SaveDraft(); SelectQuest(canvasQuests[i].Id); },
            OnNodeMoved = (i, x, y) => { canvasQuests[i].Pos = [x, y]; SaveDraft(); },
            OnLinkDrawn = (from, to) => ToggleLink(canvasQuests[from], canvasQuests[to]),
            OnEmptyRightClicked = AddQuest,
        };
        if (chapterId != null && views.TryGetValue(chapterId, out var view)) canvas.View = view;
        canvasChapter = chapterId;
        composer.AddInteractiveElement(canvas, "canvas");

        double left = canvasX;
        left += AddRowButton(composer, Lang.Get("questline:edit-new-chapter"), left, false, () => { NewChapter(); return true; }, "edit-new-chapter") + Gap / 2;
        AddRowButton(composer, Lang.Get("questline:edit-export"), left, false, () => { client.Export(); return true; }, "edit-export");
        AddRowButton(composer, Lang.Get("questline:edit-done"), canvasX + CanvasWidth, true, () => { SetEditing(false); return true; }, "edit-done", primary: true);

        var quest = canvasQuests.FirstOrDefault(q => q.Id == questId);
        if (quest != null) ComposeQuestForm(composer, quest);
        else ComposeChapterForm(composer, chapter);
    }

    static string NonEmpty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>The x, width and running y of the form inside the detail column.</summary>
    double FormX => RailWidth + Gap + CanvasWidth + Gap + Pad;
    double FormWidth => DetailWidth - 2 * Pad;

    double AddLabel(GuiComposer composer, string label, double y, double x = -1, double width = -1)
    {
        composer.AddStaticText(label, MutedSmall(), ElementBounds.Fixed(x < 0 ? FormX : x, y, width < 0 ? FormWidth : width, LabelHeight));
        return y + LabelHeight;
    }

    void AddInput(GuiComposer composer, string key, ElementBounds bounds, string value, Action<string> onChange, string placeholder = null, bool area = false)
    {
        if (area) composer.AddTextArea(bounds, v => onChange(v), Small(), key);
        else composer.AddTextInput(bounds, v => onChange(v), Small(), key);
        inputKeys.Add(key);
        afterCompose.Add(() =>
        {
            if (area) SingleComposer.GetTextArea(key).SetValue(value ?? "");
            else
            {
                var input = SingleComposer.GetTextInput(key);
                input.SetValue(value ?? "");
                if (placeholder != null) input.SetPlaceHolderText(placeholder);
            }
        });
    }

    void AddFormFrame(GuiComposer composer)
    {
        composer.AddInset(ElementBounds.Fixed(RailWidth + Gap + CanvasWidth + Gap, ContentY, DetailWidth, PanelHeight), 3);
    }

    #endregion

    #region Chapter form

    void ComposeChapterForm(GuiComposer composer, ChapterDef chapter)
    {
        AddFormFrame(composer);
        double y = ContentY + Pad;
        if (chapter == null)
        {
            composer.AddStaticText(Lang.Get("questline:edit-no-chapter"), Body().WithColor(Muted), ElementBounds.Fixed(FormX, y, FormWidth, 80));
            return;
        }

        composer.AddStaticText(Lang.Get("questline:edit-chapter").ToUpperInvariant(), Heading(), ElementBounds.Fixed(FormX, y, FormWidth, LabelHeight));
        composer.AddStaticText(chapter.Id, MutedSmall(), ElementBounds.Fixed(FormX, y + LabelHeight, FormWidth, LabelHeight));
        y += 2 * LabelHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-title"), y);
        AddInput(composer, "edit-chapter-title", ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), EditText(chapter.Id + "-title"), v => SetEditText(chapter.Id + "-title", v));
        y += FieldHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-icon"), y);
        y = AddIconField(composer, "edit-chapter-icon", y, chapter.Icon, v => { chapter.Icon = v; Changed(); });

        y = AddLabel(composer, Lang.Get("questline:edit-background"), y);
        AddInput(composer, "edit-chapter-background", ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), chapter.Background,
            v => { chapter.Background = string.IsNullOrWhiteSpace(v) ? null : v.Trim(); Changed(); }, "mypack:questline/map.png");
        y += FieldHeight + RowGap;

        composer.AddSwitch(on => { chapter.Age = on; Changed(); }, ElementBounds.Fixed(FormX, y, FieldHeight, FieldHeight), "edit-chapter-age", FieldHeight - 8);
        composer.AddStaticText(Lang.Get("questline:edit-age"), Small(), ElementBounds.Fixed(FormX + FieldHeight + 6, y + 4, FormWidth - FieldHeight - 6, FieldHeight));
        afterCompose.Add(() => SingleComposer.GetSwitch("edit-chapter-age").SetValue(chapter.Age));
        y += FieldHeight + RowGap * 2;

        composer.AddStaticText(Lang.Get("questline:edit-hint"), MutedSmall(), ElementBounds.Fixed(FormX, y, FormWidth, ContentY + PanelHeight - y - Pad));

        double x = RailWidth + Gap + CanvasWidth + Gap;
        if (client.Editor.Edited.Contains(chapter.Id))
        {
            AddRowButton(composer, Lang.Get("questline:edit-revert"), x, false, () => { client.RevertChapter(chapter.Id); return true; }, "edit-revert");
        }
        AddRowButton(composer, Lang.Get("questline:edit-save"), x + DetailWidth, true, () => { SaveDraft(); return true; }, "edit-save", primary: true);
    }

    /// <summary>An item code field with a pick slot beside it: click the slot holding an item to fill it in.</summary>
    double AddIconField(GuiComposer composer, string key, double y, string code, Action<string> onChange)
    {
        composer.AddInteractiveElement(new GuiElementItemPick(capi, ElementBounds.Fixed(FormX, y, FieldHeight, FieldHeight), text.StackFor(code), stack =>
        {
            onChange(stack.Collectible.Code.ToString());
            Compose();
        }), key + "-pick");
        AddInput(composer, key, ElementBounds.Fixed(FormX + FieldHeight + 6, y, FormWidth - FieldHeight - 6, FieldHeight), code,
            v => onChange(string.IsNullOrWhiteSpace(v) ? null : v.Trim()), "game:flint");
        return y + FieldHeight + RowGap;
    }

    #endregion

    #region Quest form

    void ComposeQuestForm(GuiComposer composer, QuestDef quest)
    {
        AddFormFrame(composer);
        double y = ContentY + Pad;
        composer.AddHorizontalTabs(
            [
                new() { Name = Lang.Get("questline:edit-page-quest"), DataInt = QuestPage },
                new() { Name = Lang.Get("questline:edit-page-tasks", quest.Tasks.Count), DataInt = TasksPage },
                new() { Name = Lang.Get("questline:edit-page-rewards", quest.Rewards.Count), DataInt = RewardsPage },
                new() { Name = Lang.Get("questline:edit-page-options"), DataInt = OptionsPage },
            ],
            ElementBounds.Fixed(FormX, y, FormWidth, 22), SelectEditPage, Small(), Small().WithWeight(Cairo.FontWeight.Bold), "edit-pages");
        afterCompose.Add(() => SingleComposer.GetHorizontalTabs("edit-pages").SetValue(editPage, false));
        y += 22 + RowGap * 2;

        if (editPage == TasksPage) ComposeTaskRows(composer, quest, y);
        else if (editPage == RewardsPage) ComposeRewardRows(composer, quest, y);
        else if (editPage == OptionsPage) ComposeOptionsPage(composer, quest, y);
        else ComposeQuestPage(composer, quest, y);

        double x = RailWidth + Gap + CanvasWidth + Gap;
        bool armed = armedDelete == quest.Id;
        AddRowButton(composer, Lang.Get(armed ? "questline:edit-delete-sure" : "questline:edit-delete"), x, false, () =>
        {
            if (armedDelete != quest.Id)
            {
                armedDelete = quest.Id;
                Compose();
                return true;
            }
            DeleteQuest(quest);
            return true;
        }, "edit-delete");
        AddRowButton(composer, Lang.Get("questline:edit-save"), x + DetailWidth, true, () => { SaveDraft(); return true; }, "edit-save", primary: true);
    }

    void ComposeQuestPage(GuiComposer composer, QuestDef quest, double y)
    {
        composer.AddStaticText(quest.Id, MutedSmall(), ElementBounds.Fixed(FormX, y, FormWidth, LabelHeight));
        y += LabelHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-title"), y);
        AddInput(composer, "edit-title", ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), EditText(quest.Id + "-title"), v => SetEditText(quest.Id + "-title", v));
        y += FieldHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-description"), y);
        AddInput(composer, "edit-desc", ElementBounds.Fixed(FormX, y, FormWidth, 56), EditText(quest.Id + "-desc"), v => SetEditText(quest.Id + "-desc", v), area: true);
        y += 56 + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-icon"), y);
        y = AddIconField(composer, "edit-icon", y, quest.Icon, v => { quest.Icon = v; Changed(); });

        // What it needs, then anything wrong with it, in the problem colour.
        var notes = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, (quest.Requires == null ? Lang.Get("questline:edit-needs-nothing") : Lang.Get("questline:requires", text.Describe(quest.Requires))) + "\n", MutedSmall()),
        };
        foreach (var problem in client.Editor.ProblemsOf(quest.Id))
        {
            notes.Add(new RichTextComponent(capi, problem[(quest.Id.Length + 2)..] + "\n", Small().WithColor(BookStyle.Problem)));
        }
        // Only once the graph is built in the background: asking sooner would stall the frame for it.
        var suggested = client.GraphReady ? SuggestedPrerequisites(quest) : [];
        if (suggested.Count > 0)
        {
            notes.Add(new RichTextComponent(capi, Lang.Get("questline:edit-suggested") + "  ", MutedSmall()));
            for (int i = 0; i < suggested.Count; i++)
            {
                var pre = suggested[i];
                notes.Add(new LinkTextComponent(capi, NonEmpty(EditText(pre.Id + "-title"), pre.Id), Link(), _ => ToggleLink(pre, quest)));
                notes.Add(new RichTextComponent(capi, i < suggested.Count - 1 ? ", " : "\n", Small()));
            }
        }
        composer.AddRichtext(notes.ToArray(), ElementBounds.Fixed(FormX, y, FormWidth, ContentY + PanelHeight - y - Pad), "edit-notes");
    }

    void ComposeOptionsPage(GuiComposer composer, QuestDef quest, double y)
    {
        y = AddLabel(composer, Lang.Get("questline:edit-shape"), y);
        var shapes = Enum.GetValues<NodeShape>();
        composer.AddDropDown(shapes.Select(v => v.ToString()).ToArray(), shapes.Select(v => Lang.Get("questline:shape-" + v.ToString().ToLowerInvariant())).ToArray(),
            Array.IndexOf(shapes, quest.Shape), (code, _) => { quest.Shape = Enum.Parse<NodeShape>(code); SaveDraft(); }, ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), Small(), "edit-shape");
        y += FieldHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-size"), y);
        int size = Array.FindIndex(SizeValues, v => Math.Abs(float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) - quest.Size) < 0.01f);
        composer.AddDropDown(SizeValues, SizeValues.Select(v => "×" + v).ToArray(), Math.Max(0, size),
            (code, _) => { quest.Size = float.Parse(code, System.Globalization.CultureInfo.InvariantCulture); SaveDraft(); }, ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), Small(), "edit-size");
        y += FieldHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-mode"), y);
        var modes = Enum.GetValues<QuestMode>();
        composer.AddDropDown(modes.Select(v => v.ToString()).ToArray(), modes.Select(v => Lang.Get("questline:mode-name-" + v.ToString().ToLowerInvariant())).ToArray(),
            Array.IndexOf(modes, quest.Mode), (code, _) => { quest.Mode = Enum.Parse<QuestMode>(code); SaveDraft(); }, ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), Small(), "edit-mode");
        y += FieldHeight + RowGap;

        y = AddLabel(composer, Lang.Get("questline:edit-visibility"), y);
        var shown = Enum.GetValues<QuestVisibility>();
        composer.AddDropDown(shown.Select(v => v.ToString()).ToArray(), shown.Select(v => Lang.Get("questline:visibility-" + v.ToString().ToLowerInvariant())).ToArray(),
            Array.IndexOf(shown, quest.Visibility), (code, _) => { quest.Visibility = Enum.Parse<QuestVisibility>(code); SaveDraft(); }, ElementBounds.Fixed(FormX, y, FormWidth, FieldHeight), Small(), "edit-visibility");
    }

    const double TaskRowHeight = FieldHeight * 2 + 4 + 12;

    int RowsThatFit(double y) => Math.Max(1, (int)((ContentY + PanelHeight - Pad - FieldHeight - RowGap - y) / TaskRowHeight));

    void ComposeTaskRows(GuiComposer composer, QuestDef quest, double y)
    {
        int fit = RowsThatFit(y);
        for (int i = 0; i < Math.Min(fit, quest.Tasks.Count); i++)
        {
            var task = quest.Tasks[i];
            int index = i;
            AddRowTop(composer, $"edit-task-{i}", y, text.StackFor(task.OnCollectible ? task.Target : null),
                TaskTypes, Array.IndexOf(TaskTypes, task.Type), task.Count,
                stack =>
                {
                    if (!task.OnCollectible) task.Type = "obtain";
                    SetTarget(task, stack.Collectible.Code.ToString());
                },
                type => { string target = task.Target; task.Type = type; SetTarget(task, target); },
                count => { task.Count = count; Changed(); },
                () => { quest.Tasks.RemoveAt(index); SaveDraft(); });

            string placeholder = task.Type switch { "kill" => "game:wolf-*", "mount" => "game:boat-*", "custom" => "mymod:caught-fish", _ => "game:flint" };
            if (task.Type != "checkbox")
            {
                AddInput(composer, $"edit-task-{i}-target", ElementBounds.Fixed(FormX, y + FieldHeight + 4, FormWidth, FieldHeight), task.Target,
                    v => { SetTarget(task, string.IsNullOrWhiteSpace(v) ? null : v.Trim()); Changed(); }, placeholder);
            }
            y += TaskRowHeight;
        }

        if (quest.Tasks.Count > fit) composer.AddStaticText(Lang.Get("questline:edit-more", quest.Tasks.Count - fit), MutedSmall(), ElementBounds.Fixed(FormX, y, FormWidth, LabelHeight));
        else composer.AddSmallButton(Lang.Get("questline:edit-add-task"), () =>
        {
            quest.Tasks.Add(new TaskDef { Type = "obtain", Item = "game:flint", Count = 1 });
            SaveDraft();
            return true;
        }, ElementBounds.Fixed(FormX, y, 120, FieldHeight), EnumButtonStyle.Small, "edit-add-task");
    }

    void ComposeRewardRows(GuiComposer composer, QuestDef quest, double y)
    {
        int fit = RowsThatFit(y);
        for (int i = 0; i < Math.Min(fit, quest.Rewards.Count); i++)
        {
            var reward = quest.Rewards[i];
            int index = i;
            if (reward.Type == "choice")
            {
                // Choices are written in JSON for now; the form shows them and can remove them.
                composer.AddStaticText(Lang.Get("questline:edit-choice", reward.Options.Count), MutedSmall(), ElementBounds.Fixed(FormX, y + 6, FormWidth - FieldHeight - 6, LabelHeight));
                composer.AddSmallButton("×", () => { quest.Rewards.RemoveAt(index); SaveDraft(); return true; },
                    ElementBounds.Fixed(FormX + FormWidth - FieldHeight, y, FieldHeight, FieldHeight), EnumButtonStyle.Small, $"edit-reward-{i}-remove");
                y += FieldHeight + 12;
                continue;
            }

            AddRowTop(composer, $"edit-reward-{i}", y, reward.Type == "item" ? text.StackFor(reward.Item) : null,
                RewardTypes, Math.Max(0, Array.IndexOf(RewardTypes, reward.Type)), reward.Count,
                stack => { reward.Type = "item"; reward.Item = stack.Collectible.Code.ToString(); },
                type => { reward.Type = type; },
                count => { reward.Count = count; Changed(); },
                () => { quest.Rewards.RemoveAt(index); SaveDraft(); });

            bool stage = reward.Type == "stage";
            AddInput(composer, $"edit-reward-{i}-target", ElementBounds.Fixed(FormX, y + FieldHeight + 4, FormWidth, FieldHeight), stage ? reward.Stage : reward.Item,
                v =>
                {
                    v = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    if (stage) reward.Stage = v;
                    else reward.Item = v;
                    Changed();
                }, stage ? DomainOf(quest.Id) + ":iron-age" : "game:ingot-copper");
            y += TaskRowHeight;
        }

        if (quest.Rewards.Count > fit) composer.AddStaticText(Lang.Get("questline:edit-more", quest.Rewards.Count - fit), MutedSmall(), ElementBounds.Fixed(FormX, y, FormWidth, LabelHeight));
        else composer.AddSmallButton(Lang.Get("questline:edit-add-reward"), () =>
        {
            quest.Rewards.Add(new RewardDef { Type = "item", Item = "game:stick", Count = 1 });
            SaveDraft();
            return true;
        }, ElementBounds.Fixed(FormX, y, 130, FieldHeight), EnumButtonStyle.Small, "edit-add-reward");
    }

    /// <summary>The first line of a task or reward: pick slot, type, count and remove.</summary>
    void AddRowTop(GuiComposer composer, string key, double y, ItemStack shown, string[] types, int typeIndex, int count,
        Action<ItemStack> onPick, Action<string> onType, Action<int> onCount, Action onRemove)
    {
        const double countWidth = 56;
        double typeWidth = FormWidth - FieldHeight - countWidth - FieldHeight - 3 * 6;
        composer.AddInteractiveElement(new GuiElementItemPick(capi, ElementBounds.Fixed(FormX, y, FieldHeight, FieldHeight), shown, stack =>
        {
            onPick(stack);
            SaveDraft();
        }), key + "-pick");
        composer.AddDropDown(types, types.Select(t => Lang.Get("questline:type-" + t)).ToArray(), Math.Max(0, typeIndex),
            (code, _) => { onType(code); SaveDraft(); },
            ElementBounds.Fixed(FormX + FieldHeight + 6, y, typeWidth, FieldHeight), Small(), key + "-type");
        AddInput(composer, key + "-count", ElementBounds.Fixed(FormX + FieldHeight + 6 + typeWidth + 6, y, countWidth, FieldHeight), count.ToString(),
            v => { if (int.TryParse(v, out int n) && n > 0) onCount(n); });
        composer.AddSmallButton("×", () => { onRemove(); return true; },
            ElementBounds.Fixed(FormX + FormWidth - FieldHeight, y, FieldHeight, FieldHeight), EnumButtonStyle.Small, key + "-remove");
    }

    /// <summary>Puts a task's target in the field its type reads: entity, block, key or item.</summary>
    static void SetTarget(TaskDef task, string target)
    {
        task.Item = task.Block = task.Entity = task.Key = null;
        switch (task.Type)
        {
            case "checkbox": break;
            case "kill" or "mount": task.Entity = target; break;
            case "place" or "break": task.Block = target; break;
            case "custom": task.Key = target; break;
            default: task.Item = target; break;
        }
    }

    #endregion

    #region Gestures

    /// <summary>A new quest where the canvas was right-clicked: a checkbox task and a placeholder title, selected for editing.</summary>
    void AddQuest(float x, float y)
    {
        var chapter = draft;
        if (chapter == null) return;
        string domain = DomainOf(chapter.Id);
        var taken = client.Editor.Chapters.Values.SelectMany(c => c.Quests).Concat(chapter.Quests).Select(q => q.Id).ToHashSet();
        int n = 1;
        while (taken.Contains($"{domain}:quest-{n}")) n++;
        var quest = new QuestDef { Id = $"{domain}:quest-{n}", Pos = [x, y], Tasks = [new TaskDef { Type = "checkbox" }] };
        chapter.Quests.Add(quest);
        draftText[quest.Id + "-title"] = Lang.Get("questline:edit-new-quest");
        questId = quest.Id;
        editPage = QuestPage;
        SaveDraft();
        Compose();
    }

    /// <summary>Adds <paramref name="pre"/> to what <paramref name="quest"/> requires, or takes it away if it's there.</summary>
    void ToggleLink(QuestDef pre, QuestDef quest)
    {
        if (quest.Requires?.QuestIds().Contains(pre.Id) == true)
        {
            quest.Requires = WithoutQuest(quest.Requires, pre.Id);
        }
        else
        {
            if (DependsOn(pre, quest.Id))
            {
                capi.TriggerIngameError(this, "questline-loop", Lang.Get("questline:edit-loop"));
                return;
            }
            quest.Requires = quest.Requires switch
            {
                null => new QuestRequirement(pre.Id),
                AllRequirement all => new AllRequirement([.. all.Of, new QuestRequirement(pre.Id)]),
                _ => new AllRequirement([quest.Requires, new QuestRequirement(pre.Id)]),
            };
        }
        SaveDraft();
        Compose();
    }

    /// <summary>
    /// Quests worth requiring before this one, from the recipe graph: those that ask for something
    /// a way of making this quest's items takes, one step back. Quests already required (directly
    /// or further back) and quests that come after this one are left out.
    /// </summary>
    public List<QuestDef> SuggestedPrerequisites(QuestDef quest)
    {
        var all = client.Editor.Chapters.Values.SelectMany(c => c.Quests).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
        if (draft != null) foreach (var q in draft.Quests) all[q.Id] = q;

        var inputs = new HashSet<string>();
        foreach (var task in quest.Tasks.Where(t => t.Target != null && t.OnCollectible))
        {
            foreach (var code in Candidates(task.Target))
            {
                foreach (var edge in client.Graph.MadeBy(code).Where(e => !Planner.FoundKinds.Contains(e.Kind)))
                {
                    foreach (var input in edge.Inputs) inputs.UnionWith(input.Codes.Where(c => !c.Contains('*')));
                }
            }
        }
        if (inputs.Count == 0) return [];

        return all.Values
            .Where(q => q.Id != quest.Id && !DependsOn(quest, q.Id) && !DependsOn(q, quest.Id))
            .Where(q => q.Tasks.Any(t => t.Target != null && t.OnCollectible && Candidates(t.Target).Any(inputs.Contains)))
            .OrderBy(q => q.Id)
            .Take(6)
            .ToList();
    }

    readonly Dictionary<string, string[]> candidates = [];

    /// <summary>The item codes a task target names: itself, or every code its wildcard matches.</summary>
    string[] Candidates(string target)
    {
        if (!TaskDef.IsPattern(target)) return [target.Contains(':') ? target : "game:" + target];
        if (!candidates.TryGetValue(target, out var codes))
        {
            var loc = new AssetLocation(target);
            candidates[target] = codes = capi.World.Collectibles.Where(c => c?.Code != null && Vintagestory.API.Util.WildcardUtil.Match(loc, c.Code)).Select(c => c.Code.ToString()).ToArray();
        }
        return codes;
    }

    /// <summary>Whether <paramref name="quest"/> needs <paramref name="id"/>, directly or further back.</summary>
    bool DependsOn(QuestDef quest, string id)
    {
        var byId = client.Editor.Chapters.Values.SelectMany(c => c.Quests).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var q in draft.Quests) byId[q.Id] = q;
        var seen = new HashSet<string>();
        var stack = new Stack<string>(quest.Requires?.QuestIds() ?? []);
        while (stack.Count > 0)
        {
            string next = stack.Pop();
            if (next == id) return true;
            if (!seen.Add(next) || !byId.TryGetValue(next, out var q)) continue;
            foreach (var pre in q.Requires?.QuestIds() ?? []) stack.Push(pre);
        }
        return false;
    }

    /// <summary>The requirement with every mention of a quest taken out, tidied: empty groups go, groups of one unwrap.</summary>
    static Requirement WithoutQuest(Requirement r, string id)
    {
        switch (r)
        {
            case QuestRequirement q:
                return q.QuestId == id ? null : q;
            case CompositeRequirement c:
                var rest = c.Of.Select(x => WithoutQuest(x, id)).Where(x => x != null).ToList();
                if (rest.Count == 0) return null;
                if (c is AtLeastRequirement atLeast) return new AtLeastRequirement(Math.Min(atLeast.N, rest.Count), rest);
                if (rest.Count == 1) return rest[0];
                return c is AnyRequirement ? new AnyRequirement(rest) : new AllRequirement(rest);
            default:
                return r;
        }
    }

    void DeleteQuest(QuestDef quest)
    {
        draft.Quests.Remove(quest);
        foreach (var q in draft.Quests.Where(q => q.Requires != null)) q.Requires = WithoutQuest(q.Requires, quest.Id);
        draftText[quest.Id + "-title"] = "";
        draftText[quest.Id + "-desc"] = "";
        questId = null;
        armedDelete = null;
        SaveDraft();
        Compose();
    }

    /// <summary>A new, empty chapter in the editor's domain, after the others.</summary>
    public void NewChapter()
    {
        if (dirty) SaveDraft();
        string domain = client.Editor.Domain;
        var taken = client.Editor.Chapters.Keys.ToHashSet();
        int n = 1;
        while (taken.Contains($"{domain}:chapter-{n}")) n++;
        draft = new ChapterDef
        {
            Id = $"{domain}:chapter-{n}",
            Questline = questlineId ?? QuestRegistry.DefaultQuestline,
            Order = client.Editor.Chapters.Values.Select(c => c.Order).DefaultIfEmpty(0).Max() + 10,
        };
        draftText.Clear();
        draftText[draft.Id + "-title"] = Lang.Get("questline:edit-new-chapter-title", n);
        chapterId = draft.Id;
        questId = null;
        SaveDraft();
        Compose();
    }

    #endregion
}
