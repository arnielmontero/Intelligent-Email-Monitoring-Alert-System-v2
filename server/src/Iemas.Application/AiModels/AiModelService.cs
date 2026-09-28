using Iemas.Application.AiModels.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.AiModels;

/// <summary>Requirements §82 — AI Provider/Model Management CRUD. The catalog is never hardcoded; this is the only place models are added/edited/enabled/defaulted.</summary>
public class AiModelService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IAiClassificationProvider _aiProvider;

    public AiModelService(IAppDbContext db, IAuditService auditService, IAiClassificationProvider aiProvider)
    {
        _db = db;
        _auditService = auditService;
        _aiProvider = aiProvider;
    }

    public async Task<List<AiModelDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await ProjectAndOrder(_db.AiModelConfigs.AsNoTracking()).ToListAsync(cancellationToken);
    }

    private static IQueryable<AiModelDto> ProjectAndOrder(IQueryable<AiModelConfig> source)
    {
        return source
            .OrderBy(m => m.FallbackOrder)
            .Select(m => new AiModelDto(
                m.Id, m.Provider, m.ModelIdentifier, m.DisplayName, m.Enabled, m.IsDefault,
                m.TaskCapability, m.TimeoutSeconds, m.MaxRetries, m.FallbackOrder));
    }

    public async Task<Result<AiModelDto>> CreateAsync(CreateAiModelRequest request, CancellationToken cancellationToken)
    {
        var modelIdentifier = request.ModelIdentifier.Trim();
        if (string.IsNullOrWhiteSpace(modelIdentifier))
        {
            return Result<AiModelDto>.Failure("A model identifier is required.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? modelIdentifier : request.DisplayName.Trim();
        if (InputSanitizer.ValidateFreeText("Display name", displayName) is { } displayNameError)
        {
            return Result<AiModelDto>.Failure(displayNameError);
        }

        if (await _db.AiModelConfigs.AnyAsync(m => m.Provider == request.Provider && m.ModelIdentifier == modelIdentifier, cancellationToken))
        {
            return Result<AiModelDto>.Failure("This provider/model combination is already configured.");
        }

        var model = new AiModelConfig
        {
            Provider = request.Provider.Trim(),
            ModelIdentifier = modelIdentifier,
            DisplayName = displayName,
            Enabled = request.Enabled,
            TaskCapability = string.IsNullOrWhiteSpace(request.TaskCapability) ? "EmailClassification" : request.TaskCapability.Trim(),
            TimeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 30,
            MaxRetries = request.MaxRetries >= 0 ? request.MaxRetries : 2,
            FallbackOrder = request.FallbackOrder,
        };

        if (request.IsDefault)
        {
            await ClearExistingDefaultAsync(model.TaskCapability, cancellationToken);
            model.IsDefault = true;
        }

        _db.AiModelConfigs.Add(model);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("AI_MODEL_CREATED", "AiModelConfig", model.Id.ToString(), $"{model.Provider}/{model.ModelIdentifier}", cancellationToken);

        return Result<AiModelDto>.Success(new AiModelDto(model.Id, model.Provider, model.ModelIdentifier, model.DisplayName, model.Enabled, model.IsDefault, model.TaskCapability, model.TimeoutSeconds, model.MaxRetries, model.FallbackOrder));
    }

    public async Task<Result<AiModelDto>> UpdateAsync(Guid id, UpdateAiModelRequest request, CancellationToken cancellationToken)
    {
        var model = await _db.AiModelConfigs.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (model is null)
        {
            return Result<AiModelDto>.Failure("AI model configuration not found.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? model.ModelIdentifier : request.DisplayName.Trim();
        if (InputSanitizer.ValidateFreeText("Display name", displayName) is { } displayNameError)
        {
            return Result<AiModelDto>.Failure(displayNameError);
        }

        var newIdentifier = request.ModelIdentifier?.Trim();
        if (!string.IsNullOrEmpty(newIdentifier) && newIdentifier != model.ModelIdentifier)
        {
            if (newIdentifier.Any(char.IsWhiteSpace) || InputSanitizer.ValidateFreeText("Model identifier", newIdentifier) is not null)
            {
                return Result<AiModelDto>.Failure("Model identifier cannot contain spaces or '<' '>' characters.");
            }
            if (await _db.AiModelConfigs.AnyAsync(m => m.Id != id && m.Provider == model.Provider && m.ModelIdentifier == newIdentifier, cancellationToken))
            {
                return Result<AiModelDto>.Failure("This provider/model combination is already configured.");
            }
            if (model.DisplayName == model.ModelIdentifier && string.IsNullOrWhiteSpace(request.DisplayName))
            {
                displayName = newIdentifier;
            }
            model.ModelIdentifier = newIdentifier;
        }

        model.DisplayName = displayName;
        model.Enabled = request.Enabled;
        model.TaskCapability = string.IsNullOrWhiteSpace(request.TaskCapability) ? model.TaskCapability : request.TaskCapability.Trim();
        model.TimeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : model.TimeoutSeconds;
        model.MaxRetries = request.MaxRetries >= 0 ? request.MaxRetries : model.MaxRetries;
        model.FallbackOrder = request.FallbackOrder;
        model.UpdatedAt = DateTimeOffset.UtcNow;

        if (request.IsDefault && !model.IsDefault)
        {
            await ClearExistingDefaultAsync(model.TaskCapability, cancellationToken);
            model.IsDefault = true;
        }
        else if (!request.IsDefault)
        {
            model.IsDefault = false;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("AI_MODEL_UPDATED", "AiModelConfig", model.Id.ToString(), $"{model.Provider}/{model.ModelIdentifier}", cancellationToken);

        return Result<AiModelDto>.Success(new AiModelDto(model.Id, model.Provider, model.ModelIdentifier, model.DisplayName, model.Enabled, model.IsDefault, model.TaskCapability, model.TimeoutSeconds, model.MaxRetries, model.FallbackOrder));
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var model = await _db.AiModelConfigs.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (model is null)
        {
            return Result<bool>.Failure("AI model configuration not found.");
        }

        _db.AiModelConfigs.Remove(model);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("AI_MODEL_DELETED", "AiModelConfig", id.ToString(), $"{model.Provider}/{model.ModelIdentifier}", cancellationToken);

        return Result<bool>.Success(true);
    }

    /// <summary>§82 "Test" — sends a trivial classification request through the real provider client to confirm the model/key/connectivity work, without touching any EmailMessage.</summary>
    public async Task<Result<TestAiModelResult>> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var model = await _db.AiModelConfigs.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (model is null)
        {
            return Result<TestAiModelResult>.Failure("AI model configuration not found.");
        }

        var request = new ClassificationRequest(
            "Test message for connectivity check", "This is a test.", "test@example.com", "test-account@sawo.com",
            false, "(test)", "General", string.Empty, string.Empty, Purpose: AiUsagePurpose.ModelTest);

        var attempt = await _aiProvider.ClassifyAsync(request, model.ModelIdentifier, TimeSpan.FromSeconds(model.TimeoutSeconds), cancellationToken);

        await _auditService.LogAsync(
            attempt.Succeeded ? "AI_MODEL_TEST_SUCCEEDED" : "AI_MODEL_TEST_FAILED",
            "AiModelConfig", model.Id.ToString(), $"{model.Provider}/{model.ModelIdentifier}", cancellationToken);

        return Result<TestAiModelResult>.Success(new TestAiModelResult(attempt.Succeeded, attempt.ErrorMessage, attempt.DurationMs));
    }

    private async Task ClearExistingDefaultAsync(string taskCapability, CancellationToken cancellationToken)
    {
        var currentDefaults = await _db.AiModelConfigs
            .Where(m => m.TaskCapability == taskCapability && m.IsDefault)
            .ToListAsync(cancellationToken);

        foreach (var existing in currentDefaults)
        {
            existing.IsDefault = false;
        }
    }
}
