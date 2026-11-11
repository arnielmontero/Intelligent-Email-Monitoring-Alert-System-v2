import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import type { DashboardSummaryDto } from "../api/types";
import StatCard from "../components/StatCard";
import { InfoTip } from "../components/help/Tooltip";

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
            <StatCard label="Important emails today" help="Customer emails received today that the filter or AI judged to be real work needing attention. Newsletters, notices and internal mail are not counted." value={summary.importantEmailsToday} to="/case-workflow" />
            <StatCard label="Open Cases" help="Customer conversations that are not finished yet, across all employees. A Case closes only when someone marks it completed or confirms it resolved." value={summary.openCases} to="/cases" />
            <StatCard label="Awaiting reply" help="Open Cases where no reply to the customer has been found in the owner's Sent folder yet. Click to see them on Reply Verification." value={summary.awaitingReply} to="/reply-verification" />
            <StatCard label="Overdue" help="Cases that passed their reply or resolution deadline and are still open. These need attention first." value={summary.overdue} tone={summary.overdue > 0 ? "warn" : "neutral"} to="/cases?status=6" />
            <StatCard label="Escalated" help="Cases left unanswered after the reminders, so a manager has been told. Click to see them." value={summary.escalated} tone={summary.escalated > 0 ? "bad" : "neutral"} to="/cases?status=7" />
            <StatCard label="Completed today" help="Cases finished today, either marked completed or confirmed resolved by the agent." value={summary.completedToday} to="/cases?status=8" />
          </div>

          <div className="section-title">Team &amp; devices</div>
          <div className="stat-grid">
            <StatCard label="Employees online" help="Employees whose IEMAS Agent (the PC app) is connected right now and can receive pop-ups." value={summary.onlineEmployees} to="/employee-activity" />
            <StatCard label="Employees offline" help="Employees whose PC Agent is not connected. Their pop-ups wait and are delivered when the PC comes back online." value={summary.offlineEmployees} to="/employee-activity" />
            <StatCard label="Agents awaiting approval" help="New PCs that installed the IEMAS Agent and wait for an administrator to approve them. Until approved they receive nothing." value={summary.pendingAgentApprovals} tone={summary.pendingAgentApprovals > 0 ? "warn" : "neutral"} to="/agents" />
          </div>

          <div className="section-title">System</div>
          <div className="stat-grid">
            <StatCard label="AI errors" help="Recent AI calls that failed, for example a wrong API key or the provider being down. Emails are retried; check System Health." value={summary.aiErrors} tone={summary.aiErrors > 0 ? "bad" : "neutral"} to="/system-health" />
            <StatCard label="Mailbox errors" help="Mailboxes IEMAS could not read recently, usually a changed password or server problem. Click to see which one." value={summary.emailMonitoringErrors} tone={summary.emailMonitoringErrors > 0 ? "bad" : "neutral"} to="/email-monitoring" />
            <StatCard label="Background job failures" help="Scheduled tasks (checking mail, reminders, escalation) that failed recently. One-off failures retry by themselves; repeated ones need a look." value={summary.backgroundJobFailures} tone={summary.backgroundJobFailures > 0 ? "bad" : "neutral"} to="/system-health" />
          </div>

          <div className="section-title" style={{ display: "flex", alignItems: "center" }}>Open Cases by employee<InfoTip text="How many unfinished Cases each employee owns. Use it to spot someone overloaded and reassign work." /></div>
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
