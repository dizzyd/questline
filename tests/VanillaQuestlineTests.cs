using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Model;
using questline.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The questline Questline ships: stone age to steel. It loads whole, every quest has its
    /// text, and the recipe graph and the spoiler rule find nothing wrong with it.
    /// </summary>
    [SingleplayerOnly]
    public class VanillaQuestlineTests
    {
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;

        [VsTest(TimeoutMs = 60000)]
        public async Task TheDefaultQuestlineLoadsCleanly()
        {
            await OnServer();
            var chapters = Engine.Registry.Chapters.Where(c => c.Id.StartsWith("questline:")).ToList();
            var problems = Engine.Problems.Where(p => p.StartsWith("questline:")).ToList();
            foreach (var p in problems) Log(p);
            Log($"{chapters.Count} chapters, {chapters.Sum(c => c.Quests.Count)} quests");

            Assert.Equal(0, problems.Count, "no problems in the default questline");
            Assert.Equal(8, chapters.Count, "stone age to steel, and winter");
            Assert.True(chapters.All(c => c.Questline == QuestRegistry.DefaultQuestline));
            Assert.Greater(chapters.Sum(c => c.Quests.Count), 50);
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task EveryQuestCanBeReachedFromTheStart()
        {
            await OnServer();
            var quests = Engine.Registry.Quests.Values.Where(q => q.Id.StartsWith("questline:")).ToList();
            var reachable = new System.Collections.Generic.HashSet<string>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var q in quests.Where(q => !reachable.Contains(q.Id)))
                {
                    if (q.Requires == null || q.Requires.QuestIds().All(reachable.Contains))
                    {
                        reachable.Add(q.Id);
                        changed = true;
                    }
                }
            }
            foreach (var q in quests.Where(q => !reachable.Contains(q.Id))) Log("unreachable: " + q.Id);
            Assert.Equal(quests.Count, reachable.Count, "every quest reachable by doing the ones before it");
        }
    }
}
