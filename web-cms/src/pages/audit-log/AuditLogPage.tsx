import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AuditLogDto } from "../../api/types";

export default function AuditLogPage() {
  const [logs, setLogs] = useState<AuditLogDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionFilter, setActionFilter] = useState("");
  const [entityTypeFilter, setEntityTypeFilter] = useState("");
  const [take, setTake] = useState(100);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams();
      if (actionFilter.trim() !== "") params.set("action", actionFilter.trim());
      if (entityTypeFilter.trim() !== "") params.set("entityType", entityTypeFilter.trim());
      params.set("take", String(take));
      const res = await apiClient.get<AuditLogDto[]>(`/audit-logs?${params.toString()}`);
      setLogs(res.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view the Audit Log (requires Auditor role or above)."
        : "Failed to load Audit Log.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Audit Log</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Every change an administrator makes to settings, users, accounts and policies, with who and when.
        This log can't be edited. What happened to individual Cases is in Case History & Logs.
      </p>

      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "flex", gap: 12, alignItems: "end", marginBottom: 16, flexWrap: "wrap" }}>
        <div>
          <label>Action contains</label><br />
          <input value={actionFilter} onChange={(e) => setActionFilter(e.target.value)} placeholder="e.g. AGENT_" />
        </div>
        <div>
          <label>Entity Type</label><br />
          <input value={entityTypeFilter} onChange={(e) => setEntityTypeFilter(e.target.value)} placeholder="e.g. Agent" />
        </div>
        <div>
          <label>Take</label><br />
          <input type="number" min={1} max={500} value={take} onChange={(e) => setTake(Number(e.target.value))} style={{ width: 80 }} />
        </div>
        <button onClick={load}>Filter</button>
      </div>

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>Occurred At</th>
            <th style={thStyle}>User</th>
            <th style={thStyle}>Action</th>
            <th style={thStyle}>Entity Type</th>
            <th style={thStyle}>Entity ID</th>
            <th style={thStyle}>Details</th>
            <th style={thStyle}>IP Address</th>
          </tr>
        </thead>
        <tbody>
          {logs.map((l) => (
            <tr key={l.id}>
              <td style={tdStyle}>{new Date(l.occurredAt).toLocaleString()}</td>
              <td style={tdStyle}>{l.userEmail ?? "—"}</td>
              <td style={tdStyle}>{l.action}</td>
              <td style={tdStyle}>{l.entityType}</td>
              <td style={tdStyle}>{l.entityId ?? "—"}</td>
              <td style={tdStyle}>{l.details ?? "—"}</td>
              <td style={tdStyle}>{l.ipAddress ?? "—"}</td>
            </tr>
          ))}
          {logs.length === 0 && (
            <tr><td style={tdStyle} colSpan={7}>No Audit Log entries found.</td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px" };
