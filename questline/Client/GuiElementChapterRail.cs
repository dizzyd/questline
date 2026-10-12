using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// The list of chapters down the left of the book: one row each, with the chapter's icon, its
/// title cut to fit, and how many of its quests are done, right-aligned. The selected row is
/// highlighted; the list scrolls with the wheel when it's longer than the rail.
/// </summary>
public class GuiElementChapterRail : GuiElement
{
    /// <summary>A row's height: room for the title at the player's text size, never less than the original 34.</summary>
    public static double RowHeight => Math.Round(34 * Math.Max(1, BookStyle.TextScale));
    const double IconSize = 22;

    public record Row(string Id, string Title, ItemSlot Icon, int Done, int Total)
    {
        /// <summary>Shown instead of done/total, e.g. a quest count in the editor.</summary>
        public string Note { get; init; }
        /// <summary>Draws the note in the problem colour.</summary>
        public bool Warn { get; init; }
        /// <summary>A heading over the rows after it, not a row to pick.</summary>
        public bool Heading { get; init; }
    }

    List<Row> rows;
    string selected;
    readonly Action<string> onSelect;
    LoadedTexture rowsTexture, hoverTexture;
    double scroll;
    int hover = -1;

    readonly ElementBounds iconCell;

    public GuiElementChapterRail(ICoreClientAPI capi, ElementBounds bounds, List<Row> rows, string selected, Action<string> onSelect) : base(capi, bounds)
    {
        iconCell = ElementBounds.Fixed(0, 0, 8 + IconSize + 4, RowHeight).WithParent(bounds);
        this.rows = rows;
        this.selected = selected;
        this.onSelect = onSelect;
    }

    static double S(double unscaled) => scaled(unscaled);
    double ContentHeight => S(RowHeight) * rows.Count;
    double MaxScroll => Math.Max(0, ContentHeight - Bounds.InnerHeight);

    public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
    {
        Bounds.CalcWorldBounds();

        // The inset the rows sit in, matching the detail panel's.
        ctxStatic.SetSourceRGBA(0, 0, 0, 0.2);
        RoundRectangle(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight, 1);
        ctxStatic.Fill();
        EmbossRoundRectangleElement(ctxStatic, Bounds, true, 2, 1);

        ComposeRows();
        int width = (int)Bounds.InnerWidth;

        using (var surface = new ImageSurface(Format.Argb32, width, (int)S(RowHeight)))
        using (var ctx = new Context(surface))
        {
            ctx.SetSourceRGBA(1, 1, 1, 0.06);
            ctx.Rectangle(0, 0, width, S(RowHeight));
            ctx.Fill();
            hoverTexture ??= new LoadedTexture(api);
            generateTexture(surface, ref hoverTexture);
        }

        // Keep the selected chapter in view.
        int index = rows.FindIndex(r => r.Id == selected);
        if (index >= 0)
        {
            double top = index * S(RowHeight), bottom = top + S(RowHeight);
            if (bottom - scroll > Bounds.InnerHeight) scroll = Math.Min(MaxScroll, bottom - Bounds.InnerHeight);
            if (top < scroll) scroll = top;
        }
    }

    void ComposeRows()
    {
        int width = (int)Bounds.InnerWidth, height = Math.Max(1, (int)Math.Ceiling(ContentHeight));
        using var surface = new ImageSurface(Format.Argb32, width, height);
        using var ctx = new Context(surface);
        for (int i = 0; i < rows.Count; i++) DrawRow(ctx, rows[i], i * S(RowHeight), width);
        rowsTexture ??= new LoadedTexture(api);
        generateTexture(surface, ref rowsTexture);
    }

    /// <summary>Replaces the rows without recomposing the dialog, so a search box keeps its focus while the list follows it.</summary>
    public void SetRows(List<Row> newRows, string newSelected)
    {
        rows = newRows;
        selected = newSelected;
        scroll = 0;
        ComposeRows();
    }

    public IReadOnlyList<Row> Rows => rows;

