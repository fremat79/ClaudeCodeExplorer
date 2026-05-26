using System;
using Microsoft.Win32;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Registers the app to start with Windows via the per-user Run key. Registration happens
/// once (on first run); the user can later remove it and it will not be re-added.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeCodeExplorer";

    public static void RegisterAutostart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        catch
        {
            // Autostart is a convenience – never fail startup over it.
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Ignore.
        }
    }
}
