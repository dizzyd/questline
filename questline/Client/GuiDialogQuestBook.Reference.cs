using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace questline.Client;

/// <summary>
/// The Reference tab: vanilla handbook item pages composed inside the book, with navigation
/// kept here. Guide pages still open in the vanilla handbook for now.
/// </summary>
public partial class GuiDialogQuestBook
{
    readonly Stack<string> history = new();
    Dictionary<string, ItemStack> stackByPageCode;
    string pageCode;

    public string PageCode => pageCode;

    // The handbook's own page list is private to its dialog, so item pages are looked up by
    // page code over the same stack list the handbook builds at LevelFinalize.
    ItemStack[] AllStacks => ObjectCacheUtil.TryGet<ItemStack[]>(capi, "handbookallstacks");

    void EnsurePageIndex()
    {
        if (stackByPageCode != null || AllStacks == null) return;

        stackByPageCode = new Dictionary<string, ItemStack>();
        foreach (var stack in AllStacks)
        {
            stackByPageCode.TryAdd(GuiHandbookItemStackPage.PageCodeForStack(stack), stack);
        }
    }

    void ComposeReference(GuiComposer composer)
    {
        EnsurePageIndex();

        if (pageCode != null && stackByPageCode != null && stackByPageCode.TryGetValue(pageCode, out var stack))
        {
            // The handbook composes its page into our bounds under the key "richtext"; the panel
            // around it is the same as every other text panel's.
            double width = Width, height = PanelHeight;
            composer.AddInset(ElementBounds.Fixed(0, ContentY, width, height), 3);
            var textBounds = ElementBounds.Fixed(BookStyle.Pad, ContentY + BookStyle.Pad, width - 2 * BookStyle.Pad - ScrollbarWidth - 4, height - 2 * BookStyle.Pad);
            composer.BeginClip(textBounds.ForkBoundingParent());
            new GuiHandbookItemStackPage(capi, stack).ComposePage(composer, textBounds, AllStacks, OpenPage);
            composer.EndClip()
                .AddVerticalScrollbar(value => ScrollPanel("richtext", value),
                    ElementBounds.Fixed(width - ScrollbarWidth - 4, ContentY + 4, ScrollbarWidth, height - 8), "richtext-scroll");
            panels["richtext"] = height - 2 * BookStyle.Pad;
        }
        else
        {
            AddTextPanel(composer, "richtext", 0, ContentY, Width, PanelHeight,
                [new RichTextComponent(capi, Lang.Get(pageCode == null ? "questline:reference-empty" : "questline:handbook-loading"), BookStyle.Body().WithColor(BookStyle.Muted))]);
        }

        if (history.Count > 0)
        {
            AddRowButton(composer, Lang.Get("questline:back"), 0, false, GoBack, "back");
        }
    }

    /// <summary>
    /// Handbook navigation from inside a hosted page or a quest. Item pages stay in the book;
    /// anything else goes to the vanilla handbook for now.
    /// </summary>
    bool OpenPage(string code)
    {
        EnsurePageIndex();
        if (stackByPageCode == null || !stackByPageCode.ContainsKey(code))
        {
            new LinkTextComponent("handbook://" + code).Trigger();
            return true;
        }

        if (pageCode != null && tab == ReferenceTab) history.Push(pageCode);
        pageCode = code;
        tab = ReferenceTab;
        if (IsOpened()) Compose();
        else TryOpen();
        return true;
    }

    /// <summary>Shows the Reference tab at an item's page.</summary>
    public bool ShowReference(ItemStack stack) => OpenPage(GuiHandbookItemStackPage.PageCodeForStack(stack));

    void OnStackClicked(ItemStack stack) => ShowReference(stack);

    void OnLinkClicked(LinkTextComponent link)
    {
        if (link.Href?.StartsWith("handbook://") == true) OpenPage(link.Href["handbook://".Length..].Replace("\\", ""));
        else link.HandleLink();
    }

    public bool GoBack()
    {
        if (history.Count == 0) return true;
        pageCode = history.Pop();
        Compose();
        return true;
    }
}
