using System;
using System.Diagnostics;

namespace FlipSwitcher.Services;

/// <summary>
/// Helper class for process-related operations
/// </summary>
public static class ProcessHelper
{
    /// <summary>
    /// Get the current executable path
    /// </summary>
    /// <returns>The executable path, or null if not available</returns>
    public static string? GetExecutablePath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            try
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                // Ignore errors
            }
        }
        return exePath;
    }
}

