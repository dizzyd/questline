using questline.Model;
using Vintagestory.API.Common;

namespace questline.Client;

/// <summary>
/// One quest as the canvas sees it: a position on the chapter grid, an icon and a state.
/// Positions are in grid cells; the canvas decides how big a cell is.
/// </summary>
public class QuestCanvasNode
{
    public string Id;
    public string Title;
    public float X, Y;
    public ItemSlot Icon;
    public QuestState State;
    public int[] Prereqs = [];
}
