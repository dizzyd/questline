using System;
using System.Collections.Generic;
using System.Linq;
using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace questline.Client;

/// <summary>
/// A dummy chapter for the rendering spike: columns of quests, each depending on one to
/// three quests in the column before, with real item icons. Seeded, so every run draws
/// the same tree.
/// </summary>
public static class SpikeTree
{
    public static List<QuestCanvasNode> Build(ICoreClientAPI capi, int columns = 25, int rows = 20, int seed = 42)
    {
        var rand = new Random(seed);
        var icons = capi.World.Collectibles
            .Where(c => c.Code != null && c.CreativeInventoryTabs?.Length > 0)
            .ToArray();

        var nodes = new List<QuestCanvasNode>(columns * rows);
        for (int c = 0; c < columns; c++)
        {
            for (int r = 0; r < rows; r++)
            {
                var item = icons[rand.Next(icons.Length)];
                var node = new QuestCanvasNode
                {
                    Id = $"spike:c{c}/r{r}",
                    Title = item.GetHeldItemName(new ItemStack(item)),
                    X = c * 2.2f,
                    Y = r * 1.6f + (c % 2) * 0.4f,
                    Icon = new DummySlot(new ItemStack(item)),
                };

                if (c > 0)
                {
                    int prereqs = 1 + rand.Next(3);
                    node.Prereqs = Enumerable.Range(0, prereqs)
                        .Select(_ => (c - 1) * rows + Math.Clamp(r + rand.Next(-3, 4), 0, rows - 1))
                        .Distinct()
                        .ToArray();
                }

                node.State = c < 4 ? QuestState.Done
                    : node.Prereqs.All(p => nodes[p].State == QuestState.Done)
                        ? (QuestState)rand.Next((int)QuestState.Available, (int)QuestState.Done)
                        : QuestState.Locked;

                nodes.Add(node);
            }
        }

        return nodes;
    }
}
