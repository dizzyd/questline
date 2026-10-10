using System.Collections.Generic;

namespace questline.Client;

/// <summary>ModConfig/questline-client.json: one player's own preferences, on their machine.</summary>
public class ClientSettings
{
    public const string FileName = "questline-client.json";
    public const int MaxPins = 3;

    /// <summary>Show a toast when a quest is completed.</summary>
    public bool Toasts = true;

    /// <summary>Pinned quest ids, per world (by savegame identifier), shown in the HUD tracker.</summary>
    public Dictionary<string, List<string>> Pins = [];
}
