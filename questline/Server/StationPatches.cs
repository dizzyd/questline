using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace questline.Server;

/// <summary>
/// Crafting stations have no shared "made something" event, so each finish is patched. Every
/// patch is a postfix (with a prefix where the method clears what it made) that reports to the
/// engine on the server only: in singleplayer both sides run the same patched methods.
/// docs/phase1-findings.md has the survey these targets came from.
/// </summary>
public static class StationPatches
{
    public const string HarmonyId = "com.dizzyd.questline";

    static QuestEngine engine;

    /// <summary>
    /// Attaches each patch on its own, so one station whose method moved in a game update costs
    /// only that station's tasks, with a warning saying so, not the whole mod.
    /// </summary>
    public static Harmony Apply(QuestEngine questEngine, ILogger logger)
    {
        engine = questEngine;
        var harmony = new Harmony(HarmonyId);
        foreach (var patch in typeof(StationPatches).GetNestedTypes(BindingFlags.NonPublic).Where(t => t.IsDefined(typeof(HarmonyPatch))))
        {
            try
            {
                harmony.CreateClassProcessor(patch).Patch();
            }
            catch (Exception e)
            {
                logger.Warning("[questline] couldn't attach the {0} patch, so its tasks won't count: {1}", patch.Name, e.InnerException?.Message ?? e.Message);
            }
        }
        return harmony;
    }

    /// <summary>Every method carrying one of these patches, with how many of each kind are ours.</summary>
    public static List<(string Method, int Prefixes, int Postfixes)> Attached() => Harmony.GetAllPatchedMethods()
        .Select(m => (Method: m, Info: Harmony.GetPatchInfo(m)))
        .Select(p => ($"{p.Method.DeclaringType?.Name}.{p.Method.Name}",
            p.Info.Prefixes.Count(x => x.owner == HarmonyId), p.Info.Postfixes.Count(x => x.owner == HarmonyId)))
        .Where(p => p.Item2 + p.Item3 > 0)
        .ToList();

    static void Credit(IPlayer player, string type, ItemStack made, int? count = null)
    {
        if (player is not IServerPlayer serverPlayer || made?.Collectible == null) return;
        engine?.Report(serverPlayer, type, made.Collectible.Code, count ?? made.StackSize);
    }

    static bool OnServer(BlockEntity be) => be.Api?.Side == EnumAppSide.Server;

    /// <summary>
    /// Every grid craft, one call per batch: single clicks, shift-click runs, flips and leftovers
    /// all consume ingredients here. The game's own onitemcrafted event reports only the last
    /// batch of a shift-click. The method returns the recipe from 1.22.7 but nothing before, so
    /// the prefix reads the recipe it is about to consume instead.
    /// </summary>
    [HarmonyPatch]
    static class GridCraft
    {
        static readonly Type GridType = AccessTools.TypeByName("Vintagestory.Common.InventoryCraftingGrid");
        static readonly FieldInfo MatchingRecipe = AccessTools.Field(GridType, "MatchingRecipe");

        static MethodBase TargetMethod() => AccessTools.Method(GridType, "ConsumeIngredients");

        static void Prefix(InventoryBasePlayer __instance, ItemSlot outputSlot, out GridRecipe __state)
        {
            __state = __instance.Api?.Side == EnumAppSide.Server && outputSlot?.Itemstack != null
                ? MatchingRecipe.GetValue(__instance) as GridRecipe
                : null;
        }

        static void Postfix(InventoryBasePlayer __instance, GridRecipe __state)
        {
            if (__state != null) Credit(__instance.Player, "craft", __state.Output.ResolvedItemStack);
        }
    }

    // Anvil, knapping and clay forming all clear the selected recipe when the work is done, so
    // the prefix keeps it and the postfix credits only if it was cleared.

    [HarmonyPatch(typeof(BlockEntityAnvil), nameof(BlockEntityAnvil.CheckIfFinished))]
    static class Anvil
    {
        static void Prefix(BlockEntityAnvil __instance, out SmithingRecipe __state) => __state = __instance.SelectedRecipe;

        static void Postfix(BlockEntityAnvil __instance, IPlayer byPlayer, SmithingRecipe __state)
        {
            // A helve hammer finishes with no player, and nobody is credited.
            if (__state != null && __instance.SelectedRecipeId == -1 && OnServer(__instance)) Credit(byPlayer, "smith", __state.Output.ResolvedItemstack);
        }
    }

    [HarmonyPatch(typeof(BlockEntityKnappingSurface), nameof(BlockEntityKnappingSurface.CheckIfFinished))]
    static class Knapping
    {
        static void Prefix(BlockEntityKnappingSurface __instance, out KnappingRecipe __state) => __state = __instance.SelectedRecipe;

