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
    /// <summary>The quest's text as blocks. Without it the book shows the <c>&lt;id&gt;-desc</c> lang entry.</summary>
    public List<PageBlock> Page = [];

    /// <summary>Set by the loader: the chapter this quest is in.</summary>
    [JsonIgnore] public string ChapterId;
}

/// <summary>One block of a quest's page: text, an image, a row of items or a recipe.</summary>
public class PageBlock
{
    /// <summary>text, image, items or recipe.</summary>
    public string Type;
    /// <summary>text: the lang key of the paragraph.</summary>
    public string Lang;
    /// <summary>image: a texture under assets/&lt;domain&gt;/textures/, e.g. mypack:questline/bloomery.png.</summary>
    public string Src;
    /// <summary>image: the share of the panel's width it fills, 0 to 1.</summary>
    public float Width = 1;
    /// <summary>image: the lang key of a caption under it.</summary>
    public string Caption;
    /// <summary>items: the codes to show in a row.</summary>
    public List<string> Items = [];
    /// <summary>recipe: the item or block whose crafting recipes are shown.</summary>
    public string Output;

    /// <summary>The texture asset an image block names: <c>textures/</c> is implied.</summary>
    public static string TexturePath(string src)
    {
        int colon = src.IndexOf(':');
        string domain = colon < 0 ? "game" : src[..colon];
        string path = colon < 0 ? src : src[(colon + 1)..];
        return domain + ":" + (path.StartsWith("textures/") ? path : "textures/" + path);
    }
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
