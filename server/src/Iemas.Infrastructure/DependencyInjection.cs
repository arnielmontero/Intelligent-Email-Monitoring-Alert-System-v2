using Hangfire;
using Hangfire.PostgreSql;
using Iemas.Application.Common.Ai;
using Iemas.Application.Common.Interfaces;
using Iemas.Application.Common.Providers;
using Iemas.Infrastructure.Ai;
using Iemas.Infrastructure.Audit;
using Iemas.Infrastructure.Common;
using Iemas.Infrastructure.Persistence;
using Iemas.Infrastructure.Providers;
using Iemas.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iemas.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default configuration.");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AgentJwtOptions>(configuration.GetSection(AgentJwtOptions.SectionName));
        services.Configure<CredentialEncryptionOptions>(configuration.GetSection(CredentialEncryptionOptions.SectionName));
        services.Configure<AiClassificationOptions>(configuration.GetSection(AiClassificationOptions.SectionName));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IAgentTokenService, AgentTokenService>();
        services.AddScoped<ICredentialEncryptionService, AesGcmCredentialEncryptionService>();
        services.AddSingleton<IRetryDelay, SystemRetryDelay>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AiCircuitBreakerStore>();

        services.AddScoped<IEmailProviderAdapter, ImapEmailProviderAdapter>();
        services.AddScoped<IEmailProviderAdapter, MicrosoftGraphEmailProviderAdapter>();
        services.AddScoped<IEmailProviderAdapter, SmtpEmailProviderAdapter>();
        services.AddScoped<IEmailProviderAdapterResolver, EmailProviderAdapterResolver>();

        // §82 — OpenRouter is the V1 AI provider; isolated behind IAiClassificationProvider so
        // the classification workflow never depends on HTTP/OpenRouter specifics directly (same
        // separation as the email provider adapters above).
        // The base URL and key are resolved per request (CMS settings first, then configuration).
        services.AddScoped<Iemas.Application.AiModels.IAiProviderConnectionResolver, AiProviderConnectionResolver>();
        services.AddHttpClient<OpenRouterClassificationProvider>();
        services.AddSingleton<Iemas.Application.AiUsage.IAiUsageRecorder, AiUsageRecorder>();
        // Every AI call goes through the usage-recording wrapper (cost monitoring).
        services.AddScoped<IAiClassificationProvider>(sp => new Iemas.Application.AiUsage.UsageRecordingAiClassificationProvider(
            sp.GetRequiredService<OpenRouterClassificationProvider>(),
            sp.GetRequiredService<Iemas.Application.AiUsage.IAiUsageRecorder>()));
        services.AddMemoryCache();
        services.AddHttpClient<Iemas.Application.AiModels.IAiProviderAdminClient, OpenRouterAdminClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(20));

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));
        services.AddHangfireServer();

        return services;
    }
}
