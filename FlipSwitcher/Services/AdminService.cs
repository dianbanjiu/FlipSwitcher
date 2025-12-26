using System;
using System.Diagnostics;
using System.Security.Principal;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for handling administrator privileges
/// </summary>
public static class AdminService
{
    /// <summary>
    /// Check if the current process is running with administrator privileges
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restart the application with administrator privileges
    /// </summary>
    /// <returns>True if restart was initiated, false if cancelled or failed</returns>
    public static bool RestartAsAdmin()
    {
        try
        {
            var exePath = ProcessHelper.GetExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas" // This triggers the UAC prompt
            };

            Process.Start(startInfo);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC prompt
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restart the application without administrator privileges.
    /// Uses Explorer.exe as a broker to launch the process without admin rights.
    /// </summary>
    /// <returns>True if restart was initiated, false if failed</returns>
    public static bool RestartAsNormalUser()
    {
        try
        {
            var exePath = ProcessHelper.GetExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            // Use Explorer.exe as a broker to start the process without admin privileges.
            // Explorer always runs as the logged-in user, so processes it starts inherit normal user rights.
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{exePath}\"",
                UseShellExecute = true
            };

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

