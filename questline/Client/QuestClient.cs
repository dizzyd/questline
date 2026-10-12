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

    /// <summary>The editor's view of every chapter, broken quests included. Null unless this player may edit.</summary>
    public EditorState Editor { get; private set; }

    public bool CanEdit => Editor != null;

    /// <summary>
    /// Every recipe as a graph, from the client's own copy of the recipes. It's built a few
    /// milliseconds a frame once the world has loaded; asking for it before then finishes it at once.
    /// </summary>
    public Graph.RecipeGraph Graph => graph ??= (graphBuild ??= new questline.Graph.GraphBuild(capi)).FinishNow();
    Graph.RecipeGraph graph;
    questline.Graph.GraphBuild graphBuild;

    /// <summary>Whether the graph is built: what's offered from it (plans, suggestions) waits for this.</summary>
    public bool GraphReady => graph != null;

    void StartGraph()
    {
        graphBuild ??= new questline.Graph.GraphBuild(capi);
        long id = 0;
        id = capi.Event.RegisterGameTickListener(_ =>
        {
            if (graph == null && !graphBuild.Advance(3)) return;
            graph ??= graphBuild.Result;
            capi.Event.UnregisterGameTickListener(id);
        }, 20);
    }
    public bool Ready => Registry != null;

    /// <summary>Fires after definitions or states arrive.</summary>
    public event Action Changed;

    /// <summary>Fires when the editor's chapters arrive, just before <see cref="Changed"/>. They follow the definitions after every reload.</summary>
    public event Action EditorChanged;

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
            .RegisterMessageType<EditorPacket>()
            .RegisterMessageType<SaveChapterRequest>()
            .RegisterMessageType<ExportRequest>()
            .SetMessageHandler<EditorPacket>(OnEditor)
            .SetMessageHandler<DefinitionsPacket>(OnDefinitions)
            .SetMessageHandler<StatePacket>(OnState)
            .SetMessageHandler<PartyPacket>(OnParty);
        capi.Event.LevelFinalize += StartGraph;

        try
        {
            Settings = capi.LoadModConfig<ClientSettings>(ClientSettings.FileName) ?? new ClientSettings();
        }
        catch (Exception e)
        {
            capi.Logger.Error("[questline] can't read ModConfig/{0}, using defaults: {1}", ClientSettings.FileName, e.Message);
        }
        BookStyle.TextScale = (Settings?.TextSize ?? 115) / 100.0;
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

    /// <summary>Sets the book's text size to the nearest of <see cref="ClientSettings.TextSizes"/>, and saves it.</summary>
    public void SetTextSize(int percent)
    {
        Settings.TextSize = ClientSettings.TextSizes.OrderBy(s => Math.Abs(s - percent)).First();
        BookStyle.TextScale = Settings.TextSize / 100.0;
        SaveSettings();
        Changed?.Invoke();
    }

    /// <summary>One step of <see cref="ClientSettings.TextSizes"/> up or down; false at either end.</summary>
    public bool StepTextSize(int direction)
    {
        int i = Array.IndexOf(ClientSettings.TextSizes, Settings.TextSize);
        if (i < 0) i = Array.IndexOf(ClientSettings.TextSizes, 115);
        int next = Math.Clamp(i + direction, 0, ClientSettings.TextSizes.Length - 1);
        if (next == i) return false;
        SetTextSize(ClientSettings.TextSizes[next]);
        return true;
    }

    public void SetToasts(bool on)
    {
        Settings.Toasts = on;
        SaveSettings();
    }

    void OnDefinitions(DefinitionsPacket packet)
    {
        ServerReveal = ParseLevel(packet.RevealLevel, RevealLevel.Hints);
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

    void OnEditor(EditorPacket packet)
    {
        var chapters = new Dictionary<string, ChapterDef>();
        foreach (var (id, json) in packet.Chapters ?? [])
        {
            chapters[id] = Newtonsoft.Json.JsonConvert.DeserializeObject<ChapterDef>(json);
        }
        Editor = new EditorState(packet.Domain, chapters, packet.Problems ?? [], (packet.Edited ?? []).ToHashSet());
        EditorChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Sends an edited chapter, and the text it uses, to be saved and reloaded.</summary>
    public void SaveChapter(ChapterDef chapter, Dictionary<string, string> text) => channel.SendPacket(new SaveChapterRequest
    {
        ChapterJson = Newtonsoft.Json.JsonConvert.SerializeObject(chapter),
        TextJson = Newtonsoft.Json.JsonConvert.SerializeObject(text),
    });

    /// <summary>Drops a chapter's working copy on the server.</summary>
    public void RevertChapter(string chapterId) => channel.SendPacket(new SaveChapterRequest { Revert = true, ChapterId = chapterId });

    public void Export() => channel.SendPacket(new ExportRequest());

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

    #region Planner

    /// <summary>The most the server lets the planner show.</summary>
    public RevealLevel ServerReveal { get; private set; } = RevealLevel.Hints;

    /// <summary>What the planner shows this player: their own choice, up to the server's limit.</summary>
    public RevealLevel Reveal => (RevealLevel)Math.Min((int)ServerReveal, (int)ParseLevel(Settings.PlannerLevel, RevealLevel.Guide));

    public static RevealLevel ParseLevel(string value, RevealLevel fallback) => Enum.TryParse<RevealLevel>(value, true, out var level) ? level : fallback;

    public void SetPlannerLevel(RevealLevel level)
    {
        Settings.PlannerLevel = level.ToString().ToLowerInvariant();
        SaveSettings();
        Changed?.Invoke();
    }

    /// <summary>This world's goal plans, by target item code.</summary>
    public IReadOnlyList<string> Plans => Settings.Plans.TryGetValue(capi.World.SavegameIdentifier, out var plans) ? plans : [];

    public void AddPlan(string code)
    {
        string world = capi.World.SavegameIdentifier;
        if (!Settings.Plans.TryGetValue(world, out var plans)) Settings.Plans[world] = plans = [];
        if (!plans.Contains(code)) plans.Add(code);
        SaveSettings();
        Changed?.Invoke();
    }

    public void RemovePlan(string code)
    {
        if (!Settings.Plans.TryGetValue(capi.World.SavegameIdentifier, out var plans) || !plans.Remove(code)) return;
        SaveSettings();
        Changed?.Invoke();
    }

    readonly Dictionary<string, string[]> matchCache = [];

    /// <summary>A planner over this player's inventory as it is now.</summary>
    public Planner MakePlanner()
    {
        var counts = new Dictionary<string, int>();
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            if (capi.World.Player.InventoryManager.GetOwnInventory(name) is not { } inv) continue;
            foreach (var slot in inv)
            {
                if (slot.Itemstack?.Collectible?.Code is not { } code) continue;
                string key = code.ToString();
                counts[key] = counts.GetValueOrDefault(key) + slot.StackSize;
            }
        }
        return new Planner(Graph, code => counts.GetValueOrDefault(code), CodesMatching, IsSpoiler);
    }

    IEnumerable<string> CodesMatching(string pattern)
    {
        if (!matchCache.TryGetValue(pattern, out var codes))
        {
            var loc = new Vintagestory.API.Common.AssetLocation(pattern);
            matchCache[pattern] = codes = capi.World.Collectibles.Where(c => c?.Code != null && Vintagestory.API.Util.WildcardUtil.Match(loc, c.Code)).Select(c => c.Code.ToString()).ToArray();
        }
        return codes;
    }

    public static bool IsSpoiler(string code) => GraphChecks.Spoilers.Any(s => Vintagestory.API.Util.WildcardUtil.Match(new Vintagestory.API.Common.AssetLocation(s), new Vintagestory.API.Common.AssetLocation(code)));

    #endregion

    /// <summary>Asks the server to create, invite, accept, decline, leave or kick. See <see cref="PartyRequest"/>.</summary>
    public void PartyAction(string action, string arg = null) => channel.SendPacket(new PartyRequest { Action = action, Arg = arg });
}

/// <summary>What the editor works from: the chapters as written and the last load's problems.</summary>
public record EditorState(string Domain, Dictionary<string, ChapterDef> Chapters, List<string> Problems, HashSet<string> Edited)
{
    /// <summary>The problems that name a quest or chapter, by its id.</summary>
    public IEnumerable<string> ProblemsOf(string id) => Problems.Where(p => p.StartsWith(id + ":"));
}
