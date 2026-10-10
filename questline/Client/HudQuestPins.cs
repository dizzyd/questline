using System.Collections.Generic;
using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace questline.Client;

/// <summary>The HUD tracker: each pinned quest's title and task progress, below the minimap.</summary>
public class HudQuestPins : HudElement
{
    const double Width = 230;

    readonly QuestClient client;
    readonly QuestText text;

    public HudQuestPins(ICoreClientAPI capi, QuestClient client) : base(capi)
    {
        this.client = client;
        text = new QuestText(capi, client);
        client.Changed += Refresh;
    }

    /// <summary>The quests shown right now, in pin order.</summary>
    public List<QuestDef> Shown { get; private set; } = [];

    public void Refresh()
    {
        Shown = client.Ready ? client.Pins.Select(client.Registry.Get).Where(q => q != null).ToList() : [];
        if (Shown.Count == 0)
        {
            if (IsOpened()) TryClose();
            return;
        }

        Compose();
        if (!IsOpened()) TryOpen();
    }

    void Compose()
    {
        var textBounds = ElementBounds.Fixed(0, 0, Width, 40);
        var bgBounds = new ElementBounds().WithSizing(ElementSizing.FitToChildren).WithFixedPadding(GuiStyle.ElementToDialogPadding / 2);
        bgBounds.WithChildren(textBounds);
        // Under the minimap, which sits at the top right.
        var dialogBounds = bgBounds.ForkBoundingParent().WithAlignment(EnumDialogArea.RightTop).WithFixedPosition(-GuiStyle.DialogToScreenPadding, 270);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui.CreateCompo("questline-pins", dialogBounds)
            .AddGameOverlay(bgBounds, GuiStyle.DialogLightBgColor)
            .AddRichtext(Components(), textBounds, "pins")
            .Compose();
    }

    RichTextComponentBase[] Components()
    {
        var title = CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold);
        var line = CairoFont.WhiteDetailText();
        var comps = new List<RichTextComponentBase>();

        foreach (var quest in Shown)
        {
            var state = client.StateOf(quest.Id);
            string mark = state == QuestState.Claimable ? "  " + Lang.Get("questline:state-claimable") : "";
            comps.Add(new RichTextComponent(capi, text.Title(quest.Id) + mark + "\n", title));

            var tasks = client.TasksOf(quest);
            for (int i = 0; i < quest.Tasks.Count; i++)
            {
                var task = quest.Tasks[i];
                bool done = tasks[i] >= task.Count;
                string progress = task.Type == "checkbox" ? "" : $"  {tasks[i]}/{task.Count}";
                var font = done ? line.Clone().WithColor([0.45, 0.85, 0.45, 1]) : line;
                comps.Add(new RichTextComponent(capi, (done ? "✓ " : "• ") + text.TaskLine(task) + progress + "\n", font));
            }
            comps.Add(new ClearFloatTextComponent(capi, 4));
        }
        return comps.ToArray();
    }

    public override bool Focusable => false;

    public override void Dispose()
    {
        client.Changed -= Refresh;
        base.Dispose();
    }
}
