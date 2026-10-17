using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using static questline.Client.BookStyle;

namespace questline.Client;

/// <summary>
/// The Reference tab: one search across quests, guides and item pages on the left; the page on
/// the right. Item pages are the game's own handbook pages, composed into the book, with a
/// Quests section on top saying which quests need the item and which give it. Guides are the
/// handbook's guide pages (and any a mod adds), re-read from their text. A quest result shows
/// a summary with a way back to the quest. Back and Forward walk the pages visited.
///
/// Pages are named "item:&lt;handbook page code&gt;", "guide:&lt;page code&gt;" or "quest:&lt;quest id&gt;".
/// A "mod:&lt;mod id&gt;" row in the results isn't a page: it searches for that mod's items.
/// </summary>
public partial class GuiDialogQuestBook
{
    const int MaxResults = 80;

    readonly Stack<string> back = new(), forward = new();
    Dictionary<string, ItemStack> stackByPageCode;
    List<RefItem> items;
    List<GuiHandbookTextPage> guides;
    List<GuiHandbookPage> customPages;
    string refPage, search = "";

    /// <summary>The page shown, as above, or null on the guide list.</summary>
    public string PageCode => refPage;

    public GuiElementChapterRail Results { get; private set; }

    // The handbook's own page list is private to its dialog, so item pages are looked up by
    // page code over the same stack list the handbook builds at LevelFinalize.
    ItemStack[] AllStacks => ObjectCacheUtil.TryGet<ItemStack[]>(capi, "handbookallstacks");

    double ResultsWidth => Math.Clamp(Width * 0.26, 200, 320);

    /// <summary>Hands over the handbook's page list as it's built, so pages other mods add are searchable too.</summary>
    public void OnHandbookPages(List<GuiHandbookPage> pages) => customPages = pages;

    void EnsureIndex()
    {
        if (stackByPageCode == null && AllStacks != null)
        {
            stackByPageCode = [];
            items = [];
            foreach (var stack in AllStacks)
            {
                string code = GuiHandbookItemStackPage.PageCodeForStack(stack);
                if (!stackByPageCode.TryAdd(code, stack)) continue;
                string name = stack.GetName();
                items.Add(new(stack, code, name, SearchKey(name), SearchKey(BaseName(name)), stack.Collectible.Code.Domain));
            }
        }

        if (guides == null)
        {
            guides = capi.Assets.GetMany<GuiHandbookTextPage>(capi.Logger, "config/handbook").OrderBy(p => p.Key.ToString()).Select(p => p.Value).ToList();
            foreach (var page in guides) page.Init(capi);
        }
    }

    /// <summary>
    /// Every page that isn't an item: the guides in config/handbook, and whatever the handbook and
    /// other mods add to its list (tutorials, meal recipes, mod guides).
    /// </summary>
    IEnumerable<GuiHandbookPage> AllGuides() => guides.Cast<GuiHandbookPage>()
        .Concat(customPages?.Where(p => p is not GuiHandbookItemStackPage) ?? [])
        .GroupBy(g => g.PageCode).Select(g => g.First());

    static string TitleOf(GuiHandbookPage page) => page is GuiHandbookTextPage text ? Lang.Get(text.Title) : page.GetPageText().Title ?? page.PageCode;

    /// <summary>Guides and tutorials, for the list shown before anything is searched; recipes and the like only come up in a search.</summary>
    static bool IsGuide(GuiHandbookPage page) => page is GuiHandbookTextPage || page.CategoryCode is "guide" or "tutorial";

