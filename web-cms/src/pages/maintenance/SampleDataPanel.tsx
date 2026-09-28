import { useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { CaseRunResult } from "../../api/types";
import { Badge } from "../../components/StatCard";

interface SampleEmail {
  emailMessageId: string;
  mailbox: string;
  fromAddress: string;
  subject: string;
  expected: string;
}

interface SampleRunResult {
  samples: { emails: SampleEmail[]; useAi: boolean; teamCreated: string[] };
  caseRun: CaseRunResult | null;
}

/** Super administrators only: adds sample customer emails to a mailbox to try the whole flow. */
export default function SampleDataPanel({ onGenerated }: { onGenerated?: () => void }) {
  const [count, setCount] = useState(10);
  const [useAi, setUseAi] = useState(false);
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<SampleRunResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function generate() {
    setRunning(true);
    setError(null);
    setResult(null);
    try {
      const res = await apiClient.post<SampleRunResult>("/maintenance/sample-data", { count, useAi });
      setResult(res.data);
      onGenerated?.();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to generate sample data.");
    } finally {
      setRunning(false);
    }
  }

  return (
    <section style={panelStyle}>
      <h2 style={{ fontSize: 16, margin: "0 0 4px" }}>Generate sample data</h2>
      <p style={mutedStyle}>
        Adds realistic sample customer emails so you can try the whole flow: Cases, pop-ups, reply checks, reminders and
        escalation. The first time, it also creates a <strong>sample team</strong>: a Sample Sales department with a manager
        (Katarina Lind), a supervisor (Mark Evans) and three staff (Liam Andersson, Aino Makinen, Diego Fernandez), each
        with a sample mailbox and a sample PC. Emails go to random active mailboxes with an owner — yours and the sample
        ones — from random senders and domains; subjects start with <strong>[Sample]</strong>. Reset email data below
        removes the emails and the whole sample team; your own employee, mailbox, PC and the admin sign-in are never touched.
      </p>
      {error && <div className="form-error">{error}</div>}

      <div style={{ display: "flex", gap: 12, alignItems: "flex-end", flexWrap: "wrap", marginTop: 14 }}>
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
        <button className="btn-primary" onClick={generate} disabled={running}>
          {running ? "Generating..." : "Generate sample emails"}
        </button>
      </div>
      <p style={{ ...mutedStyle, marginTop: 8 }}>
        {useAi
          ? "The AI reads them within 2 minutes like real email, then Cases follow within another 2 minutes."
          : "They arrive already classified (no AI cost) and are turned into Cases immediately."}
        {" "}Real owners get the pop-ups on their PC; sample PCs are not real computers, so their pop-ups stay queued and
        their mailboxes always report "no reply yet", which lets reminders and escalation run.
      </p>

      {result && (
        <>
          <div style={successStyle}>
            {result.samples.teamCreated.length > 0 && `Sample team created (${result.samples.teamCreated.length} records). `}
            Added {result.samples.emails.length} sample email{result.samples.emails.length === 1 ? "" : "s"}.
            {result.caseRun && ` ${result.caseRun.createdCount} new Case${result.caseRun.createdCount === 1 ? "" : "s"}, ${result.caseRun.updatedCount} added to an existing Case.`}
            {" "}<Link to="/case-workflow">Open Case Workflow</Link> · <Link to="/cases">Open Cases</Link>
          </div>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
            <thead>
              <tr><th>From</th><th>Subject</th><th>Mailbox</th><th>Expected</th><th>What happened</th></tr>
            </thead>
            <tbody>
              {result.samples.emails.map((e) => {
                const item = result.caseRun?.items?.find((i) => i.emailMessageId === e.emailMessageId);
                return (
                  <tr key={e.emailMessageId}>
                    <td>{e.fromAddress}</td>
                    <td>{e.subject}</td>
                    <td>{e.mailbox}</td>
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
