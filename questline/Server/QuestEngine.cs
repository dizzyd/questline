using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using questline.Model;
using questline.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace questline.Server;

/// <summary>
/// The authority on every quest: holds the registry and everyone's progress, turns task
/// reports into progress, grants rewards on a valid claim, and keeps each client's copy of its
/// quest states current.
///
/// A quest's tasks are held by whoever its mode says (<see cref="Holder"/>): the player, their
/// party, or the server. Whether a player has claimed it is always in the player's own record,
/// except for a server quest claimed once for everyone.
/// </summary>
public partial class QuestEngine
{
    public const string ChannelName = "questline";
    const string SaveKey = "questline-progress", PartiesKey = "questline-parties", ServerKey = "questline-server";

    readonly ICoreServerAPI api;
    IServerNetworkChannel channel;
    Dictionary<string, PlayerProgress> progress = [];
    PlayerProgress serverProgress = new();
    // What each client was last told, so syncs send only changes.
    readonly Dictionary<string, Dictionary<string, QuestStateEntry>> sent = [];
    Dictionary<string, List<(QuestDef Quest, int Index)>> tasksByType = [];
    readonly Dictionary<AssetLocation, AssetLocation> patternCache = [];
    readonly HashSet<string> inventoryDirty = [];
    readonly HashSet<string> syncDirty = [];
    readonly Dictionary<string, Action<int>> inventoryWatchers = [];

    public QuestRegistry Registry { get; private set; } = new();
    public QuestlineConfig Config { get; set; } = new();

    // Task types a player's history records. Obtain follows the inventory instead, and a
    // checkbox has nothing to remember.
    static readonly HashSet<string> HistoryTypes = ["kill", "mount", "place", "break", "craft", "knap", "clayform", "smith", "cook", "smelt", "trade", "custom"];
    public List<string> Problems { get; private set; } = [];

    /// <summary>Every recipe as a graph, built on first use.</summary>
    public Graph.RecipeGraph Graph => graph ??= questline.Graph.RecipeGraph.Build(api);
    Graph.RecipeGraph graph;

    /// <summary>The in-game editor's server half: working copies and export.</summary>
    public QuestEditor Editor { get; private set; }
    Dictionary<string, string> rawChapters = [];
    HashSet<string> edited = [];

    /// <summary>Fires after a quest's tasks are all done, once for each online player it was done for.</summary>
    public event Action<IServerPlayer, string> QuestCompleted;

    /// <summary>Fires when a stage is first reached, once for each online player who now has it.</summary>
    public event Action<IServerPlayer, string> StageSet;

    /// <summary>Event-bus names, for mods that use Questline without referencing it.</summary>
    public const string ReportEvent = "questline:report", QuestCompletedEvent = "questline:questcompleted", StageSetEvent = "questline:stageset";

    public QuestEngine(ICoreServerAPI api)
    {
        this.api = api;
    }

