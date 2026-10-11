using System;
using System.Collections.Generic;
using System.Linq;
using questline.Graph;
using questline.Model;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace questline.Server;

/// <summary>
/// Checks that need the recipe graph, run once the game's recipes are loaded (and by the pack
/// checker, from a saved graph):
/// <list type="bullet">
/// <item>a task asks for something nothing in the game makes, drops or places;</item>
/// <item>a craft, smith, knap, clay-form, cook or smelt task asks for something not made that way;</item>
/// <item>a quest asks for something that needs an item the questline only asks for later;</item>
/// <item>a default-questline quest names a story item without a "discovered" gate.</item>
/// </list>
/// All are warnings: the quests still load.
/// </summary>
public class GraphChecks
{
    /// <summary>Codes that give away the story. The default questline never names them unless the player has found them first.</summary>
    public static readonly string[] Spoilers =
    [
        "game:jonas*", "game:eidolongearbox*", "game:nightvisiondevice*", "game:mechhelper*", "game:rustypart*",
        "game:tobtlocatorpart*", "game:lore*", "game:schematic*", "game:resonancearchive*", "game:statictranslocator*",
        "game:devgrowth*", "game:devplate*", "game:dpanel*", "game:devastationgrowth*", "game:devastatedsoil*",
        "game:clutter-devastation*", "game:eidolon*", "game:erel*",
    ];

    // The graph edge kinds each station task is credited from.
    static readonly Dictionary<string, string[]> MethodKinds = new()
    {
        ["craft"] = ["grid"],
        ["smith"] = ["smithing"],
        ["knap"] = ["knapping"],
        ["clayform"] = ["clayforming"],
        ["cook"] = ["heat-cook", "heat-bake", "heat-smelt", "heat-convert", "heat-fire", "cooking"],
        ["smelt"] = ["cast", "heat-smelt"],
    };
    static readonly HashSet<string> ItemTasks = ["obtain", "craft", "smith", "knap", "clayform", "cook", "smelt"];

    readonly QuestRegistry registry;
    readonly RecipeGraph graph;
    readonly string[] allCodes;   // sorted, for prefix search
    readonly HashSet<string> obtainable;

    public List<string> Problems { get; } = [];

    /// <param name="blocks">Every block code: anything placed in the world can be had.</param>
    /// <param name="items">Every item code.</param>
    /// <param name="entities">Every creature code: their drops can be had.</param>
    public GraphChecks(QuestRegistry registry, RecipeGraph graph, IEnumerable<string> blocks, IEnumerable<string> items, IEnumerable<string> entities)
    {
        this.registry = registry;
        this.graph = graph;
        var blockList = blocks.ToList();
        var entityList = entities.ToList();
        allCodes = blockList.Concat(items).Concat(entityList).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray();
        obtainable = Obtainable(blockList.Concat(entityList));
    }

    public List<string> Run()
    {
        var later = LaterTargets();
        foreach (var quest in registry.Quests.Values)
        {
            foreach (var task in quest.Tasks.Where(t => ItemTasks.Contains(t.Type) && t.Target != null))
            {
                var matches = Candidates(task.Target).ToList();
                if (matches.Count == 0) continue;   // the loader already said it matches nothing

                if (!matches.Any(obtainable.Contains))
                {
                    Problems.Add($"{quest.Id}: nothing in the game makes, drops or places {task.Target}");
                    continue;
                }
                if (MethodKinds.TryGetValue(task.Type, out var kinds) && !matches.Any(code => graph.MadeBy(code).Any(e => kinds.Contains(e.Kind))))
                {
                    Problems.Add($"{quest.Id}: {task.Type} task for {task.Target}, which nothing makes that way");
                }
                if (TooEarly(quest, matches, later) is { } why) Problems.Add($"{quest.Id}: {why}");
            }
        }

        foreach (var quest in registry.Quests.Values.Where(q => registry.ChaptersById.TryGetValue(q.ChapterId, out var c) && c.Questline == QuestRegistry.DefaultQuestline))
        {
            foreach (var why in SpoilerProblems(quest)) Problems.Add($"{quest.Id}: {why}");
        }
        return Problems;
    }

    #region Obtainable

    readonly Dictionary<string, string[]> matchCache = [];

    /// <summary>
    /// Everything that can be had: the seeds, then whatever an edge makes once all its inputs can
    /// be had. A worklist: each code, once had, wakes only the edges waiting on it, and each
    /// wildcard is matched against the known codes once.
    /// </summary>
    HashSet<string> Obtainable(IEnumerable<string> seeds)
    {
        var have = new HashSet<string>();
        var queue = new Queue<string>();
        var waiting = new Dictionary<string, List<(int Edge, int Input)>>();
        var unmet = new int[graph.Edges.Count];
        var inputMet = new List<bool[]>(graph.Edges.Count);

        void Gain(string code)
        {
            if (have.Add(code)) queue.Enqueue(code);
        }

        for (int e = 0; e < graph.Edges.Count; e++)
        {
            var edge = graph.Edges[e];
            var met = new bool[edge.Inputs.Count];
            inputMet.Add(met);
            for (int i = 0; i < edge.Inputs.Count; i++)
            {
                var candidates = edge.Inputs[i].Codes.SelectMany(Candidates).ToList();
                if (edge.Inputs[i].Codes.Contains("*:*") || candidates.Count == 0 && edge.Inputs[i].Codes.Any(c => c.Contains('*') && !c.Contains(':')))
                {
                    met[i] = true;
                    continue;
                }
                unmet[e]++;
                foreach (string code in candidates)
                {
                    if (!waiting.TryGetValue(code, out var list)) waiting[code] = list = [];
                    list.Add((e, i));
                }
            }
            if (unmet[e] == 0) foreach (var output in edge.Outputs) Gain(output.Code);
        }

        foreach (string seed in seeds) Gain(seed);
        while (queue.Count > 0)
        {
            if (!waiting.TryGetValue(queue.Dequeue(), out var list)) continue;
            foreach (var (e, i) in list)
            {
                if (inputMet[e][i]) continue;
                inputMet[e][i] = true;
                if (--unmet[e] == 0) foreach (var output in graph.Edges[e].Outputs) Gain(output.Code);
            }
        }
        return have;
    }

