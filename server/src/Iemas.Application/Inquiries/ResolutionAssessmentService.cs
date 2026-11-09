using Iemas.Application.AiUsage;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Domain.Ai;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Inquiries;

/// <summary>
/// Phase 5 - asks the AI, whenever an inquiry's conversation has changed, whether the customer's
/// concern appears addressed. Distinguishes acknowledgement from resolution: "we'll look into it" is
/// AcknowledgedOnly. Only an AppearsResolved verdict at or above the configured confidence moves the
/// inquiry to Potentially resolved and asks the agent to confirm - nothing here ever closes an inquiry.
/// </summary>
public class ResolutionAssessmentService
{
    /// <summary>After a failed AI call an inquiry is left alone this long, so a missing key or an outage is not retried every minute for every inquiry.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> LastFailure = new();

    private const int MaxCharsPerTurn = 2500;
    private const int MaxConversationChars = 14000;

    private readonly IAppDbContext _db;
    private readonly IAiResolutionAssessor _assessor;
    private readonly InquiryConversationService _conversation;
    private readonly AgentReplyProtector _protector;
    private readonly IAiUsageRecorder? _usage;

    public ResolutionAssessmentService(
        IAppDbContext db, IAiResolutionAssessor assessor, InquiryConversationService conversation, AgentReplyProtector protector, IAiUsageRecorder? usage = null)
    {
        _db = db;
        _assessor = assessor;
        _conversation = conversation;
        _protector = protector;
        _usage = usage;
    }

    /// <summary>Assesses up to <paramref name="max"/> inquiries whose conversation changed since their last assessment.</summary>
    public async Task<int> AssessChangedConversationsAsync(SupervisionSettings settings, int max, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var candidates = await _db.Cases
            .Where(c => c.FirstAgentResponseAt != null
                && c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed && c.Stage != InquiryStage.AwaitingInitialResponse
                && _db.CaseEmails.Count(ce => ce.CaseId == c.Id) != c.ResolutionAssessedMessageCount)
            .OrderBy(c => c.ResolutionAssessedAt ?? DateTimeOffset.MinValue)
            .Take(max)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var assessed = 0;
        var clock = at ?? DateTimeOffset.UtcNow;
        foreach (var caseId in candidates)
        {
            if (LastFailure.TryGetValue(caseId, out var failedAt) && clock - failedAt < FailureBackoff) continue;
            if (await AssessAsync(caseId, settings, cancellationToken, at)) assessed++;
        }
        return assessed;
    }

    public async Task<bool> AssessAsync(Guid caseId, SupervisionSettings settings, CancellationToken cancellationToken, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        if (c is null || InquiryLifecycle.IsTerminal(c.Stage)) return false;

        var model = await _db.AiModelConfigs.AsNoTracking()
            .Where(m => m.Enabled && m.TaskCapability == "EmailClassification")
            .OrderBy(m => m.FallbackOrder)
            .FirstOrDefaultAsync(cancellationToken);
        if (model is null) return false;

        var messages = await _db.CaseEmails.AsNoTracking()
            .Where(ce => ce.CaseId == caseId)
            .OrderBy(ce => ce.EmailMessage.ReceivedAt)
            .Select(ce => new { ce.Role, ce.EmailMessage.Direction, ce.EmailMessage.ReceivedAt, ce.EmailMessage.Subject, ce.EmailMessage.BodyText, ce.EmailMessage.BodyTextEncrypted })
            .ToListAsync(cancellationToken);

        var turns = messages.Select(m => new ConversationTurn(
            m.Role.ToString(), m.ReceivedAt,
            m.Role == ConversationRole.Internal
                ? $"(internal message, not shown to the customer: \"{m.Subject}\")"
                : Clip(m.Direction == MessageDirection.Outbound ? _protector.Reveal(m.BodyTextEncrypted) ?? "(text no longer stored)" : AgentReplyProtector.Sanitize(m.BodyText))))
            .ToList();

        // Keep the most recent turns when the conversation is long; the latest state matters most.
        while (turns.Count > 1 && turns.Sum(t => t.Text.Length) > MaxConversationChars) turns.RemoveAt(1);

        var result = await _assessor.AssessAsync(new ResolutionAssessmentRequest(caseId, c.Subject, turns), model.ModelIdentifier,
            TimeSpan.FromSeconds(model.TimeoutSeconds), cancellationToken);

        if (_usage is not null)
        {
            await _usage.RecordAsync(new AiUsageRecord
            {
                Provider = result.Provider, ModelIdentifier = model.ModelIdentifier, Purpose = AiUsagePurpose.ResolutionAssessment,
                Succeeded = result.Succeeded, ErrorMessage = result.Error is { Length: > 1000 } e ? e[..1000] : result.Error, DurationMs = result.DurationMs,
                PromptTokens = result.Usage?.PromptTokens, CompletionTokens = result.Usage?.CompletionTokens, TotalTokens = result.Usage?.TotalTokens,
                CostUsd = result.Usage?.CostUsd, GenerationId = result.Usage?.GenerationId,
            }, CancellationToken.None);
        }

        // A failed call is retried later (after the backoff): the message count is only recorded on success.
        if (!result.Succeeded || result.Verdict is not ResolutionVerdict verdict)
        {
            LastFailure[caseId] = now;
            return false;
        }
        LastFailure.TryRemove(caseId, out _);

        c.ResolutionVerdict = verdict;
        c.ResolutionConfidence = result.Confidence;
        c.ResolutionReason = result.Reason is { Length: > 1000 } r ? r[..1000] : result.Reason;
        c.ResolutionAssessedAt = now;
        c.ResolutionAssessedMessageCount = messages.Count;
        _db.CaseEvents.Add(new CaseEvent
        {
            CaseId = c.Id,
            EventType = CaseEventType.ResolutionAssessment,
            OccurredAt = now,
            Detail = $"AI assessment: {Describe(verdict)} (confidence {result.Confidence:P0}). {c.ResolutionReason}",
        });
        await _db.SaveChangesAsync(cancellationToken);

        if (verdict == ResolutionVerdict.AppearsResolved && result.Confidence * 100 >= settings.AiResolutionMinConfidencePercent)
        {
            await _conversation.MarkPotentiallyResolvedAsync(c,
                $"The AI judged the customer's concern addressed ({result.Confidence:P0}): {c.ResolutionReason} The agent must confirm.", now, cancellationToken);
        }
        return true;
    }

    public static string Describe(ResolutionVerdict verdict) => verdict switch
    {
        ResolutionVerdict.AcknowledgedOnly => "acknowledged only, concern still open",
        ResolutionVerdict.InProgress => "in progress, not yet resolved",
        ResolutionVerdict.AppearsResolved => "appears resolved",
        ResolutionVerdict.CustomerWaiting => "customer is waiting on the agent",
        _ => verdict.ToString(),
    };

    private static string Clip(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length > MaxCharsPerTurn ? t[..MaxCharsPerTurn] + " ..." : t;
    }
}
