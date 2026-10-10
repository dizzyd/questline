using System;
using questline.Server;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace questline;

/// <summary>
/// What other mods use, server side: report progress on custom tasks, gate their own content
/// on stages and quests, and hear about completions. Get it with
/// <c>sapi.ModLoader.GetModSystem&lt;QuestlineApi&gt;()</c>.
///
/// A mod that would rather not reference Questline can do the same over the event bus:
/// push <c>questline:report</c> with a tree of <c>playeruid</c>, <c>key</c> and <c>amount</c>,
/// and listen for <c>questline:questcompleted</c> (<c>playeruid</c>, <c>questid</c>) and
/// <c>questline:stageset</c> (<c>playeruid</c>, <c>stage</c>).
/// </summary>
public class QuestlineApi : ModSystem
{
    QuestEngine engine;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    // After QuestlineModSystem, which creates the engine.
    public override double ExecuteOrder() => 0.2;

    public override void StartServerSide(ICoreServerAPI api)
    {
        engine = api.ModLoader.GetModSystem<QuestlineModSystem>().Engine;
        engine.QuestCompleted += (player, questId) => QuestCompleted?.Invoke(player, questId);
        engine.StageSet += (player, stage) => StageSet?.Invoke(player, stage);
    }

    /// <summary>Credits <paramref name="amount"/> towards every active custom task with this key.</summary>
    public void Report(IServerPlayer player, string key, int amount = 1) => engine.Report(player, "custom", new AssetLocation(key), amount);

    public bool HasStage(IPlayer player, string stage) => engine.HasStage(player.PlayerUID, stage);

    public void SetStage(IServerPlayer player, string stage) => engine.SetStage(player, stage);

    /// <summary>Whether every task of the quest is done (claimed or not).</summary>
    public bool IsComplete(IPlayer player, string questId) => engine.ProgressOf(player.PlayerUID).IsComplete(questId);

    /// <summary>A player finished every task of a quest.</summary>
    public event Action<IServerPlayer, string> QuestCompleted;

    /// <summary>A player reached a stage for the first time.</summary>
    public event Action<IServerPlayer, string> StageSet;
}
