import { useState } from "react";
import { apiClient } from "../../api/client";
import type { ReplyVerificationRunResult } from "../../api/types";

/// Requirements §42-45 (Reply Verification Engine, claim-vs-verified-fact distinction, mailbox
/// failure handling), §86 (CASE MANAGEMENT nav → Reply Verification). Normal operation is the
/// Hangfire recurring job; this is the administrative manual-trigger surface for the same
/// ReplyVerificationService.RunAsync the recurring job calls.
export default function ReplyVerificationPage() {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<ReplyVerificationRunResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [batchSize, setBatchSize] = useState(25);

  async function handleRun() {
    setError(null);
    setRunning(true);
    setResult(null);
    try {
      const res = await apiClient.post<ReplyVerificationRunResult>(`/reply-verification/run?batchSize=${batchSize}`);
      setResult(res.data);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run Reply Verification batch.");
    } finally {
      setRunning(false);
    }
  }

  return (
    <div>
      <h1>Reply Verification</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Requirements §42-45 — checks actual outgoing mailbox (Sent Items) data for Cases awaiting a
        reply. §43: an employee's "Already Replied" claim is recorded as an event, never treated as
        proof by itself — only this engine, by inspecting the real Sent mailbox, may mark a Case's
        Reply Status as Verified. §44: a temporarily unavailable mailbox produces Pending/Failed, not
        a false "no reply." Runs automatically on the server's recurring job; use this page to
        trigger an out-of-cycle run and see the immediate result.
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
            <tr><td style={tdLabel}>Verified</td><td style={tdValue}>{result.verifiedCount}</td></tr>
            <tr><td style={tdLabel}>No Reply Found</td><td style={tdValue}>{result.noReplyFoundCount}</td></tr>
            <tr><td style={tdLabel}>Pending (mailbox unavailable)</td><td style={tdValue}>{result.pendingCount}</td></tr>
            <tr><td style={tdLabel}>Failed</td><td style={tdValue}>{result.failedCount}</td></tr>
            <tr><td style={tdLabel}>Duration</td><td style={tdValue}>{result.durationMs} ms</td></tr>
          </tbody>
        </table>
      )}

      <p style={{ color: "var(--color-text-muted)", fontSize: 12, marginTop: 24 }}>
        To see the resulting Reply Status per Case, open <a href="/cases">Cases / Work Topics</a>.
      </p>
    </div>
  );
}

const tdLabel: React.CSSProperties = { padding: "4px 12px 4px 0", color: "var(--color-text-muted)" };
const tdValue: React.CSSProperties = { padding: "4px 0", fontWeight: 600 };
