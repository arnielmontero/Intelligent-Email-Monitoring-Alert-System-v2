using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Iemas.Agent.Services;
using Iemas.Agent.Views;
using Microsoft.Toolkit.Uwp.Notifications;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Iemas.Agent;

/// <summary>
/// §10 "System tray," §9 tray showing connection state, §8.1 toast display. Owns the single
/// long-lived AgentSessionManager and the NotifyIcon (System.Windows.Forms — WPF has no built-in
/// tray icon type; this is the standard, reliable approach for a WPF app that needs one).
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    public static AgentSessionManager Session { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // §8.1 "Report errors" — a single unhandled exception anywhere (a WPF async-void event
        // handler, a background timer tick) must never silently kill the whole tray app; confirmed
        // live during E2E verification that it otherwise does. Best-effort report to the server,
        // then keep running rather than crash — losing the tray icon/Agent entirely would be worse
        // than one failed operation.
        DispatcherUnhandledException += (_, args) =>
        {
            _ = Session?.ReportErrorAsync($"Unhandled UI exception: {args.Exception}");
            System.Windows.MessageBox.Show(
                $"IEMAS Agent encountered an error and will continue running:\n\n{args.Exception.Message}",
                "IEMAS Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        Session = new AgentSessionManager();

        SetupTrayIcon();
        SetupToastActivation();

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        Session.StateUpdated += UpdateTrayState;

        await Session.StartAsync();
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "IEMAS Agent - Disconnected",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open IEMAS", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => { ShowMainWindow(); _mainWindow?.ShowSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void SetupToastActivation()
    {
        // §8.1 "Display Action Required Cases" reachable directly from a toast button.
        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            Dispatcher.Invoke(() =>
            {
                ShowMainWindow();
                var args = ToastArguments.Parse(toastArgs.Argument);
                if (args.TryGetValue(ToastService.CaseIdArgumentKey, out var caseIdStr) && Guid.TryParse(caseIdStr, out var caseId))
                {
                    _mainWindow?.FocusCase(caseId);
                    if (args.TryGetValue(ToastService.ActionArgumentKey, out var action) && action == "ack")
                    {
                        _ = Session.SubmitActionAsync(caseId, Models.CaseActionType.Acknowledged, null);
                    }
                }
            });
        };
    }

    private void UpdateTrayState()
    {
        if (_notifyIcon is null) return;
        Dispatcher.Invoke(() =>
        {
            _notifyIcon.Text = Session.State switch
            {
                SessionState.Connected => $"IEMAS Agent - Connected ({Session.ActionRequired.Count} Action Required)",
                SessionState.Authenticating => "IEMAS Agent - Connecting...",
                SessionState.PendingApproval => "IEMAS Agent - Awaiting Admin Approval",
                SessionState.Disconnected => "IEMAS Agent - Disconnected",
                SessionState.Rejected => "IEMAS Agent - Registration Rejected",
                SessionState.Error => "IEMAS Agent - Error",
                _ => "IEMAS Agent - Not Registered",
            };
        });
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ExitApplication()
    {
        _notifyIcon!.Visible = false;
        _notifyIcon.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ = Session.DisposeAsync();
        base.OnExit(e);
    }
}
