using System;
using Microsoft.Win32;

namespace MicBoost.App.Services;

/// <summary>"Start with Windows" via the per-user Run key (no administrator rights needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MicBoost";
    public const string AutostartArgument = "--autostart";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find MicBoost.exe path.");
            key.SetValue(ValueName, $"\"{exe}\" {AutostartArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>If enabled, rewrites the entry so it points at the current exe (e.g. after the portable exe moved).</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            if (IsEnabled) SetEnabled(true);
        }
        catch (Exception) { }
    }
}
