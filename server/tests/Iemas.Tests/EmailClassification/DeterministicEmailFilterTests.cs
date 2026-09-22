using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Xunit;

namespace Iemas.Tests.EmailClassification;

/// <summary>§27/§28 — deterministic pre-filter, tested independently of any AI call.</summary>
public class DeterministicEmailFilterTests
{
    private static ClassificationProfile SalesProfile() => new()
    {
        Name = "Sales",
        IncludeDefinitions = "price\nquotation",
        ExcludeDefinitions = "newsletter\nunsubscribe",
    };

    [Fact]
    public void Evaluate_ExcludeTermMatch_ShortCircuitsWithoutSendingToAi()
    {
        var result = DeterministicEmailFilter.Evaluate("Weekly Newsletter", "Unsubscribe anytime.", SalesProfile());
        Assert.False(result.ShouldSendToAi);
        Assert.Contains("newsletter", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_IncludeTermMatch_SendsToAi()
    {
        var result = DeterministicEmailFilter.Evaluate("Price request", "What is your price?", SalesProfile());
        Assert.True(result.ShouldSendToAi);
        Assert.Contains("price", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ExcludeTakesPriorityOverInclude_WhenBothMatch()
    {
        var result = DeterministicEmailFilter.Evaluate("Newsletter: our latest prices", "Unsubscribe if you no longer want updates on our prices.", SalesProfile());
        Assert.False(result.ShouldSendToAi);
        Assert.Contains("newsletter", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NoKeywordMatchEitherWay_StillDefersToAi_NotRejectedOnKeywordAbsenceAlone()
    {
        // §27 — semantic classification must not rely on keywords alone; absence of a keyword
        // match must not by itself reject the message.
        var result = DeterministicEmailFilter.Evaluate("Question about your products", "Do you sell to resellers?", SalesProfile());
        Assert.True(result.ShouldSendToAi);
    }

    [Fact]
    public void Evaluate_NoProfileConfigured_DefersToAi()
    {
        var result = DeterministicEmailFilter.Evaluate("Anything", "Anything", null);
        Assert.True(result.ShouldSendToAi);
    }

    [Fact]
    public void Evaluate_ProfileWithEmptyIncludeList_DefersToAi_WhenNoExcludeMatches()
    {
        var profile = new ClassificationProfile { Name = "Empty", IncludeDefinitions = "", ExcludeDefinitions = "spam" };
        var result = DeterministicEmailFilter.Evaluate("Hello", "Just checking in.", profile);
        Assert.True(result.ShouldSendToAi);
    }
}
