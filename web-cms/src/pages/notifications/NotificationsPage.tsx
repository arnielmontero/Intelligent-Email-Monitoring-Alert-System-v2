import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmployeeDto, NotificationDto, NotificationTemplateDto, NotificationTemplatesResponse } from "../../api/types";
import { useAuthStore } from "../../store/authStore";

const TYPE_LABELS: Record<string, string> = {
  NewEmail: "New Email",
  FirstReminder: "First Reminder",
  Reminder: "Reminder",
  Overdue: "Overdue",
  EscalationWarning: "Escalation Warning",
  Escalation: "Escalation",
};

const TYPE_TRIGGERS: Record<string, string> = {
  NewEmail: "A new Case is created or reopened by a customer email.",
  FirstReminder: "The first reminder for a Case.",
  Reminder: "Every later reminder.",
  Overdue: "Not sent automatically yet — no engine moves Cases to Overdue.",
  EscalationWarning: "The reminder that reaches the escalation policy's trigger count.",
  Escalation: "An escalation level executes for the Case (sent to the Case owner).",
};

const STATUS_COLORS: Record<string, string> = {
  Sent: "#2563eb",
  Queued: "#a16207",
  Acknowledged: "#166534",
  Cancelled: "var(--color-pill-neutral)",
  Failed: "#b91c1c",
};

export default function NotificationsPage() {
  const isAdmin = useAuthStore((s) => s.hasRole("SuperAdministrator", "Administrator"));
  const [tab, setTab] = useState<"templates" | "log">(isAdmin ? "templates" : "log");

  return (
    <div>
      <h1>Notifications</h1>
      <p style={mutedStyle}>
        The pop-up messages sent to employees' Windows Agents. When they are sent is set in Reminder and Escalation Policies;
        here you edit the wording and see what was delivered.
      </p>
      <div style={{ display: "flex", gap: 8, marginBottom: 16 }}>
        {isAdmin && <button style={tab === "templates" ? activeTabStyle : undefined} onClick={() => setTab("templates")}>Templates</button>}
        <button style={tab === "log" ? activeTabStyle : undefined} onClick={() => setTab("log")}>Delivery log</button>
      </div>
      {tab === "templates" ? <TemplatesTab /> : <LogTab />}
    </div>
  );
}

