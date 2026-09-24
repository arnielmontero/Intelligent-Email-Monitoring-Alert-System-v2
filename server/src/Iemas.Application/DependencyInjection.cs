using Iemas.Application.Agents;
using Iemas.Application.AiModels;
using Iemas.Application.Audit;
using Iemas.Application.Auth;
using Iemas.Application.Cases;
using Iemas.Application.ClassificationProfiles;
using Iemas.Application.Departments;
using Iemas.Application.EmailAccounts;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.Employees;
using Iemas.Application.Escalations;
using Iemas.Application.Reminders;
using Microsoft.Extensions.DependencyInjection;

namespace Iemas.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<DepartmentService>();
        services.AddScoped<EmailAccountService>();
        services.AddScoped<EmailIntakeService>();
        services.AddScoped<ClassificationProfileService>();
        services.AddScoped<AiModelService>();
        services.AddScoped<EmailClassificationService>();
        services.AddScoped<CaseMatchingService>();
        services.AddScoped<CaseWorkflowService>();
        services.AddScoped<CaseService>();
        services.AddScoped<ReplyVerificationService>();
        services.AddScoped<AgentRegistrationService>();
        services.AddScoped<AgentAuthService>();
        services.AddScoped<AgentCaseActionService>();
        services.AddScoped<AgentSyncService>();
        services.AddScoped<AgentManagementService>();
        services.AddScoped<ReminderSchedulingService>();
        services.AddScoped<ReminderExecutionService>();
        services.AddScoped<ReminderPolicyService>();
        services.AddScoped<ReminderQueryService>();
        services.AddScoped<EscalationService>();
        services.AddScoped<EscalationPolicyService>();
        services.AddScoped<EscalationGroupService>();
        services.AddScoped<EscalationQueryService>();
        services.AddScoped<AuditQueryService>();
        return services;
    }
}
