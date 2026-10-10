using System.Collections.Generic;
using Vintagestory.API.Client;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// Scrolling text panels: an inset with rich text clipped inside it and a slim scrollbar down
/// its right edge. Every text panel in the book is one of these, so they all pad and scroll
/// the same way.
/// </summary>
public partial class GuiDialogQuestBook
{
    const double ScrollbarWidth = 12;

    // Each panel's key, and the height of the text area it shows.
    readonly Dictionary<string, double> panels = [];

    void AddTextPanel(GuiComposer composer, string key, double x, double y, double width, double height, RichTextComponentBase[] comps)
    {
        composer.AddInset(ElementBounds.Fixed(x, y, width, height), 3);

        var textBounds = ElementBounds.Fixed(x + Pad, y + Pad, width - 2 * Pad - ScrollbarWidth - 4, height - 2 * Pad);
        var clipBounds = textBounds.ForkBoundingParent();
        composer
            .BeginClip(clipBounds)
                .AddRichtext(comps, textBounds, key)
            .EndClip()
            .AddVerticalScrollbar(value => ScrollPanel(key, value),
                ElementBounds.Fixed(x + width - ScrollbarWidth - 4, y + 4, ScrollbarWidth, height - 8), key + "-scroll");
        panels[key] = height - 2 * Pad;
    }

    void InitPanels()
    {
        foreach (var (key, visible) in panels)
        {
            var text = SingleComposer.GetRichtext(key);
            if (text == null) continue;
            SingleComposer.GetScrollbar(key + "-scroll").SetHeights((float)visible, (float)text.Bounds.fixedHeight);
            ScrollPanel(key, 0);
        }
    }

    void ScrollPanel(string key, float value)
    {
        var text = SingleComposer?.GetRichtext(key);
        if (text == null) return;
        // The text is positioned inside its clip, so the scroll is its whole offset.
        text.Bounds.fixedY = -value;
        text.Bounds.CalcWorldBounds();
    }

    float PanelScroll(string key) => SingleComposer?.GetScrollbar(key + "-scroll")?.CurrentYPosition ?? 0;

    void RestorePanelScroll(string key, float value)
    {
        var bar = SingleComposer.GetScrollbar(key + "-scroll");
        if (bar == null) return;
        bar.CurrentYPosition = value;
        bar.TriggerChanged();
    }
}
