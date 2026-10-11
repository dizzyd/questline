using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// The goal planner: a flint axe worked back to flint, through the player's real inventory,
    /// and what each reveal level lets the book show of it.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class PlannerTests
    {
        const string Axe = "game:axe-flint", Head = "game:axehead-flint", Flint = "game:flint";

        static QuestlineModSystem ClientSystem => Capi.ModLoader.GetModSystem<QuestlineModSystem>();
        static QuestClient Client => ClientSystem.Client;
        static GuiDialogQuestBook Book => ClientSystem.Book;
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static IServerPlayer Me => (IServerPlayer)Player.Me;

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
            {
                foreach (var slot in Me.InventoryManager.GetOwnInventory(name))
                {
                    if (slot.Itemstack == null) continue;
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            await SetServerLevel("hints");
            await OnClient();
            Client.SetPlannerLevel(RevealLevel.Guide);
            foreach (var plan in Client.Plans.ToList()) Client.RemovePlan(plan);
            Book.TryClose();
            await Until(() => Capi.World.Player.InventoryManager.GetOwnInventory(GlobalConstants.hotBarInvClassName).All(s => s.Empty), 60, "an empty hand on the client");
        }

        static async Task SetServerLevel(string level)
        {
            await OnServer();
            Engine.Config.RevealLevel = level;
            Engine.Reload();
            await OnClient();
            await Until(() => Client.ServerReveal.ToString().ToLowerInvariant() == level, 60, $"the {level} level on the client");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task PlansAFlintAxeBackToFlint()
        {
            await OnClient();
            var plan = Client.MakePlanner().Plan(Axe);
            foreach (var step in plan.Walk()) Log($"{step.Code} ×{step.Quantity}: {step.Edge?.Kind ?? step.Source?.Kind ?? "find"}{(step.Done ? " (have)" : step.Ready ? " (ready)" : "")}");

            Assert.Equal("grid", plan.Edge?.Kind, "an axe is crafted");
            var head = plan.Inputs.FirstOrDefault(i => i.Code == Head);
            Assert.NotNull(head, "from a flint axe head");
            Assert.Equal("knapping", head.Edge?.Kind, "the head is knapped");
            var flint = head.Inputs.Single();
            Assert.Equal(Flint, flint.Code, "from flint");
            Assert.True(flint.Ready && !head.Ready, "flint first, then the head");

            // With flint in hand, the head is next.
            await OnServer();
            await Player.Hold(Flint, 2);
            await OnClient();
            await Until(() => Client.MakePlanner().Plan(Axe).Inputs.First(i => i.Code == Head).Ready, 60, "the head to be ready");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task HintsNameTheNextStepsAndNotHow()
        {
            await OnClient();
            Assert.Equal(RevealLevel.Hints, Client.Reveal, "the server's hints cap the player's guide");
            Assert.True(Book.CanPlan(Axe));
            Book.ShowPlan(Axe);
            await VsTestkit.Testing.Gui.WaitFor<GuiDialogQuestBook>();
            await Frames.Wait(20);

            var nodes = Book.Canvas.Nodes;
            var steps = Book.PlanSteps;
            int head = steps.ToList().FindIndex(s => s.Code == Head);
            Assert.Equal("?", nodes[head].Title, "a step further back stays a question mark");
            Assert.True(nodes.Any(n => n.Title.StartsWith("Flint")), "the next step is named");
            var detail = Book.SingleComposer.GetRichtext("detail").Components.OfType<RichTextComponent>().Select(c => c.DisplayText).ToList();
            Assert.False(detail.Any(t => t.ToUpperInvariant().Contains("MADE BY")), "no recipes at hints");
            Log(await Shot.Take("questline-plan-hints.png"));

            await SetServerLevel("guide");
            await Frames.Wait(20);
            nodes = Book.Canvas.Nodes;
            Assert.False(nodes.Any(n => n.Title == "?"), "every step named at guide");
            detail = Book.SingleComposer.GetRichtext("detail").Components.OfType<RichTextComponent>().Select(c => c.DisplayText).ToList();
            Assert.True(detail.Any(t => t.ToUpperInvariant().Contains("MADE BY")), "the recipe at guide");
            Log(await Shot.Take("questline-plan-guide.png"));
            Book.TryClose();
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task DiscoveryTurnsThePlannerOff()
        {
            await SetServerLevel("discovery");
            Assert.Equal(RevealLevel.Discovery, Client.Reveal);
            Assert.False(Book.CanPlan(Axe), "nothing to plan with");
            Assert.False(Book.CanPlan("game:jonasparts-pumphead"), "and never a story item");
            await SetServerLevel("hints");
        }
    }
}
