using System;
using System.Collections.ObjectModel;
using System.Windows;
using FlipSwitcher.Core;

namespace FlipSwitcher.Services;

public enum AppTheme
{
    Dark = 0,
    Light = 1,
    Latte = 2,
    Mocha = 3
}

public class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    private const string FluentColorsDark = "pack://application:,,,/Themes/FluentColors.xaml";
    private const string FluentColorsLight = "pack://application:,,,/Themes/FluentColors.Light.xaml";
    private const string FluentColorsLatte = "pack://application:,,,/Themes/FluentColors.Latte.xaml";
    private const string FluentColorsMocha = "pack://application:,,,/Themes/FluentColors.Mocha.xaml";
    private const string FluentColorsName = "FluentColors";
    private const int DarkModeEnabled = 1;
    private const int DarkModeDisabled = 0;

    private ThemeService()
    {
    }

    private bool IsDarkTheme(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        AppTheme.Latte => false,
        AppTheme.Mocha => true,
        _ => true
    };

    private string GetThemeUri(AppTheme theme) => theme switch
    {
        AppTheme.Dark => FluentColorsDark,
        AppTheme.Light => FluentColorsLight,
        AppTheme.Latte => FluentColorsLatte,
        AppTheme.Mocha => FluentColorsMocha,
        _ => FluentColorsDark
    };

    private void RemoveColorDictionaries(Collection<ResourceDictionary> dictionaries)
    {
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            var sourceStr = dictionaries[i].Source?.OriginalString;
            if (sourceStr != null && 
                (sourceStr.Contains(FluentColorsName) || 
                 sourceStr.EndsWith("/FluentColors") ||
                 sourceStr.EndsWith("/FluentColors.Light") ||
                 sourceStr.EndsWith("/FluentColors.Latte") ||
                 sourceStr.EndsWith("/FluentColors.Mocha")))
            {
                dictionaries.RemoveAt(i);
            }
        }
    }

    public void ApplyTheme(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        bool isDark = IsDarkTheme(theme);
        var dictionaries = app.Resources.MergedDictionaries;

        RemoveColorDictionaries(dictionaries);

        var colorDict = new ResourceDictionary
        {
            Source = new Uri(GetThemeUri(theme), UriKind.Absolute)
        };
        dictionaries.Insert(0, colorDict);

        UpdateWindowThemes(isDark);
    }

    private void UpdateWindowThemes(bool isDark)
    {
        foreach (Window window in Application.Current.Windows)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int darkMode = isDark ? DarkModeEnabled : DarkModeDisabled;
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
            }
            catch
            {
                // Ignore errors
            }
        }
    }

    public void Dispose()
    {
        // No resources to dispose
    }
}

