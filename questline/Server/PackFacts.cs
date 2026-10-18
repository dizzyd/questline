using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using questline.Graph;
using questline.Model;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace questline.Server;

/// <summary>
/// What a running game knows that checking a quest pack needs, saved to one file by
/// <c>/questline snapshot</c>: the game version, the loaded mods, every item, block and
/// creature code, every worldgen structure, every lang entry, and the recipe graph.
/// </summary>
public class GameSnapshot
{
    public string GameVersion;
    public List<string> Mods = [];
    public List<string> Items = [];
    public List<string> Blocks = [];
    public List<string> Entities = [];
    public Dictionary<string, string> Lang = [];
    /// <summary>Every worldgen structure's code, with its domain, as visit tasks name them.</summary>
    public List<string> Structures = [];
    public RecipeGraph Graph;

    public string ToJson() => JsonConvert.SerializeObject(this);

    public static GameSnapshot FromJson(string json)
    {
        var snapshot = JsonConvert.DeserializeObject<GameSnapshot>(json);
        // The graph is indexed again; inputs then match by code and wildcard.
        snapshot.Graph = RecipeGraph.FromJson(JsonConvert.SerializeObject(snapshot.Graph));
        return snapshot;
    }
}

/// <summary>
/// The pack checker's answers: game facts from a <see cref="GameSnapshot"/>, quest files, text and
/// textures from the packs on disk (mod folders or zips, each with an assets/ folder), and, if
/// given, textures from the game's own assets folder.
/// </summary>
public class PackFacts : IGameFacts
{
    readonly GameSnapshot snapshot;
    readonly HashSet<string> collectibles, blocks, entities, structures, mods;
    readonly Dictionary<string, string> lang;
    readonly List<(string Domain, string Path, Func<byte[]> Read)> files = [];
    readonly string gameAssets;

    public GameSnapshot Snapshot => snapshot;

    /// <param name="packs">Mod folders or zips, as they'd go in the Mods folder.</param>
    /// <param name="gameAssets">The game install's assets folder, for checking textures packs borrow from it; may be null.</param>
    public PackFacts(GameSnapshot snapshot, IEnumerable<string> packs, string gameAssets = null)
    {
        this.snapshot = snapshot;
        this.gameAssets = gameAssets;
        collectibles = [.. snapshot.Items, .. snapshot.Blocks];
        blocks = [.. snapshot.Blocks];
        entities = [.. snapshot.Entities];
        structures = [.. snapshot.Structures ?? []];
        mods = [.. snapshot.Mods];
        lang = new Dictionary<string, string>(snapshot.Lang);

        foreach (string pack in packs) AddPack(pack);

        foreach (var (domain, path, read) in files.Where(f => f.Path == "lang/en.json").ToList())
        {
            var entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(System.Text.Encoding.UTF8.GetString(read())) ?? [];
            foreach (var (key, value) in entries) lang[key.Contains(':') ? key : domain + ":" + key] = value;
        }
    }

    void AddPack(string pack)
    {
        if (Directory.Exists(pack))
        {
            string assets = Path.Combine(pack, "assets");
            if (Directory.Exists(assets))
            {
                foreach (string file in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
                {
                    var parts = Path.GetRelativePath(assets, file).Replace('\\', '/').Split('/', 2);
                    if (parts.Length == 2) files.Add((parts[0], parts[1], () => File.ReadAllBytes(file)));
                }
            }
            string modinfo = Path.Combine(pack, "modinfo.json");
            if (File.Exists(modinfo)) AddModId(File.ReadAllText(modinfo));
            return;
        }

        using var zip = ZipFile.OpenRead(pack);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("assets/") && e.Length > 0))
        {
            var parts = entry.FullName["assets/".Length..].Split('/', 2);
            if (parts.Length != 2) continue;
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            byte[] bytes = copy.ToArray();
            files.Add((parts[0], parts[1], () => bytes));
        }
        if (zip.GetEntry("modinfo.json") is { } info)
        {
            using var reader = new StreamReader(info.Open());
            AddModId(reader.ReadToEnd());
        }
    }

    void AddModId(string modinfo)
    {
        string id = Newtonsoft.Json.Linq.JObject.Parse(modinfo)["modid"]?.ToString() ?? Newtonsoft.Json.Linq.JObject.Parse(modinfo)["modID"]?.ToString();
        if (id != null) mods.Add(id.ToLowerInvariant());
    }

    string Text(Func<byte[]> read) => System.Text.Encoding.UTF8.GetString(read());

    public IEnumerable<(string Domain, string Name, string Json)> Chapters() => files
        .Where(f => f.Path.StartsWith("questline/chapters/") && f.Path.EndsWith(".json"))
        .Select(f => (f.Domain, Path.GetFileNameWithoutExtension(f.Path), Text(f.Read)));

    /// <summary>The packs' questlines, and Questline's own default one, which every pack can join without shipping it.</summary>
    public IEnumerable<(string Domain, string Json)> Questlines()
    {
        var own = files.Where(f => f.Path == "questline/questline.json").Select(f => (f.Domain, Text(f.Read))).ToList();
        if (own.All(q => q.Domain != "questline")) own.Add(("questline", "{\"id\":\"default\",\"order\":0}"));
        return own;
    }

    public bool IsModLoaded(string modId) => mods.Contains(modId);

    static string Qualified(string code) => code.Contains(':') ? code : "game:" + code;

    public bool CollectibleExists(string code) => code != null && collectibles.Contains(Qualified(code));

    public bool AnyMatches(string kind, string pattern)
    {
        var set = kind switch { "entity" => entities, "block" => blocks, "structure" => structures, _ => collectibles };
        if (!TaskDef.IsPattern(pattern)) return set.Contains(Qualified(pattern));
        var loc = new AssetLocation(pattern);
        return set.Any(c => WildcardUtil.Match(loc, new AssetLocation(c)));
    }

    public bool HasLang(string key) => lang.ContainsKey(key) || lang.ContainsKey(Qualified(key));

    public byte[] Texture(string location)
    {
        var loc = new AssetLocation(location);
        var own = files.FirstOrDefault(f => f.Domain == loc.Domain && f.Path == loc.Path);
        if (own.Read != null) return own.Read();
        if (gameAssets == null || !Directory.Exists(gameAssets)) return null;
        // The game's "game" domain is spread over assets/game, assets/survival and assets/creative.
        foreach (string folder in loc.Domain == "game" ? ["game", "survival", "creative"] : new[] { loc.Domain })
        {
            string file = Path.Combine(gameAssets, folder, loc.Path);
            if (File.Exists(file)) return File.ReadAllBytes(file);
        }
        return null;
    }

    /// <summary>Loads the packs' quests and checks them as the server would, the recipe graph included.</summary>
    public (QuestRegistry Registry, List<string> Problems) Check(ILogger logger)
    {
        var loader = new QuestLoader(this, logger);
        var registry = loader.Load();
        var problems = new List<string>(loader.Problems);
        problems.AddRange(new GraphChecks(registry, snapshot.Graph, snapshot.Blocks, snapshot.Items, snapshot.Entities).Run());
        return (registry, problems);
    }
}
