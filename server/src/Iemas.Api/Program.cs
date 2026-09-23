using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.RateLimiting;
using Iemas.Api.Hubs;
using Iemas.Api.Jobs;
using Iemas.Application;
using Iemas.Application.Cases;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;
using Iemas.Domain.Identity;
using Iemas.Infrastructure;
using Iemas.Infrastructure.Persistence;
using Iemas.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

const string AgentScheme = "AgentScheme";
const string RequireAgentPolicy = "RequireAgent";

var builder = WebApplication.CreateBuilder(args);

// {Properties} surfaces LogContext-pushed properties (CorrelationId, in particular — see
// CorrelationIdMiddleware and RecurringJobGuards) in every log line; the default Serilog templates
// omit arbitrary properties, which would otherwise make the enrichment invisible in practice.
const string LogOutputTemplate =
    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: LogOutputTemplate)
    .WriteTo.File("logs/iemas-.log", rollingInterval: RollingInterval.Day, outputTemplate: LogOutputTemplate));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<RecurringJobGuards>();

builder.Services.AddExceptionHandler<Iemas.Api.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Missing Jwt:Secret configuration.");

// §69 — Agent identity is entirely separate from CMS user identity: a second JWT bearer scheme,
// signed with its own secret/issuer/audience (AgentJwtOptions), so an Agent token cannot be used
// against CMS endpoints (no SystemRole claims exist on it at all) and a CMS user token cannot be
// used against Agent endpoints (wrong signing key entirely — validation fails outright, not just
// an authorization check).
var agentJwtSecret = builder.Configuration["AgentJwt:Secret"]
    ?? throw new InvalidOperationException("Missing AgentJwt:Secret configuration.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
})
.AddJwtBearer(AgentScheme, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["AgentJwt:Issuer"],
        ValidAudience = builder.Configuration["AgentJwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(agentJwtSecret)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };
    // SignalR sends the access token via query string (browsers/desktop clients cannot set
    // headers on the WebSocket handshake) — accepted only for hub paths, and only for this scheme.
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/agent"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RequireSuperAdministrator", p => p.RequireRole(SystemRole.SuperAdministrator))
    .AddPolicy("RequireAdministrator", p => p.RequireRole(SystemRole.SuperAdministrator, SystemRole.Administrator))
    .AddPolicy("RequireSupervisorOrAbove", p => p.RequireRole(SystemRole.SuperAdministrator, SystemRole.Administrator, SystemRole.SupervisorManager))
    .AddPolicy("RequireAuditorOrAbove", p => p.RequireRole(SystemRole.SuperAdministrator, SystemRole.Administrator, SystemRole.SupervisorManager, SystemRole.Auditor))
    .AddPolicy(RequireAgentPolicy, p => p.AddAuthenticationSchemes(AgentScheme).RequireAuthenticatedUser());

builder.Services.AddSignalR();

const string CmsCorsPolicy = "CmsCorsPolicy";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CmsCorsPolicy, policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")!, name: "postgresql");

// Finding #4 (Phase 10 security hardening) — rate limit the unauthenticated auth-boundary
// endpoints (CMS login, Agent enrollment, Agent authentication) against credential-stuffing/
// brute-force attempts. Keyed by remote IP (not by any request-supplied identifier, which an
// attacker could vary to bypass a per-account limit) with a fixed window; a limit hit returns 429
// rather than queuing, since queuing a flood of auth attempts has no benefit here.
const string AuthRateLimitPolicy = "AuthRateLimit";
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthRateLimitPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<Iemas.Application.Common.Interfaces.IPasswordHasher>();
    await DbSeeder.SeedAsync(db, passwordHasher, app.Configuration);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<Iemas.Api.CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseCors(CmsCorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireDashboardAuthFilter() }
});

app.MapControllers();
app.MapHealthChecks("/health");

// §76 SignalR — real-time delivery transport only, never a source of truth (§76: "SignalR is a
// real-time transport, not a source of truth"). Requires the Agent-scheme bearer token (via
// access_token query string, wired above) — a CMS user token cannot open this hub.
app.MapHub<AgentHub>("/hubs/agent").RequireAuthorization(RequireAgentPolicy);

// Requirements §79 (durable background processing), §20 (restart-safe pipeline). RecurringJob
// registration is itself idempotent (Hangfire upserts by job ID), so this is safe to run on
// every startup. All state the job depends on (EmailSyncState watermark, EmailAccount config)
// lives in PostgreSQL, not in the job/server process, so a server restart loses no progress.
//
// Emergency Pause (§91 "Pause Email Processing") is not yet built as a CMS-driven control
// (deferred to Phase 10); EmailIntake__Enabled is a config-level equivalent for now so intake
// can still be stopped without a code change, and is called out in the tracker as a forward
// reference to the real Phase 10 control.
//
// Phase 10 hardening: routed through RecurringJobGuards, same [DisableConcurrentExecution] reason
// as the Reminder/Escalation jobs below — this is the tightest interval of any job (2 minutes), so
// it is the one most likely to genuinely overlap its own next tick under a slow/degraded mailbox.
if (builder.Configuration.GetValue("EmailIntake:Enabled", true))
{
    RecurringJob.AddOrUpdate<RecurringJobGuards>(
        "email-intake-poll-all-accounts",
        guards => guards.RunEmailIntakeAsync(CancellationToken.None),
        builder.Configuration["EmailIntake:CronSchedule"] ?? "*/2 * * * *");
}

