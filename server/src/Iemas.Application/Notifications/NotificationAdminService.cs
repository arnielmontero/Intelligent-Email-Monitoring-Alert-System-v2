using Iemas.Application.Common;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.Notifications;

public record NotificationTemplateDto(
    string Type,
    bool Enabled,
    string Title,
    string MessageText,
    bool IsCustomized,
    string? UpdatedByEmail,
    DateTimeOffset? UpdatedAt);

public record UpdateNotificationTemplateRequest(bool Enabled, string Title, string MessageText);

public record NotificationTemplatePreviewDto(string Title, string Message);

public record NotificationDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    Guid EmployeeId,
    string EmployeeName,
    Guid? CaseId,
    string? CaseNumber,
    string Type,
    string Status,
    string Title,
    string Message,
    int DeliveredAgentCount,
    DateTimeOffset? SentAt,
    DateTimeOffset? AcknowledgedAt,
    string? FailureReason);

public record NotificationTemplatesResponse(List<NotificationTemplateDto> Templates, IReadOnlyList<string> SupportedVariables);

/// <summary>Requirements §51 (CMS list of predefined, editable notification messages) and §104 (delivery tracking view).</summary>
public class NotificationAdminService
{
    private static readonly Dictionary<string, string> SampleVariables = new()
    {
        ["employee_name"] = "John",
        ["email_subject"] = "Quotation for 20 units",
        ["sender_name"] = "Maria Santos",
        ["sender_email"] = "maria@customer.example",
        ["received_at"] = "2026-01-15 08:15 (UTC)",
        ["ai_summary"] = "Customer requests a quotation for 20 units with delivery by end of month.",
        ["elapsed_time"] = "4h 12m",
        ["reminder_count"] = "2",
        ["case_id"] = "CASE-000123",
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _auditService;

    public NotificationAdminService(IAppDbContext db, ICurrentUserService currentUser, IAuditService auditService)
    {
        _db = db;
        _currentUser = currentUser;
        _auditService = auditService;
    }

    public async Task<NotificationTemplatesResponse> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var stored = await _db.NotificationTemplates.AsNoTracking().ToListAsync(cancellationToken);
        var templates = Enum.GetValues<NotificationType>().Select(type =>
        {
            var row = stored.FirstOrDefault(t => t.Type == type);
            var effective = row ?? NotificationTemplateDefaults.For(type);
            var defaults = NotificationTemplateDefaults.For(type);
            var customized = row is not null
                && (row.Title != defaults.Title || row.MessageText != defaults.MessageText || row.Enabled != defaults.Enabled);
            return new NotificationTemplateDto(
                type.ToString(), effective.Enabled, effective.Title, effective.MessageText,
                customized, row?.UpdatedByEmail, row?.UpdatedAt);
        }).ToList();

        return new NotificationTemplatesResponse(templates, NotificationTemplateDefaults.SupportedVariables);
    }

    public async Task<Result<NotificationTemplateDto>> UpdateTemplateAsync(string typeName, UpdateNotificationTemplateRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseType(typeName, out var type))
        {
            return Result<NotificationTemplateDto>.Failure($"Unknown notification type '{typeName}'.");
        }

        var title = request.Title?.Trim() ?? string.Empty;
        var message = request.MessageText?.Trim() ?? string.Empty;
        if (ValidateContent(title, message) is { } error)
        {
            return Result<NotificationTemplateDto>.Failure(error);
        }

