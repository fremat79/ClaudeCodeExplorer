using System.Windows;
using System.Windows.Input;

namespace ClaudeCodeExplorer.Behaviors;

/// <summary>
/// Attached behavior: a single left-click on the element runs a command with a parameter. Used to
/// make config-inspector rows open their related file on click. The event is not marked handled, so
/// tree selection and the expander toggle keep working; the command decides (via CanExecute) whether
/// there is a file to open.
/// </summary>
public static class ClickToCommand
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(ClickToCommand),
            new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.RegisterAttached("CommandParameter", typeof(object), typeof(ClickToCommand),
            new PropertyMetadata(null));

    public static void SetCommand(DependencyObject o, ICommand? v) => o.SetValue(CommandProperty, v);
    public static ICommand? GetCommand(DependencyObject o) => (ICommand?)o.GetValue(CommandProperty);
    public static void SetCommandParameter(DependencyObject o, object? v) => o.SetValue(CommandParameterProperty, v);
    public static object? GetCommandParameter(DependencyObject o) => o.GetValue(CommandParameterProperty);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.MouseLeftButtonUp -= OnMouseUp;
        if (e.NewValue is not null) el.MouseLeftButtonUp += OnMouseUp;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || sender is not DependencyObject d) return;
        var cmd = GetCommand(d);
        var param = GetCommandParameter(d);
        if (cmd is not null && cmd.CanExecute(param)) cmd.Execute(param);
    }
}
