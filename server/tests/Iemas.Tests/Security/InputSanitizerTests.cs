using Iemas.Application.Common.Security;
using Xunit;

namespace Iemas.Tests.Security;

/// <summary>
/// Regression coverage for the Phase 11 stored-XSS finding: free-text fields (Employee.FullName
/// and equivalents across other services) previously accepted and persisted markup verbatim with
/// no server-side check. These tests exercise the shared write-boundary validator directly;
/// per-service wiring is covered by each service's own tests (e.g.
/// EmployeeServiceTests.CreateAsync_RejectsHtmlMarkupInFullName).
/// </summary>
public class InputSanitizerTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Normal Name<img src=x onerror=alert(1)>")]
    [InlineData("only a < angle bracket")]
    [InlineData("only a > angle bracket")]
    public void ValidateFreeText_RejectsAnyAngleBracket(string value)
    {
        var error = InputSanitizer.ValidateFreeText("Field", value);

        Assert.NotNull(error);
        Assert.Contains("Field", error);
    }

    [Theory]
    [InlineData("Jane Doe")]
    [InlineData("O'Brien")]
    [InlineData("Smith & Co")]
    [InlineData("Support Team (Tier 2)")]
    [InlineData("")]
    public void ValidateFreeText_AllowsOrdinaryNamesWithoutMarkup(string value)
    {
        var error = InputSanitizer.ValidateFreeText("Field", value);

        Assert.Null(error);
    }
}
