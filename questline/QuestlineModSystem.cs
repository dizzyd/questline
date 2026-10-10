using System;
using System.Linq;
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
    HudQuestPins pins;
    HarmonyLib.Harmony harmony;

    /// <summary>The server's quest engine. Server side only.</summary>
    public QuestEngine Engine { get; private set; }

    /// <summary>The client's copy of definitions and state. Client side only.</summary>
    public QuestClient Client { get; private set; }

    /// <summary>The quest book, created on first use. Client side only.</summary>
    public GuiDialogQuestBook Book => book ??= new GuiDialogQuestBook(capi, Client);

    /// <summary>The HUD tracker for pinned quests. Client side only.</summary>
    public HudQuestPins Pins => pins;

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

        pins = new HudQuestPins(api, Client);
        api.Event.LevelFinalize += pins.Refresh;
        api.ChatCommands.Create("questline")
            .WithDescription("Quest book settings")
            .BeginSubCommand("toasts")
                .WithDescription("Turn the quest-complete toasts on or off")
                .WithArgs(api.ChatCommands.Parsers.Bool("on or off"))
                .HandleWith(args =>
                {
                    Client.SetToasts((bool)args[0]);
                    return TextCommandResult.Success((bool)args[0] ? "Quest toasts on" : "Quest toasts off");
                })
            .EndSubCommand();

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

        // The player a command acts on: the one named, else whoever ran it.
        static (string Uid, string Name) Target(TextCommandCallingArgs args, int index)
        {
            if (args.Parsers[index].IsMissing) return (args.Caller.Player?.PlayerUID, args.Caller.Player?.PlayerName);
            var named = ((PlayerUidName[])args[index]).FirstOrDefault();
            return (named?.Uid, named?.Name);
        }

        TextCommandResult ForQuest(TextCommandCallingArgs args, Action<string, string> act, string done)
        {
            string id = (string)args[0];
            if (Engine.Registry.Get(id) == null) return TextCommandResult.Error($"No quest {id}");
            var (uid, name) = Target(args, 1);
            if (uid == null) return TextCommandResult.Error("Name a player");
            act(uid, id);
            return TextCommandResult.Success(string.Format(done, id, name));
        }

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
                .WithDescription("Complete a quest's tasks for a player, or for yourself")
                .WithArgs(parsers.Word("quest id"), parsers.OptionalPlayerUids("player"))
                .HandleWith(args => ForQuest(args, Engine.ForceComplete, "Completed {0} for {1}"))
            .EndSubCommand()
            .BeginSubCommand("unlock")
                .WithDescription("Complete what a quest needs, so a player can start it")
                .WithArgs(parsers.Word("quest id"), parsers.OptionalPlayerUids("player"))
                .HandleWith(args => ForQuest(args, Engine.Unlock, "Unlocked {0} for {1}"))
            .EndSubCommand()
            .BeginSubCommand("reset")
                .WithDescription("Forget a player's progress on one quest, or on all of them (their party's too)")
                .WithArgs(parsers.OptionalWord("quest id"), parsers.OptionalPlayerUids("player"))
                .HandleWith(args =>
                {
                    string id = (string)args[0];
                    if (id != null && Engine.Registry.Get(id) == null) return TextCommandResult.Error($"No quest {id}");
                    var (uid, name) = Target(args, 1);
                    if (uid == null) return TextCommandResult.Error("Name a player");
                    Engine.Reset(uid, id);
                    return TextCommandResult.Success(id == null ? $"Reset every quest for {name}" : $"Reset {id} for {name}");
                })
            .EndSubCommand()
            .BeginSubCommand("resetserver")
                .WithDescription("Forget the progress and stages of every server quest")
                .HandleWith(_ =>
                {
                    Engine.ResetServer();
                    return TextCommandResult.Success("Reset every server quest");
                })
            .EndSubCommand();
    }

    public override void Dispose()
    {
        book?.Dispose();
        pins?.Dispose();
        harmony?.UnpatchAll(StationPatches.HarmonyId);
        base.Dispose();
    }
}
