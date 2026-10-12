using System;
using System.Collections.Generic;
using System.Linq;
using questline.Graph;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// The player's goal plans, as a questline of their own in the switcher. Each plan is a target
/// item worked back through the recipe graph from what the player carries now, drawn as a tree
/// on the canvas: what they have is done, what they can get now is ready, the rest waits.
///
/// At the Hints level only the target, what's done and what's ready are named; the steps further
/// back show as "?", and no step says how it's made. At Guide every step is named with its recipe.
/// </summary>
public partial class GuiDialogQuestBook
{
    public const string PlansQuestline = "questline:plans";

    string planId, planStep;
    List<(PlanStep Step, string Key)> planNodes = [];

    /// <summary>The steps of the plan shown, as the canvas has them.</summary>
    public IReadOnlyList<PlanStep> PlanSteps => planNodes.Select(n => n.Step).ToList();

    bool PlansAvailable => client.Reveal != RevealLevel.Discovery && client.Plans.Count > 0;

    /// <summary>Starts a plan for an item and shows it.</summary>
    public void ShowPlan(string code)
    {
        client.AddPlan(code);
        questlineId = PlansQuestline;
        planId = code;
        planStep = null;
        tab = QuestsTab;
        Show();
    }

    /// <summary>Whether the planner can plan this item: allowed, the graph built, not a story item, and something makes it.</summary>
    public bool CanPlan(string code) => client.Reveal != RevealLevel.Discovery && client.GraphReady && !QuestClient.IsSpoiler(code)
        && client.Graph.MadeBy(code).Any(e => !Planner.FoundKinds.Contains(e.Kind) && !e.Kind.StartsWith("transition-perish"));

    string NameOf(string code)
    {
        if (code.StartsWith("meal:")) return Lang.Get("questline:plan-meal");
        return text.StackFor(code)?.GetName() ?? code;
    }

    void ComposePlans(GuiComposer composer, double railTop)
    {
        var plans = client.Plans.ToList();
        if (planId == null || !plans.Contains(planId)) planId = plans.FirstOrDefault();
        var planner = client.MakePlanner();

        rail = new GuiElementChapterRail(capi, ElementBounds.Fixed(0, railTop, RailWidth, ContentHeight - (railTop - ContentY)),
            plans.Select(code =>
            {
                var plan = planner.Plan(code);
                var stack = text.StackFor(code);
                return new GuiElementChapterRail.Row("plan:" + code, NameOf(code), stack == null ? null : new DummySlot(stack), 0, 0)
                {
                    Note = plan.Done ? Lang.Get("questline:plan-done") : "",
                };
            }).ToList(), "plan:" + planId, id => { planId = id[5..]; planStep = null; Compose(); });
        composer.AddInteractiveElement(rail, "rail");

        var root = planner.Plan(planId);
        planNodes = LayOutPlan(root, out var nodes);
        string chapterKey = "plan:" + planId;
        canvas = new GuiElementQuestCanvas(capi, ElementBounds.Fixed(RailWidth + Gap, ContentY, CanvasWidth, PanelHeight), nodes)
        {
            SelectedIndex = planNodes.FindIndex(n => n.Key == planStep),
            OnNodeClicked = i => { planStep = planNodes[i].Key; Compose(); },
        };
        if (views.TryGetValue(chapterKey, out var view)) canvas.View = view;
        canvasChapter = chapterKey;
        composer.AddInteractiveElement(canvas, "canvas");

        // What the player chooses to see, up to what the server allows.
        var levels = Enum.GetValues<RevealLevel>().Where(l => l != RevealLevel.Discovery && l <= client.ServerReveal).ToList();
        if (levels.Count > 1)
        {
            composer.AddDropDown(levels.Select(l => l.ToString()).ToArray(), levels.Select(l => Lang.Get("questline:reveal-" + l.ToString().ToLowerInvariant())).ToArray(),
                Math.Max(0, levels.IndexOf(client.Reveal)), (code, _) => client.SetPlannerLevel(Enum.Parse<RevealLevel>(code)),
                ElementBounds.Fixed(RailWidth + Gap, ButtonY, 160, ButtonHeight), Small(), "plan-level");
        }

        var selected = planNodes.FirstOrDefault(n => n.Key == planStep).Step ?? root;
        double x = RailWidth + Gap + CanvasWidth + Gap;
        AddTextPanel(composer, "detail", x, ContentY, DetailWidth, PanelHeight, StepComponents(selected, root));
        AddRowButton(composer, Lang.Get("questline:plan-remove"), x, false, () => { client.RemovePlan(planId); planStep = null; return true; }, "plan-remove");
    }

    bool Shown(PlanStep step, PlanStep root) => client.Reveal == RevealLevel.Guide || step == root || step.Done || step.Ready;