// Requirements §20 (AI Classification pipeline stage), §79/§20 (restart-safe, re-checks current
// state). Same idempotent-upsert pattern as the intake job above; AI provider/model failures are
// handled inside EmailClassificationService itself (§83) and never surface as a failed Hangfire
// job for a single bad message — a whole-batch exception would still be retried by Hangfire and
// is safe to retry since classification re-checks PendingClassification state per message.
//
// Phase 10 hardening: routed through RecurringJobGuards, same [DisableConcurrentExecution] reason
// as the jobs above — here it is also load-bearing for the circuit breaker's HALF-OPEN "exactly one
// probe" guarantee against the common case (this scheduled job racing the manual
// POST /email-classification/run trigger), not just an efficiency concern.
if (builder.Configuration.GetValue("AiClassification:Enabled", true))
{
    RecurringJob.AddOrUpdate<RecurringJobGuards>(
        "ai-classification-poll-pending-messages",
        guards => guards.RunEmailClassificationAsync(builder.Configuration.GetValue("AiClassification:BatchSize", 25), CancellationToken.None),
        builder.Configuration["AiClassification:CronSchedule"] ?? "*/2 * * * *");
}

// Requirements §20 (Case Matching/Creation pipeline stage, following Phase 4's Classification
// stage), §78 (idempotent — a message already linked to a Case via the unique CaseEmail index is
// never re-processed). Same idempotent-upsert registration pattern as the two jobs above.
if (builder.Configuration.GetValue("CaseWorkflow:Enabled", true))
{
    RecurringJob.AddOrUpdate<CaseWorkflowService>(
        "case-workflow-poll-important-messages",
        service => service.RunAsync(builder.Configuration.GetValue("CaseWorkflow:BatchSize", 25), CancellationToken.None),
        builder.Configuration["CaseWorkflow:CronSchedule"] ?? "*/2 * * * *");
}

// Requirements §42 (Reply Verification Engine), §44 (mailbox unavailability must never become
// "no reply" — handled inside ReplyVerificationService itself, not here), §20 (re-checks current
// Case state per Case before acting). Same idempotent-upsert registration pattern as the jobs
// above; a whole-batch exception (e.g. a genuinely down mailbox) is safe for Hangfire to retry
// since this job re-derives its candidate Case list fresh on every run rather than assuming
// anything about a partially-completed previous attempt.
if (builder.Configuration.GetValue("ReplyVerification:Enabled", true))
{
    RecurringJob.AddOrUpdate<ReplyVerificationService>(
        "reply-verification-poll-awaiting-reply-cases",
        service => service.RunAsync(builder.Configuration.GetValue("ReplyVerification:BatchSize", 25), CancellationToken.None),
        builder.Configuration["ReplyVerification:CronSchedule"] ?? "*/5 * * * *");
}

// Requirements §54 (Reminder Engine), §55 (Reminder Recheck Rule — re-checked inside
// ReminderExecutionService itself immediately before any reminder would be sent, never trusted
// from scheduling time), §78 (idempotent — a Reminder can only be claimed for execution once per
// due firing, guarded by the unique ExecutionClaimToken index). Same idempotent-upsert
// registration pattern as the jobs above; re-derives its candidate set fresh every run so a
// server restart mid-cycle loses no state — Reminder rows and their Status/ScheduledForUtc are
// the only source of truth, not anything held in the job/server process.
//
// Phase 10 hardening: routed through RecurringJobGuards (Iemas.Api.Jobs) rather than calling
// ReminderExecutionService directly, so [DisableConcurrentExecution] can guard against an
// overrunning run still being mid-batch when its own next cron tick fires — see that class's doc
// comment for why the per-row claim-token idempotency alone isn't a full substitute for this.
if (builder.Configuration.GetValue("ReminderEngine:Enabled", true))
{
    RecurringJob.AddOrUpdate<RecurringJobGuards>(
        "reminder-engine-execute-due-reminders",
        guards => guards.RunRemindersAsync(builder.Configuration.GetValue("ReminderEngine:BatchSize", 50), CancellationToken.None),
        builder.Configuration["ReminderEngine:CronSchedule"] ?? "*/5 * * * *");
}

// Requirements §57-§60 (Escalation Engine), §60 (every condition rechecked from the database
// immediately before any level executes, never trusted from a prior evaluation), §78 (idempotent —
// a level already recorded as Executed for a Case is never re-executed, guarded in
// EscalationService itself rather than a DB constraint, since a Skipped attempt must be allowed to
// recur on every poll). Same idempotent-upsert registration pattern as the jobs above.
//
// Phase 10 hardening: routed through RecurringJobGuards for the same [DisableConcurrentExecution]
// reason as the Reminder job above — Escalation's own idempotency check (querying for an existing
// Executed row) has a narrower TOCTOU gap under real concurrent execution than Reminder's unique-
// index claim, making the job-level lock more load-bearing here, not just extra safety.
if (builder.Configuration.GetValue("EscalationEngine:Enabled", true))
{
    RecurringJob.AddOrUpdate<RecurringJobGuards>(
        "escalation-engine-evaluate-cases",
        guards => guards.RunEscalationsAsync(builder.Configuration.GetValue("EscalationEngine:BatchSize", 50), CancellationToken.None),
        builder.Configuration["EscalationEngine:CronSchedule"] ?? "*/10 * * * *");
}

app.Run();

public class HangfireDashboardAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        // Requirements §85 — Hangfire background job dashboard is an administrative surface.
        return httpContext.User.IsInRole(SystemRole.SuperAdministrator) || httpContext.User.IsInRole(SystemRole.Administrator);
    }
}
