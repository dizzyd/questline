using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace questline.Model;

/// <summary>
/// Every active questline, chapter and quest, after loading and validation. The server builds
/// it from assets; clients receive it whole when they join.
/// </summary>
public class QuestRegistry
{
    /// <summary>The domain of the default questline, which ships with the mod.</summary>
    public const string DefaultDomain = "questline";
    public const string DefaultQuestline = DefaultDomain + ":default";

    public List<QuestlineDef> Questlines = [];
    public List<ChapterDef> Chapters = [];
    /// <summary>
    /// Lang entries written in the editor, in the server's language, for clients to use before
    /// the installed lang files. Empty unless someone has edited quests on this server.
    /// </summary>
    public Dictionary<string, string> Text = [];

    [JsonIgnore] public Dictionary<string, QuestDef> Quests { get; private set; } = [];
    [JsonIgnore] public Dictionary<string, ChapterDef> ChaptersById { get; private set; } = [];
    /// <summary>Old quest id to current, from every chapter's aliases.</summary>
    [JsonIgnore] public Dictionary<string, string> Aliases { get; private set; } = [];
    /// <summary>Quest id to the quests whose requirements name it.</summary>
    [JsonIgnore] public Dictionary<string, List<QuestDef>> Dependents { get; private set; } = [];

    public QuestDef Get(string questId) => questId != null && Quests.TryGetValue(questId, out var q) ? q : null;

    /// <summary>
    /// A quest as a player types it in a command: its full id, or the id without its domain
    /// (<c>cast-anvil</c> for <c>questline:cast-anvil</c>), either one current or an alias a
    /// chapter keeps for a renamed quest. Where two domains have the same id, the default
    /// questline's is the one meant; between two others, neither.
    /// </summary>
    public QuestDef Find(string id)
    {
        if (Current(id) is { } quest) return quest;
        if (id == null || id.Contains(':')) return null;
        if (Current(DefaultDomain + ":" + id) is { } ours) return ours;
        var matches = Quests.Keys.Concat(Aliases.Keys)
            .Where(full => full.EndsWith(":" + id, StringComparison.Ordinal))
            .Select(Current).Where(q => q != null).Distinct().Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;

        QuestDef Current(string full) => Get(full) ?? (full != null && Aliases.TryGetValue(full, out var renamed) ? Get(renamed) : null);
    }

    /// <summary>
    /// Multiplies every reward of <paramref name="item"/>, choice options included, rounding to
    /// whole items; one scaled to nothing is dropped. The server's own taste in a pack's rewards:
    /// fewer rusty gears, say. Players see the scaled amounts, since the client gets this registry.
    /// </summary>
    public void ScaleRewards(string item, double factor)
    {
        if (factor == 1) return;
        foreach (var r in Quests.Values.SelectMany(q => Flatten(q.Rewards)).Where(r => r.Type == "item" && r.Item == item))
        {
            r.Count = (int)Math.Round(r.Count * factor, MidpointRounding.AwayFromZero);
        }
        DropRewards(r => r.Type == "item" && r.Count <= 0);
    }

    /// <summary>
    /// Drops every item reward, choice options included; stages stay. Like
    /// <see cref="ScaleRewards"/>, a server setting applied to the loaded registry, not the files;
    /// clients receive the result.
    /// </summary>
    public void RemoveItemRewards() => DropRewards(r => r.Type == "item");

    /// <summary>Drops the rewards that match, choice options included, then any choice left with no options.</summary>
    void DropRewards(Predicate<RewardDef> drop)
    {
        foreach (var quest in Quests.Values) Drop(quest.Rewards);

        void Drop(List<RewardDef> rewards)
        {
            foreach (var choice in rewards.Where(r => r.Type == "choice")) Drop(choice.Options);
            rewards.RemoveAll(r => drop(r) || (r.Type == "choice" && r.Options.Count == 0));
        }
    }

    /// <summary>Rewards with each choice's options in its place.</summary>
    static IEnumerable<RewardDef> Flatten(List<RewardDef> rewards) =>
        rewards.SelectMany(r => r.Type == "choice" ? Flatten(r.Options) : [r]);

    readonly Dictionary<string, ChapterDef> ages = [];

    /// <summary>
    /// The age a quest belongs to: its own chapter if that's an <see cref="ChapterDef.Age"/>, else
    /// the latest age its prerequisites lead back to, taking the earliest way through an "any" or
    /// "at least". Null when nothing it needs is in an age.
    /// </summary>
    public ChapterDef AgeOf(QuestDef quest) => AgeOf(quest.Id, []);

    ChapterDef AgeOf(string questId, HashSet<string> visiting)
    {
        if (ages.TryGetValue(questId, out var known)) return known;
        // A cycle is disabled by the loader; it has no age rather than no end.
        if (Get(questId) is not { } quest || !visiting.Add(questId)) return null;
        var chapter = ChaptersById[quest.ChapterId];
        var age = chapter.Age ? chapter : AgeOf(quest.Requires, visiting);
        visiting.Remove(questId);
        return ages[questId] = age;
    }

    ChapterDef AgeOf(Requirement r, HashSet<string> visiting) => r switch
    {
        QuestRequirement q => AgeOf(q.QuestId, visiting),
        AllRequirement all => all.Of.Select(x => AgeOf(x, visiting)).MaxBy(AgeRank),
        AnyRequirement any => any.Of.Select(x => AgeOf(x, visiting)).MinBy(AgeRank),
        AtLeastRequirement least => least.Of.Select(x => AgeOf(x, visiting)).OrderBy(AgeRank).ElementAtOrDefault(least.N - 1),
        _ => null,
    };

    /// <summary>Ages in chapter order, with no age at all before the first.</summary>
    static int AgeRank(ChapterDef age) => age?.Order ?? int.MinValue;

    /// <summary>Rebuilds the lookups from <see cref="Chapters"/>. Call after any change to them.</summary>
    public void Index()
    {
        Chapters = Chapters.OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        Questlines = Questlines.OrderBy(q => q.Order).ThenBy(q => q.Id).ToList();
        ChaptersById = Chapters.ToDictionary(c => c.Id);
        Quests = [];
        Aliases = [];
        Dependents = [];
        ages.Clear();

        foreach (var chapter in Chapters)
        {
            foreach (var quest in chapter.Quests)
            {
                quest.ChapterId = chapter.Id;
                Quests[quest.Id] = quest;
            }
            foreach (var (from, to) in chapter.Aliases) Aliases[from] = to;
        }

        foreach (var quest in Quests.Values)
        {
            if (quest.Requires == null) continue;
            foreach (var id in quest.Requires.QuestIds().Distinct())
            {
                if (!Dependents.TryGetValue(id, out var list)) Dependents[id] = list = [];
                list.Add(quest);
            }
        }
    }

    public string ToJson() => JsonConvert.SerializeObject(this);

    public static QuestRegistry FromJson(string json)
    {
        var registry = JsonConvert.DeserializeObject<QuestRegistry>(json);
        registry.Index();
        return registry;
    }
}
