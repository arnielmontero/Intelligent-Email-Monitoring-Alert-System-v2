import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AuditLogDto, PauseControlDto } from "../../api/types";
import { useAuthStore } from "../../store/authStore";

/// Requirements §91 — Emergency Pause. Changes are permission-controlled (Administrator+),
/// audited, timestamped and attributed; pausing never deletes Cases.
export default function MaintenancePage() {
  const canEdit = useAuthStore((s) => s.hasRole("SuperAdministrator", "Administrator"));
  const [controls, setControls] = useState<PauseControlDto[]>([]);
  const [history, setHistory] = useState<AuditLogDto[]>([]);
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const [controlsRes, historyRes] = await Promise.all([
        apiClient.get<PauseControlDto[]>("/maintenance/pause"),
        apiClient.get<AuditLogDto[]>("/maintenance/pause/history?take=25"),
      ]);
      setControls(controlsRes.data);
      setHistory(historyRes.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view Emergency Pause (requires Auditor role or above)."
        : "Failed to load Emergency Pause status.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function setPaused(control: PauseControlDto, isPaused: boolean) {
    setError(null);
    const reason = (reasons[control.control] ?? "").trim();
    if (isPaused && reason === "") {
      setError(`Enter a reason before pausing "${control.label}".`);
      return;
    }
    if (isPaused && !window.confirm(`${control.label}?\n\n${control.description}`)) return;

    setBusy(control.control);
    try {
      await apiClient.put(`/maintenance/pause/${control.control}`, { isPaused, reason: reason || null });
      setReasons((r) => ({ ...r, [control.control]: "" }));
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to change pause state.");
    } finally {
      setBusy(null);
    }
  }

  if (loading) return <p>Loading...</p>;

  const pausedCount = controls.filter((c) => c.isPaused).length;

  return (
    <div>
      <h1>Maintenance / Emergency Pause</h1>
      <p style={mutedStyle}>
        Each switch stops one engine at its next run. Nothing is deleted: work waits and resumes where it left off.
        {!canEdit && " You have read-only access."}
      </p>

      {pausedCount > 0 && (
        <div style={bannerStyle}>{pausedCount} control{pausedCount === 1 ? " is" : "s are"} currently paused.</div>
      )}
      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "grid", gap: 12, marginBottom: 32 }}>
        {controls.map((c) => (
          <div key={c.control} style={{ ...cardStyle, borderColor: c.isPaused ? "var(--color-danger)" : "var(--color-border)" }}>
            <div style={{ display: "flex", justifyContent: "space-between", gap: 16, flexWrap: "wrap" }}>
              <div style={{ flex: "1 1 320px" }}>
                <strong>{c.label}</strong>{" "}
                <span style={{ ...pillStyle, background: c.isPaused ? "var(--color-danger)" : "#166534" }}>
                  {c.isPaused ? "PAUSED" : "RUNNING"}
                </span>
                <div style={mutedStyle}>{c.description}</div>
                {c.changedAt && (
                  <div style={{ ...mutedStyle, marginTop: 4 }}>
                    Last changed {new Date(c.changedAt).toLocaleString()} by {c.changedByEmail ?? "unknown"}
                    {c.isPaused && c.reason && <> — reason: <em>{c.reason}</em></>}
                  </div>
                )}
              </div>
              {canEdit && (
                <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                  {!c.isPaused && (
                    <input
                      placeholder="Reason (required)"
                      maxLength={500}
                      value={reasons[c.control] ?? ""}
                      onChange={(e) => setReasons((r) => ({ ...r, [c.control]: e.target.value }))}
                      style={{ width: 220 }}
                    />
                  )}
                  <button disabled={busy === c.control} onClick={() => setPaused(c, !c.isPaused)}>
                    {c.isPaused ? "Resume" : "Pause"}
                  </button>
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      <h2>Recent changes</h2>
      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>When</th>
            <th style={thStyle}>User</th>
            <th style={thStyle}>Change</th>
            <th style={thStyle}>Control</th>
            <th style={thStyle}>Reason</th>
          </tr>
        </thead>
        <tbody>
          {history.map((h) => (
            <tr key={h.id}>
              <td style={tdStyle}>{new Date(h.occurredAt).toLocaleString()}</td>
              <td style={tdStyle}>{h.userEmail ?? "—"}</td>
              <td style={tdStyle}>{h.action === "EMERGENCY_PAUSE_ENABLED" ? "Paused" : "Resumed"}</td>
              <td style={tdStyle}>{h.entityId}</td>
              <td style={tdStyle}>{h.details ?? "—"}</td>
            </tr>
          ))}
          {history.length === 0 && <tr><td style={tdStyle} colSpan={5}>No pause changes recorded.</td></tr>}
        </tbody>
      </table>
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const cardStyle: React.CSSProperties = { border: "1px solid", borderRadius: 8, padding: 16, background: "var(--color-surface)" };
const pillStyle: React.CSSProperties = { color: "#fff", fontSize: 11, padding: "2px 8px", borderRadius: 999, marginLeft: 6 };
const bannerStyle: React.CSSProperties = { background: "var(--color-danger)", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 16 };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
