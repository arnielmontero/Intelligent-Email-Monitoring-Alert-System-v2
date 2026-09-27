import { Navigate, Route, Routes } from "react-router-dom";
import AppShell from "./components/layout/AppShell";
import ProtectedRoute from "./routes/ProtectedRoute";
import LoginPage from "./pages/LoginPage";
import DashboardPage from "./pages/DashboardPage";
import EmployeesPage from "./pages/employees/EmployeesPage";
import EmailAccountsPage from "./pages/email-accounts/EmailAccountsPage";
import EmailMonitoringPage from "./pages/email-monitoring/EmailMonitoringPage";
import EmailClassificationPage from "./pages/email-classification/EmailClassificationPage";
import AiModelsPage from "./pages/ai-models/AiModelsPage";
import CasesPage from "./pages/cases/CasesPage";
import AgentsPage from "./pages/agents/AgentsPage";
import ReminderPoliciesPage from "./pages/reminder-policies/ReminderPoliciesPage";
import EscalationPoliciesPage from "./pages/escalation-policies/EscalationPoliciesPage";
import EscalationGroupsPage from "./pages/escalation-groups/EscalationGroupsPage";
import EscalationHistoryPage from "./pages/escalation-history/EscalationHistoryPage";
import AuditLogPage from "./pages/audit-log/AuditLogPage";
import CaseWorkflowPage from "./pages/case-workflow/CaseWorkflowPage";
import ReplyVerificationPage from "./pages/reply-verification/ReplyVerificationPage";
import SystemHealthPage from "./pages/system-health/SystemHealthPage";
import OutboundEmailPage from "./pages/outbound-email/OutboundEmailPage";
import CaseHistoryPage from "./pages/case-history/CaseHistoryPage";
import NotificationsPage from "./pages/notifications/NotificationsPage";
import UsersPage from "./pages/users/UsersPage";
import EmployeeActivityPage from "./pages/employee-activity/EmployeeActivityPage";
import SystemSettingsPage from "./pages/system-settings/SystemSettingsPage";
import MaintenancePage from "./pages/maintenance/MaintenancePage";

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />

      <Route element={<ProtectedRoute />}>
        <Route element={<AppShell />}>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/employees" element={<EmployeesPage />} />
          <Route path="/email-accounts" element={<EmailAccountsPage />} />
          <Route path="/email-monitoring" element={<EmailMonitoringPage />} />
          <Route path="/email-classification" element={<EmailClassificationPage />} />
          <Route path="/ai-models" element={<AiModelsPage />} />
          <Route path="/cases" element={<CasesPage />} />
          <Route path="/agents" element={<AgentsPage />} />
          <Route path="/reminder-policies" element={<ReminderPoliciesPage />} />
          <Route path="/escalation-policies" element={<EscalationPoliciesPage />} />
          <Route path="/escalation-groups" element={<EscalationGroupsPage />} />
          <Route path="/escalation-history" element={<EscalationHistoryPage />} />
          <Route path="/audit-log" element={<AuditLogPage />} />
          <Route path="/case-workflow" element={<CaseWorkflowPage />} />
          <Route path="/reply-verification" element={<ReplyVerificationPage />} />
          <Route path="/system-health" element={<SystemHealthPage />} />
          <Route path="/outbound-email" element={<OutboundEmailPage />} />
          <Route path="/case-history" element={<CaseHistoryPage />} />
          <Route path="/notifications" element={<NotificationsPage />} />
          <Route path="/users" element={<UsersPage />} />
          <Route path="/employee-activity" element={<EmployeeActivityPage />} />
          <Route path="/system-settings" element={<SystemSettingsPage />} />
          <Route path="/maintenance" element={<MaintenancePage />} />
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
