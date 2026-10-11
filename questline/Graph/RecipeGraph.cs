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

    #region Building

    public static RecipeGraph Build(ICoreAPI api)
    {
        var watch = Stopwatch.StartNew();
        var graph = new RecipeGraph();
        var world = api.World;

        graph.AddRecipes("grid", world.GridRecipes);
        graph.AddRecipes("smithing", api.GetSmithingRecipes());
        graph.AddRecipes("knapping", api.GetKnappingRecipes());
        graph.AddRecipes("clayforming", api.GetClayformingRecipes());
        graph.AddRecipes("barrel", api.GetBarrelRecipes());
        graph.AddCooking(api);
        graph.AddAlloys(api);

        foreach (var collectible in world.Collectibles)
        {
            if (collectible?.Code == null) continue;
            try
            {
                graph.AddProperties(world, collectible);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[questline] recipe graph: skipped the properties of {0}: {1}", collectible.Code, e.Message);
            }
        }
        foreach (var entity in world.EntityTypes) graph.AddEntityDrops(world, entity);

        graph.BuildMs = watch.Elapsed.TotalMilliseconds;
        api.Logger.Notification("[questline] recipe graph: {0} edges in {1:0} ms ({2})", graph.Edges.Count, graph.BuildMs,
            string.Join(", ", graph.CountByKind().Select(k => $"{k.Key} {k.Value}")));
        return graph;
    }

    /// <summary>The recipes of one registry that count: enabled, with an output that resolved.</summary>
    public static IEnumerable<T> Countable<T>(IEnumerable<T> recipes) where T : RecipeBase =>
        recipes.Where(r => r.Enabled && r.RecipeOutput?.ResolvedItemStack != null);

    void AddRecipes<T>(string kind, IEnumerable<T> recipes) where T : RecipeBase
    {
        foreach (var recipe in Countable(recipes ?? []))
        {
            var edge = new GraphEdge { Kind = kind, Source = recipe.Name?.ToString() ?? $"{kind}#{recipe.RecipeId}" };
            foreach (var ingredient in recipe.RecipeIngredients)
            {
                if (ingredient?.Code == null) continue;
                edge.Inputs.Add(new GraphInput
                {
                    Codes = [ingredient.Code.ToString()],
                    Quantity = ingredient.Quantity,
                    Tool = ingredient is CraftingRecipeIngredient { IsTool: true },
                    Ingredient = ingredient,
                });
            }
            Output(edge, recipe.RecipeOutput.ResolvedItemStack);
            Add(edge);
        }
    }

    void AddCooking(ICoreAPI api)
    {
        foreach (var recipe in api.GetCookingRecipes() ?? [])
        {
            if (!recipe.Enabled || recipe.Ingredients == null) continue;
            var edge = new GraphEdge { Kind = "cooking", Source = recipe.Code };
            foreach (var ingredient in recipe.Ingredients)
            {
                edge.Inputs.Add(new GraphInput
                {
                    Codes = ingredient.ValidStacks.Where(s => s.Code != null).Select(s => s.Code.ToString()).Distinct().ToList(),
                    Quantity = Math.Max(1, ingredient.MinQuantity),
                });
            }
            // A meal has no item of its own: it's named by its recipe, in the cooking pot's domain.
            if (recipe.CooksInto?.ResolvedItemstack is { } into) Output(edge, into);
            else edge.Outputs.Add(new GraphOutput { Code = "meal:" + recipe.Code });
            Add(edge);
        }
    }

    void AddAlloys(ICoreAPI api)
    {
        foreach (var alloy in api.GetMetalAlloys() ?? [])
        {
            if (!alloy.Enabled || alloy.Output?.ResolvedItemstack == null) continue;
            var edge = new GraphEdge { Kind = "alloy", Source = alloy.Output.Code.ToString() };
            foreach (var ingredient in alloy.Ingredients)
            {
                if (ingredient.ResolvedItemstack?.Collectible?.Code is { } code) edge.Inputs.Add(new GraphInput { Codes = [code.ToString()] });
            }
            Output(edge, alloy.Output.ResolvedItemstack);
            Add(edge);
        }
    }

    /// <summary>Everything an item turns into on its own or in a simple device.</summary>
    void AddProperties(IWorldAccessor world, CollectibleObject c)
    {
        var stack = new ItemStack(c);
        string code = c.Code.ToString();

        var heat = c.GetCombustibleProperties(world, stack, null);
        if (heat?.SmeltedStack?.ResolvedItemstack is { } smelted)
        {
            Single("heat-" + heat.SmeltingType.ToString().ToLowerInvariant(), code, heat.SmeltedRatio, smelted);
        }
        if (c.GetGrindingProperties(world, stack)?.GroundStack?.ResolvedItemstack is { } ground) Single("grind", code, 1, ground);
        if (c.GetCrushingProperties(world, stack)?.CrushedStack?.ResolvedItemstack is { } crushed) Single("crush", code, 1, crushed);

        foreach (var transition in c.GetTransitionableProperties(world, stack, null) ?? [])
        {
            if (transition?.TransitionedStack?.ResolvedItemstack is { } into)
            {
                Single("transition-" + transition.Type.ToString().ToLowerInvariant(), code, 1, into);
            }
        }

        var attributes = c.Attributes;
        if (attributes?["juiceableProperties"].Exists == true)
        {
            var juice = attributes["juiceableProperties"].AsObject<JuiceableProperties>(null, c.Code.Domain);
            var outs = new[] { juice?.LiquidStack, juice?.PressedStack, juice?.ReturnStack }
                .Where(j => j != null && j.Resolve(world, "questline recipe graph"))
                .Select(j => j.ResolvedItemstack).Where(s => s != null && s.Collectible != c).ToArray();
            if (outs.Length > 0) Multi("juice", code, outs);
        }
        if (attributes?["distillationProps"].Exists == true)
        {
            var distil = attributes["distillationProps"].AsObject<DistillationProps>(null, c.Code.Domain);
            if (distil?.DistilledStack != null && distil.DistilledStack.Resolve(world, "questline recipe graph")) Single("distill", code, 1, distil.DistilledStack.ResolvedItemstack);
        }
        if (attributes?["carburizableProps"]?["carburizedOutput"].Exists == true)
        {
            var carburized = attributes["carburizableProps"]["carburizedOutput"].AsObject<JsonItemStack>(null, c.Code.Domain);
            if (carburized?.Resolve(world, "questline recipe graph") == true) Single("carburize", code, 1, carburized.ResolvedItemstack);
        }
        if (attributes?["beehivekiln"].Exists == true)
        {
            foreach (var fired in attributes["beehivekiln"].AsObject<Dictionary<string, JsonItemStack>>(null, c.Code.Domain)?.Values ?? Enumerable.Empty<JsonItemStack>())
            {
                if (fired?.Resolve(world, "questline recipe graph") == true) Single("kiln", code, 1, fired.ResolvedItemstack);
            }
        }

        if (c is ItemOre && c.Variant["ore"] is { } ore && world.GetItem(new AssetLocation("nugget-" + ore)) is { } nugget)
        {
            Single("smash", code, 1, new ItemStack(nugget));
        }

        if (c.GetCollectibleBehavior<CollectibleBehaviorSqueezable>(true) is { } squeeze)
        {
            var outs = (squeeze.ReturnStacks ?? []).Where(s => s?.ResolvedItemstack != null).Select(s => s.ResolvedItemstack)
                .Concat(squeeze.SqueezedLiquid != null ? [new ItemStack(squeeze.SqueezedLiquid)] : []).ToArray();
            if (outs.Length > 0) Multi("squeeze", code, outs);
        }
        if (c.GetCollectibleBehavior<CollectibleBehaviorGroundStoredProcessable>(true) is { } processable)
        {
            var outs = (processable.ProcessedStacks ?? []).Where(s => s?.ResolvedItemstack != null).Select(s => s.ResolvedItemstack)
                .Concat(processable.RemainingItem?.ResolvedItemstack is { } rest ? [rest] : []).ToArray();
            if (outs.Length > 0) Multi("groundprocess", code, outs);
        }

        // What a block drops, when that's something other than itself.
        if (c is Block block && block.Drops != null)
        {
            var drops = block.Drops.Where(d => d?.ResolvedItemstack != null && d.ResolvedItemstack.Collectible != c).Select(d => d.ResolvedItemstack).ToArray();
            if (drops.Length > 0) Multi("drop", code, drops);
        }
    }

    void AddEntityDrops(IWorldAccessor world, EntityProperties entity)
    {
        if (entity?.Code == null) return;
        string code = entity.Code.ToString();
        var drops = (entity.Drops ?? []).Where(d => d?.ResolvedItemstack != null).Select(d => d.ResolvedItemstack).ToList();
        if (drops.Count > 0) Multi("entity-drop", code, drops.ToArray());

        // What butchering gives, from the harvestable behaviour's config.
        foreach (var behavior in entity.Server?.BehaviorsAsJsonObj ?? [])
        {
            if (behavior?["code"].AsString() != "harvestable" || !behavior["drops"].Exists) continue;
            var harvested = behavior["drops"].AsObject<BlockDropItemStack[]>(null, entity.Code.Domain) ?? [];
            var stacks = harvested.Where(d => d != null && d.Resolve(world, "questline recipe graph", entity.Code)).Select(d => d.ResolvedItemstack).Where(s => s != null).ToArray();
            if (stacks.Length > 0) Multi("butcher", code, stacks);
        }
    }

    void Single(string kind, string input, int quantity, ItemStack output)
    {
        var edge = new GraphEdge { Kind = kind, Source = input, Inputs = [new GraphInput { Codes = [input], Quantity = Math.Max(1, quantity) }] };
        Output(edge, output);
        Add(edge);
    }

    void Multi(string kind, string input, ItemStack[] outputs)
    {
        var edge = new GraphEdge { Kind = kind, Source = input, Inputs = [new GraphInput { Codes = [input] }] };
        foreach (var output in outputs) Output(edge, output);
        Add(edge);
    }

    static void Output(GraphEdge edge, ItemStack stack)
    {
        if (stack?.Collectible?.Code == null) return;
        string code = stack.Collectible.Code.ToString();
        if (edge.Outputs.Any(o => o.Code == code)) return;
        edge.Outputs.Add(new GraphOutput { Code = code, Quantity = Math.Max(1, stack.StackSize) });
    }

    #endregion

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
}
