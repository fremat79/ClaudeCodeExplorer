using System;
using System.ComponentModel;
using System.Windows;
using ClaudeCodeExplorer.Services;
using WinForms = System.Windows.Forms;

namespace ClaudeCodeExplorer;

public partial class App : Application
{
    private WinForms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The app lives in the tray, so don't quit when the window is hidden/closed.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Match the OS light/dark theme and follow live changes.
        ThemeManager.Initialize();

        bool firstRun = SettingsService.IsFirstRun();

        // Create the window but leave it hidden – we start minimized in the tray.
        _mainWindow = new MainWindow();
        _mainWindow.StateChanged += OnWindowStateChanged;
        _mainWindow.Closing += OnWindowClosing;

        CreateTrayIcon();

        if (firstRun)
        {
            _trayIcon?.ShowBalloonTip(4000, "Claude Code Explorer",
                "Running in the tray. Double-click the icon to open it.", WinForms.ToolTipIcon.Info);
            SettingsService.MarkLaunched();
        }

        // Offer to start with Windows – asked at most once, and never if already enabled.
        MaybePromptAutostart();
    }

    private void MaybePromptAutostart()
    {
        if (StartupService.IsRegistered()) return;          // already enabled – nothing to ask
        if (SettingsService.HasPromptedAutostart()) return; // already asked once – don't nag

        SettingsService.MarkAutostartPrompted();

        var answer = MessageBox.Show(
            "Start Claude Code Explorer automatically when you sign in to Windows?\n\n"
            + "It will launch minimized to the notification area (system tray).",
            "Claude Code Explorer",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            StartupService.RegisterAutostart();
    }

    private void CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open");
        openItem.Click += (_, _) => ShowMainWindow();

        var startupItem = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
        };
        startupItem.Click += (_, _) =>
        {
            // CheckOnClick has already toggled Checked to the desired state.
            if (startupItem.Checked) StartupService.RegisterAutostart();
            else StartupService.Unregister();
        };

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        // Keep the checkbox in sync with the actual registry state each time the menu opens.
        menu.Opening += (_, _) => startupItem.Checked = StartupService.IsRegistered();

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Claude Code Explorer",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;   // bring to front, then release
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void HideToTray()
    {
        if (_mainWindow is null) return;
        _mainWindow.Hide();
        _mainWindow.ShowInTaskbar = false;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_mainWindow?.WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;     // real shutdown – let it close
        e.Cancel = true;          // otherwise: just hide back to the tray
        HideToTray();
    }

    private void ExitApp()
    {
        _exiting = true;
        DisposeTray();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTray();
        base.OnExit(e);
    }

    private void DisposeTray()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/claude.ico"));
            if (res is not null)
                return new System.Drawing.Icon(res.Stream);
        }
        catch
        {
            // Fall back to a stock icon if the resource can't be loaded.
        }
        return System.Drawing.SystemIcons.Application;
    }
}
