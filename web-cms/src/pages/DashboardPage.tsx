import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import type { DashboardSummaryDto } from "../api/types";

/// Requirements §87 — every metric named there, computed from real data via GET /api/v1/dashboard/summary.
export default function DashboardPage() {
  const [summary, setSummary] = useState<DashboardSummaryDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  async function load() {
    setError(null);
    try {
      const res = await apiClient.get<DashboardSummaryDto>("/dashboard/summary");
      setSummary(res.data);
    } catch {
      setError("Failed to load Dashboard.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    const interval = setInterval(load, 60000);
    return () => clearInterval(interval);
  }, []);

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Dashboard</h1>

      {error && <div className="form-error">{error}</div>}

      {summary && (
        <>
          <div style={gridStyle}>
            <Tile label="Important Emails Today" value={summary.importantEmailsToday} />
            <Tile label="Open Cases" value={summary.openCases} />
            <Tile label="Awaiting Reply" value={summary.awaitingReply} />
            <Tile label="Overdue" value={summary.overdue} warn={summary.overdue > 0} />
            <Tile label="Escalated" value={summary.escalated} warn={summary.escalated > 0} />
            <Tile label="Completed Today" value={summary.completedToday} good />
            <Tile label="Online Employees" value={summary.onlineEmployees} good />
            <Tile label="Offline Employees" value={summary.offlineEmployees} />
            <Tile label="Pending Agent Approvals" value={summary.pendingAgentApprovals} warn={summary.pendingAgentApprovals > 0} />
            <Tile label="AI Errors" value={summary.aiErrors} warn={summary.aiErrors > 0} />
            <Tile label="Email Monitoring Errors" value={summary.emailMonitoringErrors} warn={summary.emailMonitoringErrors > 0} />
            <Tile label="Background Job Failures" value={summary.backgroundJobFailures} warn={summary.backgroundJobFailures > 0} />
          </div>

          <h2 style={{ fontSize: 15, marginTop: 28 }}>Open Cases by Employee</h2>
          {summary.openCasesByEmployee.length === 0 ? (
            <p style={{ color: "var(--color-text-muted)" }}>No open Cases.</p>
          ) : (
            <table style={{ borderCollapse: "collapse" }}>
              <thead>
                <tr><th style={thStyle}>Employee</th><th style={thStyle}>Open Cases</th></tr>
              </thead>
              <tbody>
                {summary.openCasesByEmployee.map((e) => (
                  <tr key={e.employeeId}>
                    <td style={tdStyle}>{e.employeeName}</td>
                    <td style={tdStyle}>{e.openCaseCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          <p style={{ color: "var(--color-text-muted)", fontSize: 12, marginTop: 24 }}>
            For full dependency status see <a href="/system-health">System Health</a>. Auto-refreshes every 60 seconds.
          </p>
        </>
      )}
    </div>
  );
}

function Tile({ label, value, warn, good }: { label: string; value: number; warn?: boolean; good?: boolean }) {
  const color = warn ? "#ef4444" : good ? "var(--color-success)" : "inherit";
  return (
    <div style={{ border: "1px solid var(--color-border)", borderRadius: 8, padding: 16 }}>
      <div style={{ fontSize: 12, color: "var(--color-text-muted)", marginBottom: 6 }}>{label}</div>
      <div style={{ fontSize: 28, fontWeight: 700, color }}>{value}</div>
    </div>
  );
}

const gridStyle: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 12 };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px" };
