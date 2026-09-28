import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { CaseRunResult, EmailAccountDto } from "../../api/types";
import { Badge } from "../../components/StatCard";

interface SampleEmail {
  emailMessageId: string;
  fromAddress: string;
  subject: string;
  expected: string;
}

interface SampleRunResult {
  samples: { mailbox: string; emails: SampleEmail[]; useAi: boolean };
  caseRun: CaseRunResult | null;
}

/** Super administrators only: adds sample customer emails to a mailbox to try the whole flow. */
export default function SampleDataPanel({ onGenerated }: { onGenerated?: () => void }) {
  const [mailboxes, setMailboxes] = useState<EmailAccountDto[]>([]);
  const [mailboxId, setMailboxId] = useState("");
  const [count, setCount] = useState(10);
  const [useAi, setUseAi] = useState(false);
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<SampleRunResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0")
      .then((r) => {
        const usable = r.data.filter((a) => a.isActive);
        setMailboxes(usable);
        setMailboxId((current) => current || usable.find((a) => a.ownerEmployeeId)?.id || "");
      })
      .catch(() => setError("Failed to load mailboxes."));
  }, []);

  async function generate() {
    setRunning(true);
    setError(null);
    setResult(null);
    try {
      const res = await apiClient.post<SampleRunResult>("/maintenance/sample-data", { emailAccountId: mailboxId, count, useAi });
      setResult(res.data);
      onGenerated?.();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to generate sample data.");
    } finally {
      setRunning(false);
    }
  }

  const selected = mailboxes.find((m) => m.id === mailboxId);

  return (
    <section style={panelStyle}>
      <h2 style={{ fontSize: 16, margin: "0 0 4px" }}>Generate sample data</h2>
      <p style={mutedStyle}>
        Adds realistic sample customer emails to a mailbox so you can try the whole flow: Cases, the pop-up on the owner's PC,
        reply checks, reminders and escalation. Samples are marked <strong>[Sample]</strong> in the subject and come from
        example.com addresses. Most need a reply, one is a follow-up to another, and two are not work (a newsletter and an
        auto-reply). Remove them later with Reset email data below.
      </p>
      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "flex", gap: 12, alignItems: "flex-end", flexWrap: "wrap", marginTop: 14 }}>
        <div style={fieldStyle}>
          <label htmlFor="sample-mailbox">Mailbox</label>
          <select id="sample-mailbox" value={mailboxId} onChange={(e) => setMailboxId(e.target.value)} style={{ minWidth: 280 }}>
            <option value="">Choose a mailbox…</option>
            {mailboxes.map((m) => (
              <option key={m.id} value={m.id}>{m.emailAddress} — {m.ownerEmployeeName ?? "no owner"}</option>
            ))}
          </select>
        </div>
        <div style={fieldStyle}>
          <label htmlFor="sample-count">Number of emails</label>
          <select id="sample-count" value={count} onChange={(e) => setCount(Number(e.target.value))}>
            {[3, 5, 10, 20].map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </div>
        <label style={{ display: "flex", alignItems: "center", gap: 8, paddingBottom: 8, color: "var(--color-text)" }}>
          <input type="checkbox" checked={useAi} onChange={(e) => setUseAi(e.target.checked)} />
          Let the AI read them (uses OpenRouter credit)
        </label>
        <button className="btn-primary" onClick={generate} disabled={running || !mailboxId}>
          {running ? "Generating..." : "Generate sample emails"}
        </button>
      </div>
      {selected && !selected.ownerEmployeeId && (
        <p style={{ ...mutedStyle, color: "var(--color-warning)", marginTop: 8 }}>This mailbox has no owner — set one on Email Accounts first.</p>
      )}
      <p style={{ ...mutedStyle, marginTop: 8 }}>
        {useAi
          ? "The AI reads them within 2 minutes like real email, then Cases follow within another 2 minutes."
          : "They arrive already classified (no AI cost) and are turned into Cases immediately."}
        {" "}The mailbox owner gets the pop-ups if their Windows Agent is connected.
      </p>

      {result && (
        <>
          <div style={successStyle}>
            Added {result.samples.emails.length} sample email{result.samples.emails.length === 1 ? "" : "s"} to {result.samples.mailbox}.
            {result.caseRun && ` ${result.caseRun.createdCount} new Case${result.caseRun.createdCount === 1 ? "" : "s"}, ${result.caseRun.updatedCount} added to an existing Case.`}
            {" "}<Link to="/case-workflow">Open Case Workflow</Link> · <Link to="/cases">Open Cases</Link>
          </div>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <thead>
              <tr><th>From</th><th>Subject</th><th>Expected</th><th>What happened</th></tr>
            </thead>
            <tbody>
              {result.samples.emails.map((e) => {
                const item = result.caseRun?.items?.find((i) => i.emailMessageId === e.emailMessageId);
                return (
                  <tr key={e.emailMessageId}>
                    <td>{e.fromAddress}</td>
                    <td>{e.subject}</td>
                    <td style={{ color: "var(--color-text-muted)" }}>{e.expected}</td>
                    <td>
                      {item
                        ? <><Badge tone={item.outcome === "New Case" ? "good" : "info"}>{item.outcome}</Badge>{item.caseNumber && <Link to={`/cases/${item.caseId}`} className="case-link" style={{ marginLeft: 8 }}>{item.caseNumber}</Link>}</>
                        : <span style={{ color: "var(--color-text-muted)" }}>{result.samples.useAi ? "Waiting for the AI" : "No Case (not work)"}</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13, margin: 0 };
const fieldStyle: React.CSSProperties = { display: "flex", flexDirection: "column", gap: 4 };
const panelStyle: React.CSSProperties = {
  marginTop: 40, border: "1px solid var(--color-border)", borderLeft: "4px solid var(--color-accent)", borderRadius: 8,
  padding: 20, background: "var(--color-surface)", boxShadow: "var(--shadow-card)",
};
const successStyle: React.CSSProperties = {
  background: "color-mix(in srgb, var(--color-success) 12%, transparent)", color: "var(--color-success)",
  border: "1px solid color-mix(in srgb, var(--color-success) 30%, transparent)", padding: "8px 12px", borderRadius: 6, margin: "14px 0 10px",
};
