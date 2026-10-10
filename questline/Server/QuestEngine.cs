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

namespace questline.Server;

/// <summary>
/// The authority on every quest: holds the registry and each player's progress, turns task
/// reports into progress, grants rewards on a valid claim, and keeps each client's copy of its
/// quest states current.
/// </summary>
public class QuestEngine
{
    public const string ChannelName = "questline";
    const string SaveKey = "questline-progress";

    readonly ICoreServerAPI api;
    IServerNetworkChannel channel;
    Dictionary<string, PlayerProgress> progress = [];
    // What each client was last told, so syncs send only changes.
    readonly Dictionary<string, Dictionary<string, QuestStateEntry>> sent = [];
    Dictionary<string, List<(QuestDef Quest, int Index)>> tasksByType = [];
    readonly Dictionary<AssetLocation, AssetLocation> patternCache = [];
    readonly HashSet<string> inventoryDirty = [];
    readonly Dictionary<string, Action<int>> inventoryWatchers = [];

    public QuestRegistry Registry { get; private set; } = new();
    public List<string> Problems { get; private set; } = [];

    /// <summary>Fires after a quest's tasks are all done, with the player and quest id.</summary>
    public event Action<IServerPlayer, string> QuestCompleted;

    /// <summary>Fires when a player first reaches a stage.</summary>
    public event Action<IServerPlayer, string> StageSet;

    /// <summary>Event-bus names, for mods that use Questline without referencing it.</summary>
    public const string ReportEvent = "questline:report", QuestCompletedEvent = "questline:questcompleted", StageSetEvent = "questline:stageset";

    public QuestEngine(ICoreServerAPI api)
    {
        this.api = api;
    }

    public void Start()
    {
        channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<DefinitionsPacket>()
            .RegisterMessageType<StatePacket>()
            .RegisterMessageType<ClaimRequest>()
            .RegisterMessageType<CheckboxRequest>()
            .SetMessageHandler<ClaimRequest>((player, req) => Claim(player, req.QuestId, req.Choices))
            .SetMessageHandler<CheckboxRequest>((player, req) => Check(player, req.QuestId, req.TaskIndex));

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
        if (api.World.PlayerByUid(tree.GetString("playeruid")) is not IServerPlayer player) return;
        string key = tree.GetString("key");
        if (key != null) Report(player, "custom", new AssetLocation(key), tree.GetInt("amount", 1));
    }

