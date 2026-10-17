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

/// <summary>Whose progress a quest counts, and whose rewards it gives.</summary>
[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum QuestMode
{
    /// <summary>Each player separately.</summary>
    Individual,
    /// <summary>The player's party, pooled; each member claims once. A player with no party counts alone.</summary>
    Party,
    /// <summary>Everyone on the server, pooled.</summary>
    Server
}

/// <summary>The outline of a quest's frame on the canvas.</summary>
[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum NodeShape
{
    Square,
    Circle,
    Hexagon,
    /// <summary>For milestones.</summary>
    Gear
}

/// <summary>Who may claim a server quest.</summary>
[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum ClaimMode
{
    /// <summary>Every player claims their own copy once.</summary>
    Each,
    /// <summary>The first player to claim takes the reward, and the quest is done for everyone.</summary>
    First
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
    /// <summary>A texture under assets/&lt;domain&gt;/textures/, drawn behind the whole canvas, e.g. mypack:questline/parchment.png.</summary>
    public string Background;
    /// <summary>
    /// A stage of progression, as stone, copper and iron are. A quest in any other chapter is
    /// labelled with the latest age its prerequisites lead back to, so a chapter on one theme
    /// shows which of its quests come late. See <see cref="QuestRegistry.AgeOf"/>.
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Age;
    /// <summary>Images, labels and arrows placed on the chapter's grid, behind the quests.</summary>
    public List<ArtDef> Art = [];
    public List<QuestDef> Quests = [];
    /// <summary>
    /// Another chapter this file adds to, e.g. questline:kitchen: its quests and art join that
    /// chapter when <see cref="RequiresMod"/> is met, so a mod's quests sit beside the ones they
    /// build on rather than in a chapter of their own. A quest with the id of one already in the
    /// chapter replaces it, for a mod that changes how the base quest's mechanic works.
    /// </summary>
    public string Extends;
    /// <summary>Old quest id to new, so a rename in a pack update keeps players' progress.</summary>
    public Dictionary<string, string> Aliases = [];
}

public class QuestDef
{
    public string Id;
    public float[] Pos = [0, 0];
    public string Icon;
    public NodeShape Shape = NodeShape.Square;
    /// <summary>The frame's size, 1 being normal. Milestones might be 1.5.</summary>
    public float Size = 1;
    public Requirement Requires;
    public QuestVisibility Visibility = QuestVisibility.Always;
    public QuestMode Mode = QuestMode.Individual;
    /// <summary>server quests only: one claim for the whole server, or one each.</summary>
    public ClaimMode Claim = ClaimMode.Each;
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
    /// <summary>obtain, kill, mount, place, break, craft, knap, clayform, smith, cook, smelt, trade, custom or checkbox.</summary>
    public string Type;
    public string Item;
    public string Block;
    public string Entity;
    /// <summary>custom only: the key another mod reports progress under, e.g. mymod:caught-salmon.</summary>
    public string Key;
    public int Count = 1;
    /// <summary>obtain only: take the items when the reward is claimed.</summary>
    public bool Consume;
    /// <summary>
    /// The recipe graph can't see how the target is made (a mod's own machines, say), so the
    /// checks that ask how it's made skip this task. The target must still exist.
    /// </summary>
    public bool Unchecked;

    /// <summary>The code the task matches, whichever field the author used. May carry wildcards.</summary>
    [JsonIgnore] public string Target => Item ?? Block ?? Entity ?? Key;

    /// <summary>
    /// Whether a code stands for many: a wildcard (game:saw-*) or, as the game's own matcher
    /// allows, a regular expression after an @ (game:@(redmeat|poultry)-cooked).
    /// </summary>
    public static bool IsPattern(string code) => code != null && (code.Contains('*') || code.Contains(":@") || code.StartsWith('@'));

    /// <summary>Whether the target is a creature or a vehicle rather than an item or a block.</summary>
    [JsonIgnore] public bool OnEntity => Type is "kill" or "mount";

    /// <summary>Whether the target is an item or a block, one the book can show.</summary>
    [JsonIgnore] public bool OnCollectible => !OnEntity && Type is not ("custom" or "checkbox");
}

public class RewardDef
{
    /// <summary>item, choice or stage.</summary>
    public string Type;
    public string Item;
    public int Count = 1;
    /// <summary>stage only: the stage this reward sets, e.g. mypack:iron.</summary>
    public string Stage;
    /// <summary>choice only: the player picks one of these.</summary>
    public List<RewardDef> Options = [];
}

/// <summary>A picture, a label or an arrow on a chapter's canvas. Positions are grid cells, as for quests.</summary>
public class ArtDef
{
    /// <summary>image, text or arrow.</summary>
    public string Type;
    /// <summary>The centre of an image or a label; where an arrow starts.</summary>
    public float[] Pos = [0, 0];
    /// <summary>arrow: where it points.</summary>
    public float[] To;
    /// <summary>image: width and height in grid cells.</summary>
    public float[] Size = [1, 1];
    /// <summary>image: a texture under assets/&lt;domain&gt;/textures/.</summary>
    public string Src;
    /// <summary>text: the lang key of the label.</summary>
    public string Text;
    /// <summary>text: font size relative to quest labels.</summary>
    public float Scale = 1;
    /// <summary>text and arrow: a colour as #rrggbb or #rrggbbaa.</summary>
    public string Color;
    /// <summary>arrow: line width in canvas units at zoom 1.</summary>
    public float Width = 3;
}
