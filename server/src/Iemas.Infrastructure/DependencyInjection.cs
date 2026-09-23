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
        services.AddScoped<IEmailProviderAdapterResolver, EmailProviderAdapterResolver>();
        services.AddScoped<SmtpEmailProviderAdapter>();

        // §82 — OpenRouter is the V1 AI provider; isolated behind IAiClassificationProvider so
        // the classification workflow never depends on HTTP/OpenRouter specifics directly (same
        // separation as the email provider adapters above).
        services.AddHttpClient<IAiClassificationProvider, OpenRouterClassificationProvider>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiClassificationOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.OpenRouter.BaseUrl) ? "https://openrouter.ai/api/v1" : options.OpenRouter.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        });

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));
        services.AddHangfireServer();

        return services;
    }
}
