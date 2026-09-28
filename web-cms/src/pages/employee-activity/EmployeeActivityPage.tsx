import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmployeeActivityDto, EmployeeActivitySummaryDto } from "../../api/types";

const ACTIVITY_LABELS: Record<string, string> = {
  Acknowledged: "Acknowledged",
  WillHandle: "Will handle",
  AlreadyReplied: "Already replied (claim)",
  WaitingForCustomer: "Waiting for customer",
  WaitingForInternal: "Waiting for internal",
  RemindLater: "Remind later",
  MarkCompleted: "Completed",
  Cancel: "Cancel",
  Reopen: "Reopen",
  RequestEscalation: "Requested escalation",
  Comment: "Comment",
};

const RANGES: Array<{ label: string; days: number }> = [
  { label: "Last 24 hours", days: 1 },
  { label: "Last 7 days", days: 7 },
  { label: "Last 30 days", days: 30 },
];

/// Requirements §67 — "Employee Activity: what did the employee do?". Separate from Case History
/// (what happened to the Case) and the Audit Log (administrative changes).
export default function EmployeeActivityPage() {
  const [days, setDays] = useState(7);
  const [summary, setSummary] = useState<EmployeeActivitySummaryDto[]>([]);
  const [rows, setRows] = useState<EmployeeActivityDto[]>([]);
  const [employeeId, setEmployeeId] = useState("");
  const [activity, setActivity] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function load(nextEmployeeId = employeeId, nextActivity = activity, nextDays = days) {
    setLoading(true);
    setError(null);
    try {
      const from = new Date(Date.now() - nextDays * 86400000).toISOString();
      const params = new URLSearchParams({ from, take: "200" });
      if (nextEmployeeId) params.set("employeeId", nextEmployeeId);
      if (nextActivity) params.set("activity", nextActivity);
      const [summaryRes, rowsRes] = await Promise.all([
        apiClient.get<EmployeeActivitySummaryDto[]>(`/employee-activity/summary?from=${encodeURIComponent(from)}`),
        apiClient.get<EmployeeActivityDto[]>(`/employee-activity?${params.toString()}`),
      ]);
      setSummary(summaryRes.data);
      setRows(rowsRes.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view Employee Activity (requires Auditor role or above)."
        : "Failed to load Employee Activity.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function focusEmployee(id: string) {
    setEmployeeId(id);
    load(id, activity, days);
  }

  return (
    <div>
      <h1>Employee Activity</h1>
      <p style={mutedStyle}>
        What employees did through their Windows Agents: acknowledgements, Case actions and comments. Employee actions
        are requests, not verified facts — for example "Already replied" is a claim until Reply Verification confirms it.
      </p>

      <div style={{ display: "flex", gap: 12, alignItems: "end", marginBottom: 16, flexWrap: "wrap" }}>
        <div>
          <label>Period</label><br />
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            {RANGES.map((r) => <option key={r.days} value={r.days}>{r.label}</option>)}
          </select>
        </div>
        <div>
          <label>Employee</label><br />
          <select value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
            <option value="">All</option>
            {summary.map((s) => <option key={s.employeeId} value={s.employeeId}>{s.employeeName}</option>)}
          </select>
        </div>
        <div>
          <label>Activity</label><br />
          <select value={activity} onChange={(e) => setActivity(e.target.value)}>
            <option value="">All</option>
            {Object.entries(ACTIVITY_LABELS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </div>
        <button onClick={() => load()}>Apply</button>
      </div>

      {error && <div className="form-error">{error}</div>}
      {loading ? <p>Loading...</p> : (
        <>
          <h2 style={{ fontSize: 18 }}>Summary</h2>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, marginBottom: 28 }}>
            <thead>
              <tr>
                <th style={thStyle}>Employee</th>
                <th style={thStyle}>Agent</th>
                <th style={thStyle}>Open Cases</th>
                <th style={thStyle}>Actions</th>
                <th style={thStyle}>Acknowledged</th>
                <th style={thStyle}>Completed</th>
                <th style={thStyle}>Comments</th>
                <th style={thStyle}>Notifications (ack'd)</th>
                <th style={thStyle}>Last activity</th>
              </tr>
            </thead>
            <tbody>
              {summary.filter((s) => s.isActive || s.actionCount > 0).map((s) => (
                <tr key={s.employeeId} style={{ cursor: "pointer" }} onClick={() => focusEmployee(s.employeeId)}>
                  <td style={tdStyle}>{s.employeeName}{!s.isActive && <span style={mutedStyle}> (inactive)</span>}</td>
                  <td style={tdStyle}>
                    <span style={{ color: s.connectedAgentCount > 0 ? "var(--color-success)" : "var(--color-text-muted)" }}>
                      {s.connectedAgentCount > 0 ? `Online (${s.connectedAgentCount})` : "Offline"}
                    </span>
                  </td>
                  <td style={tdStyle}>{s.openCaseCount}</td>
                  <td style={tdStyle}>{s.actionCount}</td>
                  <td style={tdStyle}>{s.acknowledgedCount}</td>
                  <td style={tdStyle}>{s.completedCount}</td>
                  <td style={tdStyle}>{s.commentCount}</td>
                  <td style={tdStyle}>{s.notificationCount} ({s.notificationsAcknowledgedCount})</td>
                  <td style={tdStyle}>{s.lastActivityAt ? new Date(s.lastActivityAt).toLocaleString() : "—"}</td>
                </tr>
              ))}
              {summary.length === 0 && <tr><td style={tdStyle} colSpan={9}>No employees.</td></tr>}
            </tbody>
          </table>

          <h2 style={{ fontSize: 18 }}>Activity</h2>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <thead>
              <tr>
                <th style={thStyle}>When</th>
                <th style={thStyle}>Employee</th>
                <th style={thStyle}>Activity</th>
                <th style={thStyle}>Case</th>
                <th style={thStyle}>Comment</th>
                <th style={thStyle}>Device</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td style={tdStyle}>{new Date(r.occurredAt).toLocaleString()}</td>
                  <td style={tdStyle}>{r.employeeName}</td>
                  <td style={tdStyle}>{ACTIVITY_LABELS[r.activity] ?? r.activity}</td>
                  <td style={tdStyle}>{r.caseNumber}<div style={mutedStyle}>{r.caseSubject}</div></td>
                  <td style={tdStyle}>{r.comment ?? "—"}</td>
                  <td style={tdStyle}>{r.agentName}</td>
                </tr>
              ))}
              {rows.length === 0 && <tr><td style={tdStyle} colSpan={6}>No activity in this period.</td></tr>}
            </tbody>
          </table>
        </>
      )}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
