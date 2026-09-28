import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { ReplyCheckCaseDto, ReplyCheckOverviewDto, ReplyVerificationRunResult } from "../../api/types";
import CaseSidePanel from "../../components/CaseSidePanel";
import { selectedRowStyle } from "../../components/styles";

interface Stage {
  label: string;
  count: number;
  hint: string;
  /** CaseReplyStatus values shown when the card is clicked. */
  filter: string;
  tone?: "warn" | "bad" | "good";
}

const STATUS_LABELS: Record<string, string> = {
  AwaitingReply: "Not checked yet",
  VerificationPending: "Couldn't check — retrying",
  VerificationFailed: "Couldn't check",
  NoReplyFound: "No reply yet",
  Replied: "Reply found",
};

const STATUS_COLORS: Record<string, string> = {
  NoReplyFound: "var(--color-warning)",
  VerificationFailed: "var(--color-danger)",
  VerificationPending: "var(--color-danger)",
  Replied: "var(--color-success)",
};

/// Whether employees really replied to their Cases — found in each mailbox's Sent folder, not just claimed.
export default function ReplyVerificationPage() {
  const [overview, setOverview] = useState<ReplyCheckOverviewDto | null>(null);
  const [cases, setCases] = useState<ReplyCheckCaseDto[]>([]);
  const [filter, setFilter] = useState("");
  const [result, setResult] = useState<ReplyVerificationRunResult | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const [o, c] = await Promise.all([
        apiClient.get<ReplyCheckOverviewDto>("/reply-verification/overview"),
        apiClient.get<ReplyCheckCaseDto[]>("/reply-verification/cases?take=300"),
      ]);
      setOverview(o.data);
      setCases(c.data);
    } catch (err: any) {
      setError(err.response?.status === 403 ? "You do not have permission to view Reply Verification (requires Administrator)." : "Failed to load Reply Verification.");
    }
  }

  useEffect(() => {
    load();
    const timer = setInterval(load, 30000);
    return () => clearInterval(timer);
  }, []);

  async function runNow() {
    setError(null);
    setRunning(true);
    setResult(null);
    try {
      const res = await apiClient.post<ReplyVerificationRunResult>("/reply-verification/run?batchSize=100");
      setResult(res.data);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to check the Sent folders.");
    } finally {
      setRunning(false);
    }
  }

  const stages: Stage[] = overview ? [
    { label: "Not checked yet", count: overview.awaitingFirstCheck, hint: "new Cases, first check within 5 min", filter: "AwaitingReply" },
    { label: "No reply yet", count: overview.noReplyYet, hint: "Sent folder checked — nothing to the customer; reminders continue", filter: "NoReplyFound", tone: overview.noReplyYet > 0 ? "warn" : undefined },
    { label: "Reply found", count: overview.replyFound, hint: "reply confirmed in Sent — reminders stop", filter: "Replied", tone: "good" },
  ] : [];

  const filters = filter.split(",").filter(Boolean);
  const shown = filters.length ? cases.filter((c) => filters.includes(c.replyStatus)) : cases;
  const filterLabel = filter === "VerificationFailed,VerificationPending" ? "Couldn't check" : STATUS_LABELS[filter];

  return (
    <div style={{ display: "flex", gap: 24 }}>
      <div style={{ flex: 1, minWidth: 0, maxWidth: 1200 }}>
        <h1>Reply Verification</h1>
        <p style={mutedStyle}>
          Did the employee really reply to the customer? IEMAS looks in each mailbox's <strong>Sent</strong> folder for a reply
          to every open Case, every 5 minutes, until one is found. Clicking “Already replied” on the PC is only a claim until
          the reply is found here. Once found, reminders and escalation stop for that Case.
        </p>
        {error && <div className="form-error">{error}</div>}

        {overview && (
          <>
            <h2 style={sectionTitle}>Open Cases by reply</h2>
            <div style={{ display: "flex", alignItems: "stretch", gap: 6, flexWrap: "wrap" }}>
              {stages.map((stage, i) => (
                <div key={stage.label} style={{ display: "flex", alignItems: "center", gap: 6, flex: "1 1 200px" }}>
                  <StageCard stage={stage} active={filter === stage.filter} onClick={() => setFilter(filter === stage.filter ? "" : stage.filter)} />
                  {i < stages.length - 1 && <span style={arrowStyle} aria-hidden="true">→</span>}
                </div>
              ))}
              <div style={{ display: "flex", flex: "1 1 200px", marginLeft: 12 }}>
                <StageCard
                  stage={{
                    label: "Couldn't check", count: overview.couldNotCheck, filter: "VerificationFailed,VerificationPending",
                    hint: "mailbox unreachable — never counted as “no reply”; retried automatically",
                    tone: overview.couldNotCheck > 0 ? "bad" : undefined,
                  }}
                  active={filter === "VerificationFailed,VerificationPending"}
                  onClick={() => setFilter(filter === "VerificationFailed,VerificationPending" ? "" : "VerificationFailed,VerificationPending")}
                />
              </div>
            </div>

            <h2 style={sectionTitle}>Sent folders checked</h2>
            <table style={tableStyle}>
              <thead>
                <tr>
                  <th style={thStyle}>Mailbox</th>
                  <th style={thStyle}>Open Cases</th>
                  <th style={thStyle}>Last checked</th>
                  <th style={thStyle}>Status</th>
                </tr>
              </thead>
              <tbody>
                {overview.mailboxes.map((m) => (
                  <tr key={m.emailAccountId}>
                    <td style={tdStyle}>{m.mailbox}</td>
                    <td style={tdStyle}>{m.openCases}</td>
                    <td style={tdStyle}>{m.lastCheckedAt ? new Date(m.lastCheckedAt).toLocaleString() : "Not yet"}</td>
                    <td style={tdStyle}>
                      {m.lastProblem
                        ? <span style={{ color: "var(--color-danger)" }}>Can't read Sent folder: {m.lastProblem}</span>
                        : <span style={{ color: "var(--color-success)" }}>OK</span>}
                    </td>
                  </tr>
                ))}
                {overview.mailboxes.length === 0 && <tr><td style={tdStyle} colSpan={4}>No mailbox has open Cases to check.</td></tr>}
              </tbody>
            </table>
          </>
        )}

        <h2 style={sectionTitle}>Check now</h2>
        <p style={mutedStyle}>
          Runs by itself every 5 minutes. Check Now looks in the Sent folders immediately (up to 100 Cases, least recently checked first)
          — useful right after an employee says they replied.
        </p>
        <button className="btn-primary" onClick={runNow} disabled={running} style={{ marginBottom: 12 }}>
          {running ? "Checking Sent folders..." : "Check Now"}
        </button>
        {result && (
          <>
            <div style={{ ...mutedStyle, margin: "4px 0 8px" }}>
              Checked <strong>{result.consideredCount}</strong> Case{result.consideredCount === 1 ? "" : "s"}:{" "}
              <span style={{ color: "var(--color-success)" }}>{result.verifiedCount} reply found</span>,{" "}
              {result.noReplyFoundCount} no reply yet, {result.failedCount + result.pendingCount} couldn't check ({result.durationMs} ms).
            </div>
            <CaseTable items={result.items ?? []} empty="No open Case is waiting for a reply." selectedId={selectedId} onSelect={setSelectedId} />
          </>
        )}

        <h2 style={sectionTitle}>
          Open Cases {filterLabel ? `— ${filterLabel}` : ""} ({shown.length})
          {filter && <button onClick={() => setFilter("")} style={{ marginLeft: 12, fontSize: 12 }}>Show all</button>}
        </h2>
        <CaseTable items={shown} empty="No open Cases here." selectedId={selectedId} onSelect={setSelectedId} />
      </div>

      {selectedId && <CaseSidePanel caseId={selectedId} onClose={() => setSelectedId(null)} onChanged={load} />}
    </div>
  );
}

