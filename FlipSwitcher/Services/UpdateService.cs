using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

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
    private readonly HttpClient _httpClient;
    private bool _isChecking;

    private UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "FlipSwitcher");
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

            if (!release.TryGetProperty("tag_name", out var tagName))
                return null;

            var tagNameStr = tagName.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(tagNameStr))
                return null;

            var versionStr = tagNameStr.TrimStart('v');
            if (string.IsNullOrEmpty(versionStr))
                return null;

            if (!Version.TryParse(versionStr, out var latestVersion))
                return null;

            var currentVersion = GetCurrentVersion();
            if (latestVersion <= currentVersion)
                return null;

            var downloadUrl = GitHubReleasesUrl;
            if (release.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var url))
                    {
                        var urlStr = url.GetString() ?? string.Empty;
                        if (urlStr.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = urlStr;
                            break;
                        }
                    }
                }
            }

            var releaseNotes = string.Empty;
            if (release.TryGetProperty("body", out var body))
            {
                releaseNotes = body.GetString() ?? string.Empty;
            }

            var publishedAt = DateTime.UtcNow;
            if (release.TryGetProperty("published_at", out var published))
            {
                if (DateTime.TryParse(published.GetString(), out var date))
                    publishedAt = date;
            }

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
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        LanguageService.GetString("MsgUpdateCheckFailed"),
                        LanguageService.GetString("MsgUpdateCheckFailedTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
            return null;
        }
        finally
        {
            _isChecking = false;
        }
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

