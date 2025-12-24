using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for managing Windows startup registration
/// </summary>
public static class StartupService
{
    private const string AppName = "FlipSwitcher";
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Check if the application is registered to start with Windows
    /// </summary>
    public static bool IsStartupEnabled()
    {
        // Check registry first
        if (IsInRegistry()) return true;
        
        // Check Task Scheduler
        if (IsInTaskScheduler()) return true;
        
        return false;
    }

    private static bool IsInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            if (key == null) return false;

            var value = key.GetValue(AppName);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInTaskScheduler()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Query /TN \"{AppName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            const int TaskQueryTimeoutMs = 3000;
            using var process = Process.Start(startInfo);
            process?.WaitForExit(TaskQueryTimeoutMs);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enable or disable starting with Windows
    /// </summary>
    /// <param name="enable">True to enable startup, false to disable</param>
    /// <returns>True if the operation succeeded</returns>
    public static bool SetStartupEnabled(bool enable)
    {
        try
        {
            var exePath = GetExecutablePath();
            if (enable && string.IsNullOrEmpty(exePath)) return false;

            var settings = SettingsService.Instance.Settings;
            
            if (enable)
            {
                if (settings.RunAsAdmin)
                {
                    // For admin startup, use Task Scheduler and remove registry entry
                    RemoveFromRegistry();
                    return SetAdminStartupEnabled(true, exePath);
                }
                else
                {
                    // Normal startup via registry and remove Task Scheduler entry
                    SetAdminStartupEnabled(false, null);
                    return AddToRegistry(exePath!);
                }
            }
            else
            {
                // Remove from both registry and Task Scheduler
                RemoveFromRegistry();
                SetAdminStartupEnabled(false, null);
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SetStartupEnabled failed: {ex.Message}");
            return false;
        }
    }

    private static bool AddToRegistry(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key == null) 
            {
                System.Diagnostics.Debug.WriteLine("Failed to open registry key for writing");
                return false;
            }

            key.SetValue(AppName, $"\"{exePath}\"");
            System.Diagnostics.Debug.WriteLine($"Added to registry: {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddToRegistry failed: {ex.Message}");
            return false;
        }
    }

    private static void RemoveFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.DeleteValue(AppName, false);
        }
        catch
        {
            // Ignore errors when removing
        }
    }

    /// <summary>
    /// Set up startup with admin privileges using Task Scheduler
    /// </summary>
    private static bool SetAdminStartupEnabled(bool enable, string? exePath)
    {
        try
        {
            if (enable && !string.IsNullOrEmpty(exePath))
            {
                // Create a scheduled task that runs at logon with highest privileges
                var taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Start FlipSwitcher at logon with administrator privileges</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{exePath}""</Command>
    </Exec>
  </Actions>
</Task>";

                // Write XML to temp file
                var tempFile = System.IO.Path.GetTempFileName();
                System.IO.File.WriteAllText(tempFile, taskXml, System.Text.Encoding.Unicode);

                try
                {
                    // Use schtasks to create the task
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = $"/Create /TN \"{AppName}\" /XML \"{tempFile}\" /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    const int TaskCreateTimeoutMs = 5000;
                    using var process = Process.Start(startInfo);
                    process?.WaitForExit(TaskCreateTimeoutMs);
                    return process?.ExitCode == 0;
                }
                finally
                {
                    try { System.IO.File.Delete(tempFile); } catch { }
                }
            }
            else
            {
                // Delete the scheduled task
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Delete /TN \"{AppName}\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                const int TaskDeleteTimeoutMs = 5000;
                using var process = Process.Start(startInfo);
                process?.WaitForExit(TaskDeleteTimeoutMs);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Update the startup registration based on current settings
    /// </summary>
    public static void UpdateStartupRegistration()
    {
        var settings = SettingsService.Instance.Settings;
        if (settings.StartWithWindows)
        {
            SetStartupEnabled(true);
        }
    }

    private static string? GetExecutablePath()
    {
        return ProcessHelper.GetExecutablePath();
    }
}

