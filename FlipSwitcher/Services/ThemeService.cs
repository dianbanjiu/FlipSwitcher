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

    private bool _isFollowingSystemTheme = false;
    private System.Threading.Timer? _registryWatchTimer;
    private AppTheme _currentAppliedTheme = AppTheme.Dark;
    private bool _isApplyingTheme = false;

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
        // Iterate backwards for safe removal
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            var dict = dictionaries[i];
            var sourceStr = dict.Source?.OriginalString;
            if (sourceStr != null && 
                (sourceStr.Contains(FluentColorsName) || 
                 sourceStr.Contains(FluentStylesName)))
            {
                dictionaries.RemoveAt(i);
                
                // Explicitly clear ResourceDictionary to prevent memory leaks
                dict.Clear();
            }
        }
        
        // Suggest lightweight garbage collection if too many dictionaries accumulated
        if (dictionaries.Count > 20)
        {
            GC.Collect(0, GCCollectionMode.Optimized);
        }
    }

    public void ApplyTheme(AppTheme theme)
    {
        // Avoid repeatedly applying the same theme to prevent memory leaks
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
        
        // Reduced polling frequency to every 3 seconds to minimize performance overhead
        _registryWatchTimer = new System.Threading.Timer(
            _ => CheckSystemThemeChanged(),
            null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3)
        );
    }

    public void StopFollowingSystemTheme()
    {
        _isFollowingSystemTheme = false;
        
        // Ensure the timer is properly disposed
        if (_registryWatchTimer != null)
        {
            _registryWatchTimer.Dispose();
            _registryWatchTimer = null;
        }
        
        _lastSystemThemeValue = null;
    }

    private int? _lastSystemThemeValue = null;

    private void CheckSystemThemeChanged()
    {
        // Double check to ensure no execution after timer is stopped
        if (!_isFollowingSystemTheme || _registryWatchTimer == null) return;

        var currentValue = GetSystemThemeValue();
        if (currentValue != _lastSystemThemeValue)
        {
            _lastSystemThemeValue = currentValue;
            
            // Avoid accumulating too many calls in the Dispatcher queue
            var app = Application.Current;
            if (app != null && !_isApplyingTheme)
            {
                app.Dispatcher.BeginInvoke(() => 
                {
                    if (_isFollowingSystemTheme) // Check state again
                    {
                        ApplySystemTheme();
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
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

