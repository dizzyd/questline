using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using questline.Model;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace questline.Server;

/// <summary>
/// Reads every mod's questline/ assets into a <see cref="QuestRegistry"/>: picks the active
/// questlines, merges their chapters and disables quests that can't work. Each problem is
/// logged and kept in <see cref="Problems"/>; a broken quest never stops its chapter loading.
/// </summary>
public class QuestLoader(IGameFacts facts, ILogger logger, string workingDir = null)
{
    public static readonly HashSet<string> TaskTypes = ["obtain", "kill", "mount", "place", "break", "craft", "knap", "clayform", "smith", "cook", "smelt", "trade", "visit", "custom", "checkbox"];
    static readonly HashSet<string> BlockTasks = ["place", "break"];
    static readonly HashSet<string> RewardTypes = ["item", "choice", "stage"];

    public List<string> Problems { get; } = [];

    /// <summary>Every chapter as loaded, ids qualified, before validation disabled anything: what the editor edits.</summary>
    public Dictionary<string, string> RawChapters { get; } = [];

    /// <summary>Lang entries written by the editor, keyed with their domain, e.g. mypack:first-quest-title.</summary>
    public Dictionary<string, string> Text { get; } = [];

    /// <summary>The ids of chapters that have a working copy from the editor.</summary>
    public HashSet<string> Edited { get; } = [];

    public QuestRegistry Load()
    {
        var registry = new QuestRegistry { Questlines = SelectActive(LoadQuestlines()) };
        var active = registry.Questlines.Select(q => q.Id).ToHashSet();

        var chapters = new List<ChapterDef>();
        var extensions = new List<ChapterDef>();
        foreach (var (domain, name, json) in facts.Chapters())
        {
            string where = $"{domain}:questline/chapters/{name}.json";
            ChapterDef chapter;
            try
            {
                chapter = JsonConvert.DeserializeObject<ChapterDef>(json);
            }
            catch (Exception e)
            {
                Problem($"{where}: can't read chapter: {e.Message}");
                continue;
            }

            Qualify(chapter, domain, name);
            if (chapter.Extends != null)
            {
                chapter.Extends = Requirement.QualifyId(chapter.Extends, domain);
                extensions.Add(chapter);
                continue;
            }
            if (chapters.Any(c => c.Id == chapter.Id))
            {
                Problem($"{where}: chapter {chapter.Id} is already defined; this copy is ignored");
                continue;
            }
            chapters.Add(chapter);
        }

        // The editor's working copies replace the installed chapter of the same id, or add a new one.
        foreach (var chapter in LoadWorking())
        {
            chapters.RemoveAll(c => c.Id == chapter.Id);
            chapters.Add(chapter);
            Edited.Add(chapter.Id);
        }

        foreach (var chapter in chapters)
        {
            if (!chapter.RequiresMod.All(facts.IsModLoaded)) continue;
            if (!active.Contains(chapter.Questline)) continue;
            RawChapters[chapter.Id] = JsonConvert.SerializeObject(chapter);
            registry.Chapters.Add(chapter);
        }

        // Extensions join their chapter after the editor's copies are in, so an edited chapter
        // keeps its mods' quests; the editor itself sees only the chapter's own file.
        var replaced = new HashSet<string>();
        foreach (var extension in extensions)
        {
            if (!extension.RequiresMod.All(facts.IsModLoaded)) continue;
            var target = registry.Chapters.FirstOrDefault(c => c.Id == extension.Extends);
            if (target == null)
            {
                if (chapters.Any(c => c.Id == extension.Extends)) continue;   // its chapter is off, so it is too
                Problem($"{extension.Id}: extends {extension.Extends}, which no questline has");
                continue;
            }
            // A quest with the id of one already in the chapter replaces it: how a mod that
            // changes a mechanic rewrites the base quest teaching it.
            foreach (var quest in extension.Quests)
            {
                int at = target.Quests.FindIndex(q => q.Id == quest.Id);
                if (at < 0)
                {
                    target.Quests.Add(quest);
                    continue;
                }
                if (!replaced.Add(quest.Id)) Problem($"{extension.Id}: {quest.Id} is replaced by more than one extension; the last one loaded wins");
                target.Quests[at] = quest;
            }
            target.Art.AddRange(extension.Art);
        }

        var seen = new HashSet<string>();
        foreach (var chapter in registry.Chapters)
        {
            chapter.Quests.RemoveAll(q =>
            {
                if (seen.Add(q.Id)) return false;
                Problem($"{q.Id}: defined twice; the copy in {chapter.Id} is disabled");
                return true;
            });
        }

        registry.Text = Text;
        registry.Index();
        Validate(registry);
        registry.Index();

        logger.Notification("[questline] loaded {0} quests in {1} chapters across {2} questlines, {3} problems",
            registry.Quests.Count, registry.Chapters.Count, registry.Questlines.Count, Problems.Count);
        return registry;
    }

