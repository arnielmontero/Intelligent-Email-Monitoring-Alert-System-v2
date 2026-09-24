using Microsoft.Toolkit.Uwp.Notifications;

namespace Iemas.Agent.Services;

/// <summary>§8.1 "Display Windows toast notifications," §9/§51-52 (notification content examples).</summary>
public static class ToastService
{
    public const string CaseIdArgumentKey = "caseId";
    public const string ActionArgumentKey = "action";

    /// <summary>Returns null on success, or the exception message on failure — callers report failures via §8.1 "Report errors" rather than losing them silently.</summary>
    public static string? Show(string title, string message, Guid? caseId)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            if (caseId is Guid id)
            {
                builder.AddArgument(CaseIdArgumentKey, id.ToString());
                builder.AddButton(new ToastButton()
                    .SetContent("Open Case")
                    .AddArgument(ActionArgumentKey, "open")
                    .SetBackgroundActivation());
                builder.AddButton(new ToastButton()
                    .SetContent("Acknowledge")
                    .AddArgument(ActionArgumentKey, "ack")
                    .SetBackgroundActivation());
            }

            builder.Show();
            return null;
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
    }
}
