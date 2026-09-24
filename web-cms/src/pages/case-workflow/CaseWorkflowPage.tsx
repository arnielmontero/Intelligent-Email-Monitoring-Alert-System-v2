import { useState } from "react";
import { apiClient } from "../../api/client";
import type { CaseRunResult } from "../../api/types";

/// Requirements §20 (pipeline: Case Matching/Creation stage), §33 (Case Creation), §86 (CASE
/// MANAGEMENT nav → Case Workflow). Normal operation is the Hangfire recurring job in Program.cs
/// (server/src/Iemas.Api/Program.cs); this page is an administrative manual-trigger surface for
/// the same CaseWorkflowService.RunAsync the recurring job calls, matching the shape of
/// EscalationHistoryPage's "Run Now" control.
export default function CaseWorkflowPage() {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<CaseRunResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [batchSize, setBatchSize] = useState(25);

  async function handleRun() {
    setError(null);
    setRunning(true);
    setResult(null);
    try {
      const res = await apiClient.post<CaseRunResult>(`/case-workflow/run?batchSize=${batchSize}`);
      setResult(res.data);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run Case Workflow batch.");
    } finally {
      setRunning(false);
    }
  }

  return (
    <div>
      <h1>Case Workflow</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Requirements §20/§33 — this is where newly-classified, Important email is matched to an
        existing Case or turned into a new one, and where a completed/cancelled Case reopens when a
        clearly related new customer email arrives. This runs automatically on the server's
        recurring job; use this page to trigger an out-of-cycle run (e.g. after fixing a
        classification issue) and see the immediate result.
      </p>

      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "flex", gap: 12, alignItems: "end", marginBottom: 16 }}>
        <div>
          <label>Batch Size</label><br />
          <input type="number" min={1} max={500} value={batchSize} onChange={(e) => setBatchSize(Number(e.target.value))} style={{ width: 100 }} />
        </div>
        <button onClick={handleRun} disabled={running}>{running ? "Running..." : "Run Now"}</button>
      </div>

      {result && (
        <table style={{ borderCollapse: "collapse" }}>
          <tbody>
            <tr><td style={tdLabel}>Considered</td><td style={tdValue}>{result.consideredCount}</td></tr>
            <tr><td style={tdLabel}>Created</td><td style={tdValue}>{result.createdCount}</td></tr>
            <tr><td style={tdLabel}>Updated</td><td style={tdValue}>{result.updatedCount}</td></tr>
            <tr><td style={tdLabel}>Reopened</td><td style={tdValue}>{result.reopenedCount}</td></tr>
            <tr><td style={tdLabel}>Duration</td><td style={tdValue}>{result.durationMs} ms</td></tr>
          </tbody>
        </table>
      )}

      <p style={{ color: "var(--color-text-muted)", fontSize: 12, marginTop: 24 }}>
        To see the actual Cases this produced, open <a href="/cases">Cases / Work Topics</a>.
      </p>
    </div>
  );
}

const tdLabel: React.CSSProperties = { padding: "4px 12px 4px 0", color: "var(--color-text-muted)" };
const tdValue: React.CSSProperties = { padding: "4px 0", fontWeight: 600 };
