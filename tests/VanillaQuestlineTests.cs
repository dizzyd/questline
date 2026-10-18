using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Model;
using questline.Server;
using Vintagestory.API.Config;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The questline Questline ships: all of vanilla, stone age to steel and sailboats. It loads
    /// whole, every quest has its text, the recipe graph and the spoiler rule find nothing wrong
    /// with it, and it links every one of the game's own guides.
    /// </summary>
    [SingleplayerOnly]
    public class VanillaQuestlineTests
    {
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;

        [VsTest(TimeoutMs = 60000)]
        public async Task TheDefaultQuestlineLoadsCleanly()
        {
            await OnServer();
            await Until(() => Engine.GraphChecksDone, 1200, "the graph checks, built in the background");
            var chapters = Engine.Registry.Chapters.Where(c => c.Id.StartsWith("questline:")).ToList();
            var problems = Engine.Problems.Where(p => p.StartsWith("questline:")).ToList();
            foreach (var p in problems) Log(p);
            Log($"{chapters.Count} chapters, {chapters.Sum(c => c.Quests.Count)} quests");

            Assert.Equal(0, problems.Count, "no problems in the default questline");
            Assert.Equal(25, chapters.Count(c => c.RequiresMod.Length == 0), "every part of vanilla, beside any chapters for installed mods");
            Assert.True(chapters.All(c => c.Questline == QuestRegistry.DefaultQuestline));
            Assert.True(chapters.All(c => c.Quests.Count >= 5), "no chapter left a stub");
            Assert.Greater(chapters.Sum(c => c.Quests.Count), 200);
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

        /// <summary>
        /// Questline's text is English only, and the game shows English where a language has no
        /// entry, so on a German game nothing is missing either.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task TextCountsInAnotherLanguage()
        {
            await OnServer();
            string language = Lang.CurrentLocale;
            var english = Engine.Problems.Where(p => p.Contains("no lang entry")).ToHashSet();
            try
            {
                Lang.ChangeLanguage("de");
                Engine.Reload();
                var missing = Engine.Problems.Where(p => p.Contains("no lang entry") && !english.Contains(p)).ToList();
                foreach (var p in missing.Take(5)) Log(p);
                Assert.Equal(0, missing.Count, "English text counts when the game is in German");
            }
            finally
            {
                Lang.ChangeLanguage(language);
                Engine.Reload();
            }
        }

        /// <summary>
        /// Rusty gears are a reward now and then, not the default one; the server's GearRewards
        /// scales every gear a quest gives, choices included, and at 0 leaves the rest standing.
        /// </summary>
        [VsTest(TimeoutMs = 30000)]
        public async Task GearRewardsAreSparingAndScale()
        {
            await OnServer();
            QuestRegistry Copy() => QuestRegistry.FromJson(Engine.Registry.ToJson());
            static int Gears(QuestRegistry r) => r.Quests.Values.Where(q => q.Id.StartsWith("questline:"))
                .SelectMany(q => q.Rewards.SelectMany(x => x.Type == "choice" ? x.Options : [x]))
                .Where(x => x.Item == QuestEngine.GearItem).Sum(x => x.Count);
            var quests = Engine.Registry.Quests.Values.Where(q => q.Id.StartsWith("questline:") && q.Shape != NodeShape.Gear).ToList();
            var generous = quests.Where(q => q.Rewards.Any(r => r.Item == QuestEngine.GearItem && r.Count > 1)).ToList();
            foreach (var q in generous) Log("more than one gear: " + q.Id);
            Assert.Equal(0, generous.Count, "an ordinary quest gives a gear at most");

            int usual = Gears(Copy());
            var doubled = Copy();
            doubled.ScaleRewards(QuestEngine.GearItem, 2);
            Assert.Equal(usual * 2, Gears(doubled));
            var none = Copy();
            none.ScaleRewards(QuestEngine.GearItem, 0);
            Assert.Equal(0, Gears(none));
            Assert.True(none.Quests.Values.Where(q => q.Id.StartsWith("questline:") && q.Shape == NodeShape.Gear).All(q => q.Rewards.Any(r => r.Type == "choice")), "milestones keep their other choices");
            Log($"{usual} gears in the questline's rewards");
        }

        /// <summary>
        /// The ages run stone, clay and fire, copper, bronze, iron and steel, and a quest in a themed
        /// chapter belongs to the age the game actually gates it behind.
        /// </summary>
        [VsTest(TimeoutMs = 30000)]
        public async Task QuestsBelongToTheAgeTheyNeed()
        {
            await OnServer();
            var registry = Engine.Registry;
            var ages = registry.Chapters.Where(c => c.Age && c.Id.StartsWith("questline:")).Select(c => c.Id[10..]);
            Assert.Equal("stone-age, clay-and-fire, copper-age, bronze-age, iron-age, steel-age", string.Join(", ", ages));
            string AgeOf(string id) => registry.AgeOf(registry.Get("questline:" + id))?.Id[10..];
            Assert.Equal("stone-age", AgeOf("knap-hoe"));
            // The quern takes a metal hammer and chisel; the pie's table, a saw.
            Assert.Equal("copper-age", AgeOf("make-quern"));
            Assert.Equal("copper-age", AgeOf("bake-pie"));
            Assert.Equal("iron-age", AgeOf("build-greenhouse"));
            Assert.Equal("steel-age", AgeOf("make-steel-plate"));
        }

        /// <summary>
        /// Each chapter reads left to right: a prerequisite in the same chapter sits to the left of
        /// what it unlocks, no two quests crowd one another, and no line passes behind a quest.
        /// </summary>
        [VsTest(TimeoutMs = 60000)]
        public async Task EveryChapterReadsLeftToRight()
        {
            await OnServer();
            int faults = 0;
            void Fault(string why) { Log(why); faults++; }
            foreach (var chapter in Engine.Registry.Chapters.Where(c => c.Id.StartsWith("questline:")))
            {
                var quests = chapter.Quests;
                var byId = quests.ToDictionary(q => q.Id);
                for (int i = 0; i < quests.Count; i++)
                {
                    for (int j = i + 1; j < quests.Count; j++)
                    {
                        if (System.Math.Abs(quests[i].Pos[0] - quests[j].Pos[0]) < 2 && System.Math.Abs(quests[i].Pos[1] - quests[j].Pos[1]) < 1.5f) Fault($"{quests[i].Id} and {quests[j].Id} crowd each other");
                    }
                }
                foreach (var quest in quests)
                {
                    foreach (string id in quest.Requires?.QuestIds() ?? [])
                    {
                        if (!byId.TryGetValue(id, out var before)) continue;
                        float px = before.Pos[0], py = before.Pos[1], vx = quest.Pos[0] - px, vy = quest.Pos[1] - py;
                        if (vx <= 0) Fault($"{id} isn't left of {quest.Id}");
                        foreach (var other in quests.Where(o => o != quest && o != before))
                        {
                            float u = ((other.Pos[0] - px) * vx + (other.Pos[1] - py) * vy) / (vx * vx + vy * vy);
                            if (u <= 0 || u >= 1) continue;
                            float dx = px + u * vx - other.Pos[0], dy = py + u * vy - other.Pos[1];
                            if (dx * dx + dy * dy < 0.45f * 0.45f) Fault($"the line {id} → {quest.Id} passes behind {other.Id}");
                        }
                    }
                }
            }
            Assert.Equal(0, faults, "every chapter laid out clearly");
        }

        /// <summary>The game's guides, as the handbook lists them: each one is linked from some quest's text.</summary>
        [VsTest(TimeoutMs = 60000), RequiresClient]
        public async Task EveryVanillaGuideIsLinked()
        {
            await OnServer();
            var quests = Engine.Registry.Quests.Values.Where(q => q.Id.StartsWith("questline:")).ToList();
            await OnClient();
            // The game's own guides only: mods' guides (Expanded Foods ships several) are theirs to teach.
            string vanilla = System.IO.Path.GetFullPath(Vintagestory.API.Config.GamePaths.AssetsPath);
            var guides = Capi.Assets.GetMany("config/handbook")
                .Where(a => a.Origin?.OriginPath != null && System.IO.Path.GetFullPath(a.Origin.OriginPath).StartsWith(vanilla))
                .Select(a => a.ToObject<Vintagestory.GameContent.GuiHandbookTextPage>()?.PageCode)
                .Where(code => code != null)
                .ToList();
            Assert.Greater(guides.Count, 25, "the game's guides found");

            var text = string.Join("\n", quests.Select(q => Vintagestory.API.Config.Lang.Get(q.Id + "-desc")));
            var missing = guides.Where(code => !text.Contains("handbook://" + code + "\"")).ToList();
            foreach (string code in missing) Log("not linked: " + code);
            Assert.Equal(0, missing.Count, "every guide linked from a quest");
        }

        /// <summary>Photographs every chapter as a new player first sees it, at the test window's 960×600, for review.</summary>
        [VsTest(TimeoutMs = 300000), RequiresClient]
        public async Task EveryChapterPhotographed()
        {
            await OnServer();
            var ids = Engine.Registry.Chapters.Where(c => c.Id.StartsWith("questline:")).OrderBy(c => c.Order).Select(c => c.Id).ToList();
            await OnClient();
            var book = Capi.ModLoader.GetModSystem<QuestlineModSystem>().Book;
            foreach (string id in ids)
            {
                book.ForgetViews();
                book.SelectChapter(id);
                await VsTestkit.Testing.Gui.WaitFor<questline.Client.GuiDialogQuestBook>();
                await Frames.Wait(20);
                Log(await Shot.Take($"questline-chapter-{id[10..]}.png"));
            }
            // A milestone's page, with its choice of three rewards.
            book.SelectQuest("questline:first-smithing");
            await Frames.Wait(20);
            Log(await Shot.Take("questline-chapter-milestone-reward.png"));
            book.TryClose();
        }

        /// <summary>Every quest that isn't ticked by hand gives something, and every milestone offers a choice.</summary>
        [VsTest(TimeoutMs = 30000)]
        public async Task QuestsGiveRewardsAndMilestonesAChoice()
        {
            await OnServer();
            var quests = Engine.Registry.Quests.Values.Where(q => q.Id.StartsWith("questline:")).ToList();
            var bare = quests.Where(q => q.Rewards.Count == 0 && q.Tasks.Any(t => t.Type != "checkbox")).ToList();
            foreach (var q in bare) Log("no reward: " + q.Id);
            Assert.Equal(0, bare.Count, "every quest with real tasks rewards the player");

            var milestones = quests.Where(q => q.Shape == NodeShape.Gear).ToList();
            foreach (var q in milestones.Where(q => !q.Rewards.Any(r => r.Type == "choice"))) Log("milestone without a choice: " + q.Id);
            Assert.True(milestones.All(q => q.Rewards.Any(r => r.Type == "choice" && r.Options.Count == 3)), "each milestone offers three");
        }
    }
}
