import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto } from "../../api/types";
import { InfoTip } from "../../components/help/Tooltip";

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
      setError(err.response?.data?.message ?? (err.response?.status === 504 ? "The mailbox took too long to answer; the background job keeps going — refresh in a minute." : "Failed to run intake."));
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
      setError(err.response?.data?.message ?? (err.response?.status === 504 ? "The mailbox took too long to answer; the background job keeps going — refresh in a minute." : "Failed to run intake."));
    } finally {
      setRunningId(null);
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Mailbox Checks</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        IEMAS checks each active mailbox for new email every minute; new email then goes straight on to the AI and into a Case. Use Run Now to check immediately, for example
        after adding or fixing an account.
      </p>

      {error && <div className="form-error">{error}</div>}

      <button onClick={runAll} disabled={runningId !== null} style={{ marginBottom: 16 }} data-tip="Check every mailbox for new email right now, instead of waiting for the automatic check every minute.">
        {runningId === "all" ? "Running..." : "Run Now (All Accounts)"}
      </button>

      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
        <thead>
          <tr>
            <th style={thStyle}>Account</th>
            <th style={thStyle}>Monitoring<InfoTip text="Whether IEMAS is checking this mailbox. Turn it on or off on Email Accounts." /></th>
            <th style={thStyle}>Last Run<InfoTip text="When the mailbox was last checked, and how many new emails were fetched." /></th>
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
                      ? <>
                          Fetched {result.fetchedCount}, new {result.persistedCount}, already had {result.duplicateCount}, unreadable {result.malformedCount} ({(result.durationMs / 1000).toFixed(1)}s)
                          {result.error && <div style={{ color: "var(--color-warning)", fontSize: 12 }}>{result.error}</div>}
                        </>
                      : <span style={{ color: "var(--color-danger)" }}>✗ {result.error}</span>
                    : "Not run this session"}
                </td>
                <td style={tdStyle}>
                  <button onClick={() => runOne(acc.id)} disabled={runningId !== null} data-tip="Check just this mailbox for new email now. Handy right after adding or fixing it.">
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

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
