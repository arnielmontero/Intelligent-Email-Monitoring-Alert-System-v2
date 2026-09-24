import { Navigate, Route, Routes } from "react-router-dom";
import AppShell from "./components/layout/AppShell";
import ProtectedRoute from "./routes/ProtectedRoute";
import LoginPage from "./pages/LoginPage";
import DashboardPage from "./pages/DashboardPage";
import PlaceholderPage from "./pages/PlaceholderPage";
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

const PLACEHOLDER_ROUTES: Array<{ path: string; title: string }> = [
  { path: "/notifications", title: "Notifications" },
  { path: "/users", title: "Users & Permissions" },
  { path: "/employee-activity", title: "Employee Activity" },
  { path: "/case-history", title: "Case History & Logs" },
  { path: "/system-settings", title: "System Settings" },
  { path: "/maintenance", title: "Maintenance / Emergency Pause" },
];

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
          {PLACEHOLDER_ROUTES.map((route) => (
            <Route key={route.path} path={route.path} element={<PlaceholderPage title={route.title} />} />
          ))}
        </Route>
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
