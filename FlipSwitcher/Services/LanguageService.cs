using System;
using System.Windows;

namespace FlipSwitcher.Services;

/// <summary>
/// Supported languages
/// </summary>
public enum AppLanguage
{
    English,
    Chinese
}

/// <summary>
/// Service for managing application language/localization
/// </summary>
public class LanguageService
{
    private static LanguageService? _instance;
    public static LanguageService Instance => _instance ??= new LanguageService();

    private const string EnglishResourcePath = "Resources/Strings.xaml";
    private const string ChineseResourcePath = "Resources/Strings.zh-CN.xaml";

    public event EventHandler? LanguageChanged;

    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    private LanguageService()
    {
    }

    /// <summary>
    /// Initialize language from settings
    /// </summary>
    public void Initialize()
    {
        var language = (AppLanguage)SettingsService.Instance.Settings.Language;
        SetLanguage(language, raiseEvent: false);
    }

    /// <summary>
    /// Set the application language
    /// </summary>
    public void SetLanguage(AppLanguage language, bool raiseEvent = true)
    {
        CurrentLanguage = language;

        var resourcePath = language switch
        {
            AppLanguage.Chinese => ChineseResourcePath,
            _ => EnglishResourcePath
        };

        var newDict = new ResourceDictionary
        {
            Source = new Uri(resourcePath, UriKind.Relative)
        };

        // Find and replace the existing string resource dictionary
        var app = Application.Current;
        ResourceDictionary? existingDict = null;

        foreach (var dict in app.Resources.MergedDictionaries)
        {
            if (dict.Source != null && dict.Source.OriginalString.Contains("Strings"))
            {
                existingDict = dict;
                break;
            }
        }

        if (existingDict != null)
        {
            var index = app.Resources.MergedDictionaries.IndexOf(existingDict);
            app.Resources.MergedDictionaries.RemoveAt(index);
            app.Resources.MergedDictionaries.Insert(index, newDict);
        }
        else
        {
            app.Resources.MergedDictionaries.Add(newDict);
        }

        if (raiseEvent)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Get a localized string by key
    /// </summary>
    public static string GetString(string key)
    {
        if (Application.Current.TryFindResource(key) is string value)
        {
            return value;
        }
        return key;
    }
}

