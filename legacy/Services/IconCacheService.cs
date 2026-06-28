using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FlipSwitcher.Core;

namespace FlipSwitcher.Services;

/// <summary>
/// Global cache for window/process icons. The cache is keyed by a stable identifier
/// (process executable path, UWP package directory or per-window override key) so that
/// repeatedly opening the switcher doesn't re-extract the same icon dozens of times.
/// </summary>
/// <remarks>
/// Cached <see cref="ImageSource"/> instances are always frozen, which makes them safe
/// to share across threads and across <see cref="Models.AppWindow"/> instances.
/// </remarks>
public class IconCacheService
{
    private static IconCacheService? _instance;
    public static IconCacheService Instance => _instance ??= new IconCacheService();

    // exePath / package dir → ImageSource
    private readonly ConcurrentDictionary<string, ImageSource> _exeIconCache =
        new(StringComparer.OrdinalIgnoreCase);

    // hwnd-derived key (used when WM_GETICON returns a per-window icon distinct from the exe icon)
    private readonly ConcurrentDictionary<string, ImageSource> _windowIconCache =
        new(StringComparer.Ordinal);

    // exePath → resolved AppxManifest icon path (avoid re-parsing manifest + 11x File.Exists probes)
    private readonly ConcurrentDictionary<string, string?> _appxLogoCache =
        new(StringComparer.OrdinalIgnoreCase);

    // exePath → process executable path resolved via OpenProcess (cached for lifetime of session)
    private readonly ConcurrentDictionary<uint, string?> _processPathCache = new();

    private IconCacheService() { }

    /// <summary>
    /// Try to get a cached icon for an executable path.
    /// </summary>
    public bool TryGetExeIcon(string exePath, out ImageSource? icon)
    {
        if (_exeIconCache.TryGetValue(exePath, out var cached))
        {
            icon = cached;
            return true;
        }
        icon = null;
        return false;
    }

    public void SetExeIcon(string exePath, ImageSource icon)
    {
        if (string.IsNullOrEmpty(exePath) || icon == null) return;
        _exeIconCache[exePath] = icon;
    }

    public bool TryGetWindowIcon(string key, out ImageSource? icon)
    {
        if (_windowIconCache.TryGetValue(key, out var cached))
        {
            icon = cached;
            return true;
        }
        icon = null;
        return false;
    }

    public void SetWindowIcon(string key, ImageSource icon)
    {
        if (string.IsNullOrEmpty(key) || icon == null) return;
        _windowIconCache[key] = icon;
    }

