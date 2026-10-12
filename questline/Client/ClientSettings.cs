using System.Collections.Generic;

namespace questline.Client;

/// <summary>ModConfig/questline-client.json: one player's own preferences, on their machine.</summary>
public class ClientSettings
{
    public const string FileName = "questline-client.json";
    public const int MaxPins = 3;

    /// <summary>The book's text size, in percent of the game's own: one of <see cref="TextSizes"/>.</summary>
    public int TextSize = 115;

    /// <summary>The steps A− and A+ move through.</summary>
    public static readonly int[] TextSizes = [90, 100, 115, 130, 150];

    /// <summary>Show a toast when a quest is completed.</summary>
    public bool Toasts = true;

    /// <summary>H opens the book's Reference tab instead of the vanilla handbook, and handbook links open there too.</summary>
    public bool TakeOverHandbook = true;

    /// <summary>How much the goal planner shows, up to what the server allows: guide, hints or discovery.</summary>
    public string PlannerLevel = "guide";

    /// <summary>Each world's goal plans: the item codes the player is working towards.</summary>
    public Dictionary<string, List<string>> Plans = [];

    /// <summary>Pinned quest ids, per world (by savegame identifier), shown in the HUD tracker.</summary>
    public Dictionary<string, List<string>> Pins = [];
}
