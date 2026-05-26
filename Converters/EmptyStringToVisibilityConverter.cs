using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeCodeExplorer.Converters;

/// <summary>Visible when the bound string is null/empty – used for the search watermark.</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
