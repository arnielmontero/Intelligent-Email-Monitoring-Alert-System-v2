using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Iemas.Agent.Services;

public enum EmailLaunchOutcome { OpenedDefaultMailProgram, OpenedOutlook, CopiedToClipboard, Failed }

public record EmailLaunchResult(EmailLaunchOutcome Outcome, string Message)
{
    public bool Opened => Outcome is EmailLaunchOutcome.OpenedDefaultMailProgram or EmailLaunchOutcome.OpenedOutlook;
}

/// <summary>
/// §8.1 "Open the normal email client/webmail when requested" — IEMAS never sends the reply itself;
/// it only hands off to the employee's own mail program. Many agent PCs have no default handler
/// for mailto: (Windows then throws "The system cannot find the file specified"), so this tries,
/// in order: the default mailto: handler, classic Outlook started directly, and finally copying
/// the customer's address and subject to the clipboard with plain instructions.
/// </summary>
public static class EmailLauncher
{
    public static EmailLaunchResult OpenCompose(string? to = null, string? subject = null)
    {
        var replySubject = ToReplySubject(subject);

        // 1. Whatever the PC has registered for mailto: (Outlook, Thunderbird, Mail, a browser...).
        try
        {
            var mailto = "mailto:" + (to ?? "");
            if (replySubject is not null) mailto += "?subject=" + Uri.EscapeDataString(replySubject);
            Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
            return new(EmailLaunchOutcome.OpenedDefaultMailProgram,
                "Your email program is opening with a new message to the customer. Send it from the mailbox the customer wrote to; IEMAS will notice the reply by itself.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No mailto: handler registered — fall through.
        }

        // 2. Classic Outlook installed but not set as the default mail program.
        var outlook = FindOutlook();
        if (outlook is not null)
        {
            try
            {
                var psi = new ProcessStartInfo(outlook) { UseShellExecute = false };
                if (!string.IsNullOrWhiteSpace(to))
                {
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add("ipm.note");
                    psi.ArgumentList.Add("/m");
                    psi.ArgumentList.Add(replySubject is null ? to : $"{to}&subject={Uri.EscapeDataString(replySubject)}");
                }
                Process.Start(psi);
                return new(EmailLaunchOutcome.OpenedOutlook,
                    "Outlook is opening. Best: find the customer's email in Outlook and click Reply, so the answer stays in the same conversation.");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Fall through to the clipboard.
            }
        }

        // 3. Nothing we can open: give the employee what they need to do it by hand.
        if (!string.IsNullOrWhiteSpace(to) && TryCopyToClipboard(to))
        {
            return new(EmailLaunchOutcome.CopiedToClipboard,
                $"No email program is set up on this PC, so IEMAS copied the customer's address ({to}). Open your email (Outlook or webmail), find their message and click Reply.");
        }

        return new(EmailLaunchOutcome.Failed,
            "No email program is set up on this PC. Open your email (Outlook or webmail) yourself and reply to the customer's message from the mailbox they wrote to.");
    }

    private static string? ToReplySubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var s = subject.Trim();
        return s.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? s : "Re: " + s;
    }

    private static string? FindOutlook()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE";
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(null) is string path)
                {
                    path = path.Trim('"');
                    if (System.IO.File.Exists(path)) return path;
                }
            }
            catch
            {
                // Registry not readable — treat as not installed.
            }
        }
        return null;
    }

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
