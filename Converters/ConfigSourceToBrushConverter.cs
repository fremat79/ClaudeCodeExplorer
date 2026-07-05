using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Converters;

/// <summary>Maps a <see cref="ConfigSource"/> to a fixed badge colour (readable in light & dark).</summary>
public sealed class ConfigSourceToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush User    = Frozen(0x8F, 0x88, 0x80); // muted grey
    private static readonly SolidColorBrush Project = Frozen(0x4A, 0x9E, 0xE0); // blue
    private static readonly SolidColorBrush Local   = Frozen(0x5F, 0xB3, 0x7A); // green
    private static readonly SolidColorBrush Managed = Frozen(0xE0, 0x67, 0x3F); // red/orange (authoritative)
    private static readonly SolidColorBrush None    = Frozen(0x8F, 0x88, 0x80);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is ConfigSource s
            ? s switch
            {
                ConfigSource.User => User,
                ConfigSource.Project => Project,
                ConfigSource.Local => Local,
                ConfigSource.Managed => Managed,
                _ => None,
            }
            : None;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
