using System;

namespace FlipSwitcher.Services;

/// <summary>
/// Computed layout parameters for one rendering of the window grid.
/// Pure value math with no WPF dependencies so the test project can compile-link this file.
/// </summary>
public readonly record struct GridLayout(int Columns, double PopupWidth, double PopupHeight)
{
    /// <summary>
    /// Fixed height budget for one row of compact icon+title cards (36px tile + margins ≈
    /// two-line row). Keep in sync with WindowCard.xaml.
    /// </summary>
    public const double CardRowHeight = 64;

    /// <summary>
    /// Estimated vertical chrome around the grid in MainWindow: header, search box, footer
    /// and grid margin, slightly over-estimated so the popup is never too small.
    /// </summary>
    public const double ChromeHeight = 200;
}

/// <summary>
/// Maps window count to grid density per the approved spec (§4.1):
/// ≤3 → count columns, 4–9 → 3, 10–19 → 4, ≥20 → 5,
/// popup clamped to [640×520, work area × 0.8].
/// </summary>
public static class GridDensity
{
    public const double MaxWorkAreaFraction = 0.8;
    public const double MinPopupWidth = 640;
    public const double MinPopupHeight = 520;
    public const double CardMinWidth = 180;

    /// <summary>Max card width when the grid has 1-2 columns, so cards don't stretch full-width.</summary>
    public const double LowDensityCardMaxWidth = 240;

    public static GridLayout Calculate(int windowCount, double workAreaWidth, double workAreaHeight)
    {
        int count = Math.Max(windowCount, 0);

        int columns = count switch
        {
            0 => 3,
            <= 3 => count,
            <= 9 => 3,
            <= 19 => 4,
            _ => 5
        };

        double maxWidth = Math.Max(workAreaWidth * MaxWorkAreaFraction, MinPopupWidth);
        double popupWidth = Math.Clamp(columns * CardMinWidth, MinPopupWidth, maxWidth);

        int rows = count == 0 ? 1 : (int)Math.Ceiling(count / (double)columns);
        double maxHeight = Math.Max(workAreaHeight * MaxWorkAreaFraction, MinPopupHeight);
        double popupHeight = Math.Clamp(rows * GridLayout.CardRowHeight + GridLayout.ChromeHeight, MinPopupHeight, maxHeight);

        return new GridLayout(columns, popupWidth, popupHeight);
    }
}