    /// <summary>
    /// Resolve and cache the executable path for a process id.
    /// </summary>
    public string? GetProcessPath(uint processId)
    {
        if (_processPathCache.TryGetValue(processId, out var cached))
            return cached;

        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            _processPathCache[processId] = null;
            return null;
        }
        try
        {
            var buffer = new StringBuilder(260);
            int size = buffer.Capacity;
            var path = NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
            _processPathCache[processId] = path;
            return path;
        }
        finally { NativeMethods.CloseHandle(hProcess); }
    }

    /// <summary>
    /// Drop cached process-id mappings; called when the source list of windows changes
    /// dramatically (e.g. a process exited) to keep the cache from growing unboundedly.
    /// Icon caches themselves are intentionally retained — same exe, same icon.
    /// </summary>
    public void TrimProcessCache(System.Collections.Generic.HashSet<uint> aliveProcessIds)
    {
        foreach (var pid in _processPathCache.Keys)
        {
            if (!aliveProcessIds.Contains(pid))
            {
                _processPathCache.TryRemove(pid, out _);
            }
        }
    }

    // -------- Icon construction primitives (used by AppWindow) --------

    /// <summary>
    /// Convert an HICON to a frozen ImageSource. Optionally destroy the source HICON
    /// (must be true for handles obtained from SHGetFileInfo or ExtractIcon).
    /// </summary>
    public static ImageSource? IconHandleToImageSource(IntPtr iconHandle, bool destroyAfter = false)
    {
        if (iconHandle == IntPtr.Zero) return null;
        try
        {
            // Imaging.CreateBitmapSourceFromHIcon copies the icon's pixels — no need to clone the handle.
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (source is { IsFrozen: false, CanFreeze: true })
                source.Freeze();
            return source;
        }
        catch { return null; }
        finally { if (destroyAfter) NativeMethods.DestroyIcon(iconHandle); }
    }

    /// <summary>
    /// Load an icon via the Shell API. Caches the result against the executable path.
    /// </summary>
    public ImageSource? LoadIconFromShell(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        if (_exeIconCache.TryGetValue(filePath, out var cached)) return cached;

        var shinfo = new NativeMethods.SHFILEINFO();
        var result = NativeMethods.SHGetFileInfo(filePath, 0, ref shinfo,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf(shinfo),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

        if (result == 0) return null;

        var icon = IconHandleToImageSource(shinfo.hIcon, destroyAfter: true);
        if (icon != null)
            _exeIconCache[filePath] = icon;
        return icon;
    }

    /// <summary>
    /// Load an icon from an arbitrary image file (used for UWP manifest logos).
    /// </summary>
    public ImageSource? LoadIconFromImageFile(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48;
            bitmap.DecodePixelHeight = 48;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolve the best AppxManifest logo path for a UWP app directory and cache the result.
    /// </summary>
    private string? ResolveAppxLogoPath(string appDir)
    {
        if (_appxLogoCache.TryGetValue(appDir, out var cached))
            return cached;

        var manifestPath = Path.Combine(appDir, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            _appxLogoCache[appDir] = null;
            return null;
        }

        try
        {
            var doc = XDocument.Load(manifestPath);
            var visualElements = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");

            string?[] logoAttrs = [
                visualElements?.Attribute("Square44x44Logo")?.Value,
                visualElements?.Attribute("Square150x150Logo")?.Value,
                visualElements?.Attribute("Square71x71Logo")?.Value
            ];

            var logoPath = logoAttrs.FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (string.IsNullOrEmpty(logoPath))
            {
                var logoElement = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "Logo" && e.Parent?.Name.LocalName == "Properties");
                logoPath = logoElement?.Value;
            }
            if (string.IsNullOrEmpty(logoPath))
            {
                _appxLogoCache[appDir] = null;
                return null;
            }

            var baseLogoPath = Path.Combine(appDir, logoPath);
            var logoDir = Path.GetDirectoryName(baseLogoPath);
            var logoName = Path.GetFileNameWithoutExtension(baseLogoPath);
            var logoExt = Path.GetExtension(baseLogoPath);

            if (string.IsNullOrEmpty(logoDir))
            {
                _appxLogoCache[appDir] = null;
                return null;
            }

            // Prefer larger sizes, unplated; matches the order originally tried in AppWindow.
            string[] suffixes = [
                ".targetsize-256_altform-unplated", ".targetsize-256",
                ".targetsize-64_altform-unplated", ".targetsize-64",
                ".targetsize-48_altform-unplated", ".targetsize-48",
                ".targetsize-32_altform-unplated", ".targetsize-32",
                ".scale-200", ".scale-100", ""
            ];
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(logoDir, $"{logoName}{suffix}{logoExt}");
                if (File.Exists(candidate))
                {
                    _appxLogoCache[appDir] = candidate;
                    return candidate;
                }
            }

            // Fall back to base logo path (may or may not exist; LoadIconFromImageFile handles that).
            _appxLogoCache[appDir] = baseLogoPath;
            return baseLogoPath;
        }
        catch
        {
            _appxLogoCache[appDir] = null;
            return null;
        }
    }

    /// <summary>
    /// Load a UWP manifest logo, with manifest parsing + suffix-probing cached per app dir.
    /// </summary>
    public ImageSource? LoadIconFromAppxManifest(string appDir)
    {
        var logoPath = ResolveAppxLogoPath(appDir);
        if (string.IsNullOrEmpty(logoPath)) return null;
        return LoadIconFromImageFile(logoPath);
    }
}
