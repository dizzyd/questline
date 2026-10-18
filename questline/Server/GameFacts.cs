using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace questline.Server;

/// <summary>
/// What loading and checking quests needs to know about the game: the quest files, which mods
/// are loaded, which items, blocks and creatures exist, and which text and textures there are.
/// The server answers from the running game; the pack checker answers from an exported snapshot
/// and the files on disk, so the same checks run in both.
/// </summary>
public interface IGameFacts
{
    /// <summary>Every questline/chapters/*.json: its domain, file name without .json, and text.</summary>
    IEnumerable<(string Domain, string Name, string Json)> Chapters();

    /// <summary>Every questline/questline.json: its domain and text.</summary>
    IEnumerable<(string Domain, string Json)> Questlines();

    bool IsModLoaded(string modId);

    /// <summary>An item or block with exactly this code.</summary>
    bool CollectibleExists(string code);

    /// <summary>Whether any item or block (<paramref name="kind"/> "collectible"), block ("block") or creature ("entity") matches a code, wildcards allowed.</summary>
    bool AnyMatches(string kind, string pattern);

    bool HasLang(string key);

    /// <summary>A texture asset's bytes, e.g. mypack:textures/questline/map.png, or null.</summary>
    byte[] Texture(string location);
}

/// <summary>The running server's answers.</summary>
public class ServerFacts(ICoreServerAPI api) : IGameFacts
{
    public IEnumerable<(string Domain, string Name, string Json)> Chapters() => api.Assets.GetMany("questline/chapters/")
        .Where(a => a.Name.EndsWith(".json"))
        .Select(a => (a.Location.Domain, a.Location.GetName().RemoveFileEnding(), a.ToText()));

    public IEnumerable<(string Domain, string Json)> Questlines() => api.Assets.GetMany("questline/questline.json")
        .Select(a => (a.Location.Domain, a.ToText()));

    public bool IsModLoaded(string modId) => api.ModLoader.IsModEnabled(modId);

    public bool CollectibleExists(string code)
    {
        if (code == null) return false;
        var loc = new AssetLocation(code);
        return api.World.GetItem(loc) != null || api.World.GetBlock(loc) != null;
    }

    public bool AnyMatches(string kind, string pattern)
    {
        var loc = new AssetLocation(pattern);
        return kind switch
        {
            "entity" => api.World.EntityTypes.Any(e => WildcardUtil.Match(loc, e.Code)),
            "block" => api.World.Blocks.Any(b => b.Code != null && WildcardUtil.Match(loc, b.Code)),
            _ => api.World.Collectibles.Any(c => c.Code != null && WildcardUtil.Match(loc, c.Code)),
        };
    }

    public bool HasLang(string key) => Translations.Has(key);

    public byte[] Texture(string location) => api.Assets.TryGet(new AssetLocation(location))?.Data;
}

/// <summary>
/// Whether there's text for a key. <c>Lang.HasTranslation</c> looks only in the player's
/// language, but <c>Lang.Get</c> falls back to English, so a key only English has is still
/// shown; on a German game every one of Questline's keys would otherwise count as missing.
/// </summary>
public static class Translations
{
    public static bool Has(string key) => Lang.HasTranslation(key, true, false)
        || (Lang.AvailableLanguages.TryGetValue(Lang.DefaultLocale, out var fallback) && fallback.HasTranslation(key, true, false));
}
