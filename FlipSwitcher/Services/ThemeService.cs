using System;
using System.Collections.ObjectModel;
using System.Windows;
using FlipSwitcher.Core;
using Microsoft.Win32;

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
    private const string FluentStyles = "pack://application:,,,/Themes/FluentStyles.xaml";
    private const string FluentColorsName = "FluentColors";
    private const string FluentStylesName = "FluentStyles";
    private const int DarkModeEnabled = 1;
    private const int DarkModeDisabled = 0;

    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValueName = "AppsUseLightTheme";

    private volatile bool _isFollowingSystemTheme = false;
    private AppTheme? _currentAppliedTheme = null; // null means not yet initialized
    private volatile bool _isApplyingTheme = false;

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

    private void RemoveThemeDictionaries(Collection<ResourceDictionary> dictionaries)
    {
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            var dict = dictionaries[i];
            var sourceStr = dict.Source?.OriginalString;
            if (sourceStr != null && 
                (sourceStr.Contains(FluentColorsName) || 
                 sourceStr.Contains(FluentStylesName)))
            {
                dictionaries.RemoveAt(i);
                dict.Clear();
            }
        }
    }

    public void ApplyTheme(AppTheme theme)
    {
        // Skip if the same theme is already applied (first time _currentAppliedTheme is null, forcing execution)
        if (_currentAppliedTheme == theme && !_isApplyingTheme)
            return;

        _isApplyingTheme = true;
        try
        {
            var app = Application.Current;
            if (app == null) return;

            bool isDark = IsDarkTheme(theme);
            var dictionaries = app.Resources.MergedDictionaries;

            RemoveThemeDictionaries(dictionaries);

            // Load color resources first, then styles (styles depend on colors)
            var colorDict = new ResourceDictionary
            {
                Source = new Uri(GetThemeUri(theme), UriKind.Absolute)
            };
            dictionaries.Insert(0, colorDict);

            var stylesDict = new ResourceDictionary
            {
                Source = new Uri(FluentStyles, UriKind.Absolute)
            };
            dictionaries.Insert(1, stylesDict);

            UpdateWindowThemes(isDark);
            _currentAppliedTheme = theme;
        }
        finally
        {
            _isApplyingTheme = false;
        }
    }

    public void StartFollowingSystemTheme()
    {
        if (_isFollowingSystemTheme) return;

        _isFollowingSystemTheme = true;
        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void StopFollowingSystemTheme()
    {
        _isFollowingSystemTheme = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (!_isFollowingSystemTheme || _isApplyingTheme) return;

        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            if (_isFollowingSystemTheme)
                ApplySystemTheme();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ApplySystemTheme()
    {
        var themeValue = GetSystemThemeValue();
        bool isLightTheme = themeValue == 1;
        AppTheme theme = isLightTheme ? AppTheme.Light : AppTheme.Dark;
        ApplyTheme(theme);
    }

    private int? GetSystemThemeValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(RegistryValueName) as int?;
        }
        catch
        {
            return null;
        }
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
        StopFollowingSystemTheme();
    }
}

