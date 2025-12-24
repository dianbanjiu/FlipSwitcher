using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace FlipSwitcher.Services;

/// <summary>
/// Supported languages
/// </summary>
public enum AppLanguage
{
    English,
    Chinese,
    ChineseTraditional
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
    private const string ChineseTraditionalResourcePath = "Resources/Strings.zh-TW.xaml";

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

    private const string StringsResourceKey = "Strings";

    private string GetResourcePath(AppLanguage language) => language switch
    {
        AppLanguage.Chinese => ChineseResourcePath,
        AppLanguage.ChineseTraditional => ChineseTraditionalResourcePath,
        _ => EnglishResourcePath
    };

    private ResourceDictionary? FindStringsDictionary(Collection<ResourceDictionary> dictionaries)
    {
        foreach (var dict in dictionaries)
        {
            if (dict.Source?.OriginalString.Contains(StringsResourceKey) == true)
            {
                return dict;
            }
        }
        return null;
    }

    /// <summary>
    /// Set the application language
    /// </summary>
    public void SetLanguage(AppLanguage language, bool raiseEvent = true)
    {
        CurrentLanguage = language;

        var resourcePath = GetResourcePath(language);
        var newDict = new ResourceDictionary
        {
            Source = new Uri(resourcePath, UriKind.Relative)
        };

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        var existingDict = FindStringsDictionary(dictionaries);

        if (existingDict != null)
        {
            var index = dictionaries.IndexOf(existingDict);
            dictionaries.RemoveAt(index);
            dictionaries.Insert(index, newDict);
        }
        else
        {
            dictionaries.Add(newDict);
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

