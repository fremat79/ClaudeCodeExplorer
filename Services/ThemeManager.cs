using System;
using System.Windows;
using Microsoft.Win32;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Applies a light or dark theme based on the current Windows "Apps" theme, and switches
/// live when the user changes it. Themes are plain brush dictionaries merged into the app.
/// </summary>
public static class ThemeManager
{
    private const string ThemeRegPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml");

    public static void Initialize()
    {
        Apply(IsDarkTheme());

        // Theme changes raise UserPreferenceChanged with the General category.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var app = Application.Current;
            app?.Dispatcher.Invoke(() => Apply(IsDarkTheme()));
        };
    }

    public static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ThemeRegPath);
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0; // 0 = dark, 1 = light
        }
        catch
        {
            // Registry unreadable – fall back to light.
        }
        return false;
    }

    private static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;

        // Remove any theme dictionary we previously merged.
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString;
            if (src is not null &&
                (src.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                 src.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                dicts.RemoveAt(i);
            }
        }

        dicts.Add(new ResourceDictionary { Source = dark ? DarkUri : LightUri });
    }
}
