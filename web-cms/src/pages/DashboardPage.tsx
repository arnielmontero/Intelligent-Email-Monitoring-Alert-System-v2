import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import type { DashboardSummaryDto } from "../api/types";
import StatCard from "../components/StatCard";

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
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>Today at a glance. Updates every 60 seconds.</p>

      {error && <div className="form-error">{error}</div>}

      {summary && (
        <>
          <div className="section-title" style={{ marginTop: 0 }}>Work</div>
          <div className="stat-grid">
            <StatCard label="Important emails today" value={summary.importantEmailsToday} to="/case-workflow" />
            <StatCard label="Open Cases" value={summary.openCases} to="/cases" />
            <StatCard label="Awaiting reply" value={summary.awaitingReply} to="/reply-verification" />
            <StatCard label="Overdue" value={summary.overdue} tone={summary.overdue > 0 ? "warn" : "neutral"} to="/cases?status=6" />
            <StatCard label="Escalated" value={summary.escalated} tone={summary.escalated > 0 ? "bad" : "neutral"} to="/cases?status=7" />
            <StatCard label="Completed today" value={summary.completedToday} to="/cases?status=8" />
          </div>

          <div className="section-title">Team &amp; devices</div>
          <div className="stat-grid">
            <StatCard label="Employees online" value={summary.onlineEmployees} to="/employee-activity" />
            <StatCard label="Employees offline" value={summary.offlineEmployees} to="/employee-activity" />
            <StatCard label="Agents awaiting approval" value={summary.pendingAgentApprovals} tone={summary.pendingAgentApprovals > 0 ? "warn" : "neutral"} to="/agents" />
          </div>

          <div className="section-title">System</div>
          <div className="stat-grid">
            <StatCard label="AI errors" value={summary.aiErrors} tone={summary.aiErrors > 0 ? "bad" : "neutral"} to="/system-health" />
            <StatCard label="Mailbox errors" value={summary.emailMonitoringErrors} tone={summary.emailMonitoringErrors > 0 ? "bad" : "neutral"} to="/email-monitoring" />
            <StatCard label="Background job failures" value={summary.backgroundJobFailures} tone={summary.backgroundJobFailures > 0 ? "bad" : "neutral"} to="/system-health" />
          </div>

          <div className="section-title">Open Cases by employee</div>
          {summary.openCasesByEmployee.length === 0 ? (
            <p style={{ color: "var(--color-text-muted)" }}>No open Cases.</p>
          ) : (
            <table style={{ borderCollapse: "separate", borderSpacing: 0, minWidth: 360 }}>
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

        </>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
