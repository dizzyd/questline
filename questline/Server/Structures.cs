using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace questline.Server;

/// <summary>
/// The structures worldgen places, by the codes visit tasks use: a structure's code in the
/// worldgen config that places it, with that file's domain (game:tobiascave,
/// betterruins:largeforlornhopetower).
///
/// The game records each structure it places in its map region, under a code of its own making:
/// "&lt;schematic&gt;/&lt;structure&gt;" for ruins and each building of a village (GenStructures), "&lt;structure&gt;:&lt;schematic&gt;" for story
/// locations (GenStoryStructures), and a tiled dungeon's pieces with the dungeon's code as their
/// group. None of them carries the structure's domain, so that comes from the configs.
/// </summary>
public class StructureCodes
{
    readonly Dictionary<string, List<string>> ruins = [], story = [], dungeons = [];

    /// <summary>Every structure code, with its domain.</summary>
    public HashSet<string> All { get; } = [];

    public StructureCodes(ICoreAPI api)
    {
        // A story location's own ruins (worldgen/story/<code>/structures.json) are placed as ruins are.
        foreach (var asset in api.Assets.GetMany("worldgen/structures.json").Concat(api.Assets.GetMany("worldgen/story/").Where(a => a.Name == "structures.json")))
        {
            Add(ruins, asset, "structures");
        }
        foreach (var asset in api.Assets.GetMany("worldgen/villages.json")) Add(ruins, asset, "villageTypes");
        foreach (var asset in api.Assets.GetMany("worldgen/storystructures.json")) Add(story, asset, "structures");
        foreach (var asset in api.Assets.GetMany("worldgen/tileddungeons.json")) Add(dungeons, asset, "dungeons");
    }

    void Add(Dictionary<string, List<string>> into, IAsset asset, string list)
    {
        JObject config;
        try
        {
            config = asset.ToObject<JObject>();
        }
        catch
        {
            return;   // the game reports its own broken configs
        }
        foreach (var entry in config?[list] as JArray ?? [])
        {
            string code = (string)entry["code"];
            if (string.IsNullOrEmpty(code)) continue;
            string qualified = asset.Location.Domain + ":" + code;
            if (!into.TryGetValue(code, out var codes)) into[code] = codes = [];
            if (!codes.Contains(qualified)) codes.Add(qualified);
            All.Add(qualified);
        }
    }

    /// <summary>The codes a placed structure answers to: one, or more where mods share a structure code.</summary>
    public IReadOnlyList<string> Of(GeneratedStructure placed)
    {
        string code = placed.Code ?? "";
        int colon = code.IndexOf(':');
        if (colon > 0 && story.TryGetValue(code[..colon], out var found)) return found;
        if (ruins.TryGetValue(code[(code.LastIndexOf('/') + 1)..], out found)) return found;
        if (placed.Group != null && dungeons.TryGetValue(placed.Group, out found)) return found;
        return [];
    }
}
