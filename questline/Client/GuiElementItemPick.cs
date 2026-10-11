using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// A slot in the editor showing an item code's icon. Clicking it with an item on the mouse (or,
/// with an empty mouse, in the active hotbar slot) hands that item to <c>onPick</c>: the way to
/// drag an item from the inventory onto a task.
/// </summary>
public class GuiElementItemPick : GuiElement
{
    readonly DummySlot shown;
    readonly Action<ItemStack> onPick;

    public GuiElementItemPick(ICoreClientAPI capi, ElementBounds bounds, ItemStack current, Action<ItemStack> onPick) : base(capi, bounds)
    {
        shown = new DummySlot(current);
        this.onPick = onPick;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        ctx.SetSourceRGBA(0, 0, 0, 0.3);
        RoundRectangle(ctx, Bounds.drawX, Bounds.drawY, Bounds.OuterWidth, Bounds.OuterHeight, 2);
        ctx.Fill();
        EmbossRoundRectangleElement(ctx, Bounds, true, 1, 2);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        if (shown.Itemstack == null) return;
        api.Render.RenderItemstackToGui(shown, Bounds.renderX + Bounds.InnerWidth / 2, Bounds.renderY + Bounds.InnerHeight / 2, 100,
            (float)(Bounds.InnerWidth * 0.75), ColorUtil.WhiteArgb, true, false, false);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        var inv = api.World.Player.InventoryManager;
        var stack = inv.MouseItemSlot?.Itemstack ?? inv.ActiveHotbarSlot?.Itemstack;
        if (stack == null) return;
        args.Handled = true;
        api.Gui.PlaySound("menubutton_press");
        onPick(stack);
    }
}