function StageCard({ stage, active, onClick }: { stage: Stage; active: boolean; onClick: () => void }) {
  const border = stage.tone === "bad" ? "var(--color-danger)" : stage.tone === "warn" ? "var(--color-warning)" : stage.tone === "good" ? "var(--color-success)" : "var(--color-border)";
  return (
    <button type="button" onClick={onClick} title="Show these Cases below"
      style={{ ...cardStyle, borderColor: border, borderWidth: active ? 2 : 1, flex: 1 }}>
      <div style={{ fontSize: 26, fontWeight: 700 }}>{stage.count}</div>
      <div style={{ fontWeight: 600 }}>{stage.label}</div>
      <div style={hintStyle}>{stage.hint}</div>
    </button>
  );
}

function CaseTable({ items, empty, selectedId, onSelect }: {
  items: ReplyCheckCaseDto[]; empty: string; selectedId: string | null; onSelect: (id: string) => void;
}) {
  return (
    <table style={tableStyle}>
      <thead>
        <tr>
          <th style={thStyle}>Case #</th>
          <th style={thStyle}>Customer</th>
          <th style={thStyle}>Subject</th>
          <th style={thStyle}>Mailbox / Owner</th>
          <th style={thStyle}>Reply</th>
          <th style={thStyle}>Last checked</th>
        </tr>
      </thead>
      <tbody>
        {items.map((c) => (
          <tr key={c.caseId} onClick={() => onSelect(c.caseId)}
            style={{ cursor: "pointer", ...(selectedId === c.caseId ? selectedRowStyle : {}) }}>
            <td style={{ ...tdStyle, whiteSpace: "nowrap" }}><span className="case-link">{c.caseNumber}</span></td>
            <td style={tdStyle}>{c.customer}</td>
            <td style={tdStyle}>{c.subject || "(no subject)"}</td>
            <td style={tdStyle}>{c.mailbox}<div style={hintStyle}>{c.owner ?? "(no owner)"}</div></td>
            <td style={tdStyle}>
              <strong style={{ color: STATUS_COLORS[c.replyStatus] ?? "var(--color-text)" }}>{STATUS_LABELS[c.replyStatus] ?? c.replyStatus}</strong>
              {c.lastResult && <div style={hintStyle}>{c.lastResult}</div>}
            </td>
            <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>{c.lastCheckedAt ? new Date(c.lastCheckedAt).toLocaleString() : "Not yet"}</td>
          </tr>
        ))}
        {items.length === 0 && <tr><td style={tdStyle} colSpan={6}>{empty}</td></tr>}
      </tbody>
    </table>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 16, margin: "24px 0 10px" };
const cardStyle: React.CSSProperties = {
  border: "1px solid", borderRadius: 8, padding: "10px 12px", background: "var(--color-surface)", height: "100%",
  textAlign: "left", color: "var(--color-text)", cursor: "pointer", font: "inherit",
};
const arrowStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 20 };
const tableStyle: React.CSSProperties = { width: "100%", borderCollapse: "collapse" };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "top" };
