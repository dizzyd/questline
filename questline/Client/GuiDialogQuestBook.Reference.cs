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

        var textBounds = ElementBounds.Fixed(3, ContentY + 3, Width - 36, ContentHeight - 40);
        var clipBounds = textBounds.ForkBoundingParent();
        var insetBounds = textBounds.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
        var scrollbarBounds = clipBounds.CopyOffsetedSibling(textBounds.fixedWidth + 10, -3, 0, 6).WithFixedWidth(20);

        composer.BeginClip(clipBounds).AddInset(insetBounds, 3);

        if (pageCode != null && stackByPageCode != null && stackByPageCode.TryGetValue(pageCode, out var stack))
        {
            new GuiHandbookItemStackPage(capi, stack).ComposePage(composer, textBounds, AllStacks, OpenPage);
        }
        else
        {
            composer.AddStaticText(Lang.Get("questline:handbook-loading"), CairoFont.WhiteSmallText(), textBounds, "richtext");
        }

        composer
            .EndClip()
            .AddVerticalScrollbar(OnReferenceScroll, scrollbarBounds, "scrollbar")
            .AddSmallButton(Lang.Get("questline:back"), GoBack, ElementBounds.Fixed(Width - 100, ContentY + ContentHeight - 26, 100, 24));
    }

    void InitReferenceScroll()
    {
        var page = SingleComposer.GetRichtext("richtext");
        if (page == null) return;

        SingleComposer.GetScrollbar("scrollbar").SetHeights((float)(ContentHeight - 40), (float)page.Bounds.fixedHeight);
        OnReferenceScroll(0);
    }

    void OnReferenceScroll(float value)
    {
        var page = SingleComposer.GetRichtext("richtext");
        if (page == null) return;

        page.Bounds.fixedY = ContentY + 3 - value;
        page.Bounds.CalcWorldBounds();
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
