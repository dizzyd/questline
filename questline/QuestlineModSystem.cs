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

    /// <summary>The H key and handbook links, pointed at the book. Client side only.</summary>
    public HandbookTakeover Takeover { get; private set; }

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
        new VisitTracker(api, Engine).Start();
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
        Takeover = new HandbookTakeover(api, () => Book);
        // The handbook sets its keys and links in its own StartClientSide; replacing them is left
        // until the level is ready, after every mod has started.
        api.Event.LevelFinalize += () => { if (Client.Settings.TakeOverHandbook) Takeover.Enable(); };
        if (api.ModLoader.GetModSystem<Vintagestory.GameContent.ModSystemSurvivalHandbook>() is { } handbook)
        {
            handbook.OnInitCustomPages += pages => Book.OnHandbookPages(pages);
        }
        api.Event.LevelFinalize += pins.Refresh;
        api.ChatCommands.Create("questline")
            .WithDescription("Quest book settings")
            .BeginSubCommand("handbook")
                .WithDescription("Whether H opens the quest book's Reference tab (on) or the vanilla handbook (off)")
                .WithArgs(api.ChatCommands.Parsers.Bool("on or off"))
                .HandleWith(args =>
                {
                    bool on = (bool)args[0];
                    Client.Settings.TakeOverHandbook = on;
                    Client.SaveSettings();
                    if (on) Takeover.Enable();
                    else Takeover.Disable();
                    return TextCommandResult.Success(on ? "H opens the quest book's Reference tab" : "H opens the vanilla handbook");
                })
            .EndSubCommand()
            .BeginSubCommand("textsize")
                .WithDescription("Set the quest book's text size, in percent: " + string.Join(", ", ClientSettings.TextSizes))
                .WithArgs(api.ChatCommands.Parsers.Int("percent"))
                .HandleWith(args =>
                {
                    Client.SetTextSize((int)args[0]);
                    return TextCommandResult.Success($"Quest book text at {Client.Settings.TextSize}%");
                })
            .EndSubCommand()
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
            // A book buried under other windows comes to the front rather than closing unseen.
            if (Book.OnTop) Book.TryClose();
            else Book.TryOpen();
            return true;
        });
        // No handler: the book reads it in its own key handling, after any text field it has focused.
        api.Input.RegisterHotKey(GuiDialogQuestBook.BackHotkey, "Quest book: back", GlKeys.BackSpace, HotkeyType.GUIOrOtherControls);
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
            .BeginSubCommand("catchup")
                .WithDescription("Complete the quests a player's carried items show they've done (for worlds older than Questline)")
                .WithArgs(parsers.OptionalPlayerUids("player"))
                .HandleWith(args =>
                {
                    var (uid, name) = Target(args, 0);
                    if (api.World.PlayerByUid(uid) is not IServerPlayer { ConnectionState: EnumClientState.Playing } player) return TextCommandResult.Error("Name a player who is playing");
                    int count = Engine.CatchUp(player).Count;
                    return TextCommandResult.Success(count == 0
                        ? $"Nothing {name} carries completes a quest: only tools and goods in the hotbar or backpack count"
                        : $"Caught {name} up: {count} quests completed");
                })
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
            .BeginSubCommand("export")
                .WithDescription("Write the editor's chapters out as content-mod assets")
                .HandleWith(_ =>
                {
                    var (folder, count) = Engine.Editor.Export();
                    return TextCommandResult.Success($"Exported {count} chapters to {folder}");
                })
            .EndSubCommand()
            .BeginSubCommand("graph")
                .WithDescription("Show what makes an item and what it's used in")
                .WithArgs(parsers.Word("item code"))
                .HandleWith(args =>
                {
                    var loc = new AssetLocation((string)args[0]);
                    CollectibleObject item = api.World.GetItem(loc) ?? (CollectibleObject)api.World.GetBlock(loc);
                    if (item == null) return TextCommandResult.Error($"No item or block {args[0]}");
                    string Line(Graph.GraphEdge e) => $"{e.Kind}: {string.Join(" + ", e.Inputs.Select(i => $"{i.Quantity}× {string.Join("|", i.Codes)}"))} → {string.Join(", ", e.Outputs.Select(o => $"{o.Quantity}× {o.Code}"))}";
                    var made = Engine.Graph.MadeBy(item.Code.ToString()).Take(15).Select(Line);
                    var used = Engine.Graph.UsedIn(item).Take(15).Select(Line);
                    return TextCommandResult.Success($"Made by:\n  {string.Join("\n  ", made)}\nUsed in:\n  {string.Join("\n  ", used)}");
                })
            .EndSubCommand()
            .BeginSubCommand("snapshot")
                .WithDescription("Save what the pack checker needs from this game (items, text, the recipe graph) to ModConfig/questline/")
                .HandleWith(_ =>
                {
                    string file = System.IO.Path.Combine(api.GetOrCreateDataPath("ModConfig"), "questline", "questline-snapshot.json");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                    var snapshot = Engine.Snapshot();
                    System.IO.File.WriteAllText(file, snapshot.ToJson());
                    return TextCommandResult.Success($"Saved {snapshot.Items.Count + snapshot.Blocks.Count} items and blocks, {snapshot.Graph.Edges.Count} recipe edges to {file}");
                })
            .EndSubCommand()
            .BeginSubCommand("structures")
                .WithDescription("Name the worldgen structures you are standing in, as a visit task would")
                .HandleWith(args =>
                {
                    if (args.Caller.Entity is not { } entity) return TextCommandResult.Error("Stand somewhere first");
                    var here = VisitTracker.At(api, entity.Pos.AsBlockPos)
                        .Select(s => $"{string.Join(", ", Engine.Structures.Of(s).DefaultIfEmpty("(no worldgen config names it)"))}  [{s.Code}, group {s.Group}]")
                        .ToList();
                    return TextCommandResult.Success(here.Count == 0 ? "No structure here" : string.Join("\n", here));
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
