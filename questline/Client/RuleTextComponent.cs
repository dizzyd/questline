using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>A hairline across the text's full width, with space above and below: a section break.</summary>
public class RuleTextComponent : RichTextComponentBase
{
    readonly double space;
    LoadedTexture line;

    public RuleTextComponent(ICoreClientAPI capi, double space = 8) : base(capi)
    {
        this.space = space;
        Float = EnumFloat.None;
        BoundsPerLine = [new LineRectangled(0, 0, 0, 0)];
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        var path = GetCurrentFlowPathSection(flowPath, lineY);
        var b = BoundsPerLine[0];
        b.X = path.X1;
        b.Y = lineY;
        b.Width = path.X2 - path.X1;
        b.Height = GuiElement.scaled(space) * 2 + 1;
        nextOffsetX = 0;
        return EnumCalcBoundsResult.Nextline;
    }

    public override void ComposeElements(Cairo.Context ctx, Cairo.ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        ctx.SetSourceRGBA(BookStyle.Muted[0], BookStyle.Muted[1], BookStyle.Muted[2], 0.35);
        ctx.Rectangle(b.X, b.Y + GuiElement.scaled(space), b.Width, 1);
        ctx.Fill();
    }

    public override void Dispose()
    {
        line?.Dispose();
        base.Dispose();
    }
}
