using System;
using System.IO;
using System.Text.Json;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for managing application settings
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlipSwitcher",
        "settings.json");

    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    public AppSettings Settings { get; private set; }

    public event EventHandler? SettingsChanged;

    private SettingsService()
    {
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ignore errors, use defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Ignore save errors
        }
    }
}

/// <summary>
/// Application settings model
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Use Alt+Tab as the activation hotkey (replaces system Alt+Tab)
    /// </summary>
    public bool UseAltTab { get; set; } = true;

    /// <summary>
    /// Use Alt+Space as the activation hotkey
    /// </summary>
    public bool UseAltSpace { get; set; } = false;

    /// <summary>
    /// Start with Windows
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Hide window when focus is lost
    /// </summary>
    public bool HideOnFocusLost { get; set; } = true;

    /// <summary>
    /// Enable Mica effect (Windows 11)
    /// </summary>
    public bool EnableMicaEffect { get; set; } = true;

    /// <summary>
    /// Theme: 0 = Dark, 1 = Light
    /// </summary>
    public int Theme { get; set; } = 0;

    /// <summary>
    /// Run as Administrator (requires restart)
    /// </summary>
    public bool RunAsAdmin { get; set; } = false;

    /// <summary>
    /// Language: 0 = English, 1 = Chinese, 2 = ChineseTraditional
    /// </summary>
    public int Language { get; set; } = 0;

    /// <summary>
    /// Check for updates automatically on startup
    /// </summary>
    public bool CheckForUpdates { get; set; } = false;

    /// <summary>
    /// Font family name (empty string means use default)
    /// </summary>
    public string FontFamily { get; set; } = string.Empty;

    /// <summary>
    /// Enable pinyin search for Chinese characters
    /// </summary>
    public bool EnablePinyinSearch { get; set; } = false;
}

