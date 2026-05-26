using System.Windows;
using ClaudeCodeExplorer.ViewModels;

namespace ClaudeCodeExplorer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
