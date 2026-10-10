using Vintagestory.API.Common;

namespace questline.Client;

public enum QuestNodeState
{
    Locked,
    Available,
    InProgress,
    Claimable,
    Done
}

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
    public QuestNodeState State;
    public int[] Prereqs = [];
}
