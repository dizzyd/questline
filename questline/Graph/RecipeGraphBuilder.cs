using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace questline.Graph;

/// <summary>
/// Scans the running game into a <see cref="RecipeGraph"/>: every recipe registry, and every item
/// property the handbook reads under "Created by". Kept apart from the graph so the pack checker
/// can load a saved graph without the game.
/// </summary>
public class RecipeGraphBuilder(ICoreAPI api)
{
    readonly RecipeGraph graph = new();
    readonly Dictionary<string, List<string>> meltsInto = [];
    List<string> cookedPots = [];

    public RecipeGraph Build()
    {
        var watch = Stopwatch.StartNew();
        var world = api.World;

        // What melts into each metal, so casting can take the metal in any form, not just as ingots.
        foreach (var c in world.Collectibles)
        {
            if (c?.Code == null) continue;
            try
            {
                if (c.GetCombustibleProperties(world, new ItemStack(c), null)?.SmeltedStack?.ResolvedItemstack is { Collectible.Code.Path: var path } smelted && path.StartsWith("ingot-"))
                {
                    string metal = smelted.Collectible.LastCodePart();
                    if (!meltsInto.TryGetValue(metal, out var list)) meltsInto[metal] = list = [];
                    list.Add(c.Code.ToString());
                }
            }
            catch (Exception)
            {
                // A collectible whose properties throw is reported when its own edges are scanned.
            }
        }
        cookedPots = world.Collectibles.Where(c => c is BlockCookedContainer).Select(c => c.Code.ToString()).ToList();

        AddRecipes("grid", world.GridRecipes);
        AddRecipes("smithing", api.GetSmithingRecipes());
        AddRecipes("knapping", api.GetKnappingRecipes());
        AddRecipes("clayforming", api.GetClayformingRecipes());
        AddRecipes("barrel", api.GetBarrelRecipes());
        AddCooking();
        AddAlloys();

        foreach (var collectible in world.Collectibles)
        {
            if (collectible?.Code == null) continue;
            try
            {
                AddProperties(world, collectible);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[questline] recipe graph: skipped the properties of {0}: {1}", collectible.Code, e.Message);
            }
        }
        foreach (var entity in world.EntityTypes) AddEntityDrops(world, entity);

        graph.BuildMs = watch.Elapsed.TotalMilliseconds;
        api.Logger.Notification("[questline] recipe graph: {0} edges in {1:0} ms ({2})", graph.Edges.Count, graph.BuildMs,
            string.Join(", ", graph.CountByKind().Select(k => $"{k.Key} {k.Value}")));
        return graph;
    }

    void AddRecipes<T>(string kind, IEnumerable<T> recipes) where T : RecipeBase
    {
        foreach (var recipe in RecipeGraph.Countable(recipes ?? []))
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
            graph.AddEdge(edge);
        }
    }

    void AddCooking()
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
            // A meal has no item of its own: it's named by its recipe, and taken from the fire in a cooked pot.
            if (recipe.CooksInto?.ResolvedItemstack is { } into) Output(edge, into);
            else
            {
                edge.Outputs.Add(new GraphOutput { Code = "meal:" + recipe.Code });
                foreach (string pot in cookedPots) edge.Outputs.Add(new GraphOutput { Code = pot });
            }
            graph.AddEdge(edge);
        }
    }

    void AddAlloys()
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
            graph.AddEdge(edge);
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

        // A tool mold casts a head or a part from any metal; its output names the metal as {metal}.
        if (c is BlockToolMold mold && mold.Attributes != null) AddCasting(world, mold);

        // What a block drops, when that's something other than itself. The handbook's list is used,
        // since it also covers drops a block works out itself, such as a loot vessel's.
        if (c is Block block)
        {
            BlockDropItemStack[] listed;
            try
            {
                listed = block.GetDropsForHandbook(stack, null) ?? block.Drops;
            }
            catch (Exception)
            {
                listed = block.Drops;
            }
            var drops = (listed ?? []).Where(d => d?.ResolvedItemstack != null && d.ResolvedItemstack.Collectible != c).Select(d => d.ResolvedItemstack).ToArray();
            if (drops.Length > 0) Multi("drop", code, drops);
        }
    }

    string[] metals;

    void AddCasting(IWorldAccessor world, Block mold)
    {
        metals ??= world.Items.Where(i => i?.Code != null && i.Code.Path.StartsWith("ingot-")).Select(i => i.LastCodePart()).Distinct().ToArray();
        var outputs = mold.Attributes["drop"].Exists ? [mold.Attributes["drop"].AsObject<JsonItemStack>(null, mold.Code.Domain)]
            : mold.Attributes["drops"].AsObject<JsonItemStack[]>(null, mold.Code.Domain) ?? [];
        foreach (string metal in metals)
        {
            var edge = new GraphEdge
            {
                Kind = "cast",
                Source = mold.Code.ToString(),
                Inputs = [new GraphInput { Codes = [$"game:ingot-{metal}", .. meltsInto.GetValueOrDefault(metal) ?? []] }, new GraphInput { Codes = [mold.Code.ToString()], Tool = true }],
            };
            foreach (var template in outputs)
            {
                if (template?.Code == null) continue;
                var stack = new JsonItemStack { Type = template.Type, Code = new AssetLocation(template.Code.Domain, template.Code.Path.Replace("{metal}", metal)), StackSize = template.StackSize };
                if (stack.Resolve(world, "questline recipe graph", false)) Output(edge, stack.ResolvedItemstack);
            }
            graph.AddEdge(edge);
        }
    }

    void AddEntityDrops(IWorldAccessor world, EntityProperties entity)
    {
        if (entity?.Code == null) return;
        string code = entity.Code.ToString();
        var drops = (entity.Drops ?? []).Where(d => d?.ResolvedItemstack != null).Select(d => d.ResolvedItemstack).ToList();
        if (drops.Count > 0) Multi("entity-drop", code, drops.ToArray());

        // What a trader sells.
        TradeProperties trade = null;
        try
        {
            string file = entity.Attributes?["tradePropsFile"].AsString();
            trade = file != null
                ? api.Assets.TryGet(AssetLocation.Create(file, entity.Code.Domain).WithPathAppendixOnce(".json"))?.ToObject<TradeProperties>()
                : entity.Attributes?["tradeProps"]?.AsObject<TradeProperties>();
        }
        catch (Exception e)
        {
            api.Logger.Warning("[questline] recipe graph: can't read what {0} trades: {1}", code, e.Message);
        }
        var sold = (trade?.Selling?.List ?? []).Where(t => t != null && t.Resolve(world, "questline recipe graph", false)).Select(t => t.ResolvedItemstack).Where(s => s != null).ToArray();
        if (sold.Length > 0) Multi("trade", code, sold);

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
        graph.AddEdge(edge);
    }

    void Multi(string kind, string input, ItemStack[] outputs)
    {
        var edge = new GraphEdge { Kind = kind, Source = input, Inputs = [new GraphInput { Codes = [input] }] };
        foreach (var output in outputs) Output(edge, output);
        graph.AddEdge(edge);
    }

    static void Output(GraphEdge edge, ItemStack stack)
    {
        if (stack?.Collectible?.Code == null) return;
        string code = stack.Collectible.Code.ToString();
        if (edge.Outputs.Any(o => o.Code == code)) return;
        edge.Outputs.Add(new GraphOutput { Code = code, Quantity = Math.Max(1, stack.StackSize) });
    }

}
