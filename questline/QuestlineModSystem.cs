using questline.Client;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace questline;

public class QuestlineModSystem : ModSystem
{
    ICoreClientAPI capi;
    GuiDialogQuestBook book;
    HarmonyLib.Harmony harmony;

    /// <summary>The server's quest engine. Server side only.</summary>
    public QuestEngine Engine { get; private set; }

    /// <summary>The client's copy of definitions and state. Client side only.</summary>
    public QuestClient Client { get; private set; }

    /// <summary>The quest book, created on first use. Client side only.</summary>
    public GuiDialogQuestBook Book => book ??= new GuiDialogQuestBook(capi, Client);

    public override void StartPre(ICoreAPI api)
    {
        // The asset manager loads only folders registered as categories. Chapters are read on
        // the server and reach clients over the network, so clients needn't load them at all.
        _ = new AssetCategory("questline", true, EnumAppSide.Server);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);

        // Every asset is loaded by now: StartServerSide runs after AssetsFinalize.
        Engine = new QuestEngine(api);
        Engine.Start();
        Engine.Reload();
        new Trackers(api, Engine).Start();
        // Server side only: in singleplayer both sides share this assembly, and patching from
        // Start() would register every patch twice.
        harmony = StationPatches.Apply(Engine, api.Logger);
        RegisterCommands(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;
        Client = new QuestClient(api);

        api.Input.RegisterHotKey("questline", "Open the quest book", GlKeys.L, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler("questline", _ =>
        {
            if (Book.IsOpened()) Book.TryClose();
            else Book.TryOpen();
            return true;
        });
    }

    void RegisterCommands(ICoreServerAPI api)
    {
        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create("questline")
            .WithDescription("Quest book administration")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("reload")
                .WithDescription("Re-read every chapter and resend it to clients")
                .HandleWith(_ =>
                {
                    Engine.Reload();
                    return TextCommandResult.Success($"{Engine.Registry.Quests.Count} quests loaded, {Engine.Problems.Count} problems (see the server log)");
                })
            .EndSubCommand()
            .BeginSubCommand("complete")
                .WithDescription("Complete a quest's tasks for yourself")
                .RequiresPlayer()
                .WithArgs(parsers.Word("quest id"))
                .HandleWith(args =>
                {
                    string id = (string)args[0];
                    if (Engine.Registry.Get(id) == null) return TextCommandResult.Error($"No quest {id}");
                    Engine.ForceComplete((IServerPlayer)args.Caller.Player, id);
                    return TextCommandResult.Success($"Completed {id}");
                })
            .EndSubCommand()
            .BeginSubCommand("reset")
                .WithDescription("Forget your progress on one quest, or on all of them")
                .RequiresPlayer()
                .WithArgs(parsers.OptionalWord("quest id"))
                .HandleWith(args =>
                {
                    string id = (string)args[0];
                    Engine.Reset((IServerPlayer)args.Caller.Player, id);
                    return TextCommandResult.Success(id == null ? "Reset every quest" : $"Reset {id}");
                })
            .EndSubCommand();
    }

    public override void Dispose()
    {
        book?.Dispose();
        harmony?.UnpatchAll(StationPatches.HarmonyId);
        base.Dispose();
    }
}
