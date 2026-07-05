using System;
using System.Globalization;
using System.Windows.Data;

namespace ClaudeCodeExplorer.Converters;

/// <summary>Returns the logical negation of a bool (e.g. to enable a control only when a flag is false).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
