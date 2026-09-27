import { useEffect, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { PauseControlDto } from "../../api/types";
import { useAuthStore } from "../../store/authStore";

/** Navigation structure per requirements §86 CMS Navigation. */
const NAV_SECTIONS = [
  {
    label: "Dashboard",
    items: [{ label: "Dashboard", path: "/" }],
  },
  {
    label: "Email Management",
    items: [
      { label: "Email Accounts", path: "/email-accounts" },
      { label: "Outbound Email", path: "/outbound-email" },
      { label: "Email Monitoring & Intake", path: "/email-monitoring" },
      { label: "Email Classification", path: "/email-classification" },
    ],
  },
  {
    label: "Case Management",
    items: [
      { label: "Cases / Work Topics", path: "/cases" },
      { label: "Case Workflow", path: "/case-workflow" },
      { label: "Reply Verification", path: "/reply-verification" },
      { label: "Notifications", path: "/notifications" },
      { label: "Reminder Policies", path: "/reminder-policies" },
      { label: "Escalation Policies", path: "/escalation-policies" },
      { label: "Escalation Groups", path: "/escalation-groups" },
      { label: "Escalation History", path: "/escalation-history" },
    ],
  },
  {
    label: "People & Devices",
    items: [
      { label: "Employees & Ownership", path: "/employees" },
      { label: "Windows Agents", path: "/agents" },
      { label: "Users & Permissions", path: "/users" },
      { label: "Employee Activity", path: "/employee-activity" },
    ],
  },
  {
    label: "AI Configuration",
    items: [{ label: "AI Models", path: "/ai-models" }],
  },
  {
    label: "History & Audit",
    items: [
      { label: "Case History & Logs", path: "/case-history" },
      { label: "Audit Log", path: "/audit-log" },
    ],
  },
  {
    label: "System",
    items: [
      { label: "System Health", path: "/system-health" },
      { label: "System Settings", path: "/system-settings" },
      { label: "Maintenance / Emergency Pause", path: "/maintenance" },
    ],
  },
];

export default function AppShell() {
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const location = useLocation();
  const [organizationName, setOrganizationName] = useState("IEMAS");
  const [pausedLabels, setPausedLabels] = useState<string[]>([]);

  useEffect(() => {
    apiClient
      .get<{ organizationName: string }>("/system-settings/public")
      .then((r) => setOrganizationName(r.data.organizationName))
      .catch(() => undefined);
  }, []);

  // Re-checked on navigation so a pause set on the Maintenance page shows up immediately elsewhere.
  useEffect(() => {
    apiClient
      .get<PauseControlDto[]>("/maintenance/pause")
      .then((r) => setPausedLabels(r.data.filter((c) => c.isPaused).map((c) => c.label.replace(/^Pause /, ""))))
      .catch(() => setPausedLabels([]));
  }, [location.pathname]);

  return (
    <div className="app-shell">
      <aside className="app-sidebar">
        <div className="app-brand">{organizationName}</div>
        <nav>
          {NAV_SECTIONS.map((section) => (
            <div key={section.label} className="nav-section">
              <div className="nav-section-label">{section.label}</div>
              {section.items.map((item) => (
                <NavLink
                  key={item.path}
                  to={item.path}
                  className={({ isActive }) => "nav-item" + (isActive ? " active" : "")}
                  end={item.path === "/"}
                >
                  {item.label}
                </NavLink>
              ))}
            </div>
          ))}
        </nav>
      </aside>

      <div className="app-main">
        <header className="app-header">
          <div className="app-header-user">{user?.displayName}</div>
          <button className="app-header-logout" onClick={() => logout()}>
            Sign out
          </button>
        </header>
        <main className="app-content">
          {pausedLabels.length > 0 && (
            <NavLink to="/maintenance" style={pauseBannerStyle}>
              Emergency Pause active: {pausedLabels.join(", ")}
            </NavLink>
          )}
          <Outlet />
        </main>
      </div>
    </div>
  );
}

const pauseBannerStyle: React.CSSProperties = {
  display: "block",
  background: "var(--color-danger)",
  color: "#fff",
  padding: "8px 12px",
  borderRadius: 6,
  marginBottom: 16,
  textDecoration: "none",
};