    void DrawRow(Context ctx, Row row, double y, int width)
    {
        double h = S(RowHeight), pad = S(8);
        if (row.Heading)
        {
            var heading = BookStyle.Heading();
            heading.SetupContext(ctx);
            new TextDrawUtil().DrawTextLine(ctx, heading, Fit(heading, row.Title.ToUpperInvariant(), width - 2 * pad), pad, y + h - heading.GetFontExtents().Height - S(4));
            return;
        }
        bool isSelected = row.Id == selected;
        if (isSelected)
        {
            ctx.SetSourceRGBA(GuiStyle.DialogHighlightColor[0], GuiStyle.DialogHighlightColor[1], GuiStyle.DialogHighlightColor[2], 0.28);
            ctx.Rectangle(0, y, width, h);
            ctx.Fill();
            ctx.SetSourceRGBA(BookStyle.Accent);
            ctx.Rectangle(0, y, S(3), h);
            ctx.Fill();
        }

        var countFont = BookStyle.Small().WithColor(row.Warn ? BookStyle.Problem : row.Note == null && row.Total > 0 && row.Done == row.Total ? BookStyle.Good : BookStyle.Muted);
        string count = row.Note ?? $"{row.Done}/{row.Total}";
        countFont.SetupContext(ctx);
        double countWidth = countFont.GetTextExtents(count).Width;
        new TextDrawUtil().DrawTextLine(ctx, countFont, count, width - pad - countWidth, y + (h - countFont.GetFontExtents().Height) / 2);

        double textX = pad + S(IconSize) + S(8);
        var titleFont = BookStyle.Body();
        if (isSelected) titleFont = titleFont.WithWeight(FontWeight.Bold);
        titleFont.SetupContext(ctx);
        string title = Fit(titleFont, row.Title, width - textX - countWidth - pad * 2);
        new TextDrawUtil().DrawTextLine(ctx, titleFont, title, textX, y + (h - titleFont.GetFontExtents().Height) / 2);
    }

    /// <summary>The text, cut short with an ellipsis if it's wider than <paramref name="max"/>.</summary>
    static string Fit(CairoFont font, string text, double max)
    {
        if (font.GetTextExtents(text).Width <= max) return text;
        for (int len = text.Length - 1; len > 0; len--)
        {
            string cut = text[..len].TrimEnd() + "…";
            if (font.GetTextExtents(cut).Width <= max) return cut;
        }
        return "…";
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        api.Render.PushScissor(Bounds);
        double x = Bounds.renderX, y = Bounds.renderY - scroll;

        int mouseRow = RowAt(api.Input.MouseX, api.Input.MouseY);
        hover = mouseRow;
        if (hover >= 0 && rows[hover].Id != selected && !rows[hover].Heading)
        {
            api.Render.Render2DTexturePremultipliedAlpha(hoverTexture.TextureId, x, y + hover * S(RowHeight), hoverTexture.Width, hoverTexture.Height, 55);
        }
        api.Render.Render2DTexturePremultipliedAlpha(rowsTexture.TextureId, x, y, rowsTexture.Width, rowsTexture.Height, 60);

        double icon = S(IconSize);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Icon?.Itemstack == null) continue;
            double cy = y + i * S(RowHeight) + S(RowHeight) / 2;
            if (cy < Bounds.renderY - icon || cy > Bounds.renderY + Bounds.InnerHeight + icon) continue;
            // Each icon is clipped to its own cell: long models such as spears would cross into the next row.
            iconCell.fixedY = (i * S(RowHeight) - scroll) / Vintagestory.API.Config.RuntimeEnv.GUIScale;
            iconCell.CalcWorldBounds();
            api.Render.PushScissor(iconCell, true);
            api.Render.RenderItemstackToGui(rows[i].Icon, x + S(8) + icon / 2, cy, 100, (float)icon * 0.85f, ColorUtil.WhiteArgb, true, false, false);
            api.Render.PopScissor();
        }
        api.Render.PopScissor();
    }

    /// <summary>The row under a screen position, or -1.</summary>
    public int RowAt(int mouseX, int mouseY)
    {
        if (!Bounds.PointInside(mouseX, mouseY)) return -1;
        int i = (int)((mouseY - Bounds.absY + scroll) / S(RowHeight));
        return i >= 0 && i < rows.Count ? i : -1;
    }

    /// <summary>Where a row's centre is on screen, for tests that click it.</summary>
    public (int X, int Y) ScreenPosOf(int index) => ((int)(Bounds.absX + Bounds.InnerWidth / 2), (int)(Bounds.absY - scroll + (index + 0.5) * S(RowHeight)));

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        int i = RowAt(args.X, args.Y);
        if (i < 0 || rows[i].Heading) return;
        args.Handled = true;
        api.Gui.PlaySound("menubutton_press");
        onSelect(rows[i].Id);
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY) || MaxScroll <= 0) return;
        scroll = Math.Clamp(scroll - args.deltaPrecise * S(RowHeight), 0, MaxScroll);
        args.SetHandled(true);
    }

    public override void Dispose()
    {
        base.Dispose();
        rowsTexture?.Dispose();
        hoverTexture?.Dispose();
    }
}
