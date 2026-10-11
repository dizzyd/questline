using System;
using System.Collections.Generic;
using System.Linq;
using questline.Graph;

namespace questline.Client;

/// <summary>How much the planner may show, from the server's limit and the player's own choice.</summary>
public enum RevealLevel
{
    /// <summary>No planner: everything is found by playing.</summary>
    Discovery,
    /// <summary>The planner names the next things to get, not how to get them.</summary>
    Hints,
    /// <summary>The whole plan, every recipe shown.</summary>
    Guide,
}

/// <summary>One thing a plan needs: how many, how many the player has, and how it's made.</summary>
public class PlanStep
{
    public string Code;
    public int Quantity;
    public int Have;
    /// <summary>The recipe chosen to make it, or null when it's found in the world (or nothing makes it).</summary>
    public GraphEdge Edge;
    /// <summary>How it's found in the world, when <see cref="Edge"/> is null: a drop, loot or trader edge, if the graph knows one.</summary>
    public GraphEdge Source;
    public List<PlanStep> Inputs = [];

    public bool Done => Have >= Quantity;
    /// <summary>Not done, but everything it needs is: what to do next.</summary>
    public bool Ready => !Done && Inputs.All(i => i.Done);

    public IEnumerable<PlanStep> Walk() => Inputs.SelectMany(i => i.Walk()).Prepend(this);
}

/// <summary>
/// Works back from a target through the recipe graph to a plan the player can follow. At each
/// step it takes the cheapest way the graph knows: simple stations first, and recipes whose
/// inputs the player already has. Things found in the world (drops, loot, traders) end a branch.
/// It never plans through story items, and stops at a depth no real chain needs.
/// </summary>
public class Planner(RecipeGraph graph, Func<string, int> have, Func<string, IEnumerable<string>> codesMatching, Func<string, bool> isSpoiler)
{
    const int MaxDepth = 8;

    // How much each way of making something costs the player, roughly: effort and equipment.
    static readonly Dictionary<string, int> KindCost = new()
    {
        ["grid"] = 1, ["knapping"] = 1, ["clayforming"] = 2,
        ["heat-cook"] = 2, ["heat-bake"] = 3, ["heat-fire"] = 2, ["heat-smelt"] = 3, ["cast"] = 3, ["smithing"] = 4,
        ["grind"] = 3, ["crush"] = 4, ["smash"] = 2, ["barrel"] = 4, ["alloy"] = 4, ["cooking"] = 3, ["juice"] = 4,
        ["transition-dry"] = 3, ["transition-cure"] = 4, ["kiln"] = 4, ["carburize"] = 6, ["distill"] = 5,
        ["charcoal"] = 4, ["coke"] = 6, ["fill"] = 1,
    };
    /// <summary>Ways of having something that are finding it, not making it. Panning is luck too.</summary>
    public static readonly HashSet<string> FoundKinds = ["drop", "entity-drop", "butcher", "milk", "pickup", "pan", "trade"];

    readonly HashSet<string> visiting = [];

    public PlanStep Plan(string code, int quantity = 1) => Need(code, quantity, 0);

    PlanStep Need(string code, int quantity, int depth)
    {
        var step = new PlanStep { Code = code, Quantity = quantity, Have = have(code) };
        if (step.Done || depth >= MaxDepth || !visiting.Add(code)) return step;
        try
        {
            var ways = graph.MadeBy(code);
            // Loot is luck, not a plan: a vessel or a ruin is the last place to say to look.
            step.Source = ways.Where(e => FoundKinds.Contains(e.Kind))
                .OrderBy(e => IsLoot(e) || e.Kind == "pan" ? 2 : e.Kind == "trade" ? 1 : 0).FirstOrDefault();
            step.Edge = ways
                .Where(e => KindCost.ContainsKey(e.Kind) && e.Inputs.All(Usable))
                .OrderBy(Cost).FirstOrDefault();
            // A recipe is always the plan when there is one; only raw materials are found.
            if (step.Edge == null) return step;

            int output = step.Edge.Outputs.FirstOrDefault(o => o.Code == code)?.Quantity ?? 1;
            int batches = (int)Math.Ceiling((double)(quantity - step.Have) / Math.Max(1, output));
            foreach (var input in step.Edge.Inputs)
            {
                string pick = Pick(input);
                if (pick == null) continue;
                step.Inputs.Add(Need(pick, input.Tool ? 1 : input.Quantity * batches, depth + 1));
            }
            return step;
        }
        finally
        {
            visiting.Remove(code);
        }
    }

    static bool IsLoot(GraphEdge edge) => edge.Kind == "drop" && edge.Inputs.Any(i => i.Codes.Any(c => c.Contains("vessel") || c.Contains("ruin") || c.Contains("chest")));

    bool Usable(GraphInput input) => input.Codes.Any(c => c == "*:*" || !c.StartsWith("meal:") && !isSpoiler(c));

    int Cost(GraphEdge edge) => KindCost[edge.Kind] * 2 + edge.Inputs.Count(i => !i.Codes.Any(c => have(c) > 0 || c.Contains('*') && codesMatching(c).Any(m => have(m) > 0)));

    /// <summary>The code to plan for an input: one the player has, else one found in the world, else the first that isn't a story item.</summary>
    string Pick(GraphInput input)
    {
        var codes = input.Codes.Where(c => c != "*:*").SelectMany(c => c.Contains('*') ? codesMatching(c) : [c]).Where(c => !isSpoiler(c)).Distinct().ToList();
        return codes.FirstOrDefault(c => have(c) > 0)
            ?? codes.FirstOrDefault(c => graph.MadeBy(c).Count == 0 || graph.MadeBy(c).Any(e => FoundKinds.Contains(e.Kind)))
            ?? codes.FirstOrDefault();
    }
}
