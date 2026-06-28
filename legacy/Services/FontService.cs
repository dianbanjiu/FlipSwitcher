using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace FlipSwitcher.Services;

public class FontService
{
    private static FontService? _instance;
    public static FontService Instance => _instance ??= new FontService();

    private const string DefaultFontFamily = "Segoe UI Variable, Segoe UI, sans-serif";
    private const string SegoeUIVariableKey = "SegoeUIVariable";

    private FontService()
    {
    }

    /// <summary>
    /// Get the list of installed system fonts
    /// </summary>
    public List<string> GetInstalledFonts()
    {
        return Fonts.SystemFontFamilies
            .OrderBy(f => f.Source)
            .Select(f => f.Source)
            .ToList();
    }

    /// <summary>
    /// Apply font to the application via app-level resources with automatic style inheritance
    /// </summary>
    public void ApplyFont(string fontFamilyName)
    {
        var app = Application.Current;
        if (app == null) return;

        var fontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(fontFamilyName) ? DefaultFontFamily : fontFamilyName);

        app.Dispatcher.Invoke(() =>
        {
            var resources = app.Resources;
            if (resources.Contains(SegoeUIVariableKey))
                resources[SegoeUIVariableKey] = fontFamily;
            else
                resources.Add(SegoeUIVariableKey, fontFamily);

            // Set Window.FontFamily to trigger inheritance without traversing the visual tree
            foreach (Window window in app.Windows)
            {
                if (window?.IsLoaded == true)
                    window.FontFamily = fontFamily;
            }
        });
    }
}

