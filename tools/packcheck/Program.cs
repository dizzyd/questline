using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using questline.Server;
using Vintagestory.API.Common;

namespace questline.PackCheck;

/// <summary>
/// questline-packcheck --snapshot questline-snapshot.json [--game &lt;install&gt;] [--strict] &lt;pack&gt;...
///
/// Loads the packs' chapters as the server would and runs every check Questline runs, the
/// recipe graph's included, against a snapshot of a game taken with /questline snapshot. Each
/// pack is a mod folder or zip. Prints each problem; exits 1 if any quest would be disabled,
/// or with --strict, if there's any problem at all.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string snapshotFile = null, gameDir = null;
        bool strict = false;
        var packs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--snapshot": snapshotFile = args[++i]; break;
                case "--game": gameDir = args[++i]; break;
                case "--strict": strict = true; break;
                case "-h" or "--help": return Usage(0);
                default: packs.Add(args[i]); break;
            }
        }
        if (snapshotFile == null || packs.Count == 0) return Usage(2);
        foreach (string pack in packs.Where(p => !File.Exists(p) && !Directory.Exists(p)))
        {
            Console.Error.WriteLine($"No pack at {pack}");
            return 2;
        }

        var snapshot = GameSnapshot.FromJson(File.ReadAllText(snapshotFile));
        Console.WriteLine($"Game {snapshot.GameVersion}: {snapshot.Items.Count + snapshot.Blocks.Count} items and blocks, {snapshot.Graph.Edges.Count} recipe edges, {snapshot.Mods.Count} mods");

        var facts = new PackFacts(snapshot, packs, gameDir == null ? null : Path.Combine(gameDir, "assets"));
        var (registry, problems) = facts.Check(new QuietLogger());

        foreach (string problem in problems) Console.WriteLine(problem);
        int disabled = problems.Count(p => p.Contains("quest disabled"));
        Console.WriteLine($"{registry.Quests.Count} quests in {registry.Chapters.Count} chapters: {problems.Count} problems, {disabled} quests disabled");
        return disabled > 0 || strict && problems.Count > 0 ? 1 : 0;
    }

    static int Usage(int code)
    {
        Console.Error.WriteLine("usage: questline-packcheck --snapshot <questline-snapshot.json> [--game <install>] [--strict] <pack folder or zip>...");
        Console.Error.WriteLine("  Take the snapshot in game with /questline snapshot, with the packs' dependencies installed.");
        return code;
    }

    /// <summary>The loader reports each problem itself as it goes; the checker prints them once, at the end.</summary>
    class QuietLogger : LoggerBase
    {
        protected override void LogImpl(EnumLogType logType, string format, params object[] args) { }
    }
}
