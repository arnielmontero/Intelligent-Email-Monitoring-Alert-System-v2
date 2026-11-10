using System.Diagnostics;
using System.Runtime.InteropServices;
using Iemas.Agent.Models;

namespace Iemas.Agent.Services;

/// <summary>
/// Reads the employee's replies from classic Outlook's Sent Items so IEMAS can see them.
///
/// Why: when the mailbox is set up in Outlook as POP, Outlook saves sent mail only in the .pst file on
/// this PC. The mail server's Sent folder stays empty, so the server can never see that the agent
/// replied. The Agent already runs on this PC next to Outlook, so it reports those replies instead.
///
/// Privacy: only emails addressed to a customer of one of this employee's open inquiries are read and
/// sent; nothing else in Sent Items leaves the PC. Outlook is only used when it is already running
/// (never started in the background). Late-bound COM, so no Office version is referenced.
/// </summary>
public static class OutlookSentItems
{
    private const int OlFolderSentMail = 5;
    private const int OlMailItemClass = 43;
    private const int MaxItemsScanned = 300;
    private const string PrInternetMessageId = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";
    private const string PrInReplyToId = "http://schemas.microsoft.com/mapi/proptag/0x1042001F";
    private const string PrInternetReferences = "http://schemas.microsoft.com/mapi/proptag/0x1039001F";
    private const string PrSmtpAddress = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";

    public static bool IsOutlookRunning() => Process.GetProcessesByName("OUTLOOK").Length > 0;

    /// <summary>Runs <see cref="Read"/> on its own STA thread so the UI never freezes while Outlook answers.</summary>
    public static Task<(List<AgentSentMessageDto> Messages, string? Problem)> ReadAsync(DateTimeOffset since, IReadOnlySet<string> customerAddresses)
    {
        var tcs = new TaskCompletionSource<(List<AgentSentMessageDto>, string?)>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Read(since, customerAddresses)); }
            catch (Exception ex) { tcs.SetResult((new List<AgentSentMessageDto>(), ex.Message)); }
        }) { IsBackground = true, Name = "IEMAS Outlook Sent Items" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static (List<AgentSentMessageDto>, string?) Read(DateTimeOffset since, IReadOnlySet<string> customerAddresses)
    {
        var result = new List<AgentSentMessageDto>();
        if (customerAddresses.Count == 0) return (result, null);
        if (!IsOutlookRunning()) return (result, "Outlook is not running.");

        dynamic? outlook = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null) return (result, "Classic Outlook is not installed.");
            outlook = Activator.CreateInstance(type); // attaches to the running Outlook
            if (outlook is null) return (result, "Outlook could not be reached.");

            foreach (dynamic store in outlook.Session.Stores)
            {
                dynamic? sent = null;
                try { sent = store.GetDefaultFolder(OlFolderSentMail); } catch { /* archives / shared stores have none */ }
                if (sent is null) continue;
                ReadFolder(sent, since, customerAddresses, result);
            }
            return (result, null);
        }
        catch (COMException ex)
        {
            return (result, $"Outlook did not respond ({ex.Message}).");
        }
        finally
        {
            if (outlook is not null)
            {
                try { Marshal.FinalReleaseComObject(outlook); } catch { /* already released */ }
            }
        }
    }

    private static void ReadFolder(dynamic folder, DateTimeOffset since, IReadOnlySet<string> customers, List<AgentSentMessageDto> into)
    {
        dynamic items = folder.Items;
        items.Sort("[SentOn]", true); // newest first; stop at the first one older than "since"
        var scanned = 0;
        dynamic? item = items.GetFirst();
        while (item is not null && scanned++ < MaxItemsScanned)
        {
            try
            {
                if ((int)item.Class == OlMailItemClass)
                {
                    DateTime sentOn = item.SentOn;
                    if (new DateTimeOffset(sentOn) < since) break;

                    var to = new List<string>();
                    var cc = new List<string>();
                    foreach (dynamic recipient in item.Recipients)
                    {
                        var address = SmtpOf(recipient);
                        if (string.IsNullOrWhiteSpace(address)) continue;
                        if ((int)recipient.Type == 2) cc.Add(address); else if ((int)recipient.Type == 1) to.Add(address);
                    }

                    if (to.Concat(cc).Any(a => customers.Contains(a.ToLowerInvariant())))
                    {
                        string from = "";
                        try { from = (string?)item.SendUsingAccount?.SmtpAddress ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(from)) from = (string?)item.SenderEmailAddress ?? "";

                        into.Add(new AgentSentMessageDto(
                            (string)item.EntryID,
                            Prop(item, PrInternetMessageId),
                            Prop(item, PrInReplyToId),
                            Prop(item, PrInternetReferences),
                            from,
                            (string?)item.SenderName,
                            to,
                            cc,
                            (string?)item.Subject ?? "",
                            (string?)item.Body,
                            new DateTimeOffset(sentOn),
                            (int)item.Attachments.Count));
                    }
                }
            }
            catch
            {
                // One unreadable item (a recall, a corrupt message) never stops the scan.
            }
            item = items.GetNext();
        }
    }

    private static string? SmtpOf(dynamic recipient)
    {
        try
        {
            string? smtp = recipient.PropertyAccessor.GetProperty(PrSmtpAddress);
            if (!string.IsNullOrWhiteSpace(smtp)) return smtp.Trim();
        }
        catch { /* not set for plain SMTP recipients */ }
        try
        {
            string? address = recipient.Address;
            return address is not null && address.Contains('@') ? address.Trim() : null;
        }
        catch { return null; }
    }

    private static string? Prop(dynamic item, string schema)
    {
        try
        {
            string? value = item.PropertyAccessor.GetProperty(schema);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch { return null; }
    }
}
