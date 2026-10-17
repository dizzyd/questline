using System;
using System.Collections.Generic;
using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// What a locked quest is waiting for, in its detail panel: each prerequisite on a row of its
/// own, ticked when it's met, named with its chapter when that's another one, and a link to it;
/// then, when the prerequisites are themselves locked, the quests the player can do now that
/// lead there. A chapter with nothing open says the same of its first quests.
///
/// On the canvas, each unmet prerequisite from another chapter is a stub: a small node beside
/// the quest it holds up that names itself and its chapter on hover, and takes the book there.
/// </summary>
public partial class GuiDialogQuestBook
{
    /// <summary>How many open quests "Start with" names before it says how many more there are.</summary>
    const int MaxNextSteps = 5;
    const double TickSize = 16;
    const float StubSize = 0.55f;
    /// <summary>Where a stub may go, tried in order: a cell left of its quest, then a little closer, at these heights.</summary>
    static readonly float[] StubLefts = [-1, -0.75f], StubRises = [0, -0.6f, 0.6f, -1.2f, 1.2f, -0.3f, 0.3f, -0.9f, 0.9f];

    /// <summary>
    /// Adds a stub to <paramref name="nodes"/> for every unmet prerequisite a quest has in another
    /// chapter, with a line to that quest, and returns their quest ids in the order added.
    /// </summary>
    List<string> AddStubs(List<QuestCanvasNode> nodes, List<QuestDef> quests)
    {
        var ids = new List<string>();
        var lines = nodes.SelectMany(n => n.Prereqs.Select(p => (nodes[p].X, nodes[p].Y, n.X, n.Y))).ToList();
        for (int i = 0; i < quests.Count; i++)
        {
            var quest = quests[i];
            var outside = quest.Requires?.QuestIds().Distinct().Select(client.Registry.Get)
                .Where(pre => pre != null && pre.ChapterId != quest.ChapterId && !client.IsMet(pre.Id) && client.IsVisible(pre))
                .ToList() ?? [];
            foreach (var pre in outside)
            {
                var (x, y) = StubSpot(nodes, quests.Count, lines, nodes[i]);
                nodes[i].Prereqs = [.. nodes[i].Prereqs, nodes.Count];
                lines.Add((x, y, nodes[i].X, nodes[i].Y));
                nodes.Add(new QuestCanvasNode
                {
                    Id = pre.Id,
                    Title = text.Title(pre.Id),
                    Note = text.ChapterTitle(pre.ChapterId),
                    X = x,
                    Y = y,
                    Icon = IconFor(pre),
                    State = client.StateOf(pre.Id),
                    Shape = NodeShape.Circle,
                    Size = StubSize,
                    Stub = true,
                });
                ids.Add(pre.Id);
            }
        }
        return ids;
    }

    /// <summary>
    /// The first spot beside <paramref name="quest"/> clear of the chapter's quests, its lines and
    /// the stubs already placed; straight to its left if none is.
    /// </summary>
    static (float X, float Y) StubSpot(List<QuestCanvasNode> nodes, int questCount, List<(float, float, float, float)> lines, QuestCanvasNode quest)
    {
        foreach (float dx in StubLefts)
        {
            foreach (float dy in StubRises)
            {
                float x = quest.X + dx, y = quest.Y + dy;
                bool clear = nodes.Take(questCount).All(n => Math.Abs(n.X - x) >= 0.9f || Math.Abs(n.Y - y) >= 0.8f)
                    && nodes.Skip(questCount).All(n => MathF.Sqrt((n.X - x) * (n.X - x) + (n.Y - y) * (n.Y - y)) >= 0.6f)
                    && lines.All(l => DistanceToLine(x, y, l) >= 0.35f);
                if (clear) return (x, y);
            }
        }
        return (quest.X - 1, quest.Y);
    }