function TemplatesTab() {
  const [data, setData] = useState<NotificationTemplatesResponse | null>(null);
  const [editing, setEditing] = useState<NotificationTemplateDto | null>(null);
  const [preview, setPreview] = useState<{ title: string; message: string } | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const res = await apiClient.get<NotificationTemplatesResponse>("/notifications/templates");
      setData(res.data);
    } catch {
      setError("Failed to load notification templates.");
    }
  }

  useEffect(() => {
    load();
  }, []);

  useEffect(() => {
    if (!editing) {
      setPreview(null);
      return;
    }
    const handle = setTimeout(async () => {
      try {
        const res = await apiClient.post("/notifications/templates/preview", editing);
        setPreview(res.data);
        setError(null);
      } catch (err: any) {
        setPreview(null);
        setError(err.response?.data?.message ?? "Preview failed.");
      }
    }, 300);
    return () => clearTimeout(handle);
  }, [editing]);

  async function save() {
    if (!editing) return;
    try {
      await apiClient.put(`/notifications/templates/${editing.type}`, {
        enabled: editing.enabled,
        title: editing.title,
        messageText: editing.messageText,
      });
      setEditing(null);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save template.");
    }
  }

  async function reset(type: string) {
    if (!window.confirm(`Restore the default "${TYPE_LABELS[type] ?? type}" message?`)) return;
    try {
      await apiClient.post(`/notifications/templates/${type}/reset`);
      setEditing(null);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to reset template.");
    }
  }

  async function toggle(template: NotificationTemplateDto) {
    try {
      await apiClient.put(`/notifications/templates/${template.type}`, {
        enabled: !template.enabled,
        title: template.title,
        messageText: template.messageText,
      });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update template.");
    }
  }

  function insertVariable(variable: string) {
    if (!editing) return;
    setEditing({ ...editing, messageText: `${editing.messageText}{{${variable}}}` });
  }

  if (!data) return error ? <div className="form-error">{error}</div> : <p>Loading...</p>;

  return (
    <div>
      {error && <div className="form-error">{error}</div>}
      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Notification</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Message</th>
            <th style={thStyle}>Action</th>
          </tr>
        </thead>
        <tbody>
          {data.templates.map((t) => (
            <tr key={t.type}>
              <td style={tdStyle}>
                <strong>{TYPE_LABELS[t.type] ?? t.type}</strong>
                <div style={mutedStyle}>{TYPE_TRIGGERS[t.type]}</div>
              </td>
              <td style={tdStyle}>
                <label style={{ display: "flex", gap: 6, alignItems: "center" }}>
                  <input type="checkbox" checked={t.enabled} onChange={() => toggle(t)} /> {t.enabled ? "On" : "Off"}
                </label>
              </td>
              <td style={tdStyle}>
                <div><em>{t.title}</em></div>
                <div style={{ fontSize: 13 }}>{t.messageText}</div>
                {t.isCustomized && t.updatedByEmail && (
                  <div style={mutedStyle}>Edited by {t.updatedByEmail}{t.updatedAt ? ` on ${new Date(t.updatedAt).toLocaleString()}` : ""}</div>
                )}
              </td>
              <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                <button onClick={() => setEditing(t)}>Edit</button>{" "}
                {t.isCustomized && <button onClick={() => reset(t.type)}>Reset</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {editing && (
        <div style={panelStyle}>
          <h2 style={{ fontSize: 18, marginTop: 0 }}>Edit: {TYPE_LABELS[editing.type] ?? editing.type}</h2>
          <div style={{ marginBottom: 8 }}>
            <label>Title</label><br />
            <input value={editing.title} maxLength={200} style={{ width: "100%" }} onChange={(e) => setEditing({ ...editing, title: e.target.value })} />
          </div>
          <div style={{ marginBottom: 8 }}>
            <label>Message text</label><br />
            <textarea
              value={editing.messageText}
              maxLength={2000}
              rows={4}
              style={{ width: "100%" }}
              onChange={(e) => setEditing({ ...editing, messageText: e.target.value })}
            />
          </div>
          <div style={{ marginBottom: 12, display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center" }}>
            <span style={mutedStyle}>Insert:</span>
            {data.supportedVariables.map((v) => (
              <button key={v} type="button" style={{ fontSize: 12 }} onClick={() => insertVariable(v)}>{`{{${v}}}`}</button>
            ))}
          </div>
          {preview && (
            <div style={{ ...panelStyle, background: "var(--color-surface-alt)" }}>
              <div style={mutedStyle}>Preview with sample data</div>
              <strong>{preview.title}</strong>
              <div>{preview.message}</div>
            </div>
          )}
          <div style={{ display: "flex", gap: 8 }}>
            <button onClick={save}>Save</button>
            <button onClick={() => { setEditing(null); setError(null); }}>Cancel</button>
          </div>
        </div>
      )}
    </div>
  );
}

function LogTab() {
  const [rows, setRows] = useState<NotificationDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [employeeId, setEmployeeId] = useState("");
  const [status, setStatus] = useState("");
  const [type, setType] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams({ take: "200" });
      if (employeeId) params.set("employeeId", employeeId);
      if (status) params.set("status", status);
      if (type) params.set("type", type);
      const res = await apiClient.get<NotificationDto[]>(`/notifications?${params.toString()}`);
      setRows(res.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view notifications (requires Supervisor role or above)."
        : "Failed to load notifications.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    apiClient.get<EmployeeDto[]>("/employees").then((r) => setEmployees(r.data)).catch(() => undefined);
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div>
      <div style={{ display: "flex", gap: 12, alignItems: "end", marginBottom: 16, flexWrap: "wrap" }}>
        <div>
          <label>Employee</label><br />
          <select value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
            <option value="">All</option>
            {employees.map((e) => <option key={e.id} value={e.id}>{e.fullName}</option>)}
          </select>
        </div>
        <div>
          <label>Type</label><br />
          <select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">All</option>
            {Object.entries(TYPE_LABELS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </div>
        <div>
          <label>Status</label><br />
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            {["Queued", "Sent", "Acknowledged", "Cancelled", "Failed"].map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>
        <button onClick={load}>Filter</button>
      </div>

      {error && <div className="form-error">{error}</div>}
      {loading ? <p>Loading...</p> : (
        <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
          <thead>
            <tr>
              <th style={thStyle}>Created</th>
              <th style={thStyle}>Employee</th>
              <th style={thStyle}>Case</th>
              <th style={thStyle}>Type</th>
              <th style={thStyle}>Status</th>
              <th style={thStyle}>Message</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((n) => (
              <tr key={n.id}>
                <td style={tdStyle}>{new Date(n.createdAt).toLocaleString()}</td>
                <td style={tdStyle}>{n.employeeName}</td>
                <td style={tdStyle}>{n.caseNumber ?? "—"}</td>
                <td style={tdStyle}>{TYPE_LABELS[n.type] ?? n.type}</td>
                <td style={tdStyle}>
                  <span style={{ ...pillStyle, background: STATUS_COLORS[n.status] ?? "var(--color-pill-neutral)" }}>{n.status}</span>
                  <div style={mutedStyle}>
                    {n.status === "Sent" && `to ${n.deliveredAgentCount} agent${n.deliveredAgentCount === 1 ? "" : "s"}`}
                    {n.status === "Queued" && "no agent online — picked up on next sync"}
                    {n.acknowledgedAt && `ack ${new Date(n.acknowledgedAt).toLocaleString()}`}
                    {n.failureReason}
                  </div>
                </td>
                <td style={tdStyle}><em>{n.title}</em><div style={{ fontSize: 13 }}>{n.message}</div></td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td style={tdStyle} colSpan={6}>No notifications found.</td></tr>}
          </tbody>
        </table>
      )}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, marginBottom: 16, background: "var(--color-surface)" };
const activeTabStyle: React.CSSProperties = { background: "var(--color-accent)", color: "#fff" };
const pillStyle: React.CSSProperties = { color: "#fff", fontSize: 11, padding: "2px 8px", borderRadius: 999 };
const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
