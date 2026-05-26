using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCodeExplorer.Converters;

/// <summary>Turns a UTC <see cref="DateTime"/> into a friendly "2 hours ago" style string.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return "";
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        var delta = DateTime.UtcNow - utc;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60)
        {
            var m = (int)delta.TotalMinutes;
            return $"{m} min ago";
        }
        if (delta.TotalHours < 24)
        {
            var h = (int)delta.TotalHours;
            return $"{h} hour{(h == 1 ? "" : "s")} ago";
        }
        if (delta.TotalDays < 2) return "yesterday";
        if (delta.TotalDays < 7)
        {
            var d = (int)delta.TotalDays;
            return $"{d} days ago";
        }
        return utc.ToLocalTime().ToString("d MMM yyyy", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
