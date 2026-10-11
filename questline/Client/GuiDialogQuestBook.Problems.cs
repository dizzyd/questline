using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// The Problems tab, for editors: everything the last load found, one line each. A line about
/// a quest or chapter links to it in edit mode.
/// </summary>
public partial class GuiDialogQuestBook
{
    void ComposeProblems(GuiComposer composer)
    {
        var problems = client.Editor.Problems;
        var comps = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, Lang.Get(problems.Count == 0 ? "questline:problems-none" : "questline:problems-count", problems.Count) + "\n", Title()),
            new RichTextComponent(capi, Lang.Get("questline:problems-about") + "\n", MutedSmall()),
            new RuleTextComponent(capi),
        };

        var quests = client.Editor.Chapters.Values.SelectMany(c => c.Quests.Select(q => (Quest: q.Id, Chapter: c.Id))).ToList();
        foreach (var problem in problems)
        {
            int colon = problem.IndexOf(": ");
            string subject = colon > 0 ? problem[..colon] : null;
            var quest = quests.FirstOrDefault(q => q.Quest == subject);
            string chapter = quest.Chapter ?? (subject != null && client.Editor.Chapters.ContainsKey(subject) ? subject : null);

            if (chapter != null)
            {
                string questId = quest.Quest;
                comps.Add(new LinkTextComponent(capi, subject, Small().WithColor(Accent), _ => ShowInEditor(chapter, questId)));
                comps.Add(new RichTextComponent(capi, problem[colon..] + "\n", Small()));
            }
            else
            {
                comps.Add(new RichTextComponent(capi, problem + "\n", Small()));
            }
            comps.Add(new ClearFloatTextComponent(capi, 4));
        }

        AddTextPanel(composer, "problems", 0, ContentY, Width, PanelHeight, comps.ToArray());
        AddRowButton(composer, Lang.Get("questline:problems-reload"), 0, false, () => { capi.SendChatMessage("/questline reload"); return true; }, "problems-reload");
    }

    /// <summary>Opens a chapter, and a quest in it if given, in edit mode.</summary>
    public void ShowInEditor(string chapter, string quest)
    {
        if (dirty) SaveDraft();
        editing = client.CanEdit;
        draft = null;
        chapterId = chapter;
        questId = quest;
        editPage = 0;
        tab = QuestsTab;
        if (IsOpened()) Compose();
        else TryOpen();
    }
}
