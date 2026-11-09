using Iemas.Agent.Models;

namespace Iemas.Agent.Views;

/// <summary>A case's state in plain words: a short label, its colours, and what the agent should do next.</summary>
public sealed record CaseStatusInfo(string Label, string Foreground, string Background, string NextStep);

public static class CaseStatusText
{
    private const string ReplyHint =
        "Reply to the customer from your mailbox, by answering their email. IEMAS finds your reply in the Sent folder by itself (within about 5 minutes).";

    public static CaseStatusInfo For(CaseDto c)
    {
        var replied = c.ReplyStatus == CaseReplyStatus.Replied;
        return c.WorkStatus switch
        {
            CaseWorkStatus.PotentiallyResolved => new("Looks resolved - please confirm", "#92400E", "#FEF3C7",
                "IEMAS thinks the customer's request has been handled. If that is right, confirm it below. If not, choose \"Not resolved yet\" and IEMAS keeps watching."),
            CaseWorkStatus.Overdue => new("Overdue - reply now", "#991B1B", "#FEE2E2",
                "The reply deadline has passed. " + ReplyHint),
            CaseWorkStatus.Escalated => new("Escalated to your manager", "#991B1B", "#FEE2E2",
                "Your manager has been told this inquiry is waiting. " + ReplyHint),
            CaseWorkStatus.WaitingForInternal => new("Waiting on a colleague", "#374151", "#E5E7EB",
                "You said you need input from someone in the company first. When you have it, reply to the customer."),
            CaseWorkStatus.WaitingForCustomer => new("Waiting for the customer", "#374151", "#E5E7EB",
                "You have replied. IEMAS keeps watching the conversation and tells you if the customer writes back or goes quiet."),
            CaseWorkStatus.Completed => new("Resolved", "#065F46", "#D1FAE5", "Nothing more to do. Monitoring has stopped."),
            CaseWorkStatus.Cancelled => new("Closed - not an inquiry", "#374151", "#E5E7EB", "Nothing more to do. Monitoring has stopped."),
            _ when replied => new("Replied - conversation still open", "#1E40AF", "#DBEAFE",
                "Your reply was found. IEMAS keeps watching until the request is resolved. When it is, click \"Mark as resolved\"."),
            CaseWorkStatus.InProgress => new("You are working on it", "#1E40AF", "#DBEAFE", ReplyHint),
            _ => new("Waiting for your reply", "#9A3412", "#FFEDD5", ReplyHint),
        };
    }
}
