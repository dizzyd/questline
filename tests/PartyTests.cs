using System.Linq;
using System.Threading.Tasks;
using questline;
using questline.Client;
using questline.Model;
using questline.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Questline.Tests
{
    /// <summary>
    /// Party and server quests from the qltest "together" chapter. Singleplayer has one player,
    /// so the second party member is a uid with no one behind it: the engine takes reports and
    /// party answers by uid, which is how an offline member looks to it anyway.
    /// </summary>
    [RequiresClient, SingleplayerOnly]
    public class PartyTests
    {
        const string Ghost = "qltest-ghost";
        const string PartyWork = "qltest:party-work";
        const string Milestone = "qltest:party-milestone";
        const string AfterMilestone = "qltest:after-milestone";
        const string ServerGoal = "qltest:server-goal";
        const string ServerRace = "qltest:server-race";

        static QuestlineModSystem ClientSystem => Capi.ModLoader.GetModSystem<QuestlineModSystem>();
        static QuestEngine Engine => Sapi.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        static GuiDialogQuestBook Book => ClientSystem.Book;
        static IServerPlayer Me => (IServerPlayer)Player.Me;
        static string MyUid => Me.PlayerUID;

        static void Teamwork(string uid, int amount) => Engine.Report(uid, "custom", new AssetLocation("qltest:teamwork"), amount);

        [BeforeEach]
        public async Task StartFresh()
        {
            await OnServer();
            if (Engine.Registry.Get(PartyWork) == null) Skip("the qltest fixture isn't loaded; pass --mods tests/fixtures/Mods");
            foreach (var uid in new[] { MyUid, Ghost })
            {
                if (Engine.PartyOf(uid) != null) Engine.Leave(uid);
            }
            Engine.Reset(MyUid);
            Engine.Reset(Ghost);
            Engine.ResetServer();
        }

        /// <summary>Me in a party with the ghost, which has been invited and accepted.</summary>
        static Party PartyWithGhost()
        {
            var (party, error) = Engine.CreateParty(MyUid, "Testers");
            Assert.Equal(null, error);
            Assert.Equal(null, Engine.Invite(MyUid, Ghost));
            Assert.Equal(null, Engine.Accept(Ghost, party.Id));
            Assert.Equal(2, party.Members.Count);
            return party;
        }

        [VsTest]
        public void CatchingUpSpendsEveryMembersClaim()
        {
            PartyWithGhost();
            var slot = Me.InventoryManager.ActiveHotbarSlot;
            slot.Itemstack = World.Stack("game:charcoal", 1);
            slot.MarkDirty();
            try
            {
                Assert.True(Engine.CatchUp(Me).Contains("qltest:cu-party"), "the charcoal proves the party quest");
                Assert.Equal(QuestState.Done, Engine.StateOf(MyUid, "qltest:cu-party"));
                Assert.Equal(QuestState.Done, Engine.StateOf(Ghost, "qltest:cu-party"), "the ghost's claim is spent too");
            }
            finally
            {
                slot.Itemstack = null;
                slot.MarkDirty();
            }
        }

        [VsTest]
        public void PartyPoolsWorkAndEachMemberClaimsOnce()
        {
            PartyWithGhost();

            Teamwork(Ghost, 2);
            Assert.Equal(QuestState.InProgress, Engine.StateOf(MyUid, PartyWork), "the ghost's work shows for me");
            Teamwork(MyUid, 1);
            Assert.Equal(QuestState.Claimable, Engine.StateOf(MyUid, PartyWork));
            Assert.Equal(QuestState.Claimable, Engine.StateOf(Ghost, PartyWork));

            Assert.Equal(null, Engine.Claim(Me, PartyWork, null));
            Assert.Equal(QuestState.Done, Engine.StateOf(MyUid, PartyWork));
            Assert.Equal(QuestState.Claimable, Engine.StateOf(Ghost, PartyWork), "the ghost's claim is still waiting");
            Assert.NotNull(Engine.Claim(Me, PartyWork, null), "a second claim");
        }

        [VsTest]
        public void JoiningBringsProgressAndLeavingKeepsIt()
        {
            Teamwork(MyUid, 2);
            var party = PartyWithGhost();
            Assert.Equal(2, party.Progress.Find(PartyWork).Tasks[0], "my progress came with me");

            Teamwork(Ghost, 1);
            Assert.Equal(QuestState.Claimable, Engine.StateOf(MyUid, PartyWork));

            Engine.Leave(MyUid);
            Assert.True(Engine.PartyOf(MyUid) == null, "I left");
            Assert.Equal(QuestState.Claimable, Engine.StateOf(MyUid, PartyWork), "finished in the party, still finished");
            Assert.Equal(Ghost, party.Owner, "ownership passed on");

            // Work from the party no longer counts for me.
            Engine.Reset(MyUid, PartyWork);
            Teamwork(Ghost, 3);
            Assert.Equal(QuestState.Available, Engine.StateOf(MyUid, PartyWork));
        }

        [VsTest]
        public void OnlyTheOwnerInvitesAndKicks()
        {
            var party = PartyWithGhost();
            Assert.NotNull(Engine.Invite(Ghost, "someone-else"), "a member who isn't the owner invites");
            Assert.NotNull(Engine.Kick(Ghost, MyUid), "a member who isn't the owner kicks");
            Assert.NotNull(Engine.Accept(MyUid, party.Id), "accepting without an invite");

            Assert.Equal(null, Engine.Kick(MyUid, Ghost));
            Assert.Equal(1, party.Members.Count);
            Assert.True(Engine.PartyOf(Ghost) == null, "the ghost is out");
        }

        [VsTest]
        public void APartyStageReachesEveryMember()
        {
            PartyWithGhost();
            Assert.Equal(QuestState.Locked, Engine.StateOf(Ghost, AfterMilestone));

            Engine.Check(Me, Milestone, 0);
            Assert.Equal(null, Engine.Claim(Me, Milestone, null));

            Assert.True(Engine.HasStage(Ghost, "qltest:party-age"), "the stage is the party's");
            Assert.Equal(QuestState.Available, Engine.StateOf(Ghost, AfterMilestone));

            // Leaving keeps the stage.
            Engine.Leave(Ghost);
            Assert.True(Engine.HasStage(Ghost, "qltest:party-age"), "kept after leaving");
        }

        [VsTest]
        public void ServerQuestsPoolEveryoneAndFirstClaimTakesIt()
        {
            Engine.Report(Ghost, "custom", new AssetLocation("qltest:townwork"), 3);
            Engine.Report(MyUid, "custom", new AssetLocation("qltest:townwork"), 2);
            Assert.Equal(QuestState.Claimable, Engine.StateOf(MyUid, ServerGoal));
            Assert.Equal(null, Engine.Claim(Me, ServerGoal, null));
            Assert.Equal(QuestState.Done, Engine.StateOf(MyUid, ServerGoal));
            Assert.Equal(QuestState.Claimable, Engine.StateOf(Ghost, ServerGoal), "each player claims a server goal once");

            Engine.Check(Me, ServerRace, 0);
            Assert.Equal(QuestState.Claimable, Engine.StateOf(Ghost, ServerRace));
            Assert.Equal(null, Engine.Claim(Me, ServerRace, null));
            Assert.Equal(QuestState.Done, Engine.StateOf(Ghost, ServerRace), "the first claim took it for everyone");
        }

        [VsTest]
        public void PartiesSurviveASaveAndLoad()
        {
            var party = PartyWithGhost();
            Teamwork(Ghost, 2);
            Engine.SaveProgress();
            Engine.Leave(Ghost);
            Engine.Leave(MyUid);

            Engine.LoadProgress();
            var loaded = Engine.PartyOf(MyUid);
            Assert.NotNull(loaded, "the party came back");
            Assert.Equal(party.Id, loaded.Id);
            Assert.Equal(loaded, Engine.PartyOf(Ghost));
            Assert.Equal(QuestState.InProgress, Engine.StateOf(MyUid, PartyWork));
        }

        [VsTest(TimeoutMs = 30000)]
        public async Task StartingAPartyFromTheBook()
        {
            await OnClient();
            await Until(() => ClientSystem.Client.Party != null, 100, "party state on the client");
            Book.SelectChapter("qltest:together");
            Book.SelectTab(2);
            await Frames.Wait(10);

            var button = Book.SingleComposer.GetButton("party-create");
            Assert.NotNull(button, "a start-a-party button");
            Book.OnMouseDown(new MouseEvent((int)(button.Bounds.absX + 10), (int)(button.Bounds.absY + 10), EnumMouseButton.Left, 0));
            Book.OnMouseUp(new MouseEvent((int)(button.Bounds.absX + 10), (int)(button.Bounds.absY + 10), EnumMouseButton.Left, 0));

            await Until(() => ClientSystem.Client.InParty, 100, "the party to reach the client");
            await Until(() => Book.SingleComposer.GetButton("party-leave") != null, 60, "the book to show the party");
            Assert.Equal(1, ClientSystem.Client.Party.Members.Count);
            await Frames.Wait(10);
            Log(await Shot.Take("questline-party.png"));

            // A party quest says whose work counts.
            Book.SelectQuest(PartyWork);
            await Frames.Wait(10);
            Log(await Shot.Take("questline-party-quest.png"));
            Book.TryClose();
        }
    }
}
