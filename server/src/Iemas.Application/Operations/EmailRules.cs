using Iemas.Application.Common.Interfaces;

namespace Iemas.Application.Operations;

/// <summary>The System Configuration email rules, formatted for the AI prompt.</summary>
public static class EmailRules
{
    public static async Task<(string Legitimacy, string Response)> LoadForPromptAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var legit = await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.LegitimacyRules, cancellationToken);
        var notLegit = await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.NotLegitimateRules, cancellationToken);
        var response = await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.ResponseRules, cancellationToken);
        var noResponse = await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.NoResponseRules, cancellationToken);

        return (
            Section("Counts as legitimate", legit) + "\n" + Section("NOT legitimate", notLegit),
            Section("A response IS needed when", response) + "\n" + Section("NO response is needed when", noResponse));
    }

    private static string Section(string title, string rules)
    {
        var lines = rules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? $"{title}: (none configured)" : $"{title}:\n" + string.Join("\n", lines.Select(l => $"- {l}"));
    }
}
