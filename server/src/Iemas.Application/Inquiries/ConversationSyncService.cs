using System.Diagnostics;
using System.Text.RegularExpressions;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Domain.Cases;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iemas.Application.Inquiries;

public sealed record ConversationSyncRunResult(int CasesChecked, int AgentMessagesStored, int InternalMessagesStored, int MailboxFailures, long DurationMs);

/// <summary>
/// Phase 2 (G5) - keeps the agent's side of every open inquiry's conversation. Unlike the older
/// reply check, which stopped at the first reply, this reads the Sent folder for every inquiry that
/// is still supervised and stores each agent message that belongs to it (read-only; IEMAS never
/// sends or changes customer email). Each stored message advances the lifecycle: the first reply
/// is "Initial response sent", later ones "Agent follow-up sent", internal-only forwards are
/// internal activity.
///
/// Only messages that belong to a supervised inquiry are stored - unrelated sent mail is never
/// kept. The text is masked and encrypted (Q4); attachments are counted, never stored.
/// </summary>
public class ConversationSyncService
{
    private const int MaxSentMessagesPerMailbox = 200;
    private static readonly TimeSpan SyncOverlap = TimeSpan.FromDays(1);
    private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);

    private readonly IAppDbContext _db;
    private readonly ICredentialEncryptionService _encryption;
    private readonly IEmailProviderAdapterResolver _adapters;
    private readonly InquiryConversationService _conversation;
    private readonly AgentReplyProtector _protector;
    private readonly ILogger<ConversationSyncService>? _logger;

    public ConversationSyncService(
        IAppDbContext db, ICredentialEncryptionService encryption, IEmailProviderAdapterResolver adapters,
        InquiryConversationService conversation, AgentReplyProtector protector, ILogger<ConversationSyncService>? logger = null)
    {
        _db = db;
        _encryption = encryption;
        _adapters = adapters;
        _conversation = conversation;
        _protector = protector;
        _logger = logger;
    }

    public async Task<ConversationSyncRunResult> RunAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var openCases = await _db.Cases
            .Where(c => c.Stage != InquiryStage.Resolved && c.Stage != InquiryStage.Closed)
            .OrderBy(c => c.ReplyLastCheckedAt ?? DateTimeOffset.MinValue)
            .Take(batchSize)
            .Select(c => new { c.Id, c.EmailAccountId })
            .ToListAsync(cancellationToken);

        int agentStored = 0, internalStored = 0, failures = 0;
        foreach (var group in openCases.GroupBy(c => c.EmailAccountId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (agent, internalCount, failed) = await SyncAccountAsync(group.Key, group.Select(c => c.Id).ToList(), cancellationToken);
            agentStored += agent;
            internalStored += internalCount;
            if (failed) failures++;
        }

        return new ConversationSyncRunResult(openCases.Count, agentStored, internalStored, failures, stopwatch.ElapsedMilliseconds);
    }

    private async Task<(int Agent, int Internal, bool Failed)> SyncAccountAsync(Guid accountId, List<Guid> caseIds, CancellationToken cancellationToken)
    {
        var account = await _db.EmailAccounts.Include(a => a.Credential).FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        var cases = await _db.Cases.Where(c => caseIds.Contains(c.Id)).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // Sample mailboxes have no server and nobody can reply from them.
        if (account is null || account.Host == Operations.SampleDataService.SampleMailboxHost || account.Credential is null)
        {
            foreach (var c in cases) c.ReplyLastCheckedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return (0, 0, account is not null && account.Host != Operations.SampleDataService.SampleMailboxHost);
        }

        IReadOnlyCollection<ProviderMessage> sent;
        try
        {
            var secret = _encryption.Decrypt(new(account.Credential.EncryptedSecret, account.Credential.Nonce, account.Credential.Tag, account.Credential.KeyId));
            var settings = new EmailProviderConnectionSettings(account.Protocol, account.Host, account.Port, account.Encryption, account.Username, account.AuthMethod, secret);
            var since = cases.Min(c => c.ConversationSyncedThrough is DateTimeOffset through ? through - SyncOverlap : c.FirstEmailReceivedAt);
            var result = await _adapters.Resolve(account.Protocol).FetchSentMessagesAsync(settings, since, MaxSentMessagesPerMailbox, cancellationToken);
            if (!result.FolderAccessible)
            {
                _logger?.LogWarning("Sent folder of {Mailbox} not accessible: {Error}", account.EmailAddress, result.AccessError);
                return (0, 0, true);
            }
            sent = result.Messages;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never treated as "no reply": nothing changes, the next run tries again.
            _logger?.LogWarning(ex, "Sent folder of {Mailbox} could not be read", account.EmailAddress);
            return (0, 0, true);
        }

        var links = await _db.CaseEmails
            .Where(ce => caseIds.Contains(ce.CaseId))
            .Select(ce => new { ce.CaseId, ce.EmailMessage.MessageId, ce.EmailMessage.ThreadId })
            .ToListAsync(cancellationToken);

        int agentStored = 0, internalStored = 0;
        foreach (var message in sent.OrderBy(m => m.ReceivedAt))
        {
            var providerId = $"sent:{message.ProviderMessageId}";
            var known = await _db.EmailMessages.AnyAsync(m => m.EmailAccountId == accountId
                && (m.ProviderMessageId == providerId || (message.MessageId != null && m.MessageId == message.MessageId)), cancellationToken);
            if (known) continue;

            var (target, signal, detail) = Assign(message, cases, links.Select(l => (l.CaseId, l.MessageId, l.ThreadId)).ToList());
            if (target is null) continue;

            var role = await _conversation.OutboundRoleAsync(target.CustomerEmailAddress, message.ToAddresses.Concat(message.CcAddresses), cancellationToken);
            var entity = new EmailMessage
            {
                EmailAccountId = accountId,
                Provider = account.Protocol,
                ProviderMessageId = providerId,
                MessageId = message.MessageId,
                ThreadId = message.ThreadId,
                InReplyTo = message.InReplyTo,
                References = message.References.Count > 0 ? string.Join(' ', message.References) : null,
                FromAddress = message.FromAddress,
                FromDisplayName = message.FromDisplayName,
                ToAddresses = string.Join(';', message.ToAddresses),
                CcAddresses = message.CcAddresses.Count > 0 ? string.Join(';', message.CcAddresses) : null,
                Subject = message.Subject,
                BodyText = null,
                BodyHtml = null,
                BodyTextEncrypted = _protector.Protect(message.BodyText ?? HtmlTag.Replace(message.BodyHtml ?? string.Empty, " ")),
                ReceivedAt = message.ReceivedAt,
                AttachmentCount = message.Attachments.Count,
                Direction = MessageDirection.Outbound,
                ProcessingStatus = EmailProcessingStatus.Outbound,
                CaseId = target.Id,
            };
            _db.EmailMessages.Add(entity);
            _db.CaseEmails.Add(new CaseEmail { CaseId = target.Id, EmailMessageId = entity.Id, MatchSignal = signal, MatchDetail = detail, Role = role });
            links.Add(new { CaseId = target.Id, entity.MessageId, entity.ThreadId });

            var wasReplied = target.ReplyStatus == CaseReplyStatus.Replied;
            _conversation.OnOutboundAttached(target, entity, role);
            if (role == ConversationRole.Agent)
            {
                agentStored++;
                if (!wasReplied)
                {
                    _db.ReplyVerificationAttempts.Add(new ReplyVerificationAttempt
                    {
                        CaseId = target.Id,
                        Outcome = ReplyVerificationOutcome.VerifiedReply,
                        MatchedSentMessageId = message.MessageId,
                        MatchSignal = ToReplySignal(signal),
                        MatchDetail = detail,
                    });
                }
            }
            else
            {
                internalStored++;
            }

            target.LastActivityAt = message.ReceivedAt > target.LastActivityAt ? message.ReceivedAt : target.LastActivityAt;
            target.ConversationSyncedThrough = target.ConversationSyncedThrough is DateTimeOffset t && t > message.ReceivedAt ? t : message.ReceivedAt;
        }

        foreach (var c in cases) c.ReplyLastCheckedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return (agentStored, internalStored, false);
    }

    /// <summary>
    /// Which open inquiry a sent message belongs to: a real thread relationship first (thread id,
    /// In-Reply-To, References - including the agent's own earlier replies), then a message to the
    /// customer sent after the inquiry began (the most recently active inquiry for that customer).
    /// Never by subject.
    /// </summary>
    private static (Case? Case, CaseMatchSignal Signal, string? Detail) Assign(
        ProviderMessage message, List<Case> cases, List<(Guid CaseId, string? MessageId, string? ThreadId)> links)
    {
        Case? ById(Guid id) => cases.FirstOrDefault(c => c.Id == id);

        if (!string.IsNullOrWhiteSpace(message.ThreadId))
        {
            var hit = links.FirstOrDefault(l => l.ThreadId == message.ThreadId);
            if (hit.CaseId != Guid.Empty) return (ById(hit.CaseId), CaseMatchSignal.ThreadId, $"ThreadId={message.ThreadId}");
        }

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            var hit = links.FirstOrDefault(l => l.MessageId == message.InReplyTo);
            if (hit.CaseId != Guid.Empty) return (ById(hit.CaseId), CaseMatchSignal.InReplyTo, $"InReplyTo={message.InReplyTo}");
        }

        var refs = message.References.ToHashSet();
        if (refs.Count > 0)
        {
            var hit = links.LastOrDefault(l => l.MessageId != null && refs.Contains(l.MessageId));
            if (hit.CaseId != Guid.Empty) return (ById(hit.CaseId), CaseMatchSignal.References, "References contains a message of this inquiry");
        }

        var byRecipient = cases
            .Where(c => message.ReceivedAt >= c.FirstEmailReceivedAt
                && message.ToAddresses.Concat(message.CcAddresses).Any(a => string.Equals(a, c.CustomerEmailAddress, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefault();
        return byRecipient is null
            ? (null, CaseMatchSignal.NewCase, null)
            : (byRecipient, CaseMatchSignal.ParticipantAndAccountRelationship, $"Sent to {byRecipient.CustomerEmailAddress}");
    }

    private static ReplyMatchSignal ToReplySignal(CaseMatchSignal signal) => signal switch
    {
        CaseMatchSignal.ThreadId => ReplyMatchSignal.ThreadId,
        CaseMatchSignal.InReplyTo => ReplyMatchSignal.InReplyTo,
        CaseMatchSignal.References => ReplyMatchSignal.References,
        _ => ReplyMatchSignal.RecipientAndAccountRelationship,
    };
}
