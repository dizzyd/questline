namespace questline;

/// <summary>ModConfig/questline.json, read by the server.</summary>
public class QuestlineConfig
{
    public const string FileName = "questline.json";

    /// <summary>
    /// When a quest unlocks, credit it with what the player already did before: blocks broken,
    /// items crafted, creatures killed. Off means only work done while a quest is unlocked counts.
    /// </summary>
    public bool CreditEarlierWork = true;
}