        static void Postfix(BlockEntityKnappingSurface __instance, IPlayer byPlayer, KnappingRecipe __state)
        {
            if (__state != null && __instance.SelectedRecipe == null && OnServer(__instance)) Credit(byPlayer, "knap", __state.Output.ResolvedItemstack);
        }
    }

    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.CheckIfFinished))]
    static class ClayForming
    {
        static void Prefix(BlockEntityClayForm __instance, out ClayFormingRecipe __state) => __state = __instance.SelectedRecipe;

        static void Postfix(BlockEntityClayForm __instance, IPlayer byPlayer, ClayFormingRecipe __state)
        {
            if (__state != null && __instance.SelectedRecipe == null && OnServer(__instance)) Credit(byPlayer, "clayform", __state.Output.ResolvedItemstack);
        }
    }

    /// <summary>Taking anything from a firepit's output slot, by click or shift-click: cooked food and smelted goods.</summary>
    [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.TryPutInto), [typeof(ItemSlot), typeof(ItemStackMoveOperation)], [ArgumentType.Normal, ArgumentType.Ref])]
    static class FirepitOutput
    {
        static void Prefix(ItemSlot __instance, out ItemStack __state) =>
            __state = __instance is ItemSlotOutput && __instance.Inventory is InventorySmelting { Api.Side: EnumAppSide.Server } ? __instance.Itemstack : null;

        static void Postfix(int __result, ref ItemStackMoveOperation op, ItemStack __state)
        {
            if (__state != null && __result > 0) Credit(op.ActingPlayer, "cook", __state, __result);
        }
    }

    /// <summary>A hardened tool or plate taken from a tool mold.</summary>
    [HarmonyPatch(typeof(BlockEntityToolMold), "TryTakeContents")]
    static class ToolMold
    {
        static void Prefix(BlockEntityToolMold __instance, out List<ItemStack> __state)
        {
            // Copied: the method hands these very stacks to the player, who may empty them.
            __state = OnServer(__instance) && __instance.IsFull && __instance.IsHardened && !__instance.Shattered
                ? __instance.GetStateAwareMoldedStacks()?.Select(s => s.Clone()).ToList()
                : null;
        }

        static void Postfix(BlockEntityToolMold __instance, IPlayer byPlayer, List<ItemStack> __state)
        {
            if (__state == null || __instance.FillLevel != 0) return;
            foreach (var stack in __state) Credit(byPlayer, "smelt", stack);
        }
    }

    /// <summary>An ingot taken from either side of an ingot mold.</summary>
    [HarmonyPatch(typeof(BlockEntityIngotMold), "TryTakeIngot")]
    static class IngotMold
    {
        static void Prefix(BlockEntityIngotMold __instance, out (ItemStack Left, ItemStack Right) __state)
        {
            __state = OnServer(__instance)
                ? (__instance.FillLevelLeft > 0 ? __instance.ContentsLeft?.Clone() : null, __instance.FillLevelRight > 0 ? __instance.ContentsRight?.Clone() : null)
                : (null, null);
        }

        static void Postfix(BlockEntityIngotMold __instance, IPlayer byPlayer, (ItemStack Left, ItemStack Right) __state)
        {
            if (__state.Left != null && __instance.FillLevelLeft == 0) Credit(byPlayer, "smelt", __state.Left, 1);
            if (__state.Right != null && __instance.FillLevelRight == 0) Credit(byPlayer, "smelt", __state.Right, 1);
        }
    }

    /// <summary>The bloom comes out of a bloomery only when the bloomery is broken.</summary>
    // What a player buys from a trader: the buying cart, read before the sale empties it.
    [HarmonyPatch(typeof(InventoryTrader), "TryBuySell")]
    static class TraderSale
    {
        static void Prefix(InventoryTrader __instance, out List<ItemStack> __state) =>
            __state = __instance.Api?.Side == EnumAppSide.Server
                ? Enumerable.Range(0, 4).Select(i => __instance.GetBuyingCartSlot(i).Itemstack?.Clone()).Where(s => s != null).ToList()
                : null;

        static void Postfix(IPlayer buyingPlayer, EnumTransactionResult __result, List<ItemStack> __state)
        {
            if (__result != EnumTransactionResult.Success || __state == null) return;
            foreach (var bought in __state) Credit(buyingPlayer, "trade", bought);
        }
    }

    [HarmonyPatch(typeof(BlockEntityBloomery), nameof(BlockEntityBloomery.OnBlockBroken))]
    static class Bloomery
    {
        static void Prefix(BlockEntityBloomery __instance, IPlayer byPlayer, InventoryGeneric ___bloomeryInv)
        {
            if (OnServer(__instance) && ___bloomeryInv?[2].Itemstack is { } bloom) Credit(byPlayer, "smelt", bloom);
        }
    }
}
