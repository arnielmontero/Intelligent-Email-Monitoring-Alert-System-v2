using System.Runtime.InteropServices;

namespace Iemas.Agent.Services;

/// <summary>
/// Opens a real Reply in classic Outlook to the customer's actual email, the same as the employee
/// finding the email and clicking Reply. That keeps the answer in the same conversation for the
/// customer (their earlier message is quoted) and gives IEMAS the In-Reply-To/References headers it
/// uses to link the reply to the inquiry reliably.
///
/// Uses Outlook's own automation (COM, late-bound so no Office version is referenced). Works only
/// with classic Outlook; the "new Outlook" app has no automation, and then the caller falls back to
/// <see cref="EmailLauncher"/>.
///
/// Finding the email: first by its Internet Message-ID (exact), then by the customer's address and
/// subject (newest first). Only the Inbox trees are searched first; then every mail folder.
/// </summary>
public static class OutlookReply
{
    private const int OlFolderInbox = 6;
    private const int OlMailItem = 0;
    private const int MaxFolders = 400;
    private const string MessageIdProp = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";

    /// <returns>null when it worked; otherwise a short reason, so the caller can fall back.</returns>
    public static string? TryOpenReply(string? messageId, string customerAddress, string? subject)
    {
        dynamic? outlook = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null) return "Classic Outlook is not installed.";

            // Outlook is single-instance: this attaches to the running Outlook, or starts it.
            outlook = Activator.CreateInstance(type);
            if (outlook is null) return "Outlook could not be started.";
            object session = outlook.Session;

            object? mail = null;
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                var id = messageId!;
                mail = Search(session, true, f => FindByMessageId(f, id))
                       ?? Search(session, false, f => FindByMessageId(f, id));
            }
            if (mail is null && !string.IsNullOrWhiteSpace(customerAddress))
            {
                mail = Search(session, true, f => FindBySenderAndSubject(f, customerAddress, subject));
            }
            if (mail is null) return "The customer's email was not found in Outlook.";

            dynamic reply = ((dynamic)mail).Reply();
            reply.Display(false);
            return null;
        }
        catch (COMException ex)
        {
            return $"Outlook did not respond ({ex.Message}).";
        }
        catch (Exception ex)
        {
            return $"Outlook could not be used ({ex.Message}).";
        }
        finally
        {
            if (outlook is not null)
            {
                try { Marshal.FinalReleaseComObject(outlook); } catch { /* already released */ }
            }
        }
    }

    /// <summary>Walks the Inbox of every mailbox (or every mail folder) and returns the first hit.</summary>
    private static object? Search(object sessionObj, bool inboxOnly, Func<object, object?> find)
    {
        dynamic session = sessionObj;
        var visited = 0;
        foreach (dynamic store in session.Stores)
        {
            object? start = null;
            try
            {
                start = inboxOnly ? store.GetDefaultFolder(OlFolderInbox) : store.GetRootFolder();
            }
            catch
            {
                // Some stores (archives, public folders) have no Inbox.
            }
            if (start is null) continue;

            var hit = Walk(start, find, ref visited);
            if (hit is not null) return hit;
            if (visited >= MaxFolders) break;
        }
        return null;
    }

    private static object? Walk(object folderObj, Func<object, object?> find, ref int visited)
    {
        dynamic folder = folderObj;
        if (visited++ >= MaxFolders) return null;

        try
        {
            if ((int)folder.DefaultItemType == OlMailItem)
            {
                var hit = find(folder);
                if (hit is not null) return hit;
            }

            foreach (object child in folder.Folders)
            {
                var hit = Walk(child, find, ref visited);
                if (hit is not null) return hit;
            }
        }
        catch
        {
            // A folder we can't read (permissions, offline) — skip it.
        }
        return null;
    }

    private static object? FindByMessageId(object folderObj, string messageId)
    {
        dynamic folder = folderObj;
        var bare = messageId.Trim().Trim('<', '>');
        foreach (var candidate in new[] { $"<{bare}>", bare })
        {
            var filter = $"@SQL=\"{MessageIdProp}\" = '{Escape(candidate)}'";
            dynamic items = folder.Items.Restrict(filter);
            if ((int)items.Count > 0) return (object)items.GetFirst();
        }
        return null;
    }

    private static object? FindBySenderAndSubject(object folderObj, string address, string? subject)
    {
        dynamic folder = folderObj;
        var filter = $"@SQL=\"urn:schemas:httpmail:fromemail\" = '{Escape(address.Trim())}'";
        dynamic items = folder.Items.Restrict(filter);
        if ((int)items.Count == 0) return null;
        items.Sort("[ReceivedTime]", true);

        var wanted = Normalize(subject);
        var checkedCount = 0;
        dynamic? item = items.GetFirst();
        dynamic? newest = item;
        while (item is not null && checkedCount++ < 50)
        {
            string topic = item.ConversationTopic ?? "";
            if (wanted.Length == 0 || Normalize(topic) == wanted) return (object)item;
            item = items.GetNext();
        }
        // Same customer, subject changed along the way: their newest email is still the best guess.
        return wanted.Length == 0 ? (object?)newest : null;
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static string Normalize(string? subject)
    {
        var s = (subject ?? "").Trim();
        var prefix = new System.Text.RegularExpressions.Regex(@"^((re|fw|fwd|aw|sv|vs)\s*:\s*)+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return prefix.Replace(s, "").Trim().ToLowerInvariant();
    }
}