    static float DistanceToLine(float px, float py, (float X1, float Y1, float X2, float Y2) l)
    {
        float dx = l.X2 - l.X1, dy = l.Y2 - l.Y1, len = dx * dx + dy * dy;
        float t = len == 0 ? 0 : Math.Clamp(((px - l.X1) * dx + (py - l.Y1) * dy) / len, 0, 1);
        float ex = px - (l.X1 + t * dx), ey = py - (l.Y1 + t * dy);
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>
    /// For a chapter with nothing open: what its first quests need from elsewhere, each once, and
    /// where to start.
    /// </summary>
    List<RichTextComponentBase> ChapterNeeds(ChapterDef chapter)
    {
        var comps = new List<RichTextComponentBase>
        {
            new RichTextComponent(capi, text.ChapterTitle(chapter.Id) + "\n", Title()),
            new RichTextComponent(capi, Lang.Get("questline:chapter-closed") + "\n", MutedSmall()),
        };
        // What opens the chapter is what its earliest first quests need: a late branch's way in
        // (Farming's compost, which waits on a barrel) doesn't open it.
        var entries = client.EntryQuests(chapter).Where(client.IsVisible).ToList();
        int earliest = entries.Select(q => client.Registry.AgeOf(q)?.Order ?? int.MinValue).DefaultIfEmpty().Min();
        entries = entries.Where(q => (client.Registry.AgeOf(q)?.Order ?? int.MinValue) == earliest).ToList();
        var outside = entries.SelectMany(q => q.Requires?.QuestIds() ?? []).Distinct()
            .Select(client.Registry.Get).Where(pre => pre != null && pre.ChapterId != chapter.Id).ToList();
        foreach (var pre in outside) AddQuestRow(comps, pre, chapter.Id, 0);
        // What isn't a quest (a stage, a mod) is said in words, once per first quest.
        foreach (var q in entries.Where(q => q.Requires != null && q.Requires.Walk().Any(r => r is not (QuestRequirement or CompositeRequirement))))
        {
            AddRow(comps, null, text.Title(q.Id) + ": " + text.Describe(q.Requires), () => SelectQuest(q.Id), null, 0);
        }
        AddNextSteps(comps, outside.Where(pre => !client.IsMet(pre.Id)).Select(pre => pre.Id).ToList(), chapter.Id);
        return comps;
    }

    void AddNeeds(List<RichTextComponentBase> comps, QuestDef quest)
    {
        comps.Add(new RichTextComponent(capi, Lang.Get("questline:needs").ToUpperInvariant() + "\n", Heading()));
        AddRequirementRows(comps, quest.Requires, quest.ChapterId, 0);
        AddNextSteps(comps, quest.Requires.QuestIds().Where(id => !client.IsMet(id)).ToList(), quest.ChapterId);
    }

    /// <summary>
    /// The top of a requirement as rows: an "all" is one row per part, an "any" or "at least"
    /// says so over its parts, indented. Anything deeper is one row in words.
    /// </summary>
    void AddRequirementRows(List<RichTextComponentBase> comps, Requirement r, string here, double indent)
    {
        switch (r)
        {
            case AllRequirement all when indent == 0:
                foreach (var part in all.Of) AddRequirementRows(comps, part, here, indent);
                break;

            case AnyRequirement or AtLeastRequirement when indent == 0:
                var composite = (CompositeRequirement)r;
                string intro = r is AtLeastRequirement least ? Lang.Get("questline:needs-atleast", least.N) : Lang.Get("questline:needs-any");
                comps.Add(new RichTextComponent(capi, intro + "\n", MutedSmall()));
                foreach (var part in composite.Of) AddRequirementRows(comps, part, here, TickSize);
                break;

            case QuestRequirement q when client.Registry.Get(q.QuestId) is { } target:
                AddQuestRow(comps, target, here, indent);
                break;

            case ChapterCompleteRequirement c when client.Registry.ChaptersById.TryGetValue(c.ChapterId, out var chapter):
                bool done = chapter.Quests.All(q => client.IsMet(q.Id));
                AddRow(comps, done, text.Describe(r), client.IsVisible(chapter) ? () => SelectChapter(chapter.Id) : null, null, indent);
                break;

            default:
                AddRow(comps, null, text.Describe(r), null, null, indent);
                break;
        }
    }

    void AddQuestRow(List<RichTextComponentBase> comps, QuestDef target, string here, double indent)
    {
        bool shown = client.IsVisible(target) && client.IsVisible(client.Registry.ChaptersById[target.ChapterId]);
        string chapter = target.ChapterId == here ? null : text.ChapterTitle(target.ChapterId);
        AddRow(comps, client.IsMet(target.Id), text.Title(target.Id), shown ? () => SelectQuest(target.Id) : null, chapter, indent);
    }

    /// <summary>One row: a tick box (or the space for one, when <paramref name="done"/> isn't known), the words, and where they are.</summary>
    void AddRow(List<RichTextComponentBase> comps, bool? done, string words, System.Action onClick, string where, double indent)
    {
        if (done is { } ticked)
        {
            comps.Add(new TickBoxComponent(capi, TickSize, 6, ticked, null) { PaddingLeft = indent, VerticalAlign = EnumVerticalAlign.Middle });
        }
        else
        {
            comps.Add(new RichTextComponent(capi, " ", Small()) { PaddingLeft = indent + TickSize + 2, VerticalAlign = EnumVerticalAlign.Middle });
        }

        var font = done == true ? Small().WithColor(Good) : onClick != null ? Link() : Small();
        comps.Add(onClick != null
            ? new LinkTextComponent(capi, words, font, _ => onClick()) { VerticalAlign = EnumVerticalAlign.Middle }
            : new RichTextComponent(capi, words, font) { VerticalAlign = EnumVerticalAlign.Middle });
        if (where != null) comps.Add(new RichTextComponent(capi, "  · " + where, MutedSmall()) { VerticalAlign = EnumVerticalAlign.Middle });
        comps.Add(new ClearFloatTextComponent(capi, 2));
    }

    /// <summary>
    /// "Start with": the open quests that lead to <paramref name="unmet"/>, when some of those are
    /// locked themselves. When they're all open, the rows above already say what to do.
    /// </summary>
    void AddNextSteps(List<RichTextComponentBase> comps, List<string> unmet, string here)
    {
        if (!unmet.Any(id => client.StateOf(id) == QuestState.Locked)) return;
        var steps = client.NextSteps(unmet);
        if (steps.Count == 0) return;

        comps.Add(new ClearFloatTextComponent(capi, 6));
        comps.Add(new RichTextComponent(capi, Lang.Get("questline:start-with").ToUpperInvariant() + "\n", Heading()));
        foreach (var step in steps.Take(MaxNextSteps)) AddQuestRow(comps, step, here, 0);
        if (steps.Count > MaxNextSteps)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:and-more", steps.Count - MaxNextSteps) + "\n", MutedSmall()) { PaddingLeft = TickSize + 6 });
        }
    }
}
