using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// A texture on a line of its own in rich text, a share of the text's width wide and centred,
/// keeping its aspect ratio. The game has no such component; quest pages need one.
/// </summary>
public class ImageTextComponent : RichTextComponentBase
{
    readonly LoadedTexture texture;
    readonly float widthShare;

    public bool Loaded => texture.TextureId != 0 && texture.Width > 0;

    public ImageTextComponent(ICoreClientAPI capi, AssetLocation texturePath, float widthShare) : base(capi)
    {
        // The render API caches textures by name, so this one is shared and never disposed here.
        texture = new LoadedTexture(capi);
        capi.Render.GetOrLoadTexture(texturePath, ref texture);
        this.widthShare = GameMath.Clamp(widthShare, 0.1f, 1f);
        Float = EnumFloat.None;
        BoundsPerLine = [new LineRectangled(0, 0, 0, 0)];
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        var path = GetCurrentFlowPathSection(flowPath, lineY);
        double available = path.X2 - path.X1;
        double width = available * widthShare;
        var bounds = BoundsPerLine[0];
        bounds.X = path.X1 + (available - width) / 2;
        bounds.Y = lineY;
        bounds.Width = width;
        bounds.Height = Loaded ? Math.Round(width * texture.Height / texture.Width) : 0;
        nextOffsetX = 0;
        return EnumCalcBoundsResult.Continue;
    }

    public override void RenderInteractiveElements(float deltaTime, double renderX, double renderY, double renderZ)
    {
        if (!Loaded) return;
        var b = BoundsPerLine[0];
        api.Render.Render2DTexture(texture.TextureId, (float)(renderX + b.X), (float)(renderY + b.Y), (float)b.Width, (float)b.Height, (float)renderZ + 10);
    }
}
