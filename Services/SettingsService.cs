using System;
using System.IO;
using System.Text.Json;

namespace ClaudeCodeExplorer.Services;

public sealed class AppSettings
{
    public bool FirstRunComplete { get; set; }

    /// <summary>True once we've asked the user about enabling autostart (so we don't nag).</summary>
    public bool AutostartPrompted { get; set; }

    /// <summary>Whether search also consults the SQLite full-text index. Off by default.</summary>
    public bool FullTextSearchEnabled { get; set; }
}

/// <summary>Tiny JSON settings store in %LOCALAPPDATA%\ClaudeCodeExplorer\settings.json.</summary>
public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeExplorer", "settings.json");

    public static bool IsFirstRun() => !Load().FirstRunComplete;

    public static void MarkLaunched()
    {
        var s = Load();
        s.FirstRunComplete = true;
        Save(s);
    }

    public static bool HasPromptedAutostart() => Load().AutostartPrompted;

    public static void MarkAutostartPrompted()
    {
        var s = Load();
        s.AutostartPrompted = true;
        Save(s);
    }

    public static bool GetFullTextEnabled() => Load().FullTextSearchEnabled;

    public static void SetFullTextEnabled(bool enabled)
    {
        var s = Load();
        s.FullTextSearchEnabled = enabled;
        Save(s);
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s is not null) return s;
            }
        }
        catch
        {
            // Treat unreadable settings as a first run.
        }
        return new AppSettings();
    }

    private static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Best-effort.
        }
    }
}
