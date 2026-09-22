import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto } from "../../api/types";

interface IntakeRunResult {
  emailAccountId: string;
  succeeded: boolean;
  fetchedCount: number;
  persistedCount: number;
  duplicateCount: number;
  malformedCount: number;
  error: string | null;
  durationMs: number;
}

export default function EmailMonitoringPage() {
  const [accounts, setAccounts] = useState<EmailAccountDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [runningId, setRunningId] = useState<string | "all" | null>(null);
  const [lastResults, setLastResults] = useState<Record<string, IntakeRunResult>>({});

  async function load() {
    setLoading(true);
    try {
      const res = await apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0");
      setAccounts(res.data);
    } catch {
      setError("Failed to load email accounts.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function runAll() {
    setRunningId("all");
    setError(null);
    try {
      const res = await apiClient.post<IntakeRunResult[]>("/email-intake/run");
      const map: Record<string, IntakeRunResult> = {};
      res.data.forEach((r) => (map[r.emailAccountId] = r));
      setLastResults((prev) => ({ ...prev, ...map }));
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run intake.");
    } finally {
      setRunningId(null);
    }
  }

  async function runOne(accountId: string) {
    setRunningId(accountId);
    setError(null);
    try {
      const res = await apiClient.post<IntakeRunResult>(`/email-intake/accounts/${accountId}/run`);
      setLastResults((prev) => ({ ...prev, [accountId]: res.data }));
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run intake.");
    } finally {
      setRunningId(null);
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Email Monitoring &amp; Intake</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Runs automatically via a scheduled background job (§79). Use "Run Now" to trigger an
        on-demand sync for verification or after reconfiguring an account.
      </p>

      {error && <div className="form-error">{error}</div>}

      <button onClick={runAll} disabled={runningId !== null} style={{ marginBottom: 16 }}>
        {runningId === "all" ? "Running..." : "Run Now (All Accounts)"}
      </button>

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>Account</th>
            <th style={thStyle}>Monitoring</th>
            <th style={thStyle}>Last Run</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {accounts.map((acc) => {
            const result = lastResults[acc.id];
            return (
              <tr key={acc.id}>
                <td style={tdStyle}>{acc.emailAddress}</td>
                <td style={tdStyle}>{acc.monitoringEnabled && acc.isActive ? "Active" : "Inactive"}</td>
                <td style={tdStyle}>
                  {result
                    ? result.succeeded
                      ? `Fetched ${result.fetchedCount}, persisted ${result.persistedCount}, duplicates ${result.duplicateCount}, malformed ${result.malformedCount} (${result.durationMs}ms)`
                      : `✗ ${result.error}`
                    : "Not run this session"}
                </td>
                <td style={tdStyle}>
                  <button onClick={() => runOne(acc.id)} disabled={runningId !== null}>
                    {runningId === acc.id ? "Running..." : "Run Now"}
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
