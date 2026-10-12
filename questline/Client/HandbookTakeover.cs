using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// Puts the book where the handbook was: the H keys and the handbook:// and handbooksearch://
/// links open the book's Reference tab. The handbook's own handlers are kept, and come back
/// when the takeover is turned off; the handbook itself is never removed.
/// </summary>
public class HandbookTakeover(ICoreClientAPI capi, Func<GuiDialogQuestBook> book)
{
    static readonly string[] Keys = ["handbook", "selectionhandbook"];
    static readonly string[] Protocols = ["handbook", "handbooksearch"];

    readonly Dictionary<string, ActionConsumable<KeyCombination>> keyHandlers = [];
    readonly Dictionary<string, Action<LinkTextComponent>> linkHandlers = [];

    public bool Active { get; private set; }

    public void Enable()
    {
        if (Active) return;
        foreach (string code in Keys)
        {
            if (capi.Input.GetHotKeyByCode(code) is not { } hotkey) continue;
            keyHandlers[code] = hotkey.Handler;
            capi.Input.SetHotKeyHandler(code, OnKey);
        }
        foreach (string protocol in Protocols)
        {
            if (capi.LinkProtocols.TryGetValue(protocol, out var handler)) linkHandlers[protocol] = handler;
        }
        capi.LinkProtocols["handbook"] = link => book().OpenHandbookLink(link.Href["handbook://".Length..]);
        capi.LinkProtocols["handbooksearch"] = link => book().SearchReference(link.Href["handbooksearch://".Length..]);
        Active = true;
    }

    public void Disable()
    {
        if (!Active) return;
        foreach (var (code, handler) in keyHandlers) capi.Input.SetHotKeyHandler(code, handler);
        foreach (var (protocol, handler) in linkHandlers) capi.LinkProtocols[protocol] = handler;
        keyHandlers.Clear();
        linkHandlers.Clear();
        Active = false;
    }

    /// <summary>
    /// As the handbook's own key does: closes the book if it's in front on Reference, else shows it
    /// at the page of the item under the mouse (or with shift, the block looked at), or on the
    /// Reference tab.
    /// </summary>
    bool OnKey(KeyCombination key)
    {
        var dialog = book();
        if (dialog.OnTop && dialog.OnReferenceTab)
        {
            dialog.TryClose();
            return true;
        }

        ItemStack stack = capi.World.Player.InventoryManager.CurrentHoveredSlot?.Itemstack;
        if (key?.Shift == true && capi.World.Player.CurrentBlockSelection?.Position is BlockPos pos)
        {
            stack = capi.World.BlockAccessor.GetBlock(pos).OnPickBlock(capi.World, pos);
        }

        if (stack != null) dialog.ShowReference(stack);
        else dialog.SelectTab(1);
        return true;
    }
}
