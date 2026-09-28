import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto, EmployeeDto, TestConnectionResult } from "../../api/types";

const PROTOCOL_LABELS = ["IMAP", "Microsoft Graph", "SMTP"];
const AUTH_METHOD_LABELS = ["Password", "OAuth2", "App Password"];

interface AccountForm {
  emailAddress: string;
  displayName: string;
  host: string;
  port: number;
  encryption: string;
  username: string;
  secret: string;
  ownerEmployeeId: string;
  classificationProfileName: string;
  monitoringEnabled: boolean;
  /** datetime-local value; "" with processAllHistory=false means "from now on" for a new account. */
  cutoffLocal: string;
  processAllHistory: boolean;
}

function toLocalInput(iso: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

const EMPTY_FORM: AccountForm = {
  emailAddress: "",
  displayName: "",
  host: "",
  port: 993,
  encryption: "SSL/TLS",
  username: "",
  secret: "",
  ownerEmployeeId: "",
  classificationProfileName: "",
  monitoringEnabled: true,
  cutoffLocal: "",
  processAllHistory: false,
};

export default function EmailAccountsPage() {
  const [accounts, setAccounts] = useState<EmailAccountDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, TestConnectionResult>>({});
  const [testingId, setTestingId] = useState<string | null>(null);

  /** null = form closed, "new" = adding, otherwise the account being edited. */
  const [editing, setEditing] = useState<EmailAccountDto | "new" | null>(null);
  const [form, setForm] = useState<AccountForm>(EMPTY_FORM);

  async function load() {
    try {
      const [accRes, empRes] = await Promise.all([
        apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0"),
        apiClient.get<EmployeeDto[]>("/employees"),
      ]);
      setAccounts(accRes.data);
      setEmployees(empRes.data);
    } catch {
      setError("Failed to load email accounts.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  function startAdd() {
    setEditing("new");
    setForm(EMPTY_FORM);
    setError(null);
    setNotice(null);
  }

  function startEdit(acc: EmailAccountDto) {
    setEditing(acc);
    setForm({
      emailAddress: acc.emailAddress,
      displayName: acc.displayName ?? "",
      host: acc.host,
      port: acc.port,
      encryption: acc.encryption,
      username: acc.username,
      secret: "",
      ownerEmployeeId: acc.ownerEmployeeId ?? "",
      classificationProfileName: acc.classificationProfileName ?? "",
      monitoringEnabled: acc.monitoringEnabled,
      cutoffLocal: toLocalInput(acc.processEmailsReceivedAfter),
      processAllHistory: acc.processEmailsReceivedAfter === null,
    });
    setError(null);
    setNotice(null);
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    const cutoff = form.processAllHistory ? null : form.cutoffLocal ? new Date(form.cutoffLocal).toISOString() : null;
    if (editing !== "new" && !form.processAllHistory && !form.cutoffLocal) {
      setError("Pick a date for 'Create Cases only for email received after', or tick 'Process the whole mailbox history'.");
      return;
    }
    try {
      if (editing === "new") {
        await apiClient.post("/email-accounts", {
          emailAddress: form.emailAddress,
          displayName: form.displayName || null,
          purpose: 0, // Inbound — outbound accounts are configured on the Outbound Email page (§18)
          kind: 0,
          protocol: 0,
          host: form.host,
          port: form.port,
          encryption: form.encryption,
          username: form.username,
          authMethod: 0,
          secret: form.secret,
          ownerEmployeeId: form.ownerEmployeeId || null,
          classificationProfileName: form.classificationProfileName || null,
          monitoringEnabled: form.monitoringEnabled,
          processEmailsReceivedAfter: form.processAllHistory ? "1900-01-01T00:00:00Z" : cutoff,
        });
        setNotice(`Added ${form.emailAddress}.`);
      } else if (editing) {
        await apiClient.put(`/email-accounts/${editing.id}`, {
          displayName: form.displayName || null,
          kind: editing.kind,
          host: form.host,
          port: form.port,
          encryption: form.encryption,
          username: form.username,
          authMethod: editing.authMethod,
          secret: form.secret || null,
          ownerEmployeeId: form.ownerEmployeeId || null,
          classificationProfileName: form.classificationProfileName || null,
          monitoringEnabled: editing.isActive && form.monitoringEnabled,
          isActive: editing.isActive,
          processEmailsReceivedAfter: cutoff,
        });
        setNotice(`Saved ${editing.emailAddress}.`);
      }
      setEditing(null);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save email account.");
    }
  }

  async function handleTestConnection(id: string) {
    setTestingId(id);
    try {
      const res = await apiClient.post<TestConnectionResult>(`/email-accounts/${id}/test-connection`);
      setTestResults((prev) => ({ ...prev, [id]: res.data }));
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to test connection.");
    } finally {
      setTestingId(null);
    }
  }

  async function handleToggleActive(account: EmailAccountDto) {
    try {
      await apiClient.post(`/email-accounts/${account.id}/${account.isActive ? "deactivate" : "activate"}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update account status.");
    }
  }

  async function handleDelete(account: EmailAccountDto) {
    if (!window.confirm(`Permanently delete ${account.emailAddress} and its stored credential? This cannot be undone.`)) return;
    setError(null);
    try {
      await apiClient.delete(`/email-accounts/${account.id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete email account.");
    }
  }

  if (loading) return <p>Loading...</p>;

  const isNew = editing === "new";

  return (
    <div>
      <h1>Email Accounts</h1>
      <p style={mutedStyle}>
        The mailboxes IEMAS watches for customer email. The <strong>owner</strong> is the employee whose Windows Agent receives this
        mailbox's alerts — an Agent can only be approved for an employee who owns its email account. Passwords are
        encrypted and never displayed after saving.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      {editing === null && (
        <button className="btn-primary" onClick={startAdd} style={{ marginBottom: 16 }}>+ Add email account</button>
      )}

      {editing !== null && (
        <form onSubmit={handleSubmit} style={panelStyle}>
          <h2 style={{ fontSize: 18, marginTop: 0 }}>{isNew ? "Add email account" : `Edit ${form.emailAddress}`}</h2>
          <div style={gridStyle}>
            <Field label="Email address">
              <input type="email" value={form.emailAddress} disabled={!isNew} required
                onChange={(e) => setForm({ ...form, emailAddress: e.target.value })} style={fullWidth} />
            </Field>
            <Field label="Display name">
              <input value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} style={fullWidth} />
            </Field>
            <Field label="Owner employee">
              <select value={form.ownerEmployeeId} onChange={(e) => setForm({ ...form, ownerEmployeeId: e.target.value })} style={fullWidth}>
                <option value="">— none —</option>
                {employees.filter((emp) => emp.isActive || emp.id === form.ownerEmployeeId).map((emp) => (
                  <option key={emp.id} value={emp.id}>{emp.fullName} ({emp.email})</option>
                ))}
              </select>
            </Field>
            <Field label="IMAP host">
              <input value={form.host} required placeholder="imap.example.com" onChange={(e) => setForm({ ...form, host: e.target.value })} style={fullWidth} />
            </Field>
            <Field label="Port">
              <input type="number" min={1} max={65535} value={form.port} required onChange={(e) => setForm({ ...form, port: Number(e.target.value) })} style={fullWidth} />
            </Field>
            <Field label="Encryption">
              <select value={form.encryption} onChange={(e) => setForm({ ...form, encryption: e.target.value })} style={fullWidth}>
                <option value="SSL/TLS">SSL/TLS</option>
                <option value="STARTTLS">STARTTLS</option>
                <option value="NONE">None</option>
              </select>
            </Field>
            <Field label="Username">
              <input value={form.username} required onChange={(e) => setForm({ ...form, username: e.target.value })} style={fullWidth} />
            </Field>
            <Field label="Password / App password">
              <input type="password" value={form.secret} required={isNew} autoComplete="new-password"
                placeholder={isNew ? "" : "Leave blank to keep the current password"}
                onChange={(e) => setForm({ ...form, secret: e.target.value })} style={fullWidth} />
            </Field>
            <Field label="Classification profile">
              <input value={form.classificationProfileName} placeholder="Sales"
                onChange={(e) => setForm({ ...form, classificationProfileName: e.target.value })} style={fullWidth} />
            </Field>
          </div>
          <div style={{ ...panelInnerStyle }}>
            <label>Create Cases only for email received after</label>
            <div style={{ display: "flex", gap: 16, alignItems: "center", flexWrap: "wrap", marginTop: 4 }}>
              <input type="datetime-local" value={form.cutoffLocal} disabled={form.processAllHistory}
                onChange={(e) => setForm({ ...form, cutoffLocal: e.target.value })} />
              <label style={{ display: "flex", gap: 8, alignItems: "center", color: "var(--color-text)" }}>
                <input type="checkbox" checked={form.processAllHistory}
                  onChange={(e) => setForm({ ...form, processAllHistory: e.target.checked })} />
                Process the whole mailbox history
              </label>
            </div>
            <div style={hintStyle}>
              {isNew && !form.cutoffLocal && !form.processAllHistory
                ? "Left empty: from the moment the account is added. Older email is stored as history but never becomes a Case."
                : "Older email is stored as history (for reply matching) but never becomes a Case, alert or reminder. Moving the date earlier queues that email for classification."}
            </div>
          </div>
          <label style={{ display: "flex", gap: 8, alignItems: "center", marginTop: 12 }}>
            <input type="checkbox" checked={form.monitoringEnabled}
              onChange={(e) => setForm({ ...form, monitoringEnabled: e.target.checked })} />
            Monitoring enabled (poll this mailbox for new email)
          </label>
          <div style={{ display: "flex", gap: 8, marginTop: 16 }}>
            <button type="submit" className="btn-primary">{isNew ? "Add account" : "Save changes"}</button>
            <button type="button" onClick={() => setEditing(null)}>Cancel</button>
          </div>
        </form>
      )}

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>Email</th>
            <th style={thStyle}>Protocol</th>
            <th style={thStyle}>Host:Port</th>
            <th style={thStyle}>Owner</th>
            <th style={thStyle}>Monitoring</th>
            <th style={thStyle}>Credential</th>
            <th style={thStyle}>Last Test</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {accounts.map((acc) => {
            const testResult = testResults[acc.id];
            return (
              <tr key={acc.id} style={{ opacity: acc.isActive ? 1 : 0.6 }}>
                <td style={tdStyle}>{acc.emailAddress}{!acc.isActive && <div style={hintStyle}>Deactivated</div>}</td>
                <td style={tdStyle}>{PROTOCOL_LABELS[acc.protocol] ?? "—"} / {AUTH_METHOD_LABELS[acc.authMethod]}</td>
                <td style={tdStyle}>{acc.host}:{acc.port}</td>
                <td style={tdStyle}>
                  {acc.ownerEmployeeName ?? <span style={{ color: "var(--color-warning)" }}>No owner</span>}
                </td>
                <td style={tdStyle}>
                  {acc.monitoringEnabled ? "Enabled" : "Disabled"}
                  <div style={hintStyle}>
                    {acc.processEmailsReceivedAfter ? `Cases from ${new Date(acc.processEmailsReceivedAfter).toLocaleDateString()}` : "Cases from all history"}
                  </div>
                </td>
                <td style={tdStyle}>{acc.hasCredential ? "Configured" : "Not set"}</td>
                <td style={tdStyle}>
                  {testResult
                    ? (testResult.succeeded ? "✓ Success" : `✗ ${testResult.errorMessage}`)
                    : (acc.lastTestedAt ? (acc.lastTestSucceeded ? "✓ Success" : `✗ ${acc.lastTestError}`) : "Not tested")}
                </td>
                <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                  <div style={{ display: "flex", gap: 6 }}>
                    <button onClick={() => handleTestConnection(acc.id)} disabled={testingId === acc.id}>
                      {testingId === acc.id ? "Testing..." : "Test"}
                    </button>
                    <button onClick={() => startEdit(acc)}>Edit</button>
                    <button onClick={() => handleToggleActive(acc)} style={{ minWidth: 88 }}>{acc.isActive ? "Deactivate" : "Activate"}</button>
                    <button
                      className="btn-danger"
                      disabled={acc.isActive}
                      title={acc.isActive ? "Deactivate this account before deleting" : "Permanently delete this account"}
                      onClick={() => handleDelete(acc)}
                    >
                      Delete
                    </button>
                  </div>
                </td>
              </tr>
            );
          })}
          {accounts.length === 0 && <tr><td style={tdStyle} colSpan={8}>No email accounts yet.</td></tr>}
        </tbody>
      </table>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
      <label>{label}</label>
      {children}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 16 };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, marginBottom: 24, background: "var(--color-surface)" };
const gridStyle: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: 12 };
const fullWidth: React.CSSProperties = { width: "100%" };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "top" };
const panelInnerStyle: React.CSSProperties = { marginTop: 16, display: "flex", flexDirection: "column", gap: 4 };