    /// <summary>The plan as canvas nodes: the target on the right, each step's inputs in the column to its left.</summary>
    List<(PlanStep, string)> LayOutPlan(PlanStep root, out List<QuestCanvasNode> nodes)
    {
        var placed = new List<(PlanStep Step, string Key, int Depth, float Y, int Parent)>();
        int leaves = 0;
        float Place(PlanStep step, string key, int depth, int parent)
        {
            int index = placed.Count;
            placed.Add((step, key, depth, 0, parent));
            float y;
            if (step.Done || step.Inputs.Count == 0) y = leaves++ * 1.5f;
            else y = step.Inputs.Select((input, i) => Place(input, key + "/" + i, depth + 1, index)).Average();
            placed[index] = (step, key, depth, y, parent);
            return y;
        }
        Place(root, "0", 0, -1);

        int deepest = placed.Max(p => p.Depth);
        nodes = placed.Select((p, i) =>
        {
            bool shown = Shown(p.Step, root);
            var stack = shown ? text.StackFor(p.Step.Code) : null;
            return new QuestCanvasNode
            {
                Id = p.Key,
                Title = shown ? NameOf(p.Step.Code) + (p.Step.Quantity > 1 ? $" ×{p.Step.Quantity}" : "") : "?",
                X = (deepest - p.Depth) * 2,
                Y = p.Y,
                Icon = stack == null ? null : new DummySlot(stack),
                State = p.Step.Done ? QuestState.Done : p.Step.Ready ? QuestState.Available : QuestState.Locked,
                Shape = i == 0 ? NodeShape.Gear : NodeShape.Square,
                Size = i == 0 ? 1.4f : 1,
                Prereqs = placed.Select((c, j) => (c, j)).Where(c => c.c.Parent == i).Select(c => c.j).ToArray(),
            };
        }).ToList();
        return placed.Select(p => (p.Step, p.Key)).ToList();
    }

    RichTextComponentBase[] StepComponents(PlanStep step, PlanStep root)
    {
        var comps = new List<RichTextComponentBase>();
        if (!Shown(step, root))
        {
            comps.Add(new RichTextComponent(capi, "?\n", Title()));
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:plan-hidden") + "\n", Body().WithColor(Muted)));
            return comps.ToArray();
        }

        comps.Add(new RichTextComponent(capi, NameOf(step.Code) + "\n", Title()));
        var (status, color) = step.Done ? (Lang.Get("questline:plan-have", step.Have, step.Quantity), Good)
            : step.Ready ? (Lang.Get("questline:plan-ready"), StateColor(QuestState.Available))
            : (Lang.Get("questline:plan-waiting"), StateColor(QuestState.Locked));
        comps.Add(new RichTextComponent(capi, status + "\n", Small().WithColor(color).WithWeight(Cairo.FontWeight.Bold)));
        if (step == root)
        {
            var all = root.Walk().ToList();
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:plan-progress", all.Count(s => s.Done), all.Count) + "\n", MutedSmall()));
        }
        comps.Add(new RuleTextComponent(capi));

        if (client.Reveal != RevealLevel.Guide)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get(step.Ready ? "questline:plan-hint-ready" : "questline:plan-hint") + "\n", Body().WithColor(Muted)));
            return comps.ToArray();
        }

        if (step.Edge is { } edge)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:plan-made-by", KindName(edge.Kind)).ToUpperInvariant() + "\n", Heading()));
            foreach (var input in step.Inputs)
            {
                AddIconRow(comps, text.StackFor(input.Code));
                comps.Add(new RichTextComponent(capi, $"{input.Quantity}× {NameOf(input.Code)}", input.Done ? Body().WithColor(Good) : Body()) { VerticalAlign = EnumVerticalAlign.Middle });
                comps.Add(new ClearFloatTextComponent(capi, 4));
            }
        }
        else if (step.Source is { } source)
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:plan-found", KindName(source.Kind)).ToUpperInvariant() + "\n", Heading()));
            string from = source.Inputs.FirstOrDefault()?.Codes.FirstOrDefault();
            if (from != null)
            {
                AddIconRow(comps, text.StackFor(from));
                comps.Add(new RichTextComponent(capi, source.Kind == "drop" ? NameOf(from) : EntityName(from), Body()) { VerticalAlign = EnumVerticalAlign.Middle });
                comps.Add(new ClearFloatTextComponent(capi, 4));
            }
        }
        else
        {
            comps.Add(new RichTextComponent(capi, Lang.Get("questline:plan-unknown") + "\n", Body().WithColor(Muted)));
        }
        return comps.ToArray();
    }

    static string KindName(string kind) => Lang.HasTranslation("questline:plan-kind-" + kind, true, false) ? Lang.Get("questline:plan-kind-" + kind) : kind;

    static string EntityName(string code)
    {
        var loc = new AssetLocation(code);
        string key = loc.Domain + ":item-creature-" + loc.Path;
        return Lang.HasTranslation(key, true, false) ? Lang.Get(key) : code;
    }
}
