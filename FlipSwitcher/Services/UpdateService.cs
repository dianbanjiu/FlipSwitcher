using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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

public class UpdateService
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
    private bool _isChecking;

    private UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public Version GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version ?? new Version(0, 0, 0);
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool silent = false)
    {
        if (_isChecking)
            return null;

        _isChecking = true;
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

            var downloadUrl = FindSetupDownloadUrl(release) ?? GitHubReleasesUrl;
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
            _isChecking = false;
        }
    }

    private string? FindSetupDownloadUrl(JsonElement release)
    {
        if (!release.TryGetProperty(AssetsProperty, out var assets) || assets.GetArrayLength() == 0)
            return null;

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

    public void OpenDownloadPage(string? url = null)
    {
        var targetUrl = url ?? GitHubReleasesUrl;
        Process.Start(new ProcessStartInfo
        {
            FileName = targetUrl,
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

