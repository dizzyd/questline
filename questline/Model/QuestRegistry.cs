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
    public const string DefaultQuestline = "questline:default";

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

    /// <summary>Rebuilds the lookups from <see cref="Chapters"/>. Call after any change to them.</summary>
    public void Index()
    {
        Chapters = Chapters.OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        Questlines = Questlines.OrderBy(q => q.Order).ThenBy(q => q.Id).ToList();
        ChaptersById = Chapters.ToDictionary(c => c.Id);
        Quests = [];
        Aliases = [];
        Dependents = [];

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
