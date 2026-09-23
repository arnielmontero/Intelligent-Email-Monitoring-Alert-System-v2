import { NavLink, Outlet } from "react-router-dom";
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

  return (
    <div className="app-shell">
      <aside className="app-sidebar">
        <div className="app-brand">IEMAS</div>
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
          <Outlet />
        </main>
      </div>
    </div>
  );
}
