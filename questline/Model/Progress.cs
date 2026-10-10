using System;
using System.Collections.Generic;

namespace questline.Model;

/// <summary>A quest's state as a player sees it, in the order a quest moves through them.</summary>
public enum QuestState
{
    Locked,
    Available,
    InProgress,
    /// <summary>Every task done; rewards waiting in the book.</summary>
    Claimable,
    Done
}

public class QuestProgress
{
    public int[] Tasks = [];
    /// <summary>Every task reached its count. Latches: an obtain task dropping back later doesn't undo it.</summary>
    public bool Complete;
    public bool Claimed;
    /// <summary>In-game day the quest completed, for repeats.</summary>
    public double CompletedDay;
    /// <summary>Credited from the player's history when it unlocked; done once.</summary>
    public bool Seeded;
    /// <summary>The obtain tasks marked consume have been taken. A shared quest takes them on its first claim only.</summary>
    public bool Consumed;
}

/// <summary>
/// Progress through every quest, keyed by quest id: one player's, or a party's or the server's
/// for the quests they share. A player's own record also holds whether they've claimed a shared quest.
/// </summary>
public class PlayerProgress
{
    public Dictionary<string, QuestProgress> Quests = [];
    /// <summary>Progression flags set by stage rewards (or other mods, through the API).</summary>
    public HashSet<string> Stages = [];
    /// <summary>Everything the player has done that a task could count, as "type|code" to a running total.</summary>
    public Dictionary<string, int> History = [];

    public static string HistoryKey(string type, string code) => type + "|" + code;

    public QuestProgress Find(string questId) => Quests.TryGetValue(questId, out var p) ? p : null;

    public QuestProgress GetOrCreate(QuestDef quest)
    {
        if (!Quests.TryGetValue(quest.Id, out var p)) Quests[quest.Id] = p = new QuestProgress();
        // A pack update can change a quest's task list; keep what lines up.
        if (p.Tasks.Length != quest.Tasks.Count) Array.Resize(ref p.Tasks, quest.Tasks.Count);
        return p;
    }

    public bool IsComplete(string questId) => Find(questId)?.Complete == true;

    /// <summary>Moves progress stored under an old quest id to its new one.</summary>
    public void ApplyAliases(IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var (from, to) in aliases)
        {
            if (Quests.Remove(from, out var p) && !Quests.ContainsKey(to)) Quests[to] = p;
        }
    }

    /// <param name="progress">The quest's tasks, from whoever holds them: the player, their party or the server.</param>
    /// <param name="claimed">Whether this player's claim is spent.</param>
    public static QuestState StateOf(QuestDef quest, QuestProgress progress, bool claimed, bool unlocked)
    {
        if (progress?.Complete == true) return claimed || quest.Rewards.Count == 0 ? QuestState.Done : QuestState.Claimable;
        if (claimed) return QuestState.Done;
        if (!unlocked) return QuestState.Locked;
        return progress != null && Array.Exists(progress.Tasks, t => t > 0) ? QuestState.InProgress : QuestState.Available;
    }
}

/// <summary>Players who share progress on party quests.</summary>
public class Party
{
    public string Id;
    public string Name;
    /// <summary>The member who can invite and kick.</summary>
    public string Owner;
    /// <summary>Player uids, the owner included.</summary>
    public List<string> Members = [];
    /// <summary>Uids of players invited and not yet answered.</summary>
    public HashSet<string> Invited = [];
    /// <summary>The party's own progress on party quests, and its stages.</summary>
    public PlayerProgress Progress = new();
}
