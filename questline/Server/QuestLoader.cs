using System;
using System.Collections.Generic;
using System.Linq;
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
public class QuestLoader(ICoreServerAPI api)
{
    public static readonly HashSet<string> TaskTypes = ["obtain", "kill", "place", "break", "craft", "knap", "clayform", "smith", "cook", "smelt", "custom", "checkbox"];
    static readonly HashSet<string> BlockTasks = ["place", "break"];
    static readonly HashSet<string> RewardTypes = ["item", "choice", "stage"];

    public List<string> Problems { get; } = [];

    public QuestRegistry Load()
    {
        var registry = new QuestRegistry { Questlines = SelectActive(LoadQuestlines()) };
        var active = registry.Questlines.Select(q => q.Id).ToHashSet();

        foreach (var asset in api.Assets.GetMany("questline/chapters/"))
        {
            if (!asset.Name.EndsWith(".json")) continue;

            ChapterDef chapter;
            try
            {
                chapter = asset.ToObject<ChapterDef>();
            }
            catch (Exception e)
            {
                Problem($"{asset.Location}: can't read chapter: {e.Message}");
                continue;
            }

            if (!Qualify(chapter, asset)) continue;
            if (!chapter.RequiresMod.All(api.ModLoader.IsModEnabled)) continue;
            if (!active.Contains(chapter.Questline)) continue;
            if (registry.Chapters.Any(c => c.Id == chapter.Id))
            {
                Problem($"{asset.Location}: chapter {chapter.Id} is already defined; this copy is ignored");
                continue;
            }

            registry.Chapters.Add(chapter);
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

        registry.Index();
        Validate(registry);
        registry.Index();

        api.Logger.Notification("[questline] loaded {0} quests in {1} chapters across {2} questlines, {3} problems",
            registry.Quests.Count, registry.Chapters.Count, registry.Questlines.Count, Problems.Count);
        return registry;
    }

    List<QuestlineDef> LoadQuestlines()
    {
        var list = new List<QuestlineDef>();
        foreach (var asset in api.Assets.GetMany("questline/questline.json"))
        {
            try
            {
                var def = asset.ToObject<QuestlineDef>();
                def.Domain = asset.Location.Domain;
                def.Id = Requirement.QualifyId(def.Id ?? "default", def.Domain);
                list.Add(def);
            }
            catch (Exception e)
            {
                Problem($"{asset.Location}: can't read questline: {e.Message}");
            }
        }
        return list;
    }

    List<QuestlineDef> SelectActive(List<QuestlineDef> all)
    {
        var shipping = all.Select(q => q.Domain).ToHashSet();
        var active = all
            .Where(q => q.RequiresMod.All(api.ModLoader.IsModEnabled))
            .Where(q => !q.YieldsTo.Any(mod => mod != q.Domain && shipping.Contains(mod)))
            .ToList();

        if (active.Any(q => q.ReplacesDefault))
        {
            active.RemoveAll(q => q.Id == QuestRegistry.DefaultQuestline);
        }
        return active;
    }

    bool Qualify(ChapterDef chapter, IAsset asset)
    {
        string domain = asset.Location.Domain;
        chapter.Id = Requirement.QualifyId(chapter.Id ?? asset.Location.GetName().RemoveFileEnding(), domain);
        chapter.Questline = chapter.Questline == null ? QuestRegistry.DefaultQuestline : Requirement.QualifyId(chapter.Questline, domain);
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
            foreach (var reward in quest.Rewards.Where(r => r.Stage != null)) reward.Stage = Requirement.QualifyId(reward.Stage, domain);
        }
        return true;
    }

    void Validate(QuestRegistry registry)
    {
        var broken = new HashSet<string>();

        foreach (var quest in registry.Quests.Values)
        {
            var errors = QuestErrors(quest, registry).ToList();
            foreach (var e in errors) Problem($"{quest.Id}: {e}; quest disabled");
            if (errors.Count > 0) broken.Add(quest.Id);

            if (quest.Icon != null && !CollectibleExists(quest.Icon)) Problem($"{quest.Id}: icon {quest.Icon} is not an item or block");
            foreach (var warning in PageWarnings(quest)) Problem($"{quest.Id}: {warning}");
            if (!Lang.HasTranslation(quest.Id + "-title", true, false)) Problem($"{quest.Id}: no lang entry {quest.Id}-title");
        }

        foreach (var id in FindCycles(registry))
        {
            Problem($"{id}: its requirements form a cycle; quest disabled");
            broken.Add(id);
        }

        foreach (var chapter in registry.Chapters) chapter.Quests.RemoveAll(q => broken.Contains(q.Id));
        registry.Index();

        var allDone = new EverythingDoneContext(registry, api);
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
                    if (block.Lang == null || !Lang.HasTranslation(block.Lang, true, false)) yield return $"page text {block.Lang} has no lang entry";
                    break;
                case "image":
                    if (block.Src == null)
                    {
                        yield return "page image has no src";
                        break;
                    }
                    var asset = api.Assets.TryGet(new AssetLocation(PageBlock.TexturePath(block.Src)));
                    if (asset == null) yield return $"page image {block.Src} not found (looked for {PageBlock.TexturePath(block.Src)})";
                    else if (PngSize(asset.Data) is var (w, h) && (w > MaxImageSize || h > MaxImageSize))
                    {
                        yield return $"page image {block.Src} is {w}×{h}; over {MaxImageSize} px it costs GPU memory on every client";
                    }
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
        var loc = new AssetLocation(task.Target);
        if (task.Type == "kill") return api.World.EntityTypes.Any(e => WildcardUtil.Match(loc, e.Code));
        if (BlockTasks.Contains(task.Type)) return api.World.Blocks.Any(b => b.Code != null && WildcardUtil.Match(loc, b.Code));
        return api.World.Collectibles.Any(c => c.Code != null && WildcardUtil.Match(loc, c.Code));
    }

    bool CollectibleExists(string code)
    {
        if (code == null) return false;
        var loc = new AssetLocation(code);
        return api.World.GetItem(loc) != null || api.World.GetBlock(loc) != null;
    }

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
        api.Logger.Warning("[questline] " + message);
    }

    /// <summary>Asks whether a requirement could ever hold: every quest done, every stage reached.</summary>
    class EverythingDoneContext(QuestRegistry registry, ICoreAPI api) : IRequirementContext
    {
        public bool IsQuestComplete(string questId) => registry.Quests.ContainsKey(questId);
        public bool IsModLoaded(string modId) => api.ModLoader.IsModEnabled(modId);
        public bool HasStage(string stage) => true;
        public bool IsChapterComplete(string chapterId) => registry.ChaptersById.ContainsKey(chapterId);
    }
}
