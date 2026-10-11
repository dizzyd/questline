using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace questline.Client;

/// <summary>Player-facing words for quests: titles, task lines, reward names and requirements.</summary>
public class QuestText(ICoreClientAPI capi, QuestClient client)
{
    /// <summary>A lang entry. The editor's own text, sent with the definitions, comes first.</summary>
    public string Get(string key) => client.Registry?.Text.TryGetValue(key, out var value) == true ? value : Lang.Get(key);

    public bool Has(string key) => client.Registry?.Text.ContainsKey(key) == true || Lang.HasTranslation(key, true, false);

    public string Title(string questId) => Get(questId + "-title");

    public string Description(string questId) => Has(questId + "-desc") ? Get(questId + "-desc") : null;

    public string ChapterTitle(string chapterId) => Get(chapterId + "-title");

    public string QuestlineTitle(string questlineId) => Lang.Get(questlineId + "-title");

    public string State(QuestState state) => Lang.Get("questline:state-" + state.ToString().ToLowerInvariant());

    /// <summary>A stack standing for a task or reward code: the item itself, or the first match of a wildcard.</summary>
    public ItemStack StackFor(string code, int size = 1)
    {
        if (code == null) return null;
        var loc = new AssetLocation(code);
        // "Anything at all" has no one item that stands for it.
        if (loc.Path == "*") return null;
        CollectibleObject collectible = capi.World.GetItem(loc) ?? (CollectibleObject)capi.World.GetBlock(loc);
        collectible ??= capi.World.Collectibles.FirstOrDefault(c => c.Code != null && WildcardUtil.Match(loc, c.Code));
        return collectible == null ? null : new ItemStack(collectible, size);
    }

    /// <summary>A stage or custom key's own lang entry (<c>&lt;name&gt;-title</c>) if it has one, or the name itself.</summary>
    string Named(string name) => Has(name + "-title") ? Get(name + "-title") : name;

    public string TaskLine(TaskDef task)
    {
        if (task.Type == "checkbox") return Lang.Get("questline:task-checkbox");
        if (task.Type == "custom") return Named(task.Key);
        return Lang.Get("questline:task-" + task.Type, TargetName(task));
    }

    string TargetName(TaskDef task)
    {
        bool wildcard = task.Target.Contains('*');
        if (task.Type == "kill")
        {
            // Creatures are named by their spawn item, item-creature-<code>. A wildcard such as
            // chicken-* is named by what comes before the star: a first match would say "Rooster".
            var loc = new AssetLocation(task.Target);
            if (wildcard)
            {
                string prefix = loc.Path[..loc.Path.IndexOf('*')].TrimEnd('-');
                string key = loc.Domain + ":item-creature-" + prefix;
                string kind = Lang.HasTranslation(key, true, false) ? Lang.Get(key) : prefix.Replace('-', ' ');
                return Lang.Get("questline:any-of", kind);
            }
            return capi.World.EntityTypes.Any(e => e.Code.Equals(loc)) ? Lang.Get(loc.Domain + ":item-creature-" + loc.Path) : task.Target;
        }

        var stack = StackFor(task.Target);
        if (stack == null) return task.Target;
        string name = stack.GetName();
        return wildcard ? Lang.Get("questline:any-of", name) : name;
    }

    public string RewardLine(RewardDef reward)
    {
        if (reward.Type == "stage") return Lang.Get("questline:reward-stage", Named(reward.Stage));
        var stack = StackFor(reward.Item, reward.Count);
        return $"{reward.Count}× {stack?.GetName() ?? reward.Item}";
    }

    /// <summary>A requirement in words: "Gather flint and (Dig in or Lay a stone)".</summary>
    public string Describe(Requirement r, bool nested = false)
    {
        string text = r switch
        {
            QuestRequirement q => client.Registry?.Get(q.QuestId) != null ? Title(q.QuestId) : q.QuestId,
            ModRequirement m => Lang.Get("questline:req-mod", m.ModId),
            StageRequirement s => Lang.Get("questline:req-stage", Named(s.Stage)),
            ChapterCompleteRequirement c => Lang.Get("questline:req-chapter", ChapterTitle(c.ChapterId)),
            DiscoveredRequirement => Lang.Get("questline:req-discovered"),
            AllRequirement a => string.Join(Lang.Get("questline:req-and"), a.Of.Select(x => Describe(x, true))),
            AnyRequirement a => string.Join(Lang.Get("questline:req-or"), a.Of.Select(x => Describe(x, true))),
            AtLeastRequirement a => Lang.Get("questline:req-atleast", a.N, string.Join(", ", a.Of.Select(x => Describe(x, true)))),
            _ => "?",
        };
        return nested && r is CompositeRequirement { Of.Count: > 1 } ? $"({text})" : text;
    }
}