    void ComposeReference(GuiComposer composer)
    {
        EnsureIndex();

        // Search box over the results, on the left.
        composer.AddTextInput(ElementBounds.Fixed(0, ContentY, ResultsWidth, FieldHeight), OnSearchChanged, Small(), "ref-search");
        inputKeys.Add("ref-search");
        afterCompose.Add(() =>
        {
            var input = SingleComposer.GetTextInput("ref-search");
            input.SetValue(search);
            input.SetPlaceHolderText(Lang.Get("questline:ref-search"));
        });
        Results = new GuiElementChapterRail(capi, ElementBounds.Fixed(0, ContentY + FieldHeight + Gap / 2, ResultsWidth, ContentHeight - FieldHeight - Gap / 2),
            ResultRows(), refPage, id =>
            {
                if (id.StartsWith("mod:")) SearchReference("@" + id[4..]);
                else OpenRef(id);
            });
        composer.AddInteractiveElement(Results, "ref-results");

        double x = ResultsWidth + Gap, width = Width - x;
        ComposeRefPage(composer, x, width);

        double left = x;
        if (back.Count > 0) left += AddRowButton(composer, Lang.Get("questline:back"), left, false, GoBack, "back") + Gap / 2;
        if (forward.Count > 0) AddRowButton(composer, Lang.Get("questline:forward"), left, false, GoForward, "forward");
        if (refPage?.StartsWith("item:") == true && stackByPageCode?.TryGetValue(refPage[5..], out var shown) == true
            && shown.Collectible.Code.ToString() is var code && CanPlan(code))
        {
            AddRowButton(composer, Lang.Get(client.Plans.Contains(code) ? "questline:plan-open" : "questline:plan-start"), Width, true, () => { ShowPlan(code); return true; }, "plan-start", primary: true);
        }
        if (refPage?.StartsWith("quest:") == true)
        {
            string id = refPage["quest:".Length..];
            AddRowButton(composer, Lang.Get("questline:ref-open-quest"), Width, true, () => { SelectQuest(id); return true; }, "ref-open-quest", primary: true);
        }
    }

    void OnSearchChanged(string value)
    {
        if (loadingForm || value == search) return;
        search = value;
        Results?.SetRows(ResultRows(), refPage);
    }

    /// <summary>
    /// The results for the search: quests, then guides, then items. With no search, every guide,
    /// then every mod with items of its own; "@mod" lists that mod's items.
    /// </summary>
    List<GuiElementChapterRail.Row> ResultRows()
    {
        var rows = new List<GuiElementChapterRail.Row>();
        string q = SearchKey(search ?? "");
        if (q.StartsWith('@')) return ModRows(q[1..]);

        string questNote = Lang.Get("questline:ref-kind-quest"), guideNote = Lang.Get("questline:ref-kind-guide");
        if (q.Length == 0)
        {
            // Guides are titled "Crafting Mechanic: Armor" and the like, so they're grouped under the
            // part before the colon, which every row would otherwise repeat.
            foreach (var group in AllGuides().Where(IsGuide).Select(g => (Guide: g, Parts: SplitTitle(TitleOf(g)))).GroupBy(g => g.Parts.Group).OrderBy(g => g.Key))
            {
                if (group.Key != "") rows.Add(new("#" + group.Key, group.Key, null, 0, 0) { Heading = true });
                rows.AddRange(group.OrderBy(g => g.Parts.Name).Select(g => new GuiElementChapterRail.Row("guide:" + g.Guide.PageCode, g.Parts.Name, null, 0, 0) { Note = "" }));
            }
            rows.AddRange(ModListRows());
            return Capped(rows);
        }

        if (client.Ready)
        {
            foreach (var quest in client.Registry.Quests.Values.Where(client.IsVisible))
            {
                string title = text.Title(quest.Id);
                if (!SearchKey(title).Contains(q)) continue;
                var icon = text.StackFor(quest.Icon ?? quest.Tasks.FirstOrDefault()?.Target);
                rows.Add(new("quest:" + quest.Id, title, icon == null ? null : new DummySlot(icon), 0, 0) { Note = questNote });
            }
        }
        foreach (var guide in AllGuides())
        {
            string title = TitleOf(guide);
            if (SearchKey(title).Contains(q)) rows.Add(new("guide:" + guide.PageCode, title, null, 0, 0) { Note = guideNote });
        }
        // Items whose name starts with the search first, then the rest that contain it.
        var found = Collapse((items ?? []).Where(i => i.Key.Contains(q)), q);
        rows.AddRange(found.OrderBy(f => f.Key.StartsWith(q) ? 0 : 1).ThenBy(f => f.Key.Length).Take(Math.Max(0, MaxResults - rows.Count)).Select(f => f.Row));
        return rows.Take(MaxResults).ToList();
    }

    /// <summary>"Items by mod": a row for every mod with items of its own, with how many there are (as listed, variants together).</summary>
    List<GuiElementChapterRail.Row> ModListRows()
    {
        var counts = (items ?? []).GroupBy(i => i.Domain).ToDictionary(g => g.Key, g => g.Select(i => i.FamilyKey).Distinct().Count());
        var mods = capi.ModLoader.Mods.Where(m => m.Info?.ModID is { } id && id != "game" && counts.ContainsKey(id)).OrderBy(m => m.Info.Name).ToList();
        if (mods.Count == 0) return [];
        return [new("#mods", Lang.Get("questline:ref-mods"), null, 0, 0) { Heading = true },
            .. mods.Select(m => new GuiElementChapterRail.Row("mod:" + m.Info.ModID, m.Info.Name, null, 0, 0) { Note = counts[m.Info.ModID].ToString() })];
    }