    List<QuestlineDef> LoadQuestlines()
    {
        var list = new List<QuestlineDef>();
        foreach (var (domain, json) in facts.Questlines())
        {
            try
            {
                var def = JsonConvert.DeserializeObject<QuestlineDef>(json);
                def.Domain = domain;
                def.Id = Requirement.QualifyId(def.Id ?? "default", def.Domain);
                list.Add(def);
            }
            catch (Exception e)
            {
                Problem($"{domain}:questline/questline.json: can't read questline: {e.Message}");
            }
        }
        return list;
    }

    List<QuestlineDef> SelectActive(List<QuestlineDef> all)
    {
        var shipping = all.Select(q => q.Domain).ToHashSet();
        var active = all
            .Where(q => q.RequiresMod.All(facts.IsModLoaded))
            .Where(q => !q.YieldsTo.Any(mod => mod != q.Domain && shipping.Contains(mod)))
            .ToList();

        if (active.Any(q => q.ReplacesDefault))
        {
            active.RemoveAll(q => q.Id == QuestRegistry.DefaultQuestline);
        }
        return active;
    }

    void Qualify(ChapterDef chapter, string domain, string fileName)
    {
        chapter.Id = Requirement.QualifyId(chapter.Id ?? fileName, domain);
        chapter.Questline = chapter.Questline == null ? QuestRegistry.DefaultQuestline : Requirement.QualifyId(chapter.Questline, domain);
        foreach (var art in chapter.Art.Where(a => a.Text != null)) art.Text = Requirement.QualifyId(art.Text, domain);
        chapter.Aliases = chapter.Aliases.ToDictionary(a => Requirement.QualifyId(a.Key, domain), a => Requirement.QualifyId(a.Value, domain));

        foreach (var quest in chapter.Quests.ToList())
        {
            if (string.IsNullOrEmpty(quest.Id))
            {
                Problem($"{chapter.Id}: a quest has no id and is disabled");
                chapter.Quests.Remove(quest);
                continue;
            }
            quest.Id = Requirement.QualifyId(quest.Id, domain);
            quest.Requires?.Qualify(domain);
            // Text keys are the domain's own, as quest ids are, unless they name another.
            foreach (var block in quest.Page)
            {
                if (block.Lang != null) block.Lang = Requirement.QualifyId(block.Lang, domain);
                if (block.Caption != null) block.Caption = Requirement.QualifyId(block.Caption, domain);
            }
            foreach (var reward in quest.Rewards.Where(r => r.Stage != null)) reward.Stage = Requirement.QualifyId(reward.Stage, domain);
        }
    }