    public void Start()
    {
        try
        {
            Config = api.LoadModConfig<QuestlineConfig>(QuestlineConfig.FileName) ?? new QuestlineConfig();
            api.StoreModConfig(Config, QuestlineConfig.FileName);
        }
        catch (Exception e)
        {
            api.Logger.Error("[questline] can't read ModConfig/{0}, using defaults: {1}", QuestlineConfig.FileName, e.Message);
        }

        channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<DefinitionsPacket>()
            .RegisterMessageType<StatePacket>()
            .RegisterMessageType<ClaimRequest>()
            .RegisterMessageType<CheckboxRequest>()
            .RegisterMessageType<PartyPacket>()
            .RegisterMessageType<PartyRequest>()
            .RegisterMessageType<EditorPacket>()
            .RegisterMessageType<SaveChapterRequest>()
            .RegisterMessageType<ExportRequest>()
            .SetMessageHandler<SaveChapterRequest>(OnSaveChapter)
            .SetMessageHandler<ExportRequest>(OnExport)
            .SetMessageHandler<ClaimRequest>((player, req) => Claim(player, req.QuestId, req.Choices))
            .SetMessageHandler<CheckboxRequest>((player, req) => Check(player, req.QuestId, req.TaskIndex))
            .SetMessageHandler<PartyRequest>(OnPartyRequest);

        Editor = new QuestEditor(api, this);
        // The graph checks need every recipe, which the game has only once the world is running.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            recipesReady = true;
            RunGraphChecks();
        });
        api.Permissions.RegisterPrivilege(QuestEditor.Privilege, "Edit quests in the quest book");

        api.Event.SaveGameLoaded += LoadProgress;
        api.Event.GameWorldSave += SaveProgress;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.RegisterGameTickListener(_ => ScanDirtyInventories(), 500);
        api.Event.RegisterEventBusListener(OnReportEvent, 0.5, ReportEvent);
    }

    /// <summary>A custom-task report posted on the event bus: playeruid, key and amount (default 1).</summary>
    void OnReportEvent(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (data is not ITreeAttribute tree) return;
        string uid = tree.GetString("playeruid"), key = tree.GetString("key");
        if (uid != null && key != null) Report(uid, "custom", new AssetLocation(key), tree.GetInt("amount", 1));
    }

    /// <summary>Re-reads every chapter and resends definitions and state to everyone online.</summary>
    public void Reload()
    {
        var loader = new QuestLoader(new ServerFacts(api), api.Logger, Editor?.WorkingDir);
        Registry = loader.Load();
        Problems = loader.Problems;
        rawChapters = loader.RawChapters;
        edited = loader.Edited;
        if (recipesReady) RunGraphChecks(resend: false);

        tasksByType = [];
        foreach (var quest in Registry.Quests.Values)
        {
            for (int i = 0; i < quest.Tasks.Count; i++)
            {
                string type = quest.Tasks[i].Type;
                if (!tasksByType.TryGetValue(type, out var list)) tasksByType[type] = list = [];
                list.Add((quest, i));
            }
        }

        foreach (var p in AllRecords()) p.ApplyAliases(Registry.Aliases);

        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            SendDefinitions(player);
            inventoryDirty.Add(player.PlayerUID);
        }
    }

    bool recipesReady;

    /// <summary>What the pack checker needs from this game, for <c>/questline snapshot</c>.</summary>
    public GameSnapshot Snapshot() => new()
    {
        // A const, so read from the running game's assembly; written plainly it's the version this mod compiled against.
        GameVersion = (string)typeof(Vintagestory.API.Config.GameVersion).GetField(nameof(Vintagestory.API.Config.GameVersion.ShortGameVersion))!.GetValue(null),
        Mods = api.ModLoader.Mods.Select(m => m.Info.ModID).ToList(),
        Items = api.World.Items.Where(i => i?.Code != null).Select(i => i.Code.ToString()).ToList(),
        Blocks = api.World.Blocks.Where(b => b?.Code != null).Select(b => b.Code.ToString()).ToList(),
        Entities = api.World.EntityTypes.Select(e => e.Code.ToString()).ToList(),
        Lang = Vintagestory.API.Config.Lang.GetAllEntries().ToDictionary(e => e.Key, e => e.Value),
        Graph = Graph,
    };

    /// <summary>Adds what the recipe graph says about the quests to <see cref="Problems"/>, and tells editors.</summary>
    // What the checks know of the game, kept across reloads: only the quests change.
    GraphIndex graphIndex;

    public void RunGraphChecks(bool resend = true)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (graphIndex?.Graph != Graph)
        {
            graphIndex = new GraphIndex(Graph,
                api.World.Blocks.Where(b => b?.Code != null).Select(b => b.Code.ToString()),
                api.World.Items.Where(i => i?.Code != null).Select(i => i.Code.ToString()),
                api.World.EntityTypes.Select(e => e.Code.ToString()));
            api.Logger.Notification("[questline] graph index: {0} codes can be had, built in {1:0} ms", graphIndex.Obtainable.Count, watch.Elapsed.TotalMilliseconds);
        }
        var checks = new GraphChecks(Registry, graphIndex);
        foreach (var problem in checks.Run())
        {
            Problems.Add(problem);
            api.Logger.Warning("[questline] " + problem);
        }
        api.Logger.Notification("[questline] graph checks in {0:0} ms", watch.Elapsed.TotalMilliseconds);
        if (!resend) return;
        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            if (QuestEditor.CanEdit(player)) SendEditor(player);
        }
    }

    IEnumerable<PlayerProgress> AllRecords() => progress.Values.Concat(parties.Values.Select(p => p.Progress)).Append(serverProgress);

    #region Progress and state

    /// <summary>A player's own record: their individual quests, their claims and their history.</summary>
    public PlayerProgress ProgressOf(string playerUid)
    {
        if (!progress.TryGetValue(playerUid, out var p)) progress[playerUid] = p = new PlayerProgress();
        return p;
    }

    /// <summary>The server's record, for server quests.</summary>
    public PlayerProgress ServerProgress => serverProgress;

    /// <summary>Whoever holds a quest's tasks for this player: the player, their party, or the server.</summary>
    public PlayerProgress Holder(QuestDef quest, string playerUid) => quest.Mode switch
    {
        QuestMode.Server => serverProgress,
        QuestMode.Party when PartyOf(playerUid) is { } party => party.Progress,
        _ => ProgressOf(playerUid),
    };

    /// <summary>The record whose <see cref="QuestProgress.Claimed"/> says whether this player's claim is spent.</summary>
    PlayerProgress ClaimHolder(QuestDef quest, string playerUid) =>
        quest.Mode == QuestMode.Server && quest.Claim == ClaimMode.First ? serverProgress : ProgressOf(playerUid);

    /// <summary>The quest's tasks as this player sees them, or null if nothing is recorded.</summary>
    public QuestProgress TasksFor(string playerUid, string questId) =>
        Registry.Get(questId) is { } quest ? Holder(quest, playerUid).Find(questId) : ProgressOf(playerUid).Find(questId);

    /// <summary>The players whose work counts towards this player's copy of the quest.</summary>
    IEnumerable<string> Contributors(QuestDef quest, string playerUid) => quest.Mode switch
    {
        QuestMode.Server => api.World.AllOnlinePlayers.Select(p => p.PlayerUID),
        QuestMode.Party when PartyOf(playerUid) is { } party => party.Members,
        _ => [playerUid],
    };

    /// <summary>The players who see a change to this player's copy of the quest.</summary>
    IEnumerable<string> Sharers(QuestDef quest, string playerUid) => quest.Mode switch
    {
        QuestMode.Server => api.World.AllOnlinePlayers.Select(p => p.PlayerUID).Append(playerUid).Distinct(),
        QuestMode.Party when PartyOf(playerUid) is { } party => party.Members,
        _ => [playerUid],
    };

    public bool IsUnlocked(QuestDef quest, string playerUid) =>
        quest.Requires == null || quest.Requires.Evaluate(new PlayerContext(this, playerUid));

    public QuestState StateOf(string playerUid, string questId)
    {
        var quest = Registry.Get(questId) ?? throw new ArgumentException($"No quest {questId}");
        return StateOf(playerUid, quest);
    }

    QuestState StateOf(string playerUid, QuestDef quest) => PlayerProgress.StateOf(quest,
        Holder(quest, playerUid).Find(quest.Id),
        ClaimHolder(quest, playerUid).Find(quest.Id)?.Claimed == true,
        IsUnlocked(quest, playerUid));

    public bool IsComplete(string playerUid, string questId) => TasksFor(playerUid, questId)?.Complete == true;

    bool IsActive(QuestDef quest, string playerUid) => Holder(quest, playerUid).Find(quest.Id)?.Complete != true && IsUnlocked(quest, playerUid);

    /// <summary>Marks the quest complete if every task has reached its count.</summary>
    bool TryComplete(QuestDef quest, string playerUid, QuestProgress qp)
    {
        if (qp.Complete) return false;
        for (int i = 0; i < quest.Tasks.Count; i++)
        {
            if (qp.Tasks[i] < quest.Tasks[i].Count) return false;
        }

        qp.Complete = true;
        qp.CompletedDay = api.World.Calendar.TotalDays;

        foreach (string uid in Sharers(quest, playerUid).ToList())
        {
            // Newly unlocked quests may already be satisfied by what the player carries.
            inventoryDirty.Add(uid);
            syncDirty.Add(uid);
            if (api.World.PlayerByUid(uid) is IServerPlayer player) QuestCompleted?.Invoke(player, quest.Id);
            api.Event.PushEvent(QuestCompletedEvent, new TreeAttribute { ["playeruid"] = new StringAttribute(uid), ["questid"] = new StringAttribute(quest.Id) });
        }
        return true;
    }

    /// <summary>Marks everyone who sees this quest as needing a sync.</summary>
    void Touch(QuestDef quest, string playerUid)
    {
        foreach (string uid in Sharers(quest, playerUid)) syncDirty.Add(uid);
    }

    #endregion

    #region Task reports

    /// <summary>
    /// Credits <paramref name="amount"/> towards every active task of <paramref name="type"/> that
    /// matches <paramref name="code"/>. The trackers call this; so can other mods.
    /// </summary>
    public void Report(IServerPlayer player, string type, AssetLocation code, int amount)
    {
        if (player != null) Report(player.PlayerUID, type, code, amount);
    }

    /// <summary>As <see cref="Report(IServerPlayer, string, AssetLocation, int)"/>, by uid: the player needn't be online.</summary>
    public void Report(string playerUid, string type, AssetLocation code, int amount)
    {
        if (playerUid == null || code == null || amount <= 0) return;

        var own = ProgressOf(playerUid);
        if (HistoryTypes.Contains(type))
        {
            string key = PlayerProgress.HistoryKey(type, code.ToString());
            own.History[key] = own.History.GetValueOrDefault(key) + amount;
        }
        if (!tasksByType.TryGetValue(type, out var tasks)) return;

        foreach (var (quest, index) in tasks)
        {
            var task = quest.Tasks[index];
            if (!WildcardUtil.Match(Pattern(task), code) || !IsActive(quest, playerUid)) continue;

            var qp = Holder(quest, playerUid).GetOrCreate(quest);
            if (qp.Tasks[index] >= task.Count) continue;
            qp.Tasks[index] = Math.Min(task.Count, qp.Tasks[index] + amount);
            TryComplete(quest, playerUid, qp);
            Touch(quest, playerUid);
        }

        FlushSync();
    }

    /// <summary>Ticks a checkbox task, on the player's request from the book.</summary>
    public void Check(IServerPlayer player, string questId, int taskIndex)
    {
        var quest = Registry.Get(questId);
        if (quest == null || taskIndex < 0 || taskIndex >= quest.Tasks.Count || quest.Tasks[taskIndex].Type != "checkbox") return;
        if (!IsActive(quest, player.PlayerUID)) return;

        var qp = Holder(quest, player.PlayerUID).GetOrCreate(quest);
        qp.Tasks[taskIndex] = quest.Tasks[taskIndex].Count;
        TryComplete(quest, player.PlayerUID, qp);
        Touch(quest, player.PlayerUID);
        FlushSync();
    }

    AssetLocation Pattern(TaskDef task)
    {
        var loc = new AssetLocation(task.Target);
        if (!patternCache.TryGetValue(loc, out var cached)) patternCache[loc] = cached = loc;
        return cached;
    }

    // Set whenever quests may have unlocked or the inventory changed: newly unlocked quests take
    // credit from history and from what the player carries.
    void ScanDirtyInventories()
    {
        if (inventoryDirty.Count == 0) return;
        foreach (string uid in inventoryDirty.ToList())
        {
            inventoryDirty.Remove(uid);
            if (api.World.PlayerByUid(uid) is not IServerPlayer { ConnectionState: EnumClientState.Playing } player) continue;
            RecordFound(player);
            SeedFromHistory(uid);
            ScanInventory(uid);
        }
        FlushSync();
    }

    /// <summary>
    /// Credits each newly unlocked quest, once, with what was done before it unlocked: by the
    /// player, or by every member for a party quest. Server quests count only live work.
    /// </summary>
    public void SeedFromHistory(string playerUid)
    {
        if (!Config.CreditEarlierWork) return;

        foreach (var quest in Registry.Quests.Values)
        {
            if (quest.Mode == QuestMode.Server || !quest.Tasks.Any(t => HistoryTypes.Contains(t.Type))) continue;
            var holder = Holder(quest, playerUid);
            if (holder.Find(quest.Id)?.Seeded == true || !IsActive(quest, playerUid)) continue;

            var qp = holder.GetOrCreate(quest);
            qp.Seeded = true;
            var histories = Contributors(quest, playerUid).Select(uid => ProgressOf(uid).History).ToList();
            for (int i = 0; i < quest.Tasks.Count; i++)
            {
                var task = quest.Tasks[i];
                if (!HistoryTypes.Contains(task.Type)) continue;

                string prefix = task.Type + "|";
                var pattern = Pattern(task);
                int done = histories.SelectMany(h => h)
                    .Where(h => h.Key.StartsWith(prefix) && WildcardUtil.Match(pattern, new AssetLocation(h.Key[prefix.Length..])))
                    .Sum(h => h.Value);
                if (done <= qp.Tasks[i]) continue;

                qp.Tasks[i] = Math.Min(task.Count, done);
                Touch(quest, playerUid);
            }
            if (TryComplete(quest, playerUid, qp)) Touch(quest, playerUid);
        }
    }

    /// <summary>Sets every active obtain task to what is carried now: by the player, or pooled across whoever shares the quest.</summary>
    void ScanInventory(string playerUid)
    {
        if (!tasksByType.TryGetValue("obtain", out var tasks)) return;

        foreach (var (quest, index) in tasks)
        {
            if (!IsActive(quest, playerUid)) continue;

            var task = quest.Tasks[index];
            var pattern = Pattern(task);
            int held = Math.Min(task.Count, OnlineContributors(quest, playerUid).Sum(p => CountCarried(p, pattern)));
            var holder = Holder(quest, playerUid);
            var qp = holder.Find(quest.Id);
            if ((qp == null ? 0 : qp.Tasks[index]) == held) continue;

            qp = holder.GetOrCreate(quest);
            qp.Tasks[index] = held;
            TryComplete(quest, playerUid, qp);
            Touch(quest, playerUid);
        }
    }

    IEnumerable<IServerPlayer> OnlineContributors(QuestDef quest, string playerUid) => Contributors(quest, playerUid)
        .Select(uid => api.World.PlayerByUid(uid) as IServerPlayer)
        .Where(p => p is { ConnectionState: EnumClientState.Playing });

    static IEnumerable<ItemSlot> CarriedSlots(IServerPlayer player)
    {
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(name);
            if (inv == null) continue;
            foreach (var slot in inv) yield return slot;
        }
    }

    /// <summary>What the player carries that matches: whole stacks, and liquids in their containers, by the litre.</summary>
    static int CountCarried(IServerPlayer player, AssetLocation pattern) => CountStacks(player, pattern) + CarriedSlots(player).Sum(s => LitresIn(s.Itemstack, pattern));

    /// <summary>Only the stacks a claim can take back; liquids stay in their buckets.</summary>
    static int CountStacks(IServerPlayer player, AssetLocation pattern) => CarriedSlots(player)
        .Where(s => s.Itemstack?.Collectible?.Code != null && WildcardUtil.Match(pattern, s.Itemstack.Collectible.Code))
        .Sum(s => s.StackSize);

    static int LitresIn(ItemStack container, AssetLocation pattern)
    {
        if (container?.Collectible is not ILiquidInterface liquids) return 0;
        var content = liquids.GetContent(container);
        if (content?.Collectible?.Code == null || !WildcardUtil.Match(pattern, content.Collectible.Code)) return 0;
        return (int)liquids.GetCurrentLitres(container);
    }

    #endregion

    #region Claiming

    /// <summary>Grants a completed quest's rewards. Returns null, or why the claim was refused.</summary>
    public string Claim(IServerPlayer player, string questId, int[] choices)
    {
        var quest = Registry.Get(questId);
        if (quest == null) return Refuse(player, $"no quest {questId}");

        string uid = player.PlayerUID;
        var qp = Holder(quest, uid).Find(questId);
        if (qp?.Complete != true) return Refuse(player, $"{questId} isn't complete");
        var claim = ClaimHolder(quest, uid).GetOrCreate(quest);
        if (claim.Claimed) return Refuse(player, $"{questId} is already claimed");

        var gifts = new List<RewardDef>();
        int choiceIndex = 0;
        foreach (var reward in quest.Rewards)
        {
            if (reward.Type != "choice")
            {
                gifts.Add(reward);
                continue;
            }
            int pick = choices != null && choiceIndex < choices.Length ? choices[choiceIndex] : -1;
            choiceIndex++;
            if (pick < 0 || pick >= reward.Options.Count) return Refuse(player, $"{questId}: pick a reward first");
            gifts.Add(reward.Options[pick]);
        }

        // Items to hand in go once per quest: on a shared quest, at the first claim, from
        // whoever shares it, the claimer first.
        var consumed = qp.Consumed ? [] : quest.Tasks.Where(t => t.Type == "obtain" && t.Consume).ToList();
        var givers = OnlineContributors(quest, uid).OrderBy(p => p.PlayerUID == uid ? 0 : 1).ToList();
        foreach (var task in consumed)
        {
            if (givers.Sum(p => CountStacks(p, Pattern(task))) < task.Count) return Refuse(player, $"{questId}: you no longer carry {task.Count}× {task.Target}");
        }
        foreach (var task in consumed)
        {
            int left = task.Count;
            foreach (var giver in givers) left = TakeCarried(giver, Pattern(task), left);
        }
        qp.Consumed = true;

        claim.Claimed = true;
        foreach (var gift in gifts)
        {
            if (gift.Type == "stage") SetStage(StageHolder(quest, uid), gift.Stage, Sharers(quest, uid));
            else Give(player, gift);
        }
        Touch(quest, uid);
        FlushSync();
        return null;
    }

    string Refuse(IServerPlayer player, string why)
    {
        api.Logger.Audit("[questline] refused claim by {0}: {1}", player.PlayerName, why);
        return why;
    }

    /// <summary>Takes up to <paramref name="count"/> matching items from what the player carries; returns how many are still owed.</summary>
    static int TakeCarried(IServerPlayer player, AssetLocation pattern, int count)
    {
        foreach (var slot in CarriedSlots(player))
        {
            if (count <= 0) break;
            if (slot.Itemstack?.Collectible?.Code == null || !WildcardUtil.Match(pattern, slot.Itemstack.Collectible.Code)) continue;
            int take = Math.Min(count, slot.StackSize);
            slot.TakeOut(take);
            slot.MarkDirty();
            count -= take;
        }
        return count;
    }

    void Give(IServerPlayer player, RewardDef reward)
    {
        var loc = new AssetLocation(reward.Item);
        CollectibleObject collectible = api.World.GetItem(loc) ?? (CollectibleObject)api.World.GetBlock(loc);
        if (collectible == null) return;

        var stack = new ItemStack(collectible, reward.Count);
        player.InventoryManager.TryGiveItemstack(stack, true);
        if (stack.StackSize > 0) api.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
    }

    #endregion

    #region Discovery

    /// <summary>Whether the player has carried, made, killed, placed or broken anything matching the pattern.</summary>
    public bool HasDiscovered(string playerUid, string pattern)
    {
        var loc = new AssetLocation(pattern);
        foreach (string key in ProgressOf(playerUid).History.Keys)
        {
            int bar = key.IndexOf('|');
            if (bar > 0 && WildcardUtil.Match(loc, new AssetLocation(key[(bar + 1)..]))) return true;
        }
        return false;
    }

    /// <summary>Remembers each thing the player carries, once, so discovered requirements can see it.</summary>
    void RecordFound(IServerPlayer player)
    {
        var history = ProgressOf(player.PlayerUID).History;
        bool added = false;
        foreach (var slot in CarriedSlots(player))
        {
            if (slot.Itemstack?.Collectible?.Code is not { } code) continue;
            added |= history.TryAdd(PlayerProgress.HistoryKey("found", code.ToString()), 1);
        }
        // Something new may unlock a quest gated on finding it.
        if (added) syncDirty.Add(player.PlayerUID);
    }

    #endregion

    #region Stages

    /// <summary>A player's stages: their own, their party's and the server's.</summary>
    public bool HasStage(string playerUid, string stage) =>
        ProgressOf(playerUid).Stages.Contains(stage)
        || PartyOf(playerUid)?.Progress.Stages.Contains(stage) == true
        || serverProgress.Stages.Contains(stage);

    /// <summary>Where a quest's stage reward goes: the server for a server quest, the party for a party quest, else the player.</summary>
    PlayerProgress StageHolder(QuestDef quest, string playerUid) => quest.Mode switch
    {
        QuestMode.Server => serverProgress,
        QuestMode.Party when PartyOf(playerUid) is { } party => party.Progress,
        _ => ProgressOf(playerUid),
    };

    /// <summary>Gives a player a stage of their own. Quests gated on it unlock; other mods hear about it.</summary>
    public void SetStage(IServerPlayer player, string stage)
    {
        SetStage(ProgressOf(player.PlayerUID), stage, [player.PlayerUID]);
        FlushSync();
    }

    void SetStage(PlayerProgress holder, string stage, IEnumerable<string> sharers)
    {
        if (!holder.Stages.Add(stage)) return;

        // A server stage reaches everyone, not only who shared the quest.
        var uids = holder == serverProgress ? api.World.AllOnlinePlayers.Select(p => p.PlayerUID) : sharers;
        foreach (string uid in uids.ToList())
        {
            inventoryDirty.Add(uid);
            syncDirty.Add(uid);
            if (api.World.PlayerByUid(uid) is IServerPlayer player) StageSet?.Invoke(player, stage);
            api.Event.PushEvent(StageSetEvent, new TreeAttribute { ["playeruid"] = new StringAttribute(uid), ["stage"] = new StringAttribute(stage) });
        }
    }

    #endregion

    #region Commands and tests

    /// <summary>Completes every task of a quest for a player, unlocked or not.</summary>
    public void ForceComplete(IServerPlayer player, string questId) => ForceComplete(player.PlayerUID, questId);

    public void ForceComplete(string playerUid, string questId)
    {
        var quest = Registry.Get(questId) ?? throw new ArgumentException($"No quest {questId}");
        var qp = Holder(quest, playerUid).GetOrCreate(quest);
        for (int i = 0; i < quest.Tasks.Count; i++) qp.Tasks[i] = quest.Tasks[i].Count;
        TryComplete(quest, playerUid, qp);
        Touch(quest, playerUid);
        FlushSync();
    }

    /// <summary>
    /// Makes a quest available to a player by meeting its requirements: completes the quests it
    /// needs (and theirs, all the way back), and grants the stages. Where it may pick, as in an
    /// any or an at-least, it meets the first options. A missing mod can't be met.
    /// </summary>
    public void Unlock(string playerUid, string questId)
    {
        var quest = Registry.Get(questId) ?? throw new ArgumentException($"No quest {questId}");
        if (quest.Requires != null) Satisfy(playerUid, quest.Requires, []);
        Touch(quest, playerUid);
        FlushSync();
    }

    void Satisfy(string playerUid, Requirement r, HashSet<string> seen)
    {
        switch (r)
        {
            case QuestRequirement q when Registry.Get(q.QuestId) is { } pre && seen.Add(pre.Id):
                if (pre.Requires != null) Satisfy(playerUid, pre.Requires, seen);
                if (!IsComplete(playerUid, pre.Id)) ForceComplete(playerUid, pre.Id);
                break;
            case StageRequirement s when !HasStage(playerUid, s.Stage):
                SetStage(ProgressOf(playerUid), s.Stage, [playerUid]);
                break;
            case ChapterCompleteRequirement c when Registry.ChaptersById.TryGetValue(c.ChapterId, out var chapter):
                foreach (var each in chapter.Quests) Satisfy(playerUid, new QuestRequirement(each.Id), seen);
                break;
            case AllRequirement all:
                foreach (var each in all.Of) Satisfy(playerUid, each, seen);
                break;
            case AnyRequirement any when any.Of.Count > 0:
                Satisfy(playerUid, any.Of[0], seen);
                break;
            case AtLeastRequirement atLeast:
                foreach (var each in atLeast.Of.Take(atLeast.N)) Satisfy(playerUid, each, seen);
                break;
        }
    }

    /// <summary>
    /// Forgets a player's progress on one quest, wherever it's held, or on every quest when
    /// <paramref name="questId"/> is null: their own record and their party's. Server quests
    /// are only reset one at a time, or by <see cref="ResetServer"/>.
    /// </summary>
    public void Reset(IServerPlayer player, string questId = null) => Reset(player.PlayerUID, questId);

    public void Reset(string playerUid, string questId = null)
    {
        var own = ProgressOf(playerUid);
        var party = PartyOf(playerUid);
        if (questId == null)
        {
            own.Quests.Clear();
            own.Stages.Clear();
            own.History.Clear();
            party?.Progress.Quests.Clear();
            party?.Progress.Stages.Clear();
        }
        else
        {
            own.Quests.Remove(questId);
            party?.Progress.Quests.Remove(questId);
            if (Registry.Get(questId) is { Mode: QuestMode.Server }) serverProgress.Quests.Remove(questId);
        }

        foreach (string uid in party?.Members ?? [playerUid])
        {
            inventoryDirty.Add(uid);
            syncDirty.Add(uid);
        }
        if (questId != null && Registry.Get(questId) is { } quest) Touch(quest, playerUid);
        FlushSync();
    }

    /// <summary>Forgets the server's progress and stages, for every player.</summary>
    public void ResetServer()
    {
        serverProgress = new PlayerProgress();
        foreach (var p in progress.Values)
        {
            foreach (var quest in Registry.Quests.Values.Where(q => q.Mode == QuestMode.Server)) p.Quests.Remove(quest.Id);
        }
        foreach (var player in api.World.AllOnlinePlayers)
        {
            inventoryDirty.Add(player.PlayerUID);
            syncDirty.Add(player.PlayerUID);
        }
        FlushSync();
    }

    #endregion

    #region Network

    void OnPlayerNowPlaying(IServerPlayer player)
    {
        SendDefinitions(player);
        SendParty(player.PlayerUID);

        var watcher = (Action<int>)(_ => inventoryDirty.Add(player.PlayerUID));
        inventoryWatchers[player.PlayerUID] = watcher;
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(name);
            if (inv != null) inv.SlotModified += watcher;
        }
        inventoryDirty.Add(player.PlayerUID);

        // Party members see who's online.
        foreach (string uid in PartyOf(player.PlayerUID)?.Members ?? []) SendParty(uid);
    }

    void OnPlayerDisconnect(IServerPlayer player)
    {
        sent.Remove(player.PlayerUID);
        if (inventoryWatchers.Remove(player.PlayerUID, out var watcher))
        {
            foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
            {
                var inv = player.InventoryManager.GetOwnInventory(name);
                if (inv != null) inv.SlotModified -= watcher;
            }
        }
        foreach (string uid in PartyOf(player.PlayerUID)?.Members ?? [])
        {
            if (uid != player.PlayerUID) SendParty(uid);
        }
    }

    void SendDefinitions(IServerPlayer player)
    {
        channel.SendPacket(new DefinitionsPacket { RegistryJson = Registry.ToJson(), RevealLevel = Config.RevealLevel }, player);
        sent.Remove(player.PlayerUID);
        Sync(player);
        if (QuestEditor.CanEdit(player)) SendEditor(player);
    }

    void SendEditor(IServerPlayer player) => channel.SendPacket(new EditorPacket
    {
        Domain = Config.EditorDomain,
        Chapters = rawChapters,
        Problems = Problems,
        Edited = edited.ToList(),
    }, player);

    void OnSaveChapter(IServerPlayer player, SaveChapterRequest req)
    {
        string error = req.Revert ? Editor.Revert(player, req.ChapterId) : Editor.Save(player, req.ChapterJson, req.TextJson);
        if (error != null) player.SendIngameError("questline-edit", error);
    }

    void OnExport(IServerPlayer player, ExportRequest req)
    {
        if (!QuestEditor.CanEdit(player))
        {
            player.SendIngameError("questline-edit", $"You need the {QuestEditor.Privilege} privilege to export quests.");
            return;
        }
        var (folder, count) = Editor.Export();
        player.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup, $"Exported {count} chapters to {folder}", EnumChatType.Notification);
    }

    /// <summary>Syncs every online player whose quest states may have changed.</summary>
    void FlushSync()
    {
        if (syncDirty.Count == 0) return;
        foreach (string uid in syncDirty.ToList())
        {
            if (api.World.PlayerByUid(uid) is IServerPlayer { ConnectionState: EnumClientState.Playing } player) Sync(player);
        }
        syncDirty.Clear();
    }

    /// <summary>Sends the player every quest state that changed since the last sync (all of them, the first time).</summary>
    public void Sync(IServerPlayer player)
    {
        string uid = player.PlayerUID;
        bool full = !sent.TryGetValue(uid, out var last);
        if (full) sent[uid] = last = [];

        var packet = new StatePacket { Full = full };
        foreach (var quest in Registry.Quests.Values)
        {
            var entry = new QuestStateEntry
            {
                QuestId = quest.Id,
                State = StateOf(uid, quest),
                Tasks = Holder(quest, uid).Find(quest.Id)?.Tasks.ToArray() ?? new int[quest.Tasks.Count],
            };

            if (last.TryGetValue(quest.Id, out var prev) && prev.State == entry.State && prev.Tasks.SequenceEqual(entry.Tasks)) continue;
            last[quest.Id] = entry;
            packet.Entries.Add(entry);
        }

        if (full || packet.Entries.Count > 0) channel.SendPacket(packet, player);
    }

    #endregion

    #region Persistence

    /// <summary>Reads everyone's progress, the parties and the server's progress from the save game, replacing what's in memory.</summary>
    public void LoadProgress()
    {
        progress = Read<Dictionary<string, PlayerProgress>>(SaveKey) ?? [];
        parties = (Read<List<Party>>(PartiesKey) ?? []).ToDictionary(p => p.Id);
        serverProgress = Read<PlayerProgress>(ServerKey) ?? new PlayerProgress();
        IndexParties();
        foreach (var p in AllRecords()) p.ApplyAliases(Registry.Aliases);
        sent.Clear();
    }

    T Read<T>(string key) where T : class
    {
        byte[] data = api.WorldManager.SaveGame.GetData(key);
        if (data == null) return null;
        try
        {
            return JsonConvert.DeserializeObject<T>(System.Text.Encoding.UTF8.GetString(data));
        }
        catch (Exception e)
        {
            api.Logger.Error("[questline] can't read saved {0}, starting fresh: {1}", key, e.Message);
            return null;
        }
    }

    /// <summary>Writes everything into the save game. The world save calls this.</summary>
    public void SaveProgress()
    {
        Write(SaveKey, progress);
        Write(PartiesKey, parties.Values.ToList());
        Write(ServerKey, serverProgress);
    }

    void Write(string key, object value) =>
        api.WorldManager.SaveGame.StoreData(key, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)));

    #endregion

    class PlayerContext(QuestEngine engine, string playerUid) : IRequirementContext
    {
        public bool IsQuestComplete(string questId) => engine.IsComplete(playerUid, questId);
        public bool IsModLoaded(string modId) => engine.api.ModLoader.IsModEnabled(modId);
        public bool HasStage(string stage) => engine.HasStage(playerUid, stage);
        public bool IsChapterComplete(string chapterId) =>
            engine.Registry.ChaptersById.TryGetValue(chapterId, out var chapter) && chapter.Quests.All(q => engine.IsComplete(playerUid, q.Id));
        public bool HasDiscovered(string pattern) => engine.HasDiscovered(playerUid, pattern);
    }
}
