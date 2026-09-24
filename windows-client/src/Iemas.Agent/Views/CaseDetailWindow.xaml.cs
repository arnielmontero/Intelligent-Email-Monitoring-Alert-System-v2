using System.Windows;
using Iemas.Agent.Models;
using Button = System.Windows.Controls.Button;

namespace Iemas.Agent.Views;

/// <summary>
/// §46 Employee Actions (all ten), §47 Employee Comments, §48 Mark Completed (reason required).
/// Every action here goes through AgentSessionManager.SubmitActionAsync, which always attaches a
/// fresh idempotency RequestId (§73/§78) and always re-syncs afterward so this window and the main
/// list reflect server-authoritative state, not an optimistic local guess (§77).
/// </summary>
public partial class CaseDetailWindow : Window
{
    private readonly CaseDto _case;

    private static readonly (CaseActionType Type, string Label)[] Actions =
    {
        (CaseActionType.Acknowledged, "Acknowledge"),
        (CaseActionType.WillHandle, "I'll Handle This"),
        (CaseActionType.AlreadyReplied, "Already Replied"),
        (CaseActionType.WaitingForCustomer, "Waiting for Customer"),
        (CaseActionType.WaitingForInternal, "Waiting Internally"),
        (CaseActionType.RemindLater, "Remind Me Later"),
        (CaseActionType.Cancel, "Cancel Case"),
        (CaseActionType.Reopen, "Reopen"),
        (CaseActionType.RequestEscalation, "Request Escalation"),
    };

    public CaseDetailWindow(CaseDto theCase)
    {
        InitializeComponent();
        _case = theCase;

        CaseNumberText.Text = theCase.CaseNumber;
        SubjectText.Text = theCase.Subject;
        CustomerText.Text = string.IsNullOrWhiteSpace(theCase.CustomerDisplayName)
            ? theCase.CustomerEmailAddress
            : $"{theCase.CustomerDisplayName} <{theCase.CustomerEmailAddress}>";
        StatusText.Text = $"Work: {theCase.WorkStatus}   Reply: {theCase.ReplyStatus}   Received: {theCase.FirstEmailReceivedAt.ToLocalTime():g}";

        foreach (var (type, label) in Actions)
        {
            var button = new Button { Content = label, Tag = type, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 5, 8, 5) };
            button.Click += ActionButton_Click;
            ActionsPanel.Children.Add(button);
        }

        foreach (CaseCompletionReason reason in Enum.GetValues(typeof(CaseCompletionReason)))
        {
            CompletionReasonCombo.Items.Add(reason);
        }
        CompletionReasonCombo.SelectedIndex = 0;
    }

    public void FocusComment() => Loaded += (_, _) => CommentTextBox.Focus();

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CaseActionType actionType }) return;

        DateTimeOffset? requestedFor = null;
        if (actionType == CaseActionType.RemindLater)
        {
            requestedFor = DateTimeOffset.UtcNow.AddHours(1); // simple default; a full time-picker is a UI refinement, not a protocol requirement
        }

        var result = await App.Session.SubmitActionAsync(_case.Id, actionType, null, requestedFor);
        ResultText.Text = result.Succeeded
            ? $"Recorded. Work status: {result.Value?.WorkStatus}, Reply status: {result.Value?.ReplyStatus}."
            : $"Failed: {result.Error}";
    }

    private async void SubmitComment_Click(object sender, RoutedEventArgs e)
    {
        var text = CommentTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ResultText.Text = "Enter a comment first.";
            return;
        }

        var result = await App.Session.SubmitCommentAsync(_case.Id, text);
        ResultText.Text = result.Succeeded ? "Comment added." : $"Failed: {result.Error}";
        if (result.Succeeded) CommentTextBox.Clear();
    }

    private async void CompleteCase_Click(object sender, RoutedEventArgs e)
    {
        if (CompletionReasonCombo.SelectedItem is not CaseCompletionReason reason)
        {
            ResultText.Text = "Select a completion reason.";
            return;
        }

        var result = await App.Session.CompleteCaseAsync(_case.Id, reason, CompletionCommentTextBox.Text.Trim());
        ResultText.Text = result.Succeeded ? "Case completed." : $"Failed: {result.Error}";
        if (result.Succeeded)
        {
            Close();
        }
    }
}
