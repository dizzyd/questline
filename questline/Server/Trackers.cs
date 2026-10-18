using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace questline.Server;

/// <summary>
/// Turns game events into task reports: kill, mount, place and break. Obtain and checkbox tasks
/// are the engine's own, crafting and trading are <see cref="StationPatches"/>, and visits are
/// <see cref="VisitTracker"/>.
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

/// <summary>
/// Credits visit tasks: every second, a player standing inside a structure worldgen placed
/// visits it, once per structure.
/// </summary>
public class VisitTracker(ICoreServerAPI api, QuestEngine engine)
{
    public void Start() => api.Event.RegisterGameTickListener(_ => Tick(), 1000);

    void Tick()
    {
        foreach (IServerPlayer player in api.World.AllOnlinePlayers)
        {
            if (player.ConnectionState != EnumClientState.Playing || player.Entity == null) continue;
            foreach (var placed in At(api, player.Entity.Pos.AsBlockPos)) engine.Visit(player.PlayerUID, placed);
        }
    }

    /// <summary>
    /// The structures a position is inside. A structure is recorded in the map region worldgen
    /// started it from, and a large one reaches into the next, so the neighbours are asked too.
    /// </summary>
    public static IEnumerable<GeneratedStructure> At(ICoreServerAPI api, BlockPos pos)
    {
        if (pos.dimension != 0) yield break;
        int size = api.WorldManager.RegionSize;
        int rx = pos.X / size, rz = pos.Z / size;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                var placed = api.WorldManager.GetMapRegion(rx + dx, rz + dz)?.GeneratedStructures;
                if (placed == null) continue;
                // Worldgen adds to a region from its own thread; reading up to the count never throws.
                for (int i = 0; i < placed.Count; i++)
                {
                    if (placed[i]?.Location is { } loc && loc.Contains(pos)) yield return placed[i];
                }
            }
        }
    }
}