    /// <summary>Re-reads every chapter and resends definitions and state to everyone online.</summary>
    public void Reload()
    {
        var loader = new QuestLoader(api);
        Registry = loader.Load();
        Problems = loader.Problems;

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

        foreach (var p in progress.Values) p.ApplyAliases(Registry.Aliases);

        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            SendDefinitions(player);
            inventoryDirty.Add(player.PlayerUID);
        }
    }

    #region Progress and state

    public PlayerProgress ProgressOf(string playerUid)
    {
        if (!progress.TryGetValue(playerUid, out var p)) progress[playerUid] = p = new PlayerProgress();
        return p;
    }

    public bool IsUnlocked(QuestDef quest, PlayerProgress p) =>
        quest.Requires == null || quest.Requires.Evaluate(new PlayerContext(this, p));

    public QuestState StateOf(string playerUid, string questId)
    {
        var quest = Registry.Get(questId) ?? throw new ArgumentException($"No quest {questId}");
        var p = ProgressOf(playerUid);
        return PlayerProgress.StateOf(quest, p.Find(questId), IsUnlocked(quest, p));
    }

    bool IsActive(QuestDef quest, PlayerProgress p) => p.Find(quest.Id)?.Complete != true && IsUnlocked(quest, p);

    /// <summary>Marks the quest complete if every task has reached its count.</summary>
    bool TryComplete(IServerPlayer player, QuestDef quest, QuestProgress qp)
    {
        if (qp.Complete) return false;
        for (int i = 0; i < quest.Tasks.Count; i++)
        {
            if (qp.Tasks[i] < quest.Tasks[i].Count) return false;
        }

        qp.Complete = true;
        qp.CompletedDay = api.World.Calendar.TotalDays;
        if (quest.Rewards.Count == 0) qp.Claimed = true;

        // Newly unlocked quests may already be satisfied by what the player carries.
        inventoryDirty.Add(player.PlayerUID);
        QuestCompleted?.Invoke(player, quest.Id);
        api.Event.PushEvent(QuestCompletedEvent, new TreeAttribute { ["playeruid"] = new StringAttribute(player.PlayerUID), ["questid"] = new StringAttribute(quest.Id) });
        return true;
    }

    #endregion

    #region Task reports

    /// <summary>
    /// Credits <paramref name="amount"/> towards every active task of <paramref name="type"/> that
    /// matches <paramref name="code"/>. The trackers call this; so can other mods.
    /// </summary>
    public void Report(IServerPlayer player, string type, AssetLocation code, int amount)
    {
        if (player == null || code == null || amount <= 0) return;
        if (!tasksByType.TryGetValue(type, out var tasks)) return;

        var p = ProgressOf(player.PlayerUID);
        bool changed = false;
        foreach (var (quest, index) in tasks)
        {
            var task = quest.Tasks[index];
            if (!WildcardUtil.Match(Pattern(task), code) || !IsActive(quest, p)) continue;

            var qp = p.GetOrCreate(quest);
            if (qp.Tasks[index] >= task.Count) continue;
            qp.Tasks[index] = Math.Min(task.Count, qp.Tasks[index] + amount);
            TryComplete(player, quest, qp);
            changed = true;
        }

        if (changed) Sync(player);
    }

    /// <summary>Ticks a checkbox task, on the player's request from the book.</summary>
    public void Check(IServerPlayer player, string questId, int taskIndex)
    {
        var quest = Registry.Get(questId);
        if (quest == null || taskIndex < 0 || taskIndex >= quest.Tasks.Count || quest.Tasks[taskIndex].Type != "checkbox") return;

        var p = ProgressOf(player.PlayerUID);
        if (!IsActive(quest, p)) return;

        var qp = p.GetOrCreate(quest);
        qp.Tasks[taskIndex] = quest.Tasks[taskIndex].Count;
        TryComplete(player, quest, qp);
        Sync(player);
    }

    AssetLocation Pattern(TaskDef task)
    {
        var loc = new AssetLocation(task.Target);
        if (!patternCache.TryGetValue(loc, out var cached)) patternCache[loc] = cached = loc;
        return cached;
    }

    void ScanDirtyInventories()
    {
        if (inventoryDirty.Count == 0) return;
        foreach (string uid in inventoryDirty.ToList())
        {
            inventoryDirty.Remove(uid);
            if (api.World.PlayerByUid(uid) is IServerPlayer { ConnectionState: EnumClientState.Playing } player) ScanInventory(player);
        }
    }

    /// <summary>Sets every active obtain task to what the player carries now.</summary>
    void ScanInventory(IServerPlayer player)
    {
        if (!tasksByType.TryGetValue("obtain", out var tasks)) return;

        var p = ProgressOf(player.PlayerUID);
        bool changed = false;
        foreach (var (quest, index) in tasks)
        {
            if (!IsActive(quest, p)) continue;

            var task = quest.Tasks[index];
            int held = Math.Min(task.Count, CountCarried(player, Pattern(task)));
            var qp = p.Find(quest.Id);
            if ((qp == null ? 0 : qp.Tasks[index]) == held) continue;

            qp = p.GetOrCreate(quest);
            qp.Tasks[index] = held;
            TryComplete(player, quest, qp);
            changed = true;
        }

        if (changed) Sync(player);
    }

    static IEnumerable<ItemSlot> CarriedSlots(IServerPlayer player)
    {
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(name);
            if (inv == null) continue;
            foreach (var slot in inv) yield return slot;
        }
    }

    static int CountCarried(IServerPlayer player, AssetLocation pattern) => CarriedSlots(player)
        .Where(s => s.Itemstack?.Collectible?.Code != null && WildcardUtil.Match(pattern, s.Itemstack.Collectible.Code))
        .Sum(s => s.StackSize);

    #endregion

    #region Claiming

    /// <summary>Grants a completed quest's rewards. Returns null, or why the claim was refused.</summary>
    public string Claim(IServerPlayer player, string questId, int[] choices)
    {
        var quest = Registry.Get(questId);
        if (quest == null) return Refuse(player, $"no quest {questId}");

        var qp = ProgressOf(player.PlayerUID).Find(questId);
        if (qp?.Complete != true) return Refuse(player, $"{questId} isn't complete");
        if (qp.Claimed) return Refuse(player, $"{questId} is already claimed");

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

        var consumed = quest.Tasks.Where(t => t.Type == "obtain" && t.Consume).ToList();
        foreach (var task in consumed)
        {
            if (CountCarried(player, Pattern(task)) < task.Count) return Refuse(player, $"{questId}: you no longer carry {task.Count}× {task.Target}");
        }
        foreach (var task in consumed) TakeCarried(player, Pattern(task), task.Count);

        qp.Claimed = true;
        foreach (var gift in gifts)
        {
            if (gift.Type == "stage") SetStage(player, gift.Stage);
            else Give(player, gift);
        }
        Sync(player);
        return null;
    }

    string Refuse(IServerPlayer player, string why)
    {
        api.Logger.Audit("[questline] refused claim by {0}: {1}", player.PlayerName, why);
        return why;
    }

    static void TakeCarried(IServerPlayer player, AssetLocation pattern, int count)
    {
        foreach (var slot in CarriedSlots(player))
        {
            if (count <= 0) return;
            if (slot.Itemstack?.Collectible?.Code == null || !WildcardUtil.Match(pattern, slot.Itemstack.Collectible.Code)) continue;
            int take = Math.Min(count, slot.StackSize);
            slot.TakeOut(take);
            slot.MarkDirty();
            count -= take;
        }
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

    #region Stages

    public bool HasStage(string playerUid, string stage) => ProgressOf(playerUid).Stages.Contains(stage);

    /// <summary>Gives a player a stage. Quests gated on it unlock; other mods hear about it.</summary>
    public void SetStage(IServerPlayer player, string stage)
    {
        if (!ProgressOf(player.PlayerUID).Stages.Add(stage)) return;

        inventoryDirty.Add(player.PlayerUID);
        StageSet?.Invoke(player, stage);
        api.Event.PushEvent(StageSetEvent, new TreeAttribute { ["playeruid"] = new StringAttribute(player.PlayerUID), ["stage"] = new StringAttribute(stage) });
        Sync(player);
    }

    #endregion

    #region Commands and tests

    /// <summary>Completes every task of a quest for a player, unlocked or not.</summary>
    public void ForceComplete(IServerPlayer player, string questId)
    {
        var quest = Registry.Get(questId) ?? throw new ArgumentException($"No quest {questId}");
        var qp = ProgressOf(player.PlayerUID).GetOrCreate(quest);
        for (int i = 0; i < quest.Tasks.Count; i++) qp.Tasks[i] = quest.Tasks[i].Count;
        TryComplete(player, quest, qp);
        Sync(player);
    }

    /// <summary>Forgets a player's progress on one quest, or on every quest when <paramref name="questId"/> is null.</summary>
    public void Reset(IServerPlayer player, string questId = null)
    {
        var p = ProgressOf(player.PlayerUID);
        if (questId == null)
        {
            p.Quests.Clear();
            p.Stages.Clear();
        }
        else p.Quests.Remove(questId);
        inventoryDirty.Add(player.PlayerUID);
        Sync(player);
    }

    #endregion

    #region Network

    void OnPlayerNowPlaying(IServerPlayer player)
    {
        SendDefinitions(player);

        var watcher = (Action<int>)(_ => inventoryDirty.Add(player.PlayerUID));
        inventoryWatchers[player.PlayerUID] = watcher;
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(name);
            if (inv != null) inv.SlotModified += watcher;
        }
        inventoryDirty.Add(player.PlayerUID);
    }

    void OnPlayerDisconnect(IServerPlayer player)
    {
        sent.Remove(player.PlayerUID);
        if (!inventoryWatchers.Remove(player.PlayerUID, out var watcher)) return;
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(name);
            if (inv != null) inv.SlotModified -= watcher;
        }
    }

    void SendDefinitions(IServerPlayer player)
    {
        channel.SendPacket(new DefinitionsPacket { RegistryJson = Registry.ToJson() }, player);
        sent.Remove(player.PlayerUID);
        Sync(player);
    }

    /// <summary>Sends the player every quest state that changed since the last sync (all of them, the first time).</summary>
    public void Sync(IServerPlayer player)
    {
        bool full = !sent.TryGetValue(player.PlayerUID, out var last);
        if (full) sent[player.PlayerUID] = last = [];

        var p = ProgressOf(player.PlayerUID);
        var packet = new StatePacket { Full = full };
        foreach (var quest in Registry.Quests.Values)
        {
            var qp = p.Find(quest.Id);
            var entry = new QuestStateEntry
            {
                QuestId = quest.Id,
                State = PlayerProgress.StateOf(quest, qp, IsUnlocked(quest, p)),
                Tasks = qp?.Tasks.ToArray() ?? new int[quest.Tasks.Count],
            };

            if (last.TryGetValue(quest.Id, out var prev) && prev.State == entry.State && prev.Tasks.SequenceEqual(entry.Tasks)) continue;
            last[quest.Id] = entry;
            packet.Entries.Add(entry);
        }

        if (full || packet.Entries.Count > 0) channel.SendPacket(packet, player);
    }

    #endregion

    #region Persistence

    /// <summary>Reads every player's progress from the save game, replacing what's in memory.</summary>
    public void LoadProgress()
    {
        byte[] data = api.WorldManager.SaveGame.GetData(SaveKey);
        if (data == null)
        {
            progress = [];
            return;
        }
        try
        {
            progress = JsonConvert.DeserializeObject<Dictionary<string, PlayerProgress>>(System.Text.Encoding.UTF8.GetString(data)) ?? [];
            foreach (var p in progress.Values) p.ApplyAliases(Registry.Aliases);
            sent.Clear();
        }
        catch (Exception e)
        {
            api.Logger.Error("[questline] can't read saved quest progress, starting fresh: {0}", e.Message);
            progress = [];
        }
    }

    /// <summary>Writes every player's progress into the save game. The world save calls this.</summary>
    public void SaveProgress()
    {
        api.WorldManager.SaveGame.StoreData(SaveKey, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(progress)));
    }

    #endregion

    class PlayerContext(QuestEngine engine, PlayerProgress p) : IRequirementContext
    {
        public bool IsQuestComplete(string questId) => p.IsComplete(questId);
        public bool IsModLoaded(string modId) => engine.api.ModLoader.IsModEnabled(modId);
        public bool HasStage(string stage) => p.Stages.Contains(stage);
        public bool IsChapterComplete(string chapterId) =>
            engine.Registry.ChaptersById.TryGetValue(chapterId, out var chapter) && chapter.Quests.All(q => p.IsComplete(q.Id));
    }
}
