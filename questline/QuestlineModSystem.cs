using questline.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace questline;

public class QuestlineModSystem : ModSystem
{
    ICoreClientAPI capi;
    GuiDialogQuestBook book;

    /// <summary>The quest book, created on first use. Client side only.</summary>
    public GuiDialogQuestBook Book => book ??= new GuiDialogQuestBook(capi);

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;

        api.Input.RegisterHotKey("questline", "Open the quest book", GlKeys.L, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler("questline", _ =>
        {
            if (Book.IsOpened()) Book.TryClose();
            else Book.TryOpen();
            return true;
        });
    }

    public override void Dispose()
    {
        book?.Dispose();
        base.Dispose();
    }
}
