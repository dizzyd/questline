using questline.Model;
using Vintagestory.API.Client;
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
    public NodeShape Shape;
    /// <summary>The frame's size, 1 being normal.</summary>
    public float Size = 1;
    public int[] Prereqs = [];
    /// <summary>The editor found something wrong with it; its label is drawn in the problem colour.</summary>
    public bool Problem;
    /// <summary>
    /// A prerequisite from another chapter, drawn small beside what it unlocks. It has no label;
    /// with the mouse over it, its title and <see cref="Note"/> show beside the pointer.
    /// It never has an age badge.
    /// </summary>
    public bool Stub;
    /// <summary>A stub's chapter, under its title.</summary>
    public string Note;
    /// <summary>
    /// The age the quest comes from, when that's later than the rest of its chapter: its icon is
    /// drawn small at the frame's corner, and its name shows with the mouse over it.
    /// </summary>
    public ItemSlot AgeIcon;
    public string AgeTitle;
}

/// <summary>One piece of chapter art, resolved for drawing. Positions are grid cells.</summary>
public class CanvasArt
{
    public string Type;
    public float X, Y;
    /// <summary>arrow: where it points.</summary>
    public float ToX, ToY;
    /// <summary>image: size in grid cells.</summary>
    public float Width = 1, Height = 1;
    /// <summary>image: the texture asset.</summary>
    public AssetLocation Src;
    /// <summary>text: what it says.</summary>
    public string Text;
    public float Scale = 1;
    /// <summary>arrow: the mesh colour.</summary>
    public int Color;
    /// <summary>text: RGBA from 0 to 1.</summary>
    public double[] TextColor = [1, 1, 1, 1];
    /// <summary>arrow: line width in canvas units.</summary>
    public float LineWidth = 3;

    internal LoadedTexture Texture;
}
