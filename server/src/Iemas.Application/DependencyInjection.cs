using Iemas.Application.Agents;
using Iemas.Application.AiModels;
using Iemas.Application.Audit;
using Iemas.Application.Auth;
using Iemas.Application.Dashboard;
using Iemas.Application.Cases;
using Iemas.Application.ClassificationProfiles;
using Iemas.Application.Departments;
using Iemas.Application.EmailAccounts;
using Iemas.Application.EmailClassification;
using Iemas.Application.EmailIntake;
using Iemas.Application.EmployeeActivity;
using Iemas.Application.Employees;
using Iemas.Application.Escalations;
using Iemas.Application.Notifications;
using Iemas.Application.Operations;
using Iemas.Application.Reminders;
using Iemas.Application.Users;
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
        services.AddScoped<AiProviderSettingsService>();
        services.AddScoped<Iemas.Application.AiUsage.AiUsageQueryService>();
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
        services.AddScoped<DashboardService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationAdminService>();
        services.AddScoped<EmergencyPauseService>();
        services.AddScoped<EmailDataResetService>();
        services.AddScoped<SampleDataService>();
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<UserManagementService>();
        services.AddScoped<EmployeeActivityQueryService>();
        return services;
    }
}
