using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace questline.Server;

/// <summary>
/// Turns game events into task reports: kill, mount, place and break. Obtain and checkbox tasks
/// are the engine's own, and crafting and trading are <see cref="StationPatches"/>.
/// </summary>
public class Trackers(ICoreServerAPI api, QuestEngine engine)
{
    public void Start()
    {
        api.Event.OnEntityDeath += OnEntityDeath;
        api.Event.DidPlaceBlock += OnDidPlaceBlock;
        api.Event.DidBreakBlock += OnDidBreakBlock;
        api.Event.EntityMounted += OnEntityMounted;

        // Crafting at the grid and at stations is credited by StationPatches.
    }

    void OnEntityDeath(Entity entity, DamageSource damage)
    {
        if (damage?.GetCauseEntity() is EntityPlayer { Player: IServerPlayer player }) engine.Report(player, "kill", entity.Code, 1);
    }

    // Rafts, boats and tamed animals: the seat's entity is the vehicle or the mount.
    void OnEntityMounted(EntityAgent rider, IMountableSeat seat)
    {
        if (rider is not EntityPlayer { Player: IServerPlayer player }) return;
        var mount = seat?.Entity ?? seat?.MountSupplier?.OnEntity;
        if (mount?.Code != null) engine.Report(player, "mount", mount.Code, 1);
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
}
