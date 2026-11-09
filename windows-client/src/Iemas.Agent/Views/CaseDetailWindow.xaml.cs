using System.Windows;
using System.Windows.Media;
using Iemas.Agent.Models;
using Iemas.Agent.Services;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Iemas.Agent.Views;

/// <summary>
/// One inquiry, readable at a glance: what it is, where it stands in plain words, what to do next,
/// and only the actions an agent actually needs. Every action still goes through
/// AgentSessionManager.SubmitActionAsync (fresh idempotency RequestId, re-sync afterwards), so the
/// window and the lists always show the server's state.
///
/// Kept deliberately small: replies are detected automatically from the Sent folder, so there is no
/// "Already replied" / "Waiting for customer" button; "Mark as resolved" replaces the old
/// completion form; the pop-up already offers Acknowledge.
/// </summary>
public partial class CaseDetailWindow : Window
{
    private CaseDto _case;

    public CaseDetailWindow(CaseDto theCase)
    {
        InitializeComponent();
        _case = theCase;

        Title = $"{theCase.CaseNumber} - IEMAS";
        CaseNumberText.Text = theCase.CaseNumber;
        SubjectText.Text = theCase.Subject;
        CustomerText.Text = string.IsNullOrWhiteSpace(theCase.CustomerDisplayName)
            ? theCase.CustomerEmailAddress
            : $"{theCase.CustomerDisplayName} <{theCase.CustomerEmailAddress}>";
        var emails = theCase.EmailCount == 1 ? "1 email" : $"{theCase.EmailCount} emails";
        ReceivedText.Text = $"{theCase.FirstEmailReceivedAt.ToLocalTime():ddd d MMM, h:mm tt}  ·  {emails} in this conversation";

        ShowStatus();
    }

    public void FocusComment() => Loaded += (_, _) => CommentTextBox.Focus();

    private void ShowStatus()
    {
        var status = CaseStatusText.For(_case);
        StatusLabel.Text = status.Label;
        NextStepText.Text = status.NextStep;
        StatusLabel.Foreground = Brush(status.Foreground);
        NextStepText.Foreground = Brush(status.Foreground);
        StatusCard.Background = Brush(status.Background);

        var looksResolved = _case.WorkStatus == CaseWorkStatus.PotentiallyResolved;
        ConfirmCard.Visibility = looksResolved ? Visibility.Visible : Visibility.Collapsed;
        ResolveButton.Visibility = looksResolved ? Visibility.Collapsed : Visibility.Visible;
        WorkingButton.IsEnabled = _case.WorkStatus is not (CaseWorkStatus.InProgress or CaseWorkStatus.PotentiallyResolved);
    }

    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AgentSessionManager.OpenEmailClient();
            ShowResult("Your email program is opening. Reply to the customer's email from this mailbox; IEMAS will notice the reply by itself.", ok: true);
        }
        catch (Exception ex)
        {
            ShowResult($"Could not open an email program: {ex.Message}", ok: false);
        }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button || !Enum.TryParse<CaseActionType>(tag, out var action)) return;

        if (action == CaseActionType.Cancel && MessageBox.Show(this,
                "Close this as NOT a customer inquiry?\n\nIt leaves your list and IEMAS stops watching it. Use this only for emails that were flagged by mistake.",
                "Not a customer inquiry", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        DateTimeOffset? remindAt = action == CaseActionType.RemindLater ? DateTimeOffset.UtcNow.AddHours(1) : null;

        button.IsEnabled = false;
        try
        {
            var result = await App.Session.SubmitActionAsync(_case.Id, action, null, remindAt);
            if (!result.Succeeded)
            {
                ShowResult($"That didn't work: {result.Error}", ok: false);
                return;
            }

            if (result.Value is not null && Enum.TryParse<CaseWorkStatus>(result.Value.WorkStatus, out var newStatus))
            {
                _case = _case with { WorkStatus = newStatus };
            }
            ShowStatus();
            ShowResult(Done(action), ok: true);

            if (action is CaseActionType.ConfirmResolved or CaseActionType.Cancel)
            {
                await Task.Delay(1500);
                Close();
            }
        }
        finally
        {
            button.IsEnabled = action != CaseActionType.WillHandle || WorkingButton.IsEnabled;
        }
    }

    private static string Done(CaseActionType action) => action switch
    {
        CaseActionType.ConfirmResolved => "Marked as resolved. IEMAS has stopped watching this inquiry.",
        CaseActionType.RejectResolution => "Noted: not resolved yet. IEMAS keeps watching and will remind you.",
        CaseActionType.WillHandle => "Noted: you are working on it. Remember to reply to the customer.",
        CaseActionType.WaitingForInternal => "Moved to Waiting. Reply to the customer once your colleague has answered.",
        CaseActionType.RemindLater => "OK - IEMAS will remind you about this in 1 hour.",
        CaseActionType.Cancel => "Closed as not a customer inquiry.",
        _ => "Saved.",
    };

    private async void SubmitComment_Click(object sender, RoutedEventArgs e)
    {
        var text = CommentTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowResult("Type a note first.", ok: false);
            CommentTextBox.Focus();
            return;
        }

        var result = await App.Session.SubmitCommentAsync(_case.Id, text);
        if (result.Succeeded)
        {
            CommentTextBox.Clear();
            ShowResult("Note saved. Your manager can see it in IEMAS.", ok: true);
        }
        else
        {
            ShowResult($"The note was not saved: {result.Error}", ok: false);
        }
    }

    private void ShowResult(string message, bool ok)
    {
        ResultText.Text = message;
        ResultText.Foreground = Brush(ok ? "#065F46" : "#991B1B");
        ResultBar.Background = Brush(ok ? "#D1FAE5" : "#FEE2E2");
        ResultBar.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
