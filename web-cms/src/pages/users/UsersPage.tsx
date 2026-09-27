import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmployeeDto, RoleDto, UserDto } from "../../api/types";
import { useAuthStore } from "../../store/authStore";

interface UserForm {
  email: string;
  displayName: string;
  password: string;
  roles: string[];
  employeeId: string;
}

const EMPTY_FORM: UserForm = { email: "", displayName: "", password: "", roles: [], employeeId: "" };

export default function UsersPage() {
  const currentUser = useAuthStore((s) => s.user);
  const isSuperAdmin = useAuthStore((s) => s.hasRole("SuperAdministrator"));
  const isAdmin = useAuthStore((s) => s.hasRole("SuperAdministrator", "Administrator"));

  const [users, setUsers] = useState<UserDto[]>([]);
  const [roles, setRoles] = useState<RoleDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [form, setForm] = useState<UserForm>(EMPTY_FORM);
  const [editingId, setEditingId] = useState<string | null>(null);

  const [ownCurrent, setOwnCurrent] = useState("");
  const [ownNew, setOwnNew] = useState("");

  async function load() {
    if (!isAdmin) {
      setLoading(false);
      return;
    }
    try {
      const [usersRes, rolesRes, employeesRes] = await Promise.all([
        apiClient.get<UserDto[]>("/users"),
        apiClient.get<RoleDto[]>("/users/roles"),
        apiClient.get<EmployeeDto[]>("/employees"),
      ]);
      setUsers(usersRes.data);
      setRoles(rolesRes.data);
      setEmployees(employeesRes.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to manage users (requires Administrator)."
        : "Failed to load users.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function run(action: () => Promise<unknown>, success: string) {
    setError(null);
    setNotice(null);
    return action()
      .then(async () => {
        setNotice(success);
        await load();
        return true;
      })
      .catch((err: any) => {
        setError(err.response?.data?.message ?? "The request failed.");
        return false;
      });
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const body = {
      displayName: form.displayName,
      roles: form.roles,
      employeeId: form.employeeId || null,
    };
    const ok = editingId
      ? await run(() => apiClient.put(`/users/${editingId}`, body), "User updated.")
      : await run(() => apiClient.post("/users", { ...body, email: form.email, password: form.password }), "User created.");
    if (ok) {
      setForm(EMPTY_FORM);
      setEditingId(null);
    }
  }

  function startEdit(user: UserDto) {
    setEditingId(user.id);
    setForm({ email: user.email, displayName: user.displayName, password: "", roles: user.roles, employeeId: user.employeeId ?? "" });
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function toggleRole(role: string) {
    setForm((f) => ({ ...f, roles: f.roles.includes(role) ? f.roles.filter((r) => r !== role) : [...f.roles, role] }));
  }

  function resetPassword(user: UserDto) {
    const password = window.prompt(`New password for ${user.email}\n(the user's existing sessions will be signed out)`);
    if (!password) return;
    run(() => apiClient.post(`/users/${user.id}/reset-password`, { newPassword: password }), `Password reset for ${user.email}.`);
  }

  function setActive(user: UserDto, active: boolean) {
    if (!active && !window.confirm(`Deactivate ${user.email}? They are signed out immediately.`)) return;
    run(() => apiClient.post(`/users/${user.id}/${active ? "activate" : "deactivate"}`), active ? "User reactivated." : "User deactivated.");
  }

  async function changeOwnPassword(e: React.FormEvent) {
    e.preventDefault();
    const ok = await run(
      () => apiClient.post("/users/me/password", { currentPassword: ownCurrent, newPassword: ownNew }),
      "Your password was changed. Other sessions have been signed out.",
    );
    if (ok) {
      setOwnCurrent("");
      setOwnNew("");
    }
  }

  const roleLabel = (name: string) => roles.find((r) => r.name === name)?.label ?? name;
  const canManage = (user: UserDto) => isSuperAdmin || !user.roles.includes("SuperAdministrator");

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Users &amp; Permissions</h1>
      <p style={mutedStyle}>
        CMS sign-in accounts and their roles (§85). Separate from Employees (who own Cases) and Windows Agents (devices);
        link a user to an employee record where they are the same person.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      {isAdmin && (
        <>
          <form onSubmit={handleSubmit} style={panelStyle}>
            <h2 style={{ fontSize: 18, marginTop: 0 }}>{editingId ? `Edit ${form.email}` : "Add user"}</h2>
            <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end" }}>
              <div>
                <label>Email</label><br />
                <input type="email" value={form.email} disabled={!!editingId} required onChange={(e) => setForm({ ...form, email: e.target.value })} />
              </div>
              <div>
                <label>Display name</label><br />
                <input value={form.displayName} required maxLength={200} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
              </div>
              {!editingId && (
                <div>
                  <label>Initial password</label><br />
                  <input type="password" value={form.password} required autoComplete="new-password" onChange={(e) => setForm({ ...form, password: e.target.value })} />
                </div>
              )}
              <div>
                <label>Linked employee</label><br />
                <select value={form.employeeId} onChange={(e) => setForm({ ...form, employeeId: e.target.value })}>
                  <option value="">— none —</option>
                  {employees.map((emp) => <option key={emp.id} value={emp.id}>{emp.fullName} ({emp.email})</option>)}
                </select>
              </div>
            </div>
            <div style={{ marginTop: 12 }}>
              <label>Roles</label>
              <div style={{ display: "flex", gap: 16, flexWrap: "wrap", marginTop: 4 }}>
                {roles.map((r) => (
                  <label key={r.name} style={{ display: "flex", gap: 6, alignItems: "center", opacity: r.name === "SuperAdministrator" && !isSuperAdmin ? 0.5 : 1 }}>
                    <input
                      type="checkbox"
                      checked={form.roles.includes(r.name)}
                      disabled={r.name === "SuperAdministrator" && !isSuperAdmin}
                      onChange={() => toggleRole(r.name)}
                    />
                    {r.label}
                  </label>
                ))}
              </div>
            </div>
            <div style={{ marginTop: 12, display: "flex", gap: 8 }}>
              <button type="submit">{editingId ? "Save changes" : "Create user"}</button>
              {editingId && <button type="button" onClick={() => { setEditingId(null); setForm(EMPTY_FORM); }}>Cancel</button>}
            </div>
          </form>

          <table style={{ width: "100%", borderCollapse: "collapse", marginBottom: 32 }}>
            <thead>
              <tr>
                <th style={thStyle}>User</th>
                <th style={thStyle}>Roles</th>
                <th style={thStyle}>Employee</th>
                <th style={thStyle}>Last sign-in</th>
                <th style={thStyle}>Status</th>
                <th style={thStyle}></th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id} style={{ opacity: u.isActive ? 1 : 0.55 }}>
                  <td style={tdStyle}>
                    {u.displayName}{u.id === currentUser?.userId && <span style={mutedStyle}> (you)</span>}
                    <div style={mutedStyle}>{u.email}</div>
                  </td>
                  <td style={tdStyle}>{u.roles.map(roleLabel).join(", ")}</td>
                  <td style={tdStyle}>{u.employeeName ?? "—"}</td>
                  <td style={tdStyle}>{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : "Never"}</td>
                  <td style={tdStyle}>{u.isActive ? "Active" : "Deactivated"}</td>
                  <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                    {canManage(u) ? (
                      <div style={{ display: "flex", gap: 6 }}>
                        <button onClick={() => startEdit(u)}>Edit</button>
                        <button onClick={() => resetPassword(u)}>Reset password</button>
                        {u.id !== currentUser?.userId && (
                          <button onClick={() => setActive(u, !u.isActive)}>{u.isActive ? "Deactivate" : "Activate"}</button>
                        )}
                      </div>
                    ) : (
                      <span style={mutedStyle}>Super Administrator only</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <h2 style={{ fontSize: 18 }}>Roles and permissions</h2>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))", gap: 12, marginBottom: 32 }}>
            {roles.map((r) => (
              <div key={r.name} style={panelStyle}>
                <strong>{r.label}</strong>
                <div style={mutedStyle}>{r.description}</div>
                <ul style={{ margin: "8px 0 0", paddingLeft: 18, fontSize: 13 }}>
                  {r.permissions.map((p) => <li key={p}>{p}</li>)}
                </ul>
              </div>
            ))}
          </div>
        </>
      )}

      <form onSubmit={changeOwnPassword} style={{ ...panelStyle, maxWidth: 520 }}>
        <h2 style={{ fontSize: 18, marginTop: 0 }}>Change my password</h2>
        <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end" }}>
          <div>
            <label>Current password</label><br />
            <input type="password" value={ownCurrent} required autoComplete="current-password" onChange={(e) => setOwnCurrent(e.target.value)} />
          </div>
          <div>
            <label>New password</label><br />
            <input type="password" value={ownNew} required autoComplete="new-password" onChange={(e) => setOwnNew(e.target.value)} />
          </div>
          <button type="submit">Change password</button>
        </div>
      </form>
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 16 };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, marginBottom: 24, background: "var(--color-surface)" };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px", verticalAlign: "top" };