    /// <summary>
    /// "@foodshelves", or "@" and part of a mod's name: every item that mod adds, by name. A
    /// mod's guide pages rarely link everything it has, so this is the way to see the rest.
    /// Words after the mod ("@game anvil") search within it; "@" alone lists the mods.
    /// </summary>
    List<GuiElementChapterRail.Row> ModRows(string q)
    {
        int space = q.IndexOf(' ');
        string mod = space < 0 ? q : q[..space], words = space < 0 ? "" : q[(space + 1)..];
        if (mod.Length == 0) return ModListRows();

        var domains = ModDomains(mod);
        var found = Collapse((items ?? []).Where(i => domains.Contains(i.Domain) && i.Key.Contains(words)), words).OrderBy(f => f.Key).ToList();
        var rows = found.Select(f => f.Row).ToList();
        return Capped(rows, more: true);
    }

    /// <summary>
    /// The rows the rail can draw: it draws them all into one texture, which some graphics cards
    /// cap at 8192 pixels tall. The rest is left out, with a note saying so when there is one.
    /// </summary>
    static List<GuiElementChapterRail.Row> Capped(List<GuiElementChapterRail.Row> rows, bool more = false)
    {
        int max = (int)(8192 / GuiElement.scaled(GuiElementChapterRail.RowHeight)) - 1;
        if (rows.Count <= max) return rows;
        var capped = rows.Take(max).ToList();
        if (more) capped.Add(new("#more", Lang.Get("questline:ref-more", rows.Count - max), null, 0, 0) { Heading = true });
        return capped;
    }

    /// <summary>
    /// The mods "@q" names: by id, or by a prefix of the id or part of the name. The game's own
    /// mods put everything in "game", so "@survival" and "@creative" mean that.
    /// </summary>
    HashSet<string> ModDomains(string q)
    {
        if (q is "survival" or "creative") q = "game";
        var mods = capi.ModLoader.Mods.Where(m => m.Info?.ModID != null).ToList();
        var exact = mods.Where(m => m.Info.ModID == q).Select(m => m.Info.ModID).ToHashSet();
        if (exact.Count > 0) return exact;
        return mods.Where(m => m.Info.ModID.StartsWith(q) || SearchKey(m.Info.Name ?? "").Contains(q)).Select(m => m.Info.ModID).ToHashSet();
    }

    /// <summary>
    /// Variants share a row ("Egg basket", 12 kinds) while the search matches their common name;
    /// a search for the variant itself ("walnut") lists each one. Each row comes with the key it
    /// sorts by.
    /// </summary>
    static IEnumerable<(string Key, GuiElementChapterRail.Row Row)> Collapse(IEnumerable<RefItem> found, string q)
    {
        // A null key: the search names the variant, not the family, so these are listed one by one.
        foreach (var family in found.GroupBy(i => i.FamilyKey.Contains(q) ? i.FamilyKey : null))
        {
            if (family.Key == null)
            {
                foreach (var i in family) yield return (i.Key, ItemRow(i, i.Name, 1));
                continue;
            }
            // Each family of the same name in its own mod
            foreach (var mod in family.GroupBy(i => i.Domain))
            {
                var list = mod.ToList();
                var shown = list.FirstOrDefault(i => i.Key == family.Key) ?? list[0];
                yield return (family.Key, ItemRow(shown, list.Count > 1 ? BaseName(shown.Name) : shown.Name, list.Count));
            }
        }
    }

    static GuiElementChapterRail.Row ItemRow(RefItem item, string title, int kinds) =>
        new("item:" + item.PageCode, title.Trim(), new DummySlot(item.Stack), 0, 0) { Note = kinds > 1 ? Lang.Get("questline:ref-kinds", kinds) : "" };

    /// <summary>
    /// One item page: its name, that name as searched, the search key of the name without its
    /// variant (shared by the family), and the mod it's from.
    /// </summary>
    record RefItem(ItemStack Stack, string PageCode, string Name, string Key, string FamilyKey, string Domain);

    /// <summary>
    /// A name as the search compares it: no accents, lower case. The game's own helper turns
    /// brackets into spaces, so runs of spaces are closed up, and "egg basket walnut" finds
    /// "Egg basket (walnut)".
    /// </summary>
    static string SearchKey(string name) => Spaces.Replace(name.ToSearchFriendly().ToLowerInvariant(), " ").Trim();

    static readonly Regex Spaces = new(@"\s+");