    string[] Candidates(string pattern)
    {
        if (!matchCache.TryGetValue(pattern, out var codes)) matchCache[pattern] = codes = pattern.Contains('*') ? Matching(pattern).ToArray() : [pattern.Contains(':') ? pattern : "game:" + pattern];
        return codes;
    }

    /// <summary>Whether anything matching this code can be had.</summary>
    public bool IsObtainable(string pattern) => Candidates(pattern).Any(obtainable.Contains);

    /// <summary>The known codes a pattern matches, found by the part before its first wildcard.</summary>
    IEnumerable<string> Matching(string pattern)
    {
        if (!pattern.Contains(':')) pattern = "game:" + pattern;
        int star = pattern.IndexOf('*');
        if (star < 0)
        {
            if (Array.BinarySearch(allCodes, pattern, StringComparer.Ordinal) >= 0) yield return pattern;
            yield break;
        }

        string prefix = pattern[..star];
        var loc = new AssetLocation(pattern);
        int i = Array.BinarySearch(allCodes, prefix, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        // A star in the domain matches everywhere; otherwise only codes starting with the prefix can.
        int start = prefix.Contains(':') ? i : 0;
        for (int j = start; j < allCodes.Length; j++)
        {
            string code = allCodes[j];
            if (prefix.Contains(':') && !code.StartsWith(prefix, StringComparison.Ordinal)) break;
            if (WildcardUtil.Match(loc, new AssetLocation(code))) yield return code;
        }
    }

    #endregion

    #region Order

    /// <summary>For each code a task asks for, the quests that ask for it.</summary>
    Dictionary<string, List<QuestDef>> LaterTargets()
    {
        var byCode = new Dictionary<string, List<QuestDef>>();
        foreach (var quest in registry.Quests.Values)
        {
            foreach (var task in quest.Tasks.Where(t => ItemTasks.Contains(t.Type) && t.Target != null && !t.Target.Contains('*')))
            {
                if (!byCode.TryGetValue(task.Target, out var list)) byCode[task.Target] = list = [];
                list.Add(quest);
            }
        }
        return byCode;
    }

    /// <summary>
    /// Why a quest's item can't be made yet, if it can't: every way of making every code it
    /// accepts needs an input that only quests after this one ask for. One level deep.
    /// </summary>
    string TooEarly(QuestDef quest, List<string> matches, Dictionary<string, List<QuestDef>> askedFor)
    {
        var after = Descendants(quest);
        string reason = null;
        foreach (string code in matches)
        {
            var ways = graph.MadeBy(code);
            if (ways.Count == 0) return null;
            foreach (var edge in ways)
            {
                var blocker = edge.Inputs.FirstOrDefault(input => input.Codes.All(c =>
                    askedFor.TryGetValue(c, out var quests) && quests.All(q => after.Contains(q.Id))));
                if (blocker == null) return null;
                string blockerCode = blocker.Codes[0];
                reason ??= $"{code} needs {blockerCode}, which the questline only asks for later, in {askedFor[blockerCode][0].Id}";
            }
        }
        return reason;
    }

    HashSet<string> Descendants(QuestDef quest)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>([quest.Id]);
        while (stack.Count > 0)
        {
            foreach (var next in registry.Dependents.GetValueOrDefault(stack.Pop()) ?? [])
            {
                if (seen.Add(next.Id)) stack.Push(next.Id);
            }
        }
        return seen;
    }

    #endregion

    #region Spoilers

    IEnumerable<string> SpoilerProblems(QuestDef quest)
    {
        var gated = quest.Requires?.Walk().OfType<DiscoveredRequirement>().Select(d => d.Pattern).ToList() ?? [];
        var named = quest.Tasks.Select(t => t.Target)
            .Concat(quest.Rewards.SelectMany(r => r.Options.Select(o => o.Item).Append(r.Item)))
            .Concat(quest.Page.SelectMany(b => b.Items.Append(b.Output)))
            .Append(quest.Icon)
            .Where(c => c != null).Distinct();
        foreach (string code in named)
        {
            string spoiler = Spoilers.FirstOrDefault(s => Overlaps(s, code));
            if (spoiler == null) continue;
            if (gated.Any(g => Overlaps(g, code))) continue;
            yield return $"names {code}, a story item ({spoiler}), without a \"discovered\" gate on it";
        }
    }

    /// <summary>Whether two patterns could match the same code: either matches the other as written.</summary>
    static bool Overlaps(string a, string b)
    {
        var la = new AssetLocation(a);
        var lb = new AssetLocation(b);
        return WildcardUtil.Match(la, lb) || WildcardUtil.Match(lb, la) || a == b;
    }

    #endregion
}
