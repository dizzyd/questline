using System.IO;
using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Server;
using Vintagestory.API.Common;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The pack checker finds what the server finds. The qltest fixture is checked twice: by the
    /// server as it loaded, and the checker's way, from a snapshot of this game and the fixture's
    /// files on disk. The snapshot is saved, so the command-line checker can be run against it too.
    /// </summary>
    [SingleplayerOnly]
    public class PackCheckTests
    {
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;

        [VsTest(TimeoutMs = 120000)]
        public async Task TheCheckerFindsWhatTheServerFinds()
        {
            await OnServer();
            var fixture = Sapi.ModLoader.GetMod("qltest");
            if (fixture == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");

            // Through JSON and back, as the command-line checker reads it.
            var snapshot = GameSnapshot.FromJson(Engine.Snapshot().ToJson());
            string file = Path.Combine(Sapi.GetOrCreateDataPath("ModConfig"), "questline", "questline-snapshot.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, Engine.Snapshot().ToJson());
            Log($"snapshot: {new FileInfo(file).Length / 1024} KB at {file}; fixture at {fixture.SourcePath}");

            var (registry, problems) = new PackFacts(snapshot, [fixture.SourcePath]).Check(Sapi.Logger);
            var checker = problems.Where(p => p.StartsWith("qltest:")).OrderBy(p => p).ToList();
            await Until(() => Engine.GraphChecksDone, 1200, "the graph checks, built in the background");
            var server = Engine.Problems.Where(p => p.StartsWith("qltest:")).OrderBy(p => p).ToList();

            foreach (var p in checker.Except(server)) Log("checker only: " + p);
            foreach (var p in server.Except(checker)) Log("server only: " + p);
            Assert.Equal(server.Count, checker.Count, "as many problems");
            Assert.True(checker.SequenceEqual(server), "the same problems");
            Assert.Equal(Engine.Registry.Quests.Keys.Count(k => k.StartsWith("qltest:")), registry.Quests.Count, "the same quests load");
        }
    }
}
