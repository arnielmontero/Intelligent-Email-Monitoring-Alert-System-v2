import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";

interface Counts {
  emails: number;
  cases: number;
  caseEvents: number;
  aiDecisions: number;
  replyChecks: number;
  reminders: number;
  escalations: number;
  notifications: number;
  employeeActions: number;
  intakeLogs: number;
  total: number;
}

interface TestRecord {
  kind: string;
  id: string;
  name: string;
  detail: string;
}

interface Preview {
  counts: Counts;
  testRecords: TestRecord[];
}

interface ResetResult {
  removed: Counts;
  newCutoff: string;
  removedTestRecords: TestRecord[];
}

const COUNT_LABELS: Array<[keyof Counts, string]> = [
  ["emails", "Emails"],
  ["cases", "Cases"],
  ["caseEvents", "Case timeline entries"],
  ["aiDecisions", "AI decisions"],
  ["replyChecks", "Reply checks"],
  ["reminders", "Reminders"],
  ["escalations", "Escalations"],
  ["notifications", "Pop-up notifications"],
  ["employeeActions", "Employee actions on Cases"],
  ["intakeLogs", "Mailbox check logs"],
];

const KEPT = [
  "Users and roles",
  "Employees, departments and supervisors",
  "Mailboxes, owners and passwords",
  "Windows Agents",
  "AI models, API key and classification profiles",
  "Reminder and escalation policies and groups",
  "Notification messages, settings and email rules",
  "AI cost history and the Audit Log",
];

/** Super administrators only: removes everything that came from email and keeps configuration. */
export default function ResetEmailDataPanel() {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [confirmation, setConfirmation] = useState("");
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<ResetResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const res = await apiClient.get<Preview>("/maintenance/reset-email-data");
      setPreview(res.data);
      setSelected(new Set(res.data.testRecords.map((r) => r.id)));
    } catch {
      setError("Failed to load what a reset would remove.");
    }
  }

  useEffect(() => {
    load();
  }, []);

  function toggle(id: string) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelected(next);
  }

  async function reset() {
    if (!window.confirm("Permanently delete all email data? This cannot be undone.")) return;
    setRunning(true);
    setError(null);
    setResult(null);
    try {
      const res = await apiClient.post<ResetResult>("/maintenance/reset-email-data", {
        confirmation,
        removeTestRecordIds: Array.from(selected),
      });
      setResult(res.data);
      setConfirmation("");
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "The reset failed. It can safely be run again.");
    } finally {
      setRunning(false);
    }
  }

  return (
    <section style={panelStyle}>
      <h2 style={{ fontSize: 16, margin: "0 0 4px" }}>Reset email data</h2>
      <p style={mutedStyle}>
        Deletes everything that came from email and starts fresh from now. Configuration is kept. Emails already in the
        mailboxes are not downloaded again — only email arriving after the reset becomes new work. The reset is recorded in the Audit Log.
      </p>
      {error && <div className="form-error">{error}</div>}

      {result && (
        <div style={successStyle}>
          Reset complete: {result.removed.total} records removed
          {result.removedTestRecords.length > 0 && `, plus ${result.removedTestRecords.length} test record${result.removedTestRecords.length === 1 ? "" : "s"}`}.
          New email is processed from {new Date(result.newCutoff).toLocaleString()}.
        </div>
      )}

      {preview && (
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(300px, 1fr))", gap: 16, marginTop: 12 }}>
          <div>
            <div className="section-title" style={{ marginTop: 0 }}>Will be deleted</div>
            <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
              <tbody>
                {COUNT_LABELS.map(([key, label]) => (
                  <tr key={key}>
                    <td>{label}</td>
                    <td style={{ textAlign: "right", fontVariantNumeric: "tabular-nums", fontWeight: 600 }}>{preview.counts[key].toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div>
            <div className="section-title" style={{ marginTop: 0 }}>Kept</div>
            <ul style={{ margin: 0, paddingLeft: 18, lineHeight: 1.9, fontSize: 13.5 }}>
              {KEPT.map((k) => <li key={k}>{k}</li>)}
            </ul>
          </div>
        </div>
      )}

      {preview && preview.testRecords.length > 0 && (
        <>
          <div className="section-title">Test records found — also remove the ticked ones</div>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <thead>
              <tr>
                <th style={{ width: 40 }}></th>
                <th>Type</th>
                <th>Name</th>
                <th>Details</th>
              </tr>
            </thead>
            <tbody>
              {preview.testRecords.map((r) => (
                <tr key={r.id} onClick={() => toggle(r.id)} style={{ cursor: "pointer" }}>
                  <td><input type="checkbox" checked={selected.has(r.id)} onChange={() => toggle(r.id)} onClick={(e) => e.stopPropagation()} aria-label={`Remove ${r.name}`} /></td>
                  <td>{r.kind}</td>
                  <td>{r.name}</td>
                  <td style={{ color: "var(--color-text-muted)" }}>{r.detail || "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p style={{ ...mutedStyle, marginTop: 8 }}>
            Reminder and escalation policies and groups are never removed. Real records linked to a removed test record
            (for example an employee whose supervisor was a test person) are kept and the link is cleared.
          </p>
        </>
      )}

      <div style={{ display: "flex", gap: 10, alignItems: "flex-end", flexWrap: "wrap", marginTop: 16 }}>
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <label htmlFor="reset-confirm">Type RESET to confirm</label>
          <input id="reset-confirm" value={confirmation} onChange={(e) => setConfirmation(e.target.value)} autoComplete="off" style={{ width: 200 }} />
        </div>
        <button className="btn-danger" disabled={running || confirmation.trim() !== "RESET"} onClick={reset}>
          {running ? "Resetting..." : "Reset email data"}
        </button>
      </div>
    </section>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13, margin: 0 };
const panelStyle: React.CSSProperties = {
  marginTop: 40, border: "1px solid var(--color-border)", borderLeft: "4px solid var(--color-danger)", borderRadius: 8,
  padding: 20, background: "var(--color-surface)", boxShadow: "var(--shadow-card)",
};
const successStyle: React.CSSProperties = {
  background: "color-mix(in srgb, var(--color-success) 12%, transparent)", color: "var(--color-success)",
  border: "1px solid color-mix(in srgb, var(--color-success) 30%, transparent)", padding: "8px 12px", borderRadius: 6, margin: "12px 0",
};
