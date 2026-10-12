using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// A checkbox task's box, in the icon column of its row: empty and clickable while the quest is
/// open, green with a tick once done, faint while the quest is locked.
/// </summary>
public class TickBoxComponent : RichTextComponentBase
{
    readonly bool ticked;
    readonly Action onTick;
    readonly double cell, padRight;
    bool pressed, hover;

    /// <param name="onTick">What a click does; null leaves the box unclickable.</param>
    public TickBoxComponent(ICoreClientAPI capi, double cell, double padRight, bool ticked, Action onTick) : base(capi)
    {
        this.cell = cell;
        this.padRight = padRight;
        this.ticked = ticked;
        this.onTick = ticked ? null : onTick;
        if (this.onTick != null) MouseOverCursor = "linkselect";
        BoundsPerLine = [new LineRectangled(0, 0, GuiElement.scaled(cell + padRight), GuiElement.scaled(cell))];
    }

    bool Clickable => onTick != null;

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        var path = GetCurrentFlowPathSection(flowPath, lineY);
        offsetX += GuiElement.scaled(PaddingLeft);
        var b = BoundsPerLine[0];
        bool wrap = offsetX + b.Width > path.X2;
        b.X = wrap ? 0 : offsetX;
        b.Y = lineY + (wrap ? currentLineHeight : 0);
        nextOffsetX = b.X + b.Width;
        return wrap ? EnumCalcBoundsResult.Nextline : EnumCalcBoundsResult.Continue;
    }

    /// <summary>The box itself, square and centred in the cell, in the richtext's coordinates.</summary>
    (double X, double Y, double Size) Box()
    {
        var b = BoundsPerLine[0];
        double size = GuiElement.scaled(cell * 0.7);
        return (b.X + (GuiElement.scaled(cell) - size) / 2, b.Y + (b.Height - size) / 2, size);
    }

    public override void ComposeElements(Cairo.Context ctx, Cairo.ImageSurface surface)
    {
        var (x, y, size) = Box();
        double r = size * 0.18, line = Math.Max(1.5, GuiElement.scaled(2));
        double[] edge = ticked ? BookStyle.Good : BookStyle.Text;
        double alpha = ticked || Clickable ? 1 : 0.4;

        GuiElement.RoundRectangle(ctx, x, y, size, size, r);
        if (ticked)
        {
            ctx.SetSourceRGBA(edge[0] * 0.55, edge[1] * 0.55, edge[2] * 0.55, 1);
            ctx.FillPreserve();
        }
        else
        {
            ctx.SetSourceRGBA(0, 0, 0, 0.3);
            ctx.FillPreserve();
        }
        ctx.SetSourceRGBA(edge[0], edge[1], edge[2], alpha);
        ctx.LineWidth = line;
        ctx.Stroke();

        if (ticked)
        {
            ctx.SetSourceRGBA(0.95, 0.97, 0.9, 1);
            ctx.LineWidth = line * 1.4;
            ctx.LineCap = Cairo.LineCap.Round;
            ctx.LineJoin = Cairo.LineJoin.Round;
            ctx.MoveTo(x + size * 0.24, y + size * 0.52);
            ctx.LineTo(x + size * 0.43, y + size * 0.72);
            ctx.LineTo(x + size * 0.77, y + size * 0.30);
            ctx.Stroke();
        }
    }

    public override void RenderInteractiveElements(float deltaTime, double renderX, double renderY, double renderZ)
    {
        if (!Clickable) return;
        var (x, y, size) = Box();
        int mx = api.Input.MouseX, my = api.Input.MouseY;
        hover = mx >= renderX + x && mx <= renderX + x + size && my >= renderY + y && my <= renderY + y + size;
        if (hover)
        {
            api.Render.RenderRectangle((float)(renderX + x), (float)(renderY + y), (float)renderZ + 1, (float)size, (float)size, ColorUtil.ColorFromRgba(255, 255, 255, 40));
        }
    }

    public override bool UseMouseOverCursor(ElementBounds richtextBounds) => Clickable && hover;

    bool Inside(MouseEvent args)
    {
        var (x, y, size) = Box();
        return args.X >= x && args.X <= x + size && args.Y >= y && args.Y <= y + size;
    }

    public override void OnMouseDown(MouseEvent args) => pressed = Clickable && Inside(args);

    public override void OnMouseUp(MouseEvent args)
    {
        if (!pressed || !Inside(args)) return;
        pressed = false;
        args.Handled = true;
        onTick();
    }

    /// <summary>Ticks the box as a click would, for tests.</summary>
    public bool Tick()
    {
        if (!Clickable) return false;
        onTick();
        return true;
    }

    public bool Ticked => ticked;
}
