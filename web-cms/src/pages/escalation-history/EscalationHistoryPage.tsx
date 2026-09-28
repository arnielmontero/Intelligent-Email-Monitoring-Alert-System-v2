import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EscalationEventDto, EscalationOutcome, EscalationRunResult } from "../../api/types";
import { ESCALATION_OUTCOME_LABELS, ESCALATION_RECIPIENT_TYPE_LABELS, ESCALATION_SKIP_REASON_LABELS } from "../../api/types";

export default function EscalationHistoryPage() {
  const [events, setEvents] = useState<EscalationEventDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [outcomeFilter, setOutcomeFilter] = useState<"" | EscalationOutcome>("");
  const [take, setTake] = useState(100);
  const [running, setRunning] = useState(false);
  const [runResult, setRunResult] = useState<EscalationRunResult | null>(null);

  const outcomeNameByValue: Record<EscalationOutcome, string> = {
    0: "Executed", 1: "Skipped", 2: "RecipientUnresolved", 3: "Failed",
  };

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams();
      if (outcomeFilter !== "") params.set("outcome", outcomeNameByValue[outcomeFilter]);
      params.set("take", String(take));
      const res = await apiClient.get<EscalationEventDto[]>(`/escalations?${params.toString()}`);
      setEvents(res.data);
    } catch {
      setError("Failed to load Escalation History.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function handleRunNow() {
    setError(null);
    setRunning(true);
    setRunResult(null);
    try {
      const res = await apiClient.post<EscalationRunResult>("/escalations/run?batchSize=25");
      setRunResult(res.data);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run escalation batch.");
    } finally {
      setRunning(false);
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Escalation History</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Every escalation attempt — sent, skipped, or no recipient found — with the reason. Escalations run automatically;
        this page is for looking back.
      </p>

      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "flex", gap: 12, alignItems: "end", marginBottom: 16, flexWrap: "wrap" }}>
        <div>
          <label>Outcome</label><br />
          <select value={outcomeFilter} onChange={(e) => setOutcomeFilter(e.target.value === "" ? "" : (Number(e.target.value) as EscalationOutcome))}>
            <option value="">All</option>
            {(Object.keys(ESCALATION_OUTCOME_LABELS) as unknown as EscalationOutcome[]).map((v) => (
              <option key={v} value={v}>{ESCALATION_OUTCOME_LABELS[Number(v) as EscalationOutcome]}</option>
            ))}
          </select>
        </div>
        <div>
          <label>Take</label><br />
          <input type="number" min={1} value={take} onChange={(e) => setTake(Number(e.target.value))} style={{ width: 80 }} />
        </div>
        <button onClick={load}>Filter</button>
        <button onClick={handleRunNow} disabled={running}>{running ? "Running..." : "Run Now"}</button>
        {runResult && (
          <span style={{ fontSize: 13 }}>
            Considered {runResult.considered}, Executed {runResult.executed}, Skipped {runResult.skipped},
            {" "}RecipientUnresolved {runResult.recipientUnresolved}, Failed {runResult.failed} ({runResult.durationMs}ms)
          </span>
        )}
      </div>

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>Occurred At</th>
            <th style={thStyle}>Case Number</th>
            <th style={thStyle}>Policy</th>
            <th style={thStyle}>Level</th>
            <th style={thStyle}>Trigger</th>
            <th style={thStyle}>Recipient Type</th>
            <th style={thStyle}>Recipient</th>
            <th style={thStyle}>Channel</th>
            <th style={thStyle}>Outcome</th>
            <th style={thStyle}>Skip Reason</th>
            <th style={thStyle}>Detail</th>
          </tr>
        </thead>
        <tbody>
          {events.map((e) => (
            <tr key={e.id}>
              <td style={tdStyle}>{new Date(e.occurredAt).toLocaleString()}</td>
              <td style={tdStyle}>{e.caseNumber}</td>
              <td style={tdStyle}>{e.escalationPolicyName ?? "—"}</td>
              <td style={tdStyle}>{e.level}</td>
              <td style={tdStyle}>{e.trigger}</td>
              <td style={tdStyle}>{e.recipientType != null ? ESCALATION_RECIPIENT_TYPE_LABELS[e.recipientType] : "—"}</td>
              <td style={tdStyle}>{e.recipientDisplay ?? "—"}</td>
              <td style={tdStyle}>{e.channel ?? "—"}</td>
              <td style={tdStyle}>{ESCALATION_OUTCOME_LABELS[e.outcome]}</td>
              <td style={tdStyle}>{e.skipReason != null ? ESCALATION_SKIP_REASON_LABELS[e.skipReason] : "—"}</td>
              <td style={tdStyle}>{e.detail ?? "—"}</td>
            </tr>
          ))}
          {events.length === 0 && (
            <tr><td style={tdStyle} colSpan={11}>No Escalation Events found.</td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px" };
