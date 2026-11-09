import { useEffect, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { PauseControlDto } from "../../api/types";
import { useAuthStore } from "../../store/authStore";
import { getThemePreference, setThemePreference, type ThemePreference } from "../../theme";
import { NAV_SECTIONS, helpAnchor } from "./navigation";
import NavIcon from "./NavIcon";

const OPEN_SECTION_KEY = "iemas-nav-open-group";

/** The menu is an accordion: at most one group is expanded (plus the group holding the current page). */
function loadOpenSection(): string | null {
  try {
    return localStorage.getItem(OPEN_SECTION_KEY);
  } catch {
    return null;
  }
}

function saveOpenSection(id: string | null) {
  try {
    if (id) localStorage.setItem(OPEN_SECTION_KEY, id);
    else localStorage.removeItem(OPEN_SECTION_KEY);
  } catch {
    // Storage unavailable: the menu still works, it just won't remember.
  }
}

function isActivePath(itemPath: string, pathname: string) {
  return itemPath === "/" ? pathname === "/" : pathname === itemPath || pathname.startsWith(itemPath + "/");
}

export default function AppShell() {
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const location = useLocation();
  const [organizationName, setOrganizationName] = useState("IEMAS");
  const [pausedLabels, setPausedLabels] = useState<string[]>([]);
  const [theme, setTheme] = useState<ThemePreference>(getThemePreference());
  const [openSection, setOpenSection] = useState<string | null>(loadOpenSection);

  const activeSection = NAV_SECTIONS.find((section) => section.items.some((item) => isActivePath(item.path, location.pathname)));
  const activeItem = activeSection?.items.find((item) => isActivePath(item.path, location.pathname));

  function toggleSection(id: string) {
    setOpenSection((current) => {
      const next = current === id ? null : id;
      saveOpenSection(next);
      return next;
    });
  }

  function chooseTheme(next: ThemePreference) {
    setTheme(next);
    setThemePreference(next);
  }

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
        <div className="app-brand" title="IEMAS — Intelligent Email Monitoring & Alert System">
          <img src="/logo.png" alt="" className="brand-logo" />
          <span>{organizationName}</span>
        </div>
        <nav>
          {NAV_SECTIONS.map((section) => {
            const heading = section.heading && <div key={`${section.id}-heading`} className="nav-heading">{section.heading}</div>;
            // A group with a single page (Dashboard, Cases, Help) is a plain link.
            if (section.items.length === 1) {
              const item = section.items[0];
              return [heading,
                <NavLink key={section.id} to={item.path} end={item.path === "/"}
                  className={({ isActive }) => "nav-top" + (isActive ? " active" : "")}>
                  <NavIcon name={section.icon} />
                  <span>{item.label}</span>
                </NavLink>];
            }
            const open = openSection === section.id || activeSection?.id === section.id;
            return [heading,
              <div key={section.id} className="nav-group">
                <button className={"nav-group-toggle" + (activeSection?.id === section.id ? " has-active" : "")}
                  aria-expanded={open} onClick={() => toggleSection(section.id)}>
                  <NavIcon name={section.icon} />
                  <span>{section.label}</span>
                  <span className="nav-chevron" aria-hidden="true">{open ? "▾" : "▸"}</span>
                </button>
                {open && (
                  <div className="nav-group-items">
                    {section.items.map((item) => (
                      <NavLink key={item.path} to={item.path}
                        className={({ isActive }) => "nav-item" + (isActive ? " active" : "")}>
                        {item.label}
                      </NavLink>
                    ))}
                  </div>
                )}
              </div>];
          })}
        </nav>
      </aside>

      <div className="app-main">
        <header className="app-header">
          {activeItem && activeItem.path !== "/help" && (
            <NavLink to={`/help#${helpAnchor(activeItem.path)}`} className="app-header-help" title={`What is ${activeItem.label}?`}>
              <NavIcon name="help" size={16} /> Help for this page
            </NavLink>
          )}
          <div className="theme-switch" role="group" aria-label="Colour theme">
            {(["system", "light", "dark"] as ThemePreference[]).map((option) => (
              <button key={option} aria-pressed={theme === option} onClick={() => chooseTheme(option)}
                title={option === "system" ? "Follow the Windows / browser setting" : `${option[0].toUpperCase()}${option.slice(1)} theme`}>
                {option === "system" ? "System" : option === "light" ? "Light" : "Dark"}
              </button>
            ))}
          </div>
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
