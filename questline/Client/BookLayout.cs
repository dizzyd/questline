using System;
using Vintagestory.API.Client;

namespace questline.Client;

/// <summary>
/// How big the book is, and how its width splits into columns, for a screen of a given size.
/// All values are unscaled GUI units, as the dialog's bounds take them.
///
/// The book aims for <see cref="ScreenShare"/> of the screen each way. On a small screen that
/// would leave too little room, so it never goes below <see cref="MinWidth"/> by
/// <see cref="MinHeight"/> unless the screen itself is smaller, and it always leaves a margin.
/// The chapter rail and the detail panel take a share of the width within limits, so text in
/// them stays readable; the tree canvas gets the rest.
/// </summary>
public readonly record struct BookLayout(double Width, double ContentHeight, double RailWidth, double DetailWidth, double CanvasWidth)
{
    public const double ScreenShare = 0.8;
    public const double MinWidth = 900, MinHeight = 560;
    /// <summary>Kept clear around the dialog, so it never touches the screen edge.</summary>
    const double Margin = 20;

    /// <param name="frameWidth">The window in pixels.</param>
    /// <param name="frameHeight">The window in pixels.</param>
    /// <param name="guiScale">The player's GUI scale.</param>
    /// <param name="chromeHeight">What sits above the content inside the dialog: the tabs.</param>
    public static BookLayout For(double frameWidth, double frameHeight, double guiScale, double chromeHeight)
    {
        double pad = 2 * GuiStyle.ElementToDialogPadding;
        double screenW = frameWidth / guiScale, screenH = frameHeight / guiScale;
        double roomW = screenW - pad - 2 * Margin, roomH = screenH - pad - GuiStyle.TitleBarHeight - 2 * Margin;

        double width = Math.Clamp(screenW * ScreenShare - pad, Math.Min(MinWidth, roomW), roomW);
        double height = Math.Clamp(screenH * ScreenShare - pad - GuiStyle.TitleBarHeight, Math.Min(MinHeight, roomH), roomH);

        double gap = BookStyle.Gap;
        double rail = Math.Clamp(width * 0.17, 170, 260);
        double detail = Math.Clamp(width * 0.28, 260, 440);
        double canvas = width - rail - detail - 2 * gap;
        return new BookLayout(width, height - chromeHeight, rail, detail, canvas);
    }
}
