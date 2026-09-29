using System.Media;
using System.Windows;
using Iemas.Agent.Models;

namespace Iemas.Agent.Views;

/// <summary>
/// The Agent's own alert window. Windows toast notifications can be switched off or silenced by Do not disturb /
/// Focus assist without the Agent knowing, so every alert is shown here as well: always on top, bottom-right,
/// stacked upwards when several are open, and kept until the employee opens, acknowledges or dismisses it.
/// </summary>
public partial class PopupWindow : Window
{
    private const int MaxOpen = 4;
    private static readonly List<PopupWindow> Open = new();

    private readonly Guid? _caseId;
    private readonly Action<Guid> _openCase;
    private readonly Action<Guid> _acknowledge;

    private PopupWindow(AgentPushMessage message, Action<Guid> openCase, Action<Guid> acknowledge)
    {
        InitializeComponent();
        _caseId = message.CaseId;
        _openCase = openCase;
        _acknowledge = acknowledge;

        TitleText.Text = string.IsNullOrWhiteSpace(message.Title) ? "IEMAS" : message.Title;
        MessageText.Text = message.Message ?? string.Empty;
        CaseText.Text = message.CaseNumber ?? string.Empty;
        ButtonsPanel.Visibility = _caseId is null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += (_, _) => Arrange();
        Closed += (_, _) => { Open.Remove(this); Arrange(); };
    }

    /// <summary>Shows an alert; must be called on the UI thread.</summary>
    public static void Show(AgentPushMessage message, Action<Guid> openCase, Action<Guid> acknowledge)
    {
        // The newest alert for a Case replaces an older one still on screen for the same Case.
        foreach (var existing in Open.Where(p => p._caseId is not null && p._caseId == message.CaseId).ToList()) existing.Close();
        while (Open.Count >= MaxOpen) Open[0].Close();

        var popup = new PopupWindow(message, openCase, acknowledge);
        Open.Add(popup);
        popup.Show();
        SystemSounds.Asterisk.Play();
    }

    /// <summary>Stacks the open alerts upwards from the bottom-right corner of the work area (above the taskbar).</summary>
    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var popup = Open[i];
            var height = popup.ActualHeight > 0 ? popup.ActualHeight : 180;
            popup.Left = area.Right - popup.Width;
            popup.Top = bottom - height;
            bottom -= height;
        }
    }

    private void OpenCase_Click(object sender, RoutedEventArgs e)
    {
        if (_caseId is Guid id) _openCase(id);
        Close();
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (_caseId is Guid id) _acknowledge(id);
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