    /// <summary>
    /// The editor's working chapters: &lt;working&gt;/&lt;domain&gt;/chapters/*.json, with the lang
    /// entries beside them in &lt;domain&gt;/lang/en.json going into <see cref="Text"/>.
    /// </summary>
    List<ChapterDef> LoadWorking()
    {
        var list = new List<ChapterDef>();
        if (workingDir == null || !Directory.Exists(workingDir)) return list;

        foreach (var domainDir in Directory.GetDirectories(workingDir))
        {
            string domain = Path.GetFileName(domainDir);
            string langFile = Path.Combine(domainDir, "lang", "en.json");
            if (File.Exists(langFile))
            {
                try
                {
                    foreach (var (key, value) in JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(langFile)) ?? [])
                    {
                        Text[Requirement.QualifyId(key, domain)] = value;
                    }
                }
                catch (Exception e)
                {
                    Problem($"{langFile}: can't read the editor's text: {e.Message}");
                }
            }

            string chaptersDir = Path.Combine(domainDir, "chapters");
            if (!Directory.Exists(chaptersDir)) continue;
            foreach (var file in Directory.GetFiles(chaptersDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var chapter = JsonConvert.DeserializeObject<ChapterDef>(File.ReadAllText(file));
                    Qualify(chapter, domain, Path.GetFileNameWithoutExtension(file));
                    list.Add(chapter);
                }
                catch (Exception e)
                {
                    Problem($"{file}: can't read working chapter: {e.Message}");
                }
            }
        }
        return list;
    }

    bool HasText(string key) => key != null && (Text.ContainsKey(key) || facts.HasLang(key));

    void Validate(QuestRegistry registry)
    {
        var broken = new HashSet<string>();

        foreach (var quest in registry.Quests.Values)
        {
            var errors = QuestErrors(quest, registry).ToList();
            foreach (var e in errors) Problem($"{quest.Id}: {e}; quest disabled");
            if (errors.Count > 0) broken.Add(quest.Id);

            if (quest.Size is < 0.5f or > 3f) Problem($"{quest.Id}: size {quest.Size} is outside 0.5 to 3; drawn clamped");
            if (quest.Claim == ClaimMode.First && quest.Mode != QuestMode.Server) Problem($"{quest.Id}: claim 'first' only applies to server quests; each player claims their own");
            if (quest.Icon != null && !CollectibleExists(quest.Icon)) Problem($"{quest.Id}: icon {quest.Icon} is not an item or block");
            foreach (var warning in PageWarnings(quest)) Problem($"{quest.Id}: {warning}");
            if (!HasText(quest.Id + "-title")) Problem($"{quest.Id}: no lang entry {quest.Id}-title");
        }

        foreach (var chapter in registry.Chapters)
        {
            foreach (var warning in ArtWarnings(chapter)) Problem($"{chapter.Id}: {warning}");
        }

        foreach (var id in FindCycles(registry))
        {
            Problem($"{id}: its requirements form a cycle; quest disabled");
            broken.Add(id);
        }

        foreach (var chapter in registry.Chapters) chapter.Quests.RemoveAll(q => broken.Contains(q.Id));
        registry.Index();

        var allDone = new EverythingDoneContext(registry, facts);
        foreach (var quest in registry.Quests.Values)
        {
            if (quest.Requires != null && !quest.Requires.Evaluate(allDone))
            {
                Problem($"{quest.Id}: can never unlock (it needs a disabled quest or a mod that isn't loaded)");
            }
        }
    }

    IEnumerable<string> QuestErrors(QuestDef quest, QuestRegistry registry)
    {
        if (quest.Requires != null)
        {
            foreach (var id in quest.Requires.QuestIds().Where(id => !registry.Quests.ContainsKey(id)).Distinct())
            {
                yield return $"requires unknown quest {id}";
            }
        }

        if (quest.Tasks.Count == 0) yield return "has no tasks";

        foreach (var task in quest.Tasks)
        {
            if (!TaskTypes.Contains(task.Type ?? ""))
            {
                yield return $"unknown task type '{task.Type}'";
                continue;
            }
            if (task.Count < 1) yield return $"{task.Type} task count must be at least 1";
            if (task.Type == "checkbox") continue;
            if (task.Type == "custom")
            {
                // Another mod reports these, so there's nothing to check the key against.
                if (task.Key == null) yield return "custom task has no key";
                continue;
            }

            if (task.Target == null) yield return $"{task.Type} task names nothing to match";
            else if (!TargetExists(task)) yield return $"{task.Type} task target {task.Target} matches nothing";
            // Structures have no names of their own in the game.
            else if (task.Type == "visit" && !facts.HasLang(TaskDef.StructureNameKey(task.Target))) yield return $"visit task target {task.Target} has no name: no lang entry {TaskDef.StructureNameKey(task.Target)}";
        }

        foreach (var reward in quest.Rewards)
        {
            if (!RewardTypes.Contains(reward.Type ?? "")) yield return $"unknown reward type '{reward.Type}'";
            else if (reward.Type == "choice" && reward.Options.Count == 0) yield return "choice reward has no options";
            else if (reward.Type == "stage" && string.IsNullOrEmpty(reward.Stage)) yield return "stage reward names no stage";

            foreach (var item in reward.Type == "choice" ? reward.Options : [reward])
            {
                if (item.Type == "item" && !CollectibleExists(item.Item)) yield return $"reward item {item.Item} is not an item or block";
            }
        }
    }

    /// <summary>Page problems are warnings: the book skips a block it can't show, and the quest still works.</summary>
    IEnumerable<string> PageWarnings(QuestDef quest)
    {
        foreach (var block in quest.Page)
        {
            switch (block.Type)
            {
                case "text":
                    if (!HasText(block.Lang)) yield return $"page text {block.Lang} has no lang entry";
                    break;
                case "image":
                    if (block.Src == null)
                    {
                        yield return "page image has no src";
                        break;
                    }
                    foreach (var w in TextureWarnings("page image", block.Src)) yield return w;
                    break;
                case "items":
                    foreach (var code in block.Items.Where(c => !CollectibleExists(c))) yield return $"page item {code} is not an item or block";
                    break;
                case "recipe":
                    if (!CollectibleExists(block.Output)) yield return $"page recipe output {block.Output} is not an item or block";
                    break;
                default:
                    yield return $"unknown page block type '{block.Type}'";
                    break;
            }
        }
    }

    /// <summary>Art problems are warnings too: the canvas skips what it can't draw.</summary>
    IEnumerable<string> ArtWarnings(ChapterDef chapter)
    {
        if (chapter.Background != null) foreach (var w in TextureWarnings("background", chapter.Background)) yield return w;
        foreach (var a in chapter.Art)
        {
            switch (a.Type)
            {
                case "image":
                    if (a.Src == null) yield return "art image has no src";
                    else foreach (var w in TextureWarnings("art image", a.Src)) yield return w;
                    break;
                case "text":
                    if (!HasText(a.Text)) yield return $"art text {a.Text} has no lang entry";
                    break;
                case "arrow":
                    if (a.To is not { Length: 2 }) yield return "art arrow has no 'to'";
                    break;
                default:
                    yield return $"unknown art type '{a.Type}'";
                    break;
            }
        }
    }

    IEnumerable<string> TextureWarnings(string what, string src)
    {
        var data = facts.Texture(PageBlock.TexturePath(src));
        if (data == null) yield return $"{what} {src} not found (looked for {PageBlock.TexturePath(src)})";
        else if (PngSize(data) is var (w, h) && (w > MaxImageSize || h > MaxImageSize))
        {
            yield return $"{what} {src} is {w}×{h}; over {MaxImageSize} px it costs GPU memory on every client";
        }
    }

    const int MaxImageSize = 2048;

    /// <summary>Width and height from a PNG's header, or null if it isn't one.</summary>
    static (int, int)? PngSize(byte[] data)
    {
        if (data == null || data.Length < 24 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G') return null;
        int Read(int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
        return (Read(16), Read(20));
    }

    bool TargetExists(TaskDef task)
    {
        if (task.OnEntity) return facts.AnyMatches("entity", task.Target);
        if (task.Type == "visit") return facts.AnyMatches("structure", task.Target);
        if (BlockTasks.Contains(task.Type)) return facts.AnyMatches("block", task.Target);
        return facts.AnyMatches("collectible", task.Target);
    }

    bool CollectibleExists(string code) => facts.CollectibleExists(code);

    /// <summary>Quests on a requirement cycle, found by depth-first search over quest-to-prerequisite edges.</summary>
    static IEnumerable<string> FindCycles(QuestRegistry registry)
    {
        var state = new Dictionary<string, int>();   // 1 on the stack, 2 finished
        var stack = new List<string>();
        var onCycle = new HashSet<string>();

        void Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (var pre in registry.Get(id)?.Requires?.QuestIds() ?? [])
            {
                if (!registry.Quests.ContainsKey(pre)) continue;
                if (!state.TryGetValue(pre, out int s)) Visit(pre);
                else if (s == 1) onCycle.UnionWith(stack.Skip(stack.IndexOf(pre)));
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        foreach (var id in registry.Quests.Keys)
        {
            if (!state.ContainsKey(id)) Visit(id);
        }
        return onCycle;
    }

    void Problem(string message)
    {
        Problems.Add(message);
        logger.Warning("[questline] " + message);
    }

    /// <summary>Asks whether a requirement could ever hold: every quest done, every stage reached.</summary>
    class EverythingDoneContext(QuestRegistry registry, IGameFacts facts) : IRequirementContext
    {
        public bool IsQuestComplete(string questId) => registry.Quests.ContainsKey(questId);
        public bool IsModLoaded(string modId) => facts.IsModLoaded(modId);
        public bool HasStage(string stage) => true;
        public bool IsChapterComplete(string chapterId) => registry.ChaptersById.ContainsKey(chapterId);
        public bool HasDiscovered(string pattern) => true;
    }
}
