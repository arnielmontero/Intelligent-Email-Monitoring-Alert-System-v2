using Iemas.Application.ClassificationProfiles.Dtos;
using Iemas.Application.Common;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Security;
using Iemas.Application.EmailClassification;
using Iemas.Domain.Ai;
using Microsoft.EntityFrameworkCore;

namespace Iemas.Application.ClassificationProfiles;

/// <summary>Requirements §28 (Classification Profiles CRUD), §29 (Classification Testing).</summary>
public class ClassificationProfileService
{
    private readonly IAppDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IAiClassificationProvider _aiProvider;

    public ClassificationProfileService(IAppDbContext db, IAuditService auditService, IAiClassificationProvider aiProvider)
    {
        _db = db;
        _auditService = auditService;
        _aiProvider = aiProvider;
    }

    public async Task<List<ClassificationProfileDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await ProjectAndOrder(_db.ClassificationProfiles.AsNoTracking()).ToListAsync(cancellationToken);
    }

    public async Task<ClassificationProfileDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = _db.ClassificationProfiles.AsNoTracking().Where(p => p.Id == id);
        return await ProjectAndOrder(query).FirstOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<ClassificationProfileDto> ProjectAndOrder(IQueryable<ClassificationProfile> source)
    {
        // Order before projecting — see EmailAccountService for why (EF Core can't translate
        // ORDER BY applied after a record-constructing Select; live-verified in Phase 2).
        return source
            .OrderBy(p => p.Name)
            .Select(p => new ClassificationProfileDto(
                p.Id, p.Name, p.Description, p.Enabled, p.Categories, p.IncludeDefinitions, p.ExcludeDefinitions,
                p.ExampleSubject, p.ExampleContent, p.ExpectedClassification,
                p.HighConfidenceThreshold, p.MediumConfidenceThreshold, p.TreatMediumConfidenceAsReviewRequired));
    }

    public async Task<Result<ClassificationProfileDto>> CreateAsync(CreateClassificationProfileRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<ClassificationProfileDto>.Failure("A profile name is required.");
        }

        if (InputSanitizer.ValidateFreeText("Profile name", name) is { } nameError)
        {
            return Result<ClassificationProfileDto>.Failure(nameError);
        }

        var description = request.Description?.Trim();
        if (description is not null && InputSanitizer.ValidateFreeText("Description", description) is { } descriptionError)
        {
            return Result<ClassificationProfileDto>.Failure(descriptionError);
        }

        if (await _db.ClassificationProfiles.AnyAsync(p => p.Name == name, cancellationToken))
        {
            return Result<ClassificationProfileDto>.Failure("A classification profile with this name already exists.");
        }

        if (AreThresholdsInvalid(request.HighConfidenceThreshold, request.MediumConfidenceThreshold, out var thresholdError))
        {
            return Result<ClassificationProfileDto>.Failure(thresholdError!);
        }

        var profile = new ClassificationProfile
        {
            Name = name,
            Description = request.Description?.Trim(),
            Enabled = request.Enabled,
            Categories = request.Categories?.Trim() ?? string.Empty,
            IncludeDefinitions = request.IncludeDefinitions?.Trim() ?? string.Empty,
            ExcludeDefinitions = request.ExcludeDefinitions?.Trim() ?? string.Empty,
            ExampleSubject = request.ExampleSubject?.Trim(),
            ExampleContent = request.ExampleContent?.Trim(),
            ExpectedClassification = request.ExpectedClassification?.Trim(),
            HighConfidenceThreshold = request.HighConfidenceThreshold,
            MediumConfidenceThreshold = request.MediumConfidenceThreshold,
            TreatMediumConfidenceAsReviewRequired = request.TreatMediumConfidenceAsReviewRequired,
        };

        _db.ClassificationProfiles.Add(profile);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("CLASSIFICATION_PROFILE_CREATED", "ClassificationProfile", profile.Id.ToString(), profile.Name, cancellationToken);

        return Result<ClassificationProfileDto>.Success(await GetByIdAsync(profile.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<ClassificationProfileDto>> UpdateAsync(Guid id, UpdateClassificationProfileRequest request, CancellationToken cancellationToken)
    {
        var profile = await _db.ClassificationProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile is null)
        {
            return Result<ClassificationProfileDto>.Failure("Classification profile not found.");
        }

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<ClassificationProfileDto>.Failure("A profile name is required.");
        }

        if (InputSanitizer.ValidateFreeText("Profile name", name) is { } nameError)
        {
            return Result<ClassificationProfileDto>.Failure(nameError);
        }

        var description = request.Description?.Trim();
        if (description is not null && InputSanitizer.ValidateFreeText("Description", description) is { } descriptionError)
        {
            return Result<ClassificationProfileDto>.Failure(descriptionError);
        }

        if (await _db.ClassificationProfiles.AnyAsync(p => p.Name == name && p.Id != id, cancellationToken))
        {
            return Result<ClassificationProfileDto>.Failure("A classification profile with this name already exists.");
        }

        if (AreThresholdsInvalid(request.HighConfidenceThreshold, request.MediumConfidenceThreshold, out var thresholdError))
        {
            return Result<ClassificationProfileDto>.Failure(thresholdError!);
        }

        profile.Name = name;
        profile.Description = request.Description?.Trim();
        profile.Enabled = request.Enabled;
        profile.Categories = request.Categories?.Trim() ?? string.Empty;
        profile.IncludeDefinitions = request.IncludeDefinitions?.Trim() ?? string.Empty;
        profile.ExcludeDefinitions = request.ExcludeDefinitions?.Trim() ?? string.Empty;
        profile.ExampleSubject = request.ExampleSubject?.Trim();
        profile.ExampleContent = request.ExampleContent?.Trim();
        profile.ExpectedClassification = request.ExpectedClassification?.Trim();
        profile.HighConfidenceThreshold = request.HighConfidenceThreshold;
        profile.MediumConfidenceThreshold = request.MediumConfidenceThreshold;
        profile.TreatMediumConfidenceAsReviewRequired = request.TreatMediumConfidenceAsReviewRequired;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("CLASSIFICATION_PROFILE_UPDATED", "ClassificationProfile", profile.Id.ToString(), profile.Name, cancellationToken);

        return Result<ClassificationProfileDto>.Success(await GetByIdAsync(profile.Id, cancellationToken) ?? throw new InvalidOperationException());
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _db.ClassificationProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (profile is null)
        {
            return Result<bool>.Failure("Classification profile not found.");
        }

        if (await _db.EmailAccounts.AnyAsync(a => a.ClassificationProfileName == profile.Name, cancellationToken))
        {
            return Result<bool>.Failure("This profile is still assigned to one or more email accounts and cannot be deleted.");
        }

        _db.ClassificationProfiles.Remove(profile);
        await _db.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync("CLASSIFICATION_PROFILE_DELETED", "ClassificationProfile", id.ToString(), profile.Name, cancellationToken);

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// §29 — Classification Testing. Runs the exact same deterministic filter + AI call the live
    /// pipeline uses, against ad-hoc subject/content, and never persists an EmailMessage/Case/
    /// notification. Uses whatever model is currently the lowest-FallbackOrder enabled model.
    /// </summary>
    public async Task<Result<TestClassificationResult>> TestClassificationAsync(TestClassificationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) && string.IsNullOrWhiteSpace(request.Content))
        {
            return Result<TestClassificationResult>.Failure("Subject or content is required to run a test classification.");
        }

        var profile = request.ClassificationProfileId is not null
            ? await _db.ClassificationProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ClassificationProfileId, cancellationToken)
            : null;

        var filterResult = DeterministicEmailFilter.Evaluate(request.Subject, request.Content, profile);

        if (!filterResult.ShouldSendToAi)
        {
            return Result<TestClassificationResult>.Success(new TestClassificationResult(
                false, "N/A", false, "LOW", 0, "Filtered before reaching AI.", filterResult.Reason, "NOT_IMPORTANT", null));
        }

        var model = await _db.AiModelConfigs
            .AsNoTracking()
            .Where(m => m.Enabled && m.TaskCapability == "EmailClassification")
            .OrderBy(m => m.FallbackOrder)
            .FirstOrDefaultAsync(cancellationToken);

        if (model is null)
        {
            return Result<TestClassificationResult>.Failure("No enabled AI model is configured for EmailClassification.");
        }

        var aiRequest = new ClassificationRequest(
            request.Subject, request.Content, "test@example.com", "test-account@sawo.com", false,
            profile?.Name ?? "(none)", profile?.Categories ?? string.Empty, profile?.IncludeDefinitions ?? string.Empty, profile?.ExcludeDefinitions ?? string.Empty);

        var attempt = await _aiProvider.ClassifyAsync(aiRequest, model.ModelIdentifier, TimeSpan.FromSeconds(model.TimeoutSeconds), cancellationToken);

        if (!attempt.Succeeded || attempt.Response is null)
        {
            return Result<TestClassificationResult>.Success(new TestClassificationResult(
                false, "N/A", false, "LOW", 0, string.Empty, filterResult.Reason, "REVIEW_REQUIRED", attempt.ErrorMessage));
        }

        var policyResult = ClassificationDecisionPolicy.Decide(
            attempt.Response.Relevant, attempt.Response.Confidence,
            profile?.HighConfidenceThreshold, profile?.MediumConfidenceThreshold, profile?.TreatMediumConfidenceAsReviewRequired);

        return Result<TestClassificationResult>.Success(new TestClassificationResult(
            attempt.Response.Relevant, attempt.Response.Category, attempt.Response.ActionRequired,
            attempt.Response.Priority, attempt.Response.Confidence, attempt.Response.Summary,
            filterResult.Reason, policyResult.Decision.ToString(), null));
    }

    private static bool AreThresholdsInvalid(double? high, double? medium, out string? error)
    {
        if (high is not null and (< 0 or > 1))
        {
            error = "High confidence threshold must be between 0 and 1.";
            return true;
        }
        if (medium is not null and (< 0 or > 1))
        {
            error = "Medium confidence threshold must be between 0 and 1.";
            return true;
        }
        if (high is not null && medium is not null && high < medium)
        {
            error = "High confidence threshold must be greater than or equal to the medium threshold.";
            return true;
        }
        error = null;
        return false;
    }
}
