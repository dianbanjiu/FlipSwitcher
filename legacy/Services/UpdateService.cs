using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipSwitcher.Views;

namespace FlipSwitcher.Services;

public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
}

public class UpdateService : IDisposable
{
    private static UpdateService? _instance;
    public static UpdateService Instance => _instance ??= new UpdateService();

    private const string GitHubApiUrl = "https://api.github.com/repos/dianbanjiu/FlipSwitcher/releases/latest";
    private const string GitHubReleasesUrl = "https://github.com/dianbanjiu/FlipSwitcher/releases/latest";
    private const string UserAgent = "FlipSwitcher";
    private const string SetupExeSuffix = "-Setup.exe";
    private const string TagNameProperty = "tag_name";
    private const string AssetsProperty = "assets";
    private const string BrowserDownloadUrlProperty = "browser_download_url";
    private const string BodyProperty = "body";
    private const string PublishedAtProperty = "published_at";
    private const char VersionPrefix = 'v';

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public Version GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version ?? new Version(0, 0, 0);
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool silent = false)
    {
        if (!await _checkLock.WaitAsync(0))
            return null;

        try
        {
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            var release = JsonSerializer.Deserialize<JsonElement>(response);

            if (!release.TryGetProperty(TagNameProperty, out var tagName))
                return null;

            var tagNameStr = tagName.GetString();
            if (string.IsNullOrEmpty(tagNameStr))
                return null;

            var versionStr = tagNameStr.TrimStart(VersionPrefix);
            if (string.IsNullOrEmpty(versionStr) || !Version.TryParse(versionStr, out var latestVersion))
                return null;

            if (latestVersion <= GetCurrentVersion())
                return null;

            var downloadUrl = FindSetupDownloadUrl(release, versionStr) ?? GitHubReleasesUrl;
            var releaseNotes = GetReleaseNotes(release);
            var publishedAt = GetPublishedDate(release);

            return new UpdateInfo
            {
                Version = tagNameStr,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes,
                PublishedAt = publishedAt
            };
        }
        catch
        {
            if (!silent)
            {
                ShowUpdateCheckFailedMessage();
            }
            return null;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private string? FindSetupDownloadUrl(JsonElement release, string version)
    {
        if (!release.TryGetProperty(AssetsProperty, out var assets) || assets.GetArrayLength() == 0)
            return null;

        // Prefer versioned installer
        var versionedFileName = $"FlipSwitcher-{version}-windows-x64-Setup.exe";
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty(BrowserDownloadUrlProperty, out var url))
            {
                var urlStr = url.GetString();
                if (!string.IsNullOrEmpty(urlStr) && urlStr.EndsWith(versionedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return urlStr;
                }
            }
        }

        // Fall back to generic installer
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty(BrowserDownloadUrlProperty, out var url))
            {
                var urlStr = url.GetString();
                if (!string.IsNullOrEmpty(urlStr) && urlStr.EndsWith(SetupExeSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return urlStr;
                }
            }
        }
        return null;
    }

    private string GetReleaseNotes(JsonElement release)
    {
        return release.TryGetProperty(BodyProperty, out var body)
            ? body.GetString() ?? string.Empty
            : string.Empty;
    }

    private DateTime GetPublishedDate(JsonElement release)
    {
        if (release.TryGetProperty(PublishedAtProperty, out var published))
        {
            var dateStr = published.GetString();
            if (dateStr != null && DateTime.TryParse(dateStr, out var date))
            {
                return date;
            }
        }
        return DateTime.UtcNow;
    }

    private void ShowUpdateCheckFailedMessage()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            FluentDialog.Show(
                LanguageService.GetString("MsgUpdateCheckFailed"),
                LanguageService.GetString("MsgUpdateCheckFailedTitle"),
                FluentDialogButton.OK,
                FluentDialogIcon.Warning);
        });
    }

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com"
    };

    public void OpenDownloadPage(string? url = null)
    {
        var targetUrl = url ?? GitHubReleasesUrl;

        // Validate URL is HTTPS and belongs to trusted domains to prevent opening malicious links from tampered API responses
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            AllowedHosts.Contains(uri.Host))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetUrl,
                UseShellExecute = true
            });
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _checkLock.Dispose();
    }
}

