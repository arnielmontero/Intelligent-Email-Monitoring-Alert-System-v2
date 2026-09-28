using System.ComponentModel;
using System.Windows;
using Iemas.Agent.Models;
using Iemas.Agent.Services;
using Iemas.Agent.Views;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Iemas.Agent;

/// <summary>
/// §9 Windows Agent UX mockup: header (name + connection dot), Action Required / Waiting / History
/// / Settings. §77 "the Agent should be able to rebuild its current UI from a fresh server
/// synchronization" — RenderCaseList always redraws from AgentSessionManager's current
/// ActionRequired/Waiting lists, never from locally-mutated state.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private enum Tab { ActionRequired, Waiting, History, Settings, Help }
    private Tab _currentTab = Tab.ActionRequired;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string StatusText => App.Session.State switch
    {
        SessionState.Connected => "Connected",
        SessionState.Authenticating => "Connecting...",
        SessionState.PendingApproval => "Awaiting Approval",
        SessionState.Disconnected => "Disconnected",
        SessionState.Rejected => "Registration Rejected",
        SessionState.Error => "Error",
        _ => "Not Registered",
    };

    public Brush StatusColor => App.Session.State == SessionState.Connected
        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
        : new SolidColorBrush(Color.FromRgb(156, 163, 175));

    public string EmployeeName => App.Session.State == SessionState.Connected ? App.Session.EmployeeName : string.Empty;

    public string VersionText => $"IEMAS Agent v{AgentSessionManager.AgentVersion}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        App.Session.StateUpdated += OnSessionStateUpdated;
        ServerUrlTextBox.Text = "https://localhost:8443";
        ClientNameTextBox.Text = Environment.MachineName;
        Loaded += (_, _) => RenderForState();
    }

    private void OnSessionStateUpdated()
    {
        Dispatcher.Invoke(() =>
        {
            Raise(nameof(StatusText));
            Raise(nameof(StatusColor));
            Raise(nameof(EmployeeName));
            RenderForState();
        });
    }

    private void RenderForState()
    {
        OnboardingPanel.Visibility = Visibility.Collapsed;
        CaseListPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        HelpPanel.Visibility = Visibility.Collapsed;

        // Help is readable before registration too - that is when people need it most.
        if (_currentTab == Tab.Help)
        {
            HelpPanel.Visibility = Visibility.Visible;
            return;
        }

        // Settings (server URL) must always be reachable, even before registration — otherwise a
        // user pointed at the wrong/unreachable server URL has no way to fix it from the UI.
        if (_currentTab == Tab.Settings)
        {
            ShowSettingsPanel();
            return;
        }

        switch (App.Session.State)
        {
            case SessionState.NotRegistered:
                ShowOnboarding("Register this device", "This computer is not yet registered with IEMAS. Enter your enrolled email address to request registration; an administrator must approve it before you can connect.", showForm: true);
                break;
            case SessionState.PendingApproval:
                ShowOnboarding("Awaiting Administrator Approval", "Your registration request has been sent. An administrator needs to approve it in the IEMAS CMS before this device can connect. This screen updates automatically once approved.", showForm: false);
                break;
            case SessionState.Rejected:
                ShowOnboarding("Registration Rejected", "An administrator rejected this device's registration request. Contact your administrator, or try registering again.", showForm: true);
                break;
            case SessionState.Error:
                ShowOnboarding("Connection Error", "Could not authenticate with the IEMAS server. Your stored credential may have been revoked or expired. Please register again.", showForm: true);
                break;
            case SessionState.Authenticating:
                ShowOnboarding("Connecting...", "Authenticating with the IEMAS server.", showForm: false);
                break;
            default:
                if (_currentTab == Tab.History) HistoryPanel.Visibility = Visibility.Visible;
                else RenderCaseList();
                break;
        }
    }

    private void ShowOnboarding(string title, string message, bool showForm)
    {
        OnboardingPanel.Visibility = Visibility.Visible;
        OnboardingTitle.Text = title;
        OnboardingMessage.Text = message;
        RegistrationForm.Visibility = showForm ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderCaseList()
    {
        CaseListPanel.Visibility = Visibility.Visible;
        var cases = _currentTab == Tab.Waiting ? App.Session.Waiting : App.Session.ActionRequired;
        ListHeader.Text = _currentTab == Tab.Waiting ? $"WAITING ({cases.Count})" : $"ACTION REQUIRED ({cases.Count})";
        CaseItemsControl.ItemsSource = cases.Select(c => new CaseListItem(c)).ToList();
    }

    private record CaseListItem(CaseDto Case)
    {
        public string CustomerDisplay => string.IsNullOrWhiteSpace(Case.CustomerDisplayName) ? Case.CustomerEmailAddress : Case.CustomerDisplayName!;
        public string Subject => Case.Subject;
        public string ReceivedSummary => $"Received: {Case.FirstEmailReceivedAt.ToLocalTime():t} • {Case.EmailCount} message(s) • {Case.CaseNumber}{StatusSuffix}";

        /// <summary>Shown when the Case is more than plainly "action required", e.g. after "I'll Handle This".</summary>
        private string StatusSuffix => Case.WorkStatus switch
        {
            CaseWorkStatus.InProgress => " • In progress",
            CaseWorkStatus.Overdue => " • Overdue",
            CaseWorkStatus.Escalated => " • Escalated",
            CaseWorkStatus.WaitingForCustomer => " • Waiting for customer",
            CaseWorkStatus.WaitingForInternal => " • Waiting internally",
            CaseWorkStatus.WaitingForApproval => " • Waiting for approval",
            _ => string.Empty,
        };
    }

    // --- Registration ---

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        RegistrationError.Text = string.Empty;
        var email = EmailTextBox.Text.Trim();
        var clientName = string.IsNullOrWhiteSpace(ClientNameTextBox.Text) ? Environment.MachineName : ClientNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            RegistrationError.Text = "Email address is required.";
            return;
        }

        var (succeeded, error) = await App.Session.RegisterAsync(email, clientName);
        if (!succeeded)
        {
            RegistrationError.Text = error ?? "Registration failed.";
        }
    }

    // --- Case actions ---

    private void OpenCaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CaseListItem item }) return;
        var dialog = new CaseDetailWindow(item.Case) { Owner = this };
        dialog.ShowDialog();
    }

    private void AddUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CaseListItem item }) return;
        var dialog = new CaseDetailWindow(item.Case) { Owner = this };
        dialog.FocusComment();
        dialog.ShowDialog();
    }

    public void FocusCase(Guid caseId)
    {
        var match = App.Session.ActionRequired.Concat(App.Session.Waiting).FirstOrDefault(c => c.Id == caseId);
        if (match is not null)
        {
            var dialog = new CaseDetailWindow(match) { Owner = this };
            dialog.ShowDialog();
        }
    }

    // --- Tabs ---

    private void ShowActionRequired_Click(object sender, RoutedEventArgs e) { _currentTab = Tab.ActionRequired; RenderForState(); }
    private void ShowWaiting_Click(object sender, RoutedEventArgs e) { _currentTab = Tab.Waiting; RenderForState(); }
    private void ShowHistory_Click(object sender, RoutedEventArgs e) { _currentTab = Tab.History; RenderForState(); }
    private void ShowSettings_Click(object sender, RoutedEventArgs e) { ShowSettings(); }
    private void ShowHelp_Click(object sender, RoutedEventArgs e) { ShowHelp(); }

    public void ShowHelp()
    {
        _currentTab = Tab.Help;
        RenderForState();
    }

    public void ShowSettings()
    {
        _currentTab = Tab.Settings;
        RenderForState();
    }

    private void ShowSettingsPanel()
    {
        SettingsPanel.Visibility = Visibility.Visible;
        var settings = AgentSettings.Load();
        ServerUrlTextBox.Text = settings.ServerUrl;
        AllowInsecureTlsCheckBox.IsChecked = settings.AllowInsecureTls;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        Raise(nameof(VersionText));
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.Session.ApplyServerUrlChange(ServerUrlTextBox.Text.Trim(), AllowInsecureTlsCheckBox.IsChecked == true);
        StartupRegistration.Apply(StartWithWindowsCheckBox.IsChecked == true);
        var settings = AgentSettings.Load();
        settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        settings.Save();
        // A non-blocking inline status message rather than a modal MessageBox — a modal here would
        // block the UI thread synchronously, which live testing showed is worth avoiding for a
        // background tray app where the user may not be looking at the window at all.
        SettingsStatusText.Text = "Settings saved. Restart the Agent for the server URL change to take effect if you were already connected.";
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        App.Session.SignOut();
        _currentTab = Tab.ActionRequired;
    }

    private void OpenEmail_Click(object sender, RoutedEventArgs e) => AgentSessionManager.OpenEmailClient();

    // --- Window chrome: minimize-to-tray instead of exit (§10 System tray) ---

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }
}
