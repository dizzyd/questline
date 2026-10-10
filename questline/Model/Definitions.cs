using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace questline.Model;

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum QuestVisibility
{
    /// <summary>Shown from the start, locked or not.</summary>
    Always,
    WhenUnlocked,
    WhenAnyPrereqDone,
    /// <summary>Hidden until complete.</summary>
    Secret
}

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum ChapterVisibility
{
    Always,
    WhenAnyUnlocked,
    Hidden
}

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum QuestMode
{
    Individual,
    Party,
    Server
}

/// <summary>assets/&lt;domain&gt;/questline/questline.json: one progression the book can switch to.</summary>
public class QuestlineDef
{
    public string Id;
    public string Icon;
    public int Order;
    public string[] RequiresMod = [];
    public bool ReplacesDefault;
    /// <summary>Mod ids this questline covers; it steps aside if any of them ships its own.</summary>
    public string[] YieldsTo = [];

    /// <summary>Set by the loader: the asset domain that shipped this questline.</summary>
    [JsonIgnore] public string Domain;
}

/// <summary>assets/&lt;domain&gt;/questline/chapters/*.json.</summary>
public class ChapterDef
{
    public string Id;
    /// <summary>The questline this chapter belongs to; <see cref="QuestRegistry.DefaultQuestline"/> when absent.</summary>
    public string Questline;
    public string Icon;
    public int Order;
    public string[] RequiresMod = [];
    public ChapterVisibility Visibility = ChapterVisibility.Always;
    public List<QuestDef> Quests = [];
    /// <summary>Old quest id to new, so a rename in a pack update keeps players' progress.</summary>
    public Dictionary<string, string> Aliases = [];
}

public class QuestDef
{
    public string Id;
    public float[] Pos = [0, 0];
    public string Icon;
    public Requirement Requires;
    public QuestVisibility Visibility = QuestVisibility.Always;
    public QuestMode Mode = QuestMode.Individual;
    /// <summary>In-game days between repeats, or null for a one-off quest.</summary>
    public double? Repeat;
    public List<TaskDef> Tasks = [];
    public List<RewardDef> Rewards = [];

    /// <summary>Set by the loader: the chapter this quest is in.</summary>
    [JsonIgnore] public string ChapterId;
}

public class TaskDef
{
    /// <summary>obtain, kill, place, break, craft, knap, clayform or checkbox.</summary>
    public string Type;
    public string Item;
    public string Block;
    public string Entity;
    public int Count = 1;
    /// <summary>obtain only: take the items when the reward is claimed.</summary>
    public bool Consume;

    /// <summary>The code the task matches, whichever field the author used. May carry wildcards.</summary>
    [JsonIgnore] public string Target => Item ?? Block ?? Entity;
}

public class RewardDef
{
    /// <summary>item or choice.</summary>
    public string Type;
    public string Item;
    public int Count = 1;
    /// <summary>choice only: the player picks one of these.</summary>
    public List<RewardDef> Options = [];
}
