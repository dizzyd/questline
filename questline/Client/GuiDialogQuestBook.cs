using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace questline.Client;

/// <summary>
/// The quest book. For now it is the rendering spike: a Quests tab with a 500-node dummy tree
/// on the custom canvas, and a Reference tab that hosts the vanilla handbook's item pages.
/// </summary>
public class GuiDialogQuestBook : GuiDialog
{
    const double Width = 960, ContentY = 70, ContentHeight = 560;
    const int QuestsTab = 0, ReferenceTab = 1;

    readonly List<QuestCanvasNode> nodes;
    readonly Stack<string> history = new();
    Dictionary<string, ItemStack> stackByPageCode;
    GuiElementQuestCanvas canvas;
    int tab;
    string pageCode;
    string pageTiming = "";
    string tourResult = "";
    float sinceStatsUpdate;

    public override string ToggleKeyCombinationCode => "questline";

    public GuiElementQuestCanvas Canvas => canvas;

    public GuiDialogQuestBook(ICoreClientAPI capi, QuestClient client) : base(capi)
    {
        nodes = SpikeTree.Build(capi);
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Compose();
    }

    void Compose()
    {
        SingleComposer?.Dispose();
        canvas = null;

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var tabsBounds = ElementBounds.Fixed(0, 30, 400, 24);
        var footerBounds = ElementBounds.Fixed(0, ContentY + ContentHeight + 10, Width - 110, 20);
        var buttonBounds = ElementBounds.Fixed(Width - 100, ContentY + ContentHeight + 6, 100, 22);

        GuiTab[] tabs =
        [
            new() { Name = "Quests", DataInt = QuestsTab },
            new() { Name = "Reference", DataInt = ReferenceTab },
        ];

        var composer = capi.Gui.CreateCompo("questline-book", dialogBounds)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar("Questline", () => TryClose())
            .BeginChildElements(bg)
            .AddHorizontalTabs(tabs, tabsBounds, OnTabClicked, CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), "tabs");

        if (tab == QuestsTab)
        {
            canvas = new GuiElementQuestCanvas(capi, ElementBounds.Fixed(0, ContentY, Width, ContentHeight), nodes);
            composer
                .AddInteractiveElement(canvas, "canvas")
                .AddDynamicText(tourResult, CairoFont.WhiteDetailText(), footerBounds, "footer")
                .AddSmallButton("Benchmark", OnBenchmark, buttonBounds);
        }
        else
        {
            ComposeReference(composer, footerBounds, buttonBounds);
        }

        SingleComposer = composer.EndChildElements().Compose();
        SingleComposer.GetHorizontalTabs("tabs").SetValue(tab, false);

        if (tab == ReferenceTab) InitReferenceScroll();
    }

    void OnTabClicked(int dataInt)
    {
        tab = dataInt;
        Compose();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        base.OnRenderGUI(deltaTime);

        if (canvas == null || canvas.Touring) return;
        sinceStatsUpdate += deltaTime;
        if (sinceStatsUpdate < 0.5f) return;
        sinceStatsUpdate = 0;

        string live = $"{canvas.VisibleNodes}/{nodes.Count} in view · zoom {canvas.Zoom:0.00} · canvas {canvas.Stats.AvgCanvasMs:0.00} ms · frame {canvas.Stats.AvgFrameMs:0.0} ms";
        SingleComposer.GetDynamicText("footer").SetNewText(tourResult.Length > 0 ? tourResult : live);
    }

    bool OnBenchmark()
    {
        tourResult = "";
        SingleComposer.GetDynamicText("footer").SetNewText("Touring the tree for 20 s...");
        canvas.StartTour(20, OnTourDone);
        return true;
    }

    void OnTourDone(CanvasFrameStats stats)
    {
        tourResult = $"Tour: {stats}";
        capi.Logger.Notification("[questline] canvas tour, {0} nodes: {1}", nodes.Count, stats);
        SingleComposer.GetDynamicText("footer").SetNewText(tourResult);
    }

    /// <summary>Runs the benchmark tour from code, for tests.</summary>
    public void StartTour(float seconds, Action<CanvasFrameStats> done)
    {
        canvas.StartTour(seconds, stats =>
        {
            OnTourDone(stats);
            done(stats);
        });
    }

    #region Reference tab

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

        var anvil = capi.World.GetBlock(new AssetLocation("anvil-copper"));
        pageCode ??= anvil != null
            ? GuiHandbookItemStackPage.PageCodeForStack(new ItemStack(anvil))
            : GuiHandbookItemStackPage.PageCodeForStack(AllStacks[0]);
    }

    void ComposeReference(GuiComposer composer, ElementBounds footerBounds, ElementBounds buttonBounds)
    {
        EnsurePageIndex();

        var textBounds = ElementBounds.Fixed(3, ContentY + 3, Width - 36, ContentHeight - 6);
        var clipBounds = textBounds.ForkBoundingParent();
        var insetBounds = textBounds.FlatCopy().FixedGrow(6).WithFixedOffset(-3, -3);
        var scrollbarBounds = clipBounds.CopyOffsetedSibling(textBounds.fixedWidth + 10, -3, 0, 6).WithFixedWidth(20);

        composer.BeginClip(clipBounds).AddInset(insetBounds, 3);

        if (pageCode != null && stackByPageCode.TryGetValue(pageCode, out var stack))
        {
            long start = Stopwatch.GetTimestamp();
            new GuiHandbookItemStackPage(capi, stack).ComposePage(composer, textBounds, AllStacks, OpenPage);
            pageTiming = $"{pageCode} · page built in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms";
        }
        else
        {
            composer.AddStaticText("The handbook hasn't finished loading.", CairoFont.WhiteSmallText(), textBounds, "richtext");
        }

        composer
            .EndClip()
            .AddVerticalScrollbar(OnScroll, scrollbarBounds, "scrollbar")
            .AddDynamicText(pageTiming, CairoFont.WhiteDetailText(), footerBounds, "footer")
            .AddSmallButton("Back", GoBack, buttonBounds);
    }

    void InitReferenceScroll()
    {
        var text = SingleComposer.GetRichtext("richtext");
        if (text == null) return;

        SingleComposer.GetScrollbar("scrollbar").SetHeights((float)ContentHeight, (float)text.Bounds.fixedHeight);
        OnScroll(0);
    }

    void OnScroll(float value)
    {
        var text = SingleComposer.GetRichtext("richtext");
        if (text == null) return;

        text.Bounds.fixedY = ContentY + 3 - value;
        text.Bounds.CalcWorldBounds();
    }

    /// <summary>
    /// Handbook navigation from inside a hosted page. Item pages stay in the book; anything
    /// else (guide pages) goes to the vanilla handbook for now.
    /// </summary>
    bool OpenPage(string code)
    {
        if (stackByPageCode == null || !stackByPageCode.ContainsKey(code))
        {
            new LinkTextComponent("handbook://" + code).Trigger();
            return true;
        }

        if (pageCode != null) history.Push(pageCode);
        pageCode = code;
        Compose();
        return true;
    }

    /// <summary>Shows the Reference tab at an item's page, for tests.</summary>
    public bool ShowReference(ItemStack stack)
    {
        tab = ReferenceTab;
        EnsurePageIndex();
        if (!IsOpened()) TryOpen();
        return OpenPage(GuiHandbookItemStackPage.PageCodeForStack(stack));
    }

    public string PageCode => pageCode;

    public bool GoBack()
    {
        if (history.Count == 0) return true;
        pageCode = history.Pop();
        Compose();
        return true;
    }

    #endregion
}
