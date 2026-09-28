using System.Net;
using System.Text;
using Iemas.Application.Common.Ai;
using Iemas.Application.EmailClassification;
using Iemas.Application.Operations;
using Iemas.Domain.Ai;
using Iemas.Domain.Identity;
using Iemas.Infrastructure.Ai;
using Iemas.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iemas.Tests.EmailClassification;

/// <summary>Only relevant, legitimate email that needs a response becomes a Case (System Configuration → Email rules).</summary>
public class EmailRulesTests
{
    [Fact]
    public void NotLegitimate_IsNotImportant()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.95, null, null, null, legitimate: false, responseExpected: true, requireResponseForCase: true);
        Assert.Equal(ImportanceDecision.NotImportant, result.Decision);
        Assert.Contains("Not a legitimate", result.Reason);
    }

    [Fact]
    public void NoResponseNeeded_IsNotImportant_WhenRequired()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.95, null, null, null, legitimate: true, responseExpected: false, requireResponseForCase: true);
        Assert.Equal(ImportanceDecision.NotImportant, result.Decision);
        Assert.Contains("no response is needed", result.Reason);
    }

    [Fact]
    public void NoResponseNeeded_StillImportant_WhenRuleSwitchedOff()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.95, null, null, null, legitimate: true, responseExpected: false, requireResponseForCase: false);
        Assert.Equal(ImportanceDecision.Important, result.Decision);
    }

    [Fact]
    public void LegitimateAndNeedsResponse_IsImportant()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.95, null, null, null, legitimate: true, responseExpected: true, requireResponseForCase: true);
        Assert.Equal(ImportanceDecision.Important, result.Decision);
    }

    /// <summary>An unsure AI call goes to review — it is never silently dropped as "not legitimate" or "no response".</summary>
    [Fact]
    public void LowConfidence_GoesToReview_EvenIfAiSaysNoResponse()
    {
        var result = ClassificationDecisionPolicy.Decide(true, 0.3, null, null, null, legitimate: false, responseExpected: false, requireResponseForCase: true);
        Assert.Equal(ImportanceDecision.ReviewRequired, result.Decision);
    }

    [Fact]
    public async Task Provider_SendsRulesToAi_AndReadsLegitimate()
    {
        string? body = null;
        var handler = new FakeHttpMessageHandler
        {
            Behavior = async (req, _) =>
            {
                body = await req.Content!.ReadAsStringAsync();
                const string json = """
                    {"choices":[{"message":{"content":"{\"relevant\":true,\"legitimate\":false,\"category\":\"OTHER\",\"action_required\":false,\"response_expected\":false,\"priority\":\"LOW\",\"confidence\":0.9,\"summary\":\"Newsletter\"}"}}]}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            },
        };
        var provider = new OpenRouterClassificationProvider(new HttpClient(handler), new StaticAiProviderConnectionResolver("sk-test-key"),
            Options.Create(new AiClassificationOptions()), NullLogger<OpenRouterClassificationProvider>.Instance);
        var request = new ClassificationRequest("Weekly deals", "Buy now", "promo@shop.example", "sales@sawo.com", false, "Sales", "", "", "",
            LegitimacyRules: "Marketing blasts are never legitimate.", ResponseRules: "Quote requests always need a reply.");

        var result = await provider.ClassifyAsync(request, "openai/gpt-4o-mini", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Contains("Marketing blasts are never legitimate.", body);
        Assert.Contains("Quote requests always need a reply.", body);
        Assert.Contains("legitimate", body);
        Assert.False(result.Response!.Legitimate);
        Assert.False(result.Response.ResponseExpected);
    }

    [Theory]
    [InlineData("on", "true")]
    [InlineData("FALSE", "false")]
    public async Task BooleanSetting_Normalizes(string input, string stored)
    {
        using var db = TestDbContext.CreateNew();
        var service = new SystemSettingsService(db, new FakeCurrentUserService(roles: SystemRole.Administrator), new NoOpAuditService());
        var result = await service.UpdateAsync(new UpdateSystemSettingsRequest(new() { [SystemSettingKeys.RequireResponseForCase] = input }), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(stored, await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.RequireResponseForCase, CancellationToken.None));
    }

    [Fact]
    public async Task Defaults_HaveRulesAndRequireResponse()
    {
        using var db = TestDbContext.CreateNew();
        Assert.True(await SystemSettingsService.GetBoolAsync(db, SystemSettingKeys.RequireResponseForCase, CancellationToken.None));
        Assert.Contains("Spam", await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.NotLegitimateRules, CancellationToken.None));
        var (legitimacy, response) = await EmailRules.LoadForPromptAsync(db, CancellationToken.None);
        Assert.Contains("NOT legitimate:\n- Spam, phishing or scam attempts", legitimacy);
        Assert.Contains("NO response is needed when:\n- FYI or CC-only updates", response);
    }

    [Fact]
    public async Task RuleList_IsTidied_AndLimited()
    {
        using var db = TestDbContext.CreateNew();
        var service = new SystemSettingsService(db, new FakeCurrentUserService(roles: SystemRole.Administrator), new NoOpAuditService());

        var ok = await service.UpdateAsync(new UpdateSystemSettingsRequest(new()
        {
            [SystemSettingKeys.NotLegitimateRules] = "- Crypto offers\r\n\n  • Lottery wins  \ncrypto offers",
        }), CancellationToken.None);
        Assert.True(ok.Succeeded, ok.Error);
        Assert.Equal("Crypto offers\nLottery wins", await SystemSettingsService.GetValueAsync(db, SystemSettingKeys.NotLegitimateRules, CancellationToken.None));

        var tooMany = await service.UpdateAsync(new UpdateSystemSettingsRequest(new()
        {
            [SystemSettingKeys.NotLegitimateRules] = string.Join("\n", Enumerable.Range(1, 31).Select(i => $"Rule {i}")),
        }), CancellationToken.None);
        Assert.False(tooMany.Succeeded);
    }
}
