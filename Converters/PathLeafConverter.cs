using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCodeExplorer.Converters;

/// <summary>Returns the last segment of a file-system path (the project folder name).</summary>
public sealed class PathLeafConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return "";
        var trimmed = s.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
