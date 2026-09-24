import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { SystemHealthDto } from "../../api/types";

const STATUS_COLOR: Record<string, string> = {
  Healthy: "#22c55e",
  Degraded: "#f59e0b",
  Unhealthy: "#ef4444",
};

/// Requirements §106 (monitor database, email provider, AI provider, background jobs, queue depth,
/// failed jobs; health states HEALTHY/WARNING/FAILED), §86 (SYSTEM nav → System Health). Polls the
/// authenticated GET /api/v1/system-health, which reuses the same health checks as the
/// unauthenticated /health/ready infra endpoint plus Hangfire job counts.
export default function SystemHealthPage() {
  const [health, setHealth] = useState<SystemHealthDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  async function load() {
    setError(null);
    try {
      const res = await apiClient.get<SystemHealthDto>("/system-health");
      setHealth(res.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view System Health (requires Auditor role or above)."
        : "Failed to load System Health.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    const interval = setInterval(load, 30000); // auto-refresh every 30s — this is a live status page
    return () => clearInterval(interval);
  }, []);

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>System Health</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Requirements §106 — database, email provider (IMAP), AI provider (OpenRouter), and
        background job (Hangfire) health, plus current queue depth. Auto-refreshes every 30 seconds.
      </p>

      {error && <div className="form-error">{error}</div>}

      {health && (
        <>
          <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 20 }}>
            <span style={{ width: 12, height: 12, borderRadius: 6, background: STATUS_COLOR[health.status] ?? "#6b7280", display: "inline-block" }} />
            <strong style={{ fontSize: 18 }}>{health.status}</strong>
            <span style={{ color: "var(--color-text-muted)", fontSize: 12 }}>({health.totalDurationMs.toFixed(0)} ms)</span>
            <button onClick={load} style={{ marginLeft: "auto" }}>Refresh Now</button>
          </div>

          <table style={{ width: "100%", borderCollapse: "collapse", marginBottom: 24 }}>
            <thead>
              <tr>
                <th style={thStyle}>Dependency</th>
                <th style={thStyle}>Status</th>
                <th style={thStyle}>Description</th>
                <th style={thStyle}>Duration</th>
              </tr>
            </thead>
            <tbody>
              {health.checks.map((c) => (
                <tr key={c.name}>
                  <td style={tdStyle}>{c.name}</td>
                  <td style={tdStyle}>
                    <span style={{ color: STATUS_COLOR[c.status] ?? "inherit", fontWeight: 600 }}>{c.status}</span>
                  </td>
                  <td style={tdStyle}>{c.description ?? "—"}</td>
                  <td style={tdStyle}>{c.durationMs.toFixed(1)} ms</td>
                </tr>
              ))}
            </tbody>
          </table>

          <h2 style={{ fontSize: 15 }}>Background Jobs (Hangfire)</h2>
          <table style={{ borderCollapse: "collapse" }}>
            <tbody>
              <tr><td style={tdLabel}>Processing</td><td style={tdValue}>{health.processingJobCount ?? "unavailable"}</td></tr>
              <tr><td style={tdLabel}>Scheduled</td><td style={tdValue}>{health.scheduledJobCount ?? "unavailable"}</td></tr>
              <tr><td style={tdLabel}>Failed</td><td style={tdValue}>{health.failedJobCount ?? "unavailable"}</td></tr>
            </tbody>
          </table>
        </>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
const tdLabel: React.CSSProperties = { padding: "4px 12px 4px 0", color: "var(--color-text-muted)" };
const tdValue: React.CSSProperties = { padding: "4px 0", fontWeight: 600 };
