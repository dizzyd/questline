using System;
using System.Collections.Generic;
using System.Linq;
using questline.Model;
using questline.Network;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace questline.Client;

/// <summary>
/// The client's copy of the quest world: definitions and this player's quest states, both as
/// the server last sent them. Displays only; every action is a request to the server.
/// </summary>
public class QuestClient
{
    readonly ICoreClientAPI capi;
    readonly IClientNetworkChannel channel;
    readonly Dictionary<string, QuestStateEntry> states = [];

    public QuestRegistry Registry { get; private set; }

    /// <summary>The player's party and invites, as the server last sent them. Null until the server says.</summary>
    public PartyPacket Party { get; private set; }

    public bool InParty => Party?.Id != null;

    public ClientSettings Settings { get; private set; } = new();
    public bool Ready => Registry != null;

    /// <summary>Fires after definitions or states arrive.</summary>
    public event Action Changed;

    public QuestClient(ICoreClientAPI capi)
    {
        this.capi = capi;
        channel = capi.Network.RegisterChannel(QuestEngine.ChannelName)
            .RegisterMessageType<DefinitionsPacket>()
            .RegisterMessageType<StatePacket>()
            .RegisterMessageType<ClaimRequest>()
            .RegisterMessageType<CheckboxRequest>()
            .RegisterMessageType<PartyPacket>()
            .RegisterMessageType<PartyRequest>()
            .SetMessageHandler<DefinitionsPacket>(OnDefinitions)
            .SetMessageHandler<StatePacket>(OnState)
            .SetMessageHandler<PartyPacket>(OnParty);

        try
        {
            Settings = capi.LoadModConfig<ClientSettings>(ClientSettings.FileName) ?? new ClientSettings();
        }
        catch (Exception e)
        {
            capi.Logger.Error("[questline] can't read ModConfig/{0}, using defaults: {1}", ClientSettings.FileName, e.Message);
        }
    }

    public void SaveSettings() => capi.StoreModConfig(Settings, ClientSettings.FileName);

    /// <summary>This world's pinned quest ids, in pin order.</summary>
    public IReadOnlyList<string> Pins => Settings.Pins.TryGetValue(capi.World.SavegameIdentifier, out var pins) ? pins : [];

    public bool IsPinned(string questId) => Pins.Contains(questId);

    /// <summary>Pins a quest to the HUD. False when <see cref="ClientSettings.MaxPins"/> are already pinned.</summary>
    public bool Pin(string questId)
    {
        string world = capi.World.SavegameIdentifier;
        if (!Settings.Pins.TryGetValue(world, out var pins)) Settings.Pins[world] = pins = [];
        if (pins.Contains(questId)) return true;
        if (pins.Count >= ClientSettings.MaxPins) return false;
        pins.Add(questId);
        SaveSettings();
        Changed?.Invoke();
        return true;
    }

    public void Unpin(string questId)
    {
        if (!Settings.Pins.TryGetValue(capi.World.SavegameIdentifier, out var pins) || !pins.Remove(questId)) return;
        SaveSettings();
        Changed?.Invoke();
    }

    public void SetToasts(bool on)
    {
        Settings.Toasts = on;
        SaveSettings();
    }

    void OnDefinitions(DefinitionsPacket packet)
    {
        Registry = QuestRegistry.FromJson(packet.RegistryJson);
        states.Clear();
        Changed?.Invoke();
    }

    void OnState(StatePacket packet)
    {
        if (packet.Full) states.Clear();
        foreach (var entry in packet.Entries)
        {
            // A change, not the first full sync, finishing a quest gets a toast.
            if (Settings.Toasts && !packet.Full && entry.State >= QuestState.Claimable && StateOf(entry.QuestId) < QuestState.Claimable)
            {
                capi.TriggerIngameDiscovery(this, "questline-complete", Lang.Get("questline:toast-complete", TextOf(entry.QuestId + "-title")));
            }
            states[entry.QuestId] = entry;
        }

        // A pin has done its job once the quest is claimed.
        if (Settings.Pins.TryGetValue(capi.World.SavegameIdentifier, out var pins) && pins.RemoveAll(id => StateOf(id) == QuestState.Done) > 0)
        {
            SaveSettings();
        }
        Changed?.Invoke();
    }

    void OnParty(PartyPacket packet)
    {
        // protobuf sends an empty list as null.
        packet.Members ??= [];
        packet.Invites ??= [];
        Party = packet;
        Changed?.Invoke();
    }

    /// <summary>A lang entry, from the editor's text if the server sent it one.</summary>
    public string TextOf(string key) => Registry?.Text.TryGetValue(key, out var value) == true ? value : Lang.Get(key);

    public QuestState StateOf(string questId) => states.TryGetValue(questId, out var e) ? e.State : QuestState.Locked;

    /// <summary>Progress per task; protobuf sends an empty array as null, so this is never null.</summary>
    public int[] TasksOf(QuestDef quest) =>
        states.TryGetValue(quest.Id, out var e) && e.Tasks != null ? e.Tasks : new int[quest.Tasks.Count];

    public bool IsVisible(QuestDef quest)
    {
        var state = StateOf(quest.Id);
        return quest.Visibility switch
        {
            QuestVisibility.WhenUnlocked => state != QuestState.Locked,
            QuestVisibility.WhenAnyPrereqDone => state != QuestState.Locked
                || (quest.Requires?.QuestIds().Any(id => StateOf(id) >= QuestState.Claimable) ?? true),
            QuestVisibility.Secret => state >= QuestState.Claimable,
            _ => true,
        };
    }

    public bool IsVisible(ChapterDef chapter) => chapter.Visibility switch
    {
        ChapterVisibility.Hidden => false,
        ChapterVisibility.WhenAnyUnlocked => chapter.Quests.Any(q => StateOf(q.Id) != QuestState.Locked),
        _ => true,
    };

    public void Claim(string questId, int[] choices) => channel.SendPacket(new ClaimRequest { QuestId = questId, Choices = choices });

    public void Check(string questId, int taskIndex) => channel.SendPacket(new CheckboxRequest { QuestId = questId, TaskIndex = taskIndex });

    /// <summary>Asks the server to create, invite, accept, decline, leave or kick. See <see cref="PartyRequest"/>.</summary>
    public void PartyAction(string action, string arg = null) => channel.SendPacket(new PartyRequest { Action = action, Arg = arg });
}