    static readonly Regex VariantSuffix = new(@"\s*\([^()]*\)\s*$");

    /// <summary>"Egg basket (walnut)" as "Egg basket". Names that are nothing but a bracket stay whole.</summary>
    static string BaseName(string name)
    {
        string stripped = name;
        while (VariantSuffix.Match(stripped) is { Success: true } m && m.Index > 0) stripped = stripped[..m.Index];
        return stripped;
    }

    /// <summary>"Crafting Mechanic: Armor" as its group and name; a title without a colon has no group.</summary>
    static (string Group, string Name) SplitTitle(string title)
    {
        int colon = title.IndexOf(": ");
        return colon > 0 ? (title[..colon], title[(colon + 2)..]) : ("", title);
    }

    void ComposeRefPage(GuiComposer composer, double x, double width)
    {
        if (refPage?.StartsWith("item:") == true && stackByPageCode != null && stackByPageCode.TryGetValue(refPage[5..], out var stack))
        {
            ComposeHosted(composer, x, width, new GuiHandbookItemStackPage(capi, stack), [.. QuestLinks(stack), .. VariantLinks(refPage[5..])]);
            return;
        }

        var guide = refPage?.StartsWith("guide:") == true ? AllGuides().FirstOrDefault(g => g.PageCode == refPage[6..]) : null;
        // Another kind of page (a tutorial, a meal recipe, a mod's) composes itself, as item pages do.
        if (guide != null && guide.GetType() != typeof(GuiHandbookTextPage))
        {
            ComposeHosted(composer, x, width, guide, [new RichTextComponent(capi, TitleOf(guide) + "\n", Title()), new RuleTextComponent(capi)]);
            return;
        }

        RichTextComponentBase[] comps;
        if (guide is GuiHandbookTextPage text)
        {
            // A plain guide's own components are disposed with whatever composed them, so the text is read afresh.
            comps = [new RichTextComponent(capi, TitleOf(text) + "\n", Title()), new RuleTextComponent(capi), .. VtmlUtil.Richtextify(capi, text.Text, Body().WithLineHeightMultiplier(1.15), OnLinkClicked)];
        }
        else if (refPage?.StartsWith("quest:") == true && client.Registry?.Get(refPage[6..]) is { } quest)
        {
            comps = [new RichTextComponent(capi, this.text.ChapterTitle(quest.ChapterId).ToUpperInvariant() + "\n", Heading()), .. DetailComponents(quest)];
        }
        else
        {
            comps = [new RichTextComponent(capi, Lang.Get(AllStacks == null ? "questline:handbook-loading" : "questline:reference-empty"), Body().WithColor(Muted))];
        }
        AddTextPanel(composer, "richtext", x, ContentY, width, PanelHeight, comps);
    }

    /// <summary>
    /// One of the handbook's own pages, composed into the book's panel. It adds its text under the
    /// key "richtext"; <paramref name="before"/> goes on top of it.
    /// </summary>
    void ComposeHosted(GuiComposer composer, double x, double width, GuiHandbookPage page, List<RichTextComponentBase> before)
    {
        double height = PanelHeight;
        composer.AddInset(ElementBounds.Fixed(x, ContentY, width, height), 3);
        var textBounds = ElementBounds.Fixed(x + Pad, ContentY + Pad, width - 2 * Pad - ScrollbarWidth - 4, height - 2 * Pad);
        composer.BeginClip(textBounds.ForkBoundingParent());
        page.ComposePage(composer, textBounds, AllStacks, OpenPage);
        if (before.Count > 0 && composer.GetRichtext("richtext") is { } hosted) hosted.Components = [.. before, .. hosted.Components];
        composer.EndClip()
            .AddVerticalScrollbar(value => ScrollPanel("richtext", value),
                ElementBounds.Fixed(x + width - ScrollbarWidth - 4, ContentY + 4, ScrollbarWidth, height - 8), "richtext-scroll");
        panels["richtext"] = height - 2 * Pad;
    }

