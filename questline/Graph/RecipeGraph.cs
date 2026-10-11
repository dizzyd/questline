using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace questline.Graph;

/// <summary>One thing a recipe takes: any one of <see cref="Codes"/> (which may carry wildcards), so many of it.</summary>
public class GraphInput
{
    public List<string> Codes = [];
    public int Quantity = 1;
    /// <summary>Used but not used up, like a knife in a grid recipe.</summary>
    public bool Tool;

    /// <summary>The game's own ingredient, for matching with its variant rules. Not saved.</summary>
    [JsonIgnore] internal IRecipeIngredientBase Ingredient;
}

public class GraphOutput
{
    public string Code;
    public int Quantity = 1;
}

/// <summary>
/// One way to make something: a recipe, or a property of an item such as what it smelts or
/// dries into. <see cref="Kind"/> names the way (grid, smithing, heat-smelt, transition-dry, drop...).
/// </summary>
public class GraphEdge
{
    public string Kind;
    /// <summary>Where it came from: the recipe's name or code, or the item whose property it is.</summary>
    public string Source;
    public List<GraphInput> Inputs = [];
    public List<GraphOutput> Outputs = [];
}

/// <summary>
/// The game's recipes as a graph of item codes: what each thing is made by, and what it's used
/// in. The handbook keeps this only as rendered text, so it is scanned here from the same
/// sources: every recipe registry, and every item property that turns one thing into another.
/// Built once per side on first use; a few thousand edges for vanilla.
/// </summary>
public class RecipeGraph
{
    public List<GraphEdge> Edges = [];
    /// <summary>How long the build took, in milliseconds.</summary>
    [JsonIgnore] public double BuildMs;

    readonly Dictionary<string, List<GraphEdge>> madeBy = [];
    readonly Dictionary<string, List<GraphEdge>> usedInExact = [];
    readonly List<(GraphEdge Edge, GraphInput Input)> wildcardInputs = [];

    public IReadOnlyList<GraphEdge> MadeBy(string code) => madeBy.TryGetValue(code, out var list) ? list : [];

    /// <summary>Every edge with an input this collectible satisfies.</summary>
    public IEnumerable<GraphEdge> UsedIn(CollectibleObject collectible)
    {
        string code = collectible.Code.ToString();
        var exact = usedInExact.TryGetValue(code, out var list) ? list : [];
        var stack = new ItemStack(collectible);
        var wild = wildcardInputs.Where(w => Matches(w.Input, collectible, stack)).Select(w => w.Edge);
        return exact.Concat(wild).Distinct();
    }

    /// <summary>Whether an input takes this code: by the game's ingredient rules when there's a game, else by code and wildcard.</summary>
    public static bool Takes(GraphInput input, string code) =>
        input.Codes.Any(c => c == code || c == "*:*" || (c.Contains('*') && WildcardUtil.Match(new AssetLocation(c), new AssetLocation(code))));

    static bool Matches(GraphInput input, CollectibleObject collectible, ItemStack stack)
    {
        if (input.Ingredient != null) return input.Ingredient.SatisfiesAsIngredient(stack, false);
        return input.Codes.Any(c => WildcardUtil.Match(new AssetLocation(c), collectible.Code));
    }

    public Dictionary<string, int> CountByKind() => Edges.GroupBy(e => e.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    void Add(GraphEdge edge)
    {
        if (edge.Outputs.Count == 0) return;
        Edges.Add(edge);
        foreach (var output in edge.Outputs)
        {
            if (!madeBy.TryGetValue(output.Code, out var made)) madeBy[output.Code] = made = [];
            made.Add(edge);
        }
        foreach (var input in edge.Inputs)
        {
            // One plain code is looked up directly; wildcards and tag ingredients are matched per query.
            if (input.Codes.Count == 1 && !input.Codes[0].Contains('*') && (input.Ingredient == null || input.Ingredient.ResolvedItemStack != null))
            {
                if (!usedInExact.TryGetValue(input.Codes[0], out var used)) usedInExact[input.Codes[0]] = used = [];
                used.Add(edge);
            }
            else wildcardInputs.Add((edge, input));
        }
    }

    /// <summary>Adds an edge and indexes it. The builder calls this; so does loading from JSON.</summary>
    internal void AddEdge(GraphEdge edge) => Add(edge);

    /// <summary>A graph saved with <see cref="ToJson"/>, indexed again. Inputs match by code and wildcard only.</summary>
    public static RecipeGraph FromJson(string json)
    {
        var saved = JsonConvert.DeserializeObject<RecipeGraph>(json);
        var graph = new RecipeGraph();
        foreach (var edge in saved.Edges) graph.Add(edge);
        return graph;
    }

    /// <summary>The recipes of one registry that count: enabled, with an output that resolved.</summary>
    public static IEnumerable<T> Countable<T>(IEnumerable<T> recipes) where T : RecipeBase =>
        recipes.Where(r => r.Enabled && r.RecipeOutput?.ResolvedItemStack != null);

#if !PACKCHECK
    /// <summary>Scans the running game; see <see cref="RecipeGraphBuilder"/>.</summary>
    public static RecipeGraph Build(ICoreAPI api) => new RecipeGraphBuilder(api).Build();
#endif

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
}
