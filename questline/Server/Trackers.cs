using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace questline.Server;

/// <summary>
/// Turns game events into task reports. Inventory (obtain) and checkbox tasks are the engine's
/// own; everything here hangs off an existing server event, with no Harmony.
/// </summary>
public class Trackers(ICoreServerAPI api, QuestEngine engine)
{
    public void Start()
    {
        api.Event.OnEntityDeath += OnEntityDeath;
        api.Event.DidPlaceBlock += OnDidPlaceBlock;
        api.Event.DidBreakBlock += OnDidBreakBlock;

        // The engine posts these when a grid craft is taken, a knapping or a clay form finishes.
        // Known gaps: shift-click crafting reports only its last batch; knapping into a placed
        // block and clay forming into ground-storable pottery post nothing (docs/phase1-findings.md).
        api.Event.RegisterEventBusListener((string _, ref EnumHandling _, IAttribute data) => OnCrafted("craft", data), 0.5, "onitemcrafted");
        api.Event.RegisterEventBusListener((string _, ref EnumHandling _, IAttribute data) => OnCrafted("knap", data), 0.5, "onitemknapped");
        api.Event.RegisterEventBusListener((string _, ref EnumHandling _, IAttribute data) => OnCrafted("clayform", data), 0.5, "onitemclayformed");
    }

    void OnEntityDeath(Entity entity, DamageSource damage)
    {
        if (damage?.GetCauseEntity() is EntityPlayer { Player: IServerPlayer player }) engine.Report(player, "kill", entity.Code, 1);
    }

    void OnDidPlaceBlock(IServerPlayer player, int oldBlockId, BlockSelection blockSel, ItemStack withItemStack)
    {
        var block = api.World.BlockAccessor.GetBlock(blockSel.Position);
        engine.Report(player, "place", block.Code, 1);
    }

    void OnDidBreakBlock(IServerPlayer player, int oldBlockId, BlockSelection blockSel)
    {
        engine.Report(player, "break", api.World.GetBlock(oldBlockId)?.Code, 1);
    }

    void OnCrafted(string type, IAttribute data)
    {
        if (data is not ITreeAttribute tree) return;
        var stack = (tree["itemstack"] as ItemstackAttribute)?.value;
        if (api.World.GetEntityById(tree.GetLong("byentityid")) is not EntityPlayer { Player: IServerPlayer player }) return;
        if (stack?.Collectible == null) return;

        engine.Report(player, type, stack.Collectible.Code, stack.StackSize);
    }
}
