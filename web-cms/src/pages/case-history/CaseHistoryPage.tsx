import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import { CASE_EVENT_TYPE_LABELS } from "../../api/types";
import type { CaseEventSearchResultDto, CaseEventType } from "../../api/types";

/// Requirements §66 (Case History, append-only), §89 (Case Investigation timeline), §86 (HISTORY
/// & AUDIT nav -> Case History & Logs). This is the global, cross-Case search - distinct from the
/// per-Case timeline already shown inside Cases / Work Topics (CasesPage.tsx's own detail view).
export default function CaseHistoryPage() {
  const [events, setEvents] = useState<CaseEventSearchResultDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [caseNumberFilter, setCaseNumberFilter] = useState("");
  const [eventTypeFilter, setEventTypeFilter] = useState("");
  const [take, setTake] = useState(100);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams();
      if (eventTypeFilter !== "") params.set("eventType", eventTypeFilter);
      params.set("take", String(take));
      const res = await apiClient.get<CaseEventSearchResultDto[]>(`/cases/events?${params.toString()}`);
      const filtered = caseNumberFilter.trim() === ""
        ? res.data
        : res.data.filter((e) => e.caseNumber.toLowerCase().includes(caseNumberFilter.trim().toLowerCase()));
      setEvents(filtered);
    } catch {
      setError("Failed to load Case History.");
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
      <h1>Case History & Logs</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Requirements §66/§89 — every event across every Case (email received, status changes,
        employee actions/comments, reminders, escalations, reply verification), append-only.
        For a single Case's full chronological timeline, open it from Cases / Work Topics instead.
      </p>

      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "flex", gap: 12, alignItems: "end", marginBottom: 16, flexWrap: "wrap" }}>
        <div>
          <label>Case Number contains</label><br />
          <input value={caseNumberFilter} onChange={(e) => setCaseNumberFilter(e.target.value)} placeholder="CASE-000123" />
        </div>
        <div>
          <label>Event Type</label><br />
          <select value={eventTypeFilter} onChange={(e) => setEventTypeFilter(e.target.value)}>
            <option value="">All</option>
            {Object.entries(CASE_EVENT_TYPE_LABELS).map(([v, label]) => (
              <option key={v} value={v}>{label}</option>
            ))}
          </select>
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
            <th style={thStyle}>Case</th>
            <th style={thStyle}>Event Type</th>
            <th style={thStyle}>Detail</th>
            <th style={thStyle}>Actor</th>
          </tr>
        </thead>
        <tbody>
          {events.map((e) => (
            <tr key={e.id}>
              <td style={tdStyle}>{new Date(e.occurredAt).toLocaleString()}</td>
              <td style={tdStyle}><a href="/cases">{e.caseNumber}</a> — {e.caseSubject}</td>
              <td style={tdStyle}>{CASE_EVENT_TYPE_LABELS[e.eventType as CaseEventType] ?? e.eventType}</td>
              <td style={tdStyle}>{e.detail}</td>
              <td style={tdStyle}>{e.actorEmployeeName ?? "System"}</td>
            </tr>
          ))}
          {events.length === 0 && (
            <tr><td style={tdStyle} colSpan={5}>No Case History events found.</td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
