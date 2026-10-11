using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using questline.Model;
using Vintagestory.API.Server;

namespace questline.Server;

/// <summary>
/// The server half of the in-game editor. Edits are kept as working copies under
/// ModConfig/questline/working/&lt;domain&gt;/, never in installed mods: chapters/&lt;name&gt;.json
/// and lang/en.json, the same layout a content mod uses. The loader reads them over the
/// installed chapters, so a save is a write and a reload. Export copies them out as assets
/// ready to drop into a content mod, with ids written short as a pack's own files would.
/// </summary>
public class QuestEditor(ICoreServerAPI api, QuestEngine engine)
{
    public const string Privilege = "questline.edit";

    static readonly Regex SafeName = new("^[a-z0-9][a-z0-9_-]*$");
    // Written as a pack author would: camelCase names, indented, nothing unset.
    static readonly JsonSerializerSettings Pretty = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
    };

    public string WorkingDir => Path.Combine(api.GetOrCreateDataPath("ModConfig"), "questline", "working");
    public string ExportDir => Path.Combine(api.GetOrCreateDataPath("ModConfig"), "questline", "export");

    public static bool CanEdit(IServerPlayer player) => player?.HasPrivilege(Privilege) == true;

    /// <summary>Writes a chapter's working copy and its text, then reloads. Returns null, or why it was refused.</summary>
    public string Save(IServerPlayer player, string chapterJson, string textJson)
    {
        if (!CanEdit(player)) return $"You need the {Privilege} privilege to edit quests.";

        ChapterDef chapter;
        Dictionary<string, string> text;
        try
        {
            chapter = JsonConvert.DeserializeObject<ChapterDef>(chapterJson);
            text = textJson == null ? [] : JsonConvert.DeserializeObject<Dictionary<string, string>>(textJson) ?? [];
        }
        catch (Exception e)
        {
            return $"That chapter can't be read: {e.Message}";
        }

        if (Split(chapter?.Id) is not var (domain, name)) return $"Chapter id '{chapter?.Id}' must be domain:name, in lowercase letters, digits, - and _.";
        if (chapter.Quests.FirstOrDefault(q => Split(q.Id) == null) is { } bad) return $"Quest id '{bad.Id}' must be domain:name, in lowercase letters, digits, - and _.";

        string dir = Path.Combine(WorkingDir, domain);
        Directory.CreateDirectory(Path.Combine(dir, "chapters"));
        File.WriteAllText(Path.Combine(dir, "chapters", name + ".json"), JsonConvert.SerializeObject(chapter, Pretty));

        // Text for this domain merges into its lang file; an empty value removes the entry.
        string prefix = domain + ":";
        var mine = text.Where(t => t.Key.StartsWith(prefix)).ToList();
        if (mine.Count > 0)
        {
            var lang = ReadLang(domain);
            foreach (var (key, value) in mine)
            {
                if (string.IsNullOrEmpty(value)) lang.Remove(key[prefix.Length..]);
                else lang[key[prefix.Length..]] = value;
            }
            WriteLang(domain, lang);
        }

        api.Logger.Audit("[questline] {0} saved chapter {1} ({2} quests)", player.PlayerName, chapter.Id, chapter.Quests.Count);
        engine.Reload();
        return null;
    }

    /// <summary>Drops a chapter's working copy, going back to the installed one if there is one, and reloads.</summary>
    public string Revert(IServerPlayer player, string chapterId)
    {
        if (!CanEdit(player)) return $"You need the {Privilege} privilege to edit quests.";
        if (Split(chapterId) is not var (domain, name)) return $"No chapter {chapterId}";

        string file = Path.Combine(WorkingDir, domain, "chapters", name + ".json");
        if (!File.Exists(file)) return $"{chapterId} has no working copy.";
        File.Delete(file);
        api.Logger.Audit("[questline] {0} reverted chapter {1}", player.PlayerName, chapterId);
        engine.Reload();
        return null;
    }

    /// <summary>
    /// Writes every working copy out as content-mod assets: export/&lt;domain&gt;/assets/&lt;domain&gt;/...
    /// Returns the folder and how many chapters went into it.
    /// </summary>
    public (string Folder, int Chapters) Export()
    {
        int count = 0;
        if (Directory.Exists(ExportDir)) Directory.Delete(ExportDir, true);
        if (!Directory.Exists(WorkingDir)) return (ExportDir, 0);

        foreach (var domainDir in Directory.GetDirectories(WorkingDir))
        {
            string domain = Path.GetFileName(domainDir);
            string assets = Path.Combine(ExportDir, domain, "assets", domain);

            string chapters = Path.Combine(domainDir, "chapters");
            if (Directory.Exists(chapters))
            {
                Directory.CreateDirectory(Path.Combine(assets, "questline", "chapters"));
                foreach (var file in Directory.GetFiles(chapters, "*.json"))
                {
                    var chapter = JsonConvert.DeserializeObject<ChapterDef>(File.ReadAllText(file));
                    Shorten(chapter, domain);
                    File.WriteAllText(Path.Combine(assets, "questline", "chapters", Path.GetFileName(file)), JsonConvert.SerializeObject(chapter, Pretty));
                    count++;
                }
            }

            string lang = Path.Combine(domainDir, "lang", "en.json");
            if (File.Exists(lang))
            {
                Directory.CreateDirectory(Path.Combine(assets, "lang"));
                File.Copy(lang, Path.Combine(assets, "lang", "en.json"), true);
            }
        }
        return (ExportDir, count);
    }

    /// <summary>Writes the chapter's own ids without its domain, as a pack's files would. Item codes keep theirs.</summary>
    static void Shorten(ChapterDef chapter, string domain)
    {
        chapter.Id = Requirement.UnqualifyId(chapter.Id, domain);
        if (chapter.Questline == QuestRegistry.DefaultQuestline) chapter.Questline = null;
        else chapter.Questline = Requirement.UnqualifyId(chapter.Questline, domain);
        chapter.Aliases = chapter.Aliases.ToDictionary(a => Requirement.UnqualifyId(a.Key, domain), a => Requirement.UnqualifyId(a.Value, domain));
        foreach (var quest in chapter.Quests)
        {
            quest.Id = Requirement.UnqualifyId(quest.Id, domain);
            quest.Requires?.Unqualify(domain);
            foreach (var reward in quest.Rewards.Where(r => r.Stage != null)) reward.Stage = Requirement.UnqualifyId(reward.Stage, domain);
        }
    }

    Dictionary<string, string> ReadLang(string domain)
    {
        string file = Path.Combine(WorkingDir, domain, "lang", "en.json");
        return File.Exists(file) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];
    }

    void WriteLang(string domain, Dictionary<string, string> lang)
    {
        string dir = Path.Combine(WorkingDir, domain, "lang");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "en.json"), JsonConvert.SerializeObject(lang.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value), Pretty));
    }

    /// <summary>A qualified id's domain and name, if both are safe to use as folder and file names.</summary>
    static (string Domain, string Name)? Split(string id)
    {
        int colon = id?.IndexOf(':') ?? -1;
        if (colon <= 0) return null;
        string domain = id[..colon], name = id[(colon + 1)..];
        return SafeName.IsMatch(domain) && SafeName.IsMatch(name) ? (domain, name) : null;
    }
}
