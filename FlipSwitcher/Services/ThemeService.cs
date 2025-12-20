using System;
using System.Windows;
using FlipSwitcher.Core;

namespace FlipSwitcher.Services;

public enum AppTheme
{
    Dark = 0,
    Light = 1
}

public class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    private ThemeService()
    {
    }

    public void ApplyTheme(AppTheme theme)
    {
        bool isDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => true
        };

        var app = Application.Current;
        if (app == null) return;

        var resources = app.Resources;
        var mergedDictionaries = resources.MergedDictionaries;

        // 移除现有的颜色资源
        for (int i = mergedDictionaries.Count - 1; i >= 0; i--)
        {
            var dict = mergedDictionaries[i];
            if (dict.Source != null)
            {
                var sourceStr = dict.Source.OriginalString;
                if (sourceStr.Contains("FluentColors.xaml") || 
                    sourceStr.Contains("FluentColors.Light.xaml") ||
                    sourceStr.EndsWith("/FluentColors") ||
                    sourceStr.EndsWith("/FluentColors.Light"))
                {
                    mergedDictionaries.RemoveAt(i);
                }
            }
        }

        // 添加新的颜色资源
        var colorDict = new ResourceDictionary();
        if (isDark)
        {
            colorDict.Source = new Uri("pack://application:,,,/Themes/FluentColors.xaml", UriKind.Absolute);
        }
        else
        {
            colorDict.Source = new Uri("pack://application:,,,/Themes/FluentColors.Light.xaml", UriKind.Absolute);
        }
        mergedDictionaries.Insert(0, colorDict);

        // 更新所有窗口的 DWM 主题
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
                    int darkMode = isDark ? 1 : 0;
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

