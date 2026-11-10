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
        ApplyColours(TitleText.Text);
        ShowUnansweredCount(MessageText.Text);

        Loaded += (_, _) => Arrange();
        Closed += (_, _) => { Open.Remove(this); Arrange(); };
    }

    /// <summary>
    /// Colour-codes the alert by urgency, using the alert title the server sends:
    /// red = escalated / overdue, orange = escalation warning, amber = reminder,
    /// green = looks resolved, rose = customer wrote again on an unanswered inquiry,
    /// purple = follow-up / update needed, blue = new email.
    /// </summary>
    private void ApplyColours(string title)
    {
        var t = title.ToLowerInvariant();
        (string Badge, string Accent, string Tint, string Text) style =
            // Checked before "follow": the customer chased an inquiry we still haven't answered. Not a new email.
            t.Contains("needs your response") || t.Contains("wrote again") ? ("NEEDS RESPONSE", "#E11D48", "#FFE4E6", "#9F1239")
            : t.Contains("warning") ? ("ESCALATION WARNING", "#EA580C", "#FFEDD5", "#9A3412")
            : t.Contains("escalat") ? ("ESCALATED", "#DC2626", "#FEE2E2", "#991B1B")
            : t.Contains("overdue") ? ("OVERDUE", "#DC2626", "#FEE2E2", "#991B1B")
            : t.Contains("remind") ? ("REMINDER", "#D97706", "#FEF3C7", "#92400E")
            : t.Contains("resolved") || t.Contains("confirm") ? ("PLEASE CONFIRM", "#059669", "#D1FAE5", "#065F46")
            : t.Contains("follow") || t.Contains("update") ? ("FOLLOW UP", "#7C3AED", "#EDE9FE", "#5B21B6")
            : ("NEW EMAIL", "#2563EB", "#DBEAFE", "#1E40AF");

        var converter = new System.Windows.Media.BrushConverter();
        System.Windows.Media.Brush Brush(string hex) => (System.Windows.Media.Brush)converter.ConvertFromString(hex)!;
        AccentBorder.BorderBrush = Brush(style.Accent);
        BadgeBorder.Background = Brush(style.Tint);
        BadgeText.Foreground = Brush(style.Text);
        BadgeText.Text = style.Badge;
        OpenButton.Background = Brush(style.Accent);
    }

    /// <summary>
    /// Pulls "N unanswered email(s)" out of the server's message and shows it as a solid pill next to the
    /// badge, so the count can't be missed inside the sentence.
    /// </summary>
    private void ShowUnansweredCount(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(message, @"(\d+)\s+unanswered\s+email", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var count) || count < 1) return;
        CountText.Text = count == 1 ? "1 UNANSWERED" : $"{count} UNANSWERED";
        CountBorder.Visibility = Visibility.Visible;
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