        return Result<NotificationTemplateDto>.Success(
            await SaveAsync(type, request.Enabled, title, message, "NOTIFICATION_TEMPLATE_UPDATED", cancellationToken));
    }

    public async Task<Result<NotificationTemplateDto>> ResetTemplateAsync(string typeName, CancellationToken cancellationToken)
    {
        if (!TryParseType(typeName, out var type))
        {
            return Result<NotificationTemplateDto>.Failure($"Unknown notification type '{typeName}'.");
        }

        var defaults = NotificationTemplateDefaults.For(type);
        return Result<NotificationTemplateDto>.Success(
            await SaveAsync(type, defaults.Enabled, defaults.Title, defaults.MessageText, "NOTIFICATION_TEMPLATE_RESET", cancellationToken));
    }

    public Result<NotificationTemplatePreviewDto> Preview(UpdateNotificationTemplateRequest request)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var message = request.MessageText?.Trim() ?? string.Empty;
        if (ValidateContent(title, message) is { } error)
        {
            return Result<NotificationTemplatePreviewDto>.Failure(error);
        }

        return Result<NotificationTemplatePreviewDto>.Success(new NotificationTemplatePreviewDto(
            NotificationService.Render(title, SampleVariables),
            NotificationService.Render(message, SampleVariables)));
    }

    public async Task<List<NotificationDto>> SearchAsync(
        Guid? employeeId, Guid? caseId, string? status, string? type, DateTimeOffset? from, DateTimeOffset? to, int take,
        CancellationToken cancellationToken)
    {
        var query = _db.Notifications.AsNoTracking().AsQueryable();

        if (employeeId is Guid employee) query = query.Where(n => n.EmployeeId == employee);
        if (caseId is Guid caseValue) query = query.Where(n => n.CaseId == caseValue);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<NotificationDeliveryStatus>(status, true, out var statusValue))
        {
            query = query.Where(n => n.Status == statusValue);
        }
        if (!string.IsNullOrWhiteSpace(type) && TryParseType(type, out var typeValue))
        {
            query = query.Where(n => n.Type == typeValue);
        }
        if (from is DateTimeOffset fromValue) query = query.Where(n => n.CreatedAt >= fromValue);
        if (to is DateTimeOffset toValue) query = query.Where(n => n.CreatedAt <= toValue);

        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(n => new
            {
                n.Id, n.CreatedAt, n.EmployeeId, EmployeeName = n.Employee.FullName, n.CaseId,
                CaseNumber = n.Case != null ? n.Case.CaseNumber : null, n.Type, n.Status, n.Title, n.Message,
                n.DeliveredAgentCount, n.SentAt, n.AcknowledgedAt, n.FailureReason,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(n => new NotificationDto(
            n.Id, n.CreatedAt, n.EmployeeId, n.EmployeeName, n.CaseId, n.CaseNumber, n.Type.ToString(), n.Status.ToString(),
            n.Title, n.Message, n.DeliveredAgentCount, n.SentAt, n.AcknowledgedAt, n.FailureReason)).ToList();
    }

    private async Task<NotificationTemplateDto> SaveAsync(
        NotificationType type, bool enabled, string title, string message, string auditAction, CancellationToken cancellationToken)
    {
        var row = await _db.NotificationTemplates.FirstOrDefaultAsync(t => t.Type == type, cancellationToken);
        if (row is null)
        {
            row = new NotificationTemplate { Type = type };
            _db.NotificationTemplates.Add(row);
        }

        row.Enabled = enabled;
        row.Title = title;
        row.MessageText = message;
        row.UpdatedByUserId = _currentUser.UserId;
        row.UpdatedByEmail = _currentUser.Email;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(auditAction, "NotificationTemplate", type.ToString(),
            $"Enabled={enabled}; Title=\"{title}\"", cancellationToken);

        var defaults = NotificationTemplateDefaults.For(type);
        var customized = title != defaults.Title || message != defaults.MessageText || enabled != defaults.Enabled;
        return new NotificationTemplateDto(type.ToString(), enabled, title, message, customized, row.UpdatedByEmail, row.UpdatedAt);
    }

    private static string? ValidateContent(string title, string message)
    {
        if (title.Length == 0 || title.Length > 200) return "Title must be between 1 and 200 characters.";
        if (message.Length == 0 || message.Length > 2000) return "Message text must be between 1 and 2000 characters.";
        if (InputSanitizer.ValidateFreeText("Title", title) is { } titleError) return titleError;
        if (InputSanitizer.ValidateFreeText("Message text", message) is { } messageError) return messageError;

        var unknown = NotificationService.FindUnknownVariables(title).Concat(NotificationService.FindUnknownVariables(message)).Distinct().ToList();
        return unknown.Count > 0
            ? $"Unsupported variable(s): {string.Join(", ", unknown.Select(v => "{{" + v + "}}"))}."
            : null;
    }

    private static bool TryParseType(string value, out NotificationType type) =>
        Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);
}
