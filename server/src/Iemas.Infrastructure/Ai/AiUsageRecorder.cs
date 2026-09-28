using Iemas.Application.AiUsage;
using Iemas.Domain.Ai;
using Iemas.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iemas.Infrastructure.Ai;

/// <summary>
/// Writes each usage record through its own DbContext scope, so recording never flushes (or is
/// rolled back with) the classification workflow's pending changes, and a failure here is only logged.
/// </summary>
public class AiUsageRecorder : IAiUsageRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiUsageRecorder> _logger;

    public AiUsageRecorder(IServiceScopeFactory scopeFactory, ILogger<AiUsageRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiUsageRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record AI usage for model {Model}", record.ModelIdentifier);
        }
    }
}
