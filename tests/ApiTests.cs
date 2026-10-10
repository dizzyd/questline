using System.Collections.Generic;
using System.Threading.Tasks;
using questline;
using questline.Model;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Stages and the API other mods use: a stage reward unlocking a gated quest, and custom-task
    /// progress reported both through QuestlineApi and over the event bus.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class ApiTests
    {
        const string ForgeAhead = "qltest:forge-ahead";
        const string IronWork = "qltest:iron-work";
        const string IronAge = "qltest:iron-age";

        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static QuestlineApi Api => Sapi.ModLoader.GetModSystem<QuestlineApi>();
        static IServerPlayer Me => (IServerPlayer)Player.Me;
        static QuestState StateOf(string id) => Engine.StateOf(Me.PlayerUID, id);

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get(IronWork) == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            Engine.Reset(Me);
        }

        [VsTest]
        public void StageRewardUnlocksTheQuestGatedOnIt()
        {
            Assert.Equal(QuestState.Locked, StateOf(IronWork));
            Assert.False(Api.HasStage(Me, IronAge));

            Engine.Check(Me, ForgeAhead, 0);
            Assert.Equal(null, Engine.Claim(Me, ForgeAhead, null));

            Assert.True(Api.HasStage(Me, IronAge), "the stage reward was granted");
            Assert.Equal(QuestState.Available, StateOf(IronWork));
        }

        [VsTest]
        public void CustomTasksTakeReportsFromTheApiAndTheEventBus()
        {
            Api.SetStage(Me, IronAge);
            var completed = new List<string>();
            void OnCompleted(IServerPlayer player, string questId) => completed.Add(questId);
            Api.QuestCompleted += OnCompleted;

            string busCompleted = null;
            Sapi.Event.RegisterEventBusListener((string _, ref EnumHandling _, IAttribute data) =>
                busCompleted = (data as ITreeAttribute)?.GetString("questid"), 0.5, QuestEngine.QuestCompletedEvent);

            try
            {
                Api.Report(Me, "qltest:caught-fish", 2);
                Assert.Equal(2, Engine.ProgressOf(Me.PlayerUID).Find(IronWork).Tasks[0]);

                // A mod with no reference to Questline reports the same way over the event bus.
                Sapi.Event.PushEvent(QuestEngine.ReportEvent, new TreeAttribute
                {
                    ["playeruid"] = new StringAttribute(Me.PlayerUID),
                    ["key"] = new StringAttribute("qltest:caught-fish"),
                });

                Assert.Equal(QuestState.Done, StateOf(IronWork));
                Assert.True(Api.IsComplete(Me, IronWork));
                Assert.Contains(string.Join(",", completed), IronWork);
                Assert.Equal(IronWork, busCompleted);
            }
            finally
            {
                Api.QuestCompleted -= OnCompleted;
            }
        }
    }
}
