using System.Collections.Generic;
using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace questline.Client;

/// <summary>The HUD tracker: each pinned quest's title and task progress, below the minimap.</summary>
public class HudQuestPins : HudElement
{
    const double Width = 240;

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
        var comps = new List<RichTextComponentBase>();
        for (int q = 0; q < Shown.Count; q++)
        {
            var quest = Shown[q];
            if (q > 0) comps.Add(new ClearFloatTextComponent(capi, 8));

            var state = client.StateOf(quest.Id);
            comps.Add(new RichTextComponent(capi, text.Title(quest.Id), BookStyle.Body().WithWeight(Cairo.FontWeight.Bold)));
            if (state == QuestState.Claimable)
            {
                comps.Add(new RichTextComponent(capi, "  " + Lang.Get("questline:state-claimable"), BookStyle.Small().WithColor(BookStyle.StateColor(state))));
            }
            comps.Add(new RichTextComponent(capi, "\n", BookStyle.Small()));

            var tasks = client.TasksOf(quest);
            for (int i = 0; i < quest.Tasks.Count; i++)
            {
                var task = quest.Tasks[i];
                bool done = tasks[i] >= task.Count;
                var font = done ? BookStyle.Small().WithColor(BookStyle.Good) : BookStyle.Small();
                comps.Add(new RichTextComponent(capi, (done ? "✓ " : "• ") + (task.Type == "checkbox" ? Lang.Get("questline:task-checkbox-hud") : text.TaskLine(task)), font));
                if (task.Type != "checkbox") comps.Add(new RichTextComponent(capi, $"  {tasks[i]}/{task.Count}", done ? font : BookStyle.MutedSmall()));
                comps.Add(new RichTextComponent(capi, "\n", font));
            }
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