    /// <summary>A Quests section for an item page: the visible quests that need it and that give it, each a link.</summary>
    List<RichTextComponentBase> QuestLinks(ItemStack stack)
    {
        var comps = new List<RichTextComponentBase>();
        if (!client.Ready) return comps;
        var code = stack.Collectible.Code;

        bool Matches(string target) => target != null && WildcardUtil.Match(new AssetLocation(target), code);
        var visible = client.Registry.Quests.Values.Where(client.IsVisible).ToList();
        var needs = visible.Where(q => q.Tasks.Any(t => t.OnCollectible && Matches(t.Target))).ToList();
        var gives = visible.Where(q => q.Rewards.Any(r => Matches(r.Item) || r.Options.Any(o => Matches(o.Item)))).ToList();
        if (needs.Count + gives.Count == 0) return comps;

        comps.Add(new RichTextComponent(capi, Lang.Get("questline:ref-quests").ToUpperInvariant() + "\n", Heading()));
        void Line(string label, List<QuestDef> quests)
        {
            if (quests.Count == 0) return;
            comps.Add(new RichTextComponent(capi, label + "  ", MutedSmall()));
            for (int i = 0; i < quests.Count; i++)
            {
                string id = quests[i].Id;
                comps.Add(new LinkTextComponent(capi, text.Title(id), Small().WithColor(Accent), _ => SelectQuest(id)));
                comps.Add(new RichTextComponent(capi, i < quests.Count - 1 ? ", " : "\n", Small()));
            }
        }
        Line(Lang.Get("questline:ref-needed-by"), needs);
        Line(Lang.Get("questline:ref-given-by"), gives);
        comps.Add(new RuleTextComponent(capi));
        return comps;
    }

    /// <summary>
    /// A Variants section for an item page whose item shares its row in the results with others
    /// ("Egg basket", 13 kinds): every one of them, each opening its own page.
    /// </summary>
    List<RichTextComponentBase> VariantLinks(string pageCode)
    {
        var item = items?.FirstOrDefault(i => i.PageCode == pageCode);
        if (item == null) return [];
        var family = items.Where(i => i.Domain == item.Domain && i.FamilyKey == item.FamilyKey).ToList();
        if (family.Count < 2) return [];

        List<RichTextComponentBase> comps = [new RichTextComponent(capi, Lang.Get("questline:ref-variants").ToUpperInvariant() + "\n", Heading())];
        foreach (var i in family)
        {
            comps.Add(new ItemstackTextComponent(capi, i.Stack, 36, 4, EnumFloat.Inline, OnStackClicked) { ShowStacksize = false });
        }
        comps.Add(new RichTextComponent(capi, "\n", Small()));
        comps.Add(new RuleTextComponent(capi));
        return comps;
    }

    /// <summary>Opens a page by its full name, remembering where the book was.</summary>
    void OpenRef(string page)
    {
        if (page == refPage && tab == ReferenceTab) { TryOpen(); return; }
        if (tab == ReferenceTab && refPage != null) back.Push(refPage);
        forward.Clear();
        refPage = page;
        tab = ReferenceTab;
        Show();
    }

    /// <summary>
    /// Handbook navigation from inside a hosted page or a quest. Item pages stay in the book;
    /// anything else (a guide, a category) is looked for among the guides, then left to the
    /// vanilla handbook.
    /// </summary>
    bool OpenPage(string code)
    {
        EnsureIndex();
        if (stackByPageCode != null && stackByPageCode.ContainsKey(code)) OpenRef("item:" + code);
        else if (AllGuides().Any(g => g.PageCode == code)) OpenRef("guide:" + code);
        // Not a page the book has (a handbook category, say): search for it instead.
        else SearchReference(code.StartsWith("tab-") ? "" : code);
        return true;
    }

    /// <summary>A handbook:// link, from anywhere: chat, another mod's text, a hosted page.</summary>
    public void OpenHandbookLink(string target) => OpenPage(target.Replace("\\", ""));

    public bool OnReferenceTab => tab == ReferenceTab;

    /// <summary>Shows the Reference tab at an item's page.</summary>
    public bool ShowReference(ItemStack stack) => OpenPage(GuiHandbookItemStackPage.PageCodeForStack(stack));

    /// <summary>Shows the Reference tab with a search typed in.</summary>
    public void SearchReference(string query)
    {
        search = query ?? "";
        tab = ReferenceTab;
        Show();
    }

    void OnStackClicked(ItemStack stack) => ShowReference(stack);

    void OnLinkClicked(LinkTextComponent link)
    {
        if (link.Href?.StartsWith("handbook://") == true) OpenPage(link.Href["handbook://".Length..].Replace("\\", ""));
        else link.HandleLink();
    }

    bool CanGoBack => back.Count > 0;

    public bool GoBack()
    {
        if (back.Count == 0) return true;
        if (refPage != null) forward.Push(refPage);
        refPage = back.Pop();
        Compose();
        return true;
    }

    public bool GoForward()
    {
        if (forward.Count == 0) return true;
        if (refPage != null) back.Push(refPage);
        refPage = forward.Pop();
        Compose();
        return true;
    }
}
