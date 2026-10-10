using questline.Model;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace questline.Client;

/// <summary>
/// The book's type scale and colours, in one place so every panel agrees. Sizes are the
/// game's unscaled font sizes; colours are RGBA from 0 to 1, on the game's brown dialog.
/// </summary>
public static class BookStyle
{
    /// <summary>Space between the book's columns, and inside panels.</summary>
    public const double Gap = 12, Pad = 10;
    /// <summary>The height of the row of buttons under a panel.</summary>
    public const double ButtonHeight = 28;

    public static readonly double[] Text = GuiStyle.DialogDefaultTextColor;
    public static readonly double[] Muted = ColorUtil.Hex2Doubles("#b4a690");
    public static readonly double[] Accent = GuiStyle.ActiveButtonTextColor;
    public static readonly double[] Good = [0.50, 0.82, 0.48, 1];

    /// <summary>A quest's name at the top of the detail panel.</summary>
    public static CairoFont Title() => CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold);

    public static CairoFont Body() => CairoFont.WhiteSmallText();

    public static CairoFont Small() => CairoFont.WhiteDetailText();

    public static CairoFont MutedSmall() => CairoFont.WhiteDetailText().WithColor(Muted);

    /// <summary>TASKS, REWARDS and the like.</summary>
    public static CairoFont Heading() => CairoFont.WhiteDetailText().WithFontSize(13).WithWeight(Cairo.FontWeight.Bold).WithColor(Muted);

    /// <summary>An action in running text, in the game's button-text accent.</summary>
    public static CairoFont Link() => CairoFont.WhiteDetailText().WithColor(Accent);

    public static double[] StateColor(QuestState state) => state switch
    {
        QuestState.Done => Good,
        QuestState.Claimable => [1.0, 0.8, 0.25, 1],
        QuestState.InProgress => [0.55, 0.72, 1.0, 1],
        QuestState.Available => [0.9, 0.85, 0.7, 1],
        _ => [0.62, 0.6, 0.57, 1],
    };
}
