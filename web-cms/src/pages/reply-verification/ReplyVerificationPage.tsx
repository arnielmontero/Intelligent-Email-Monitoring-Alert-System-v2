import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { ReplyCheckCaseDto, ReplyCheckOverviewDto, ReplyVerificationRunResult } from "../../api/types";
import CaseSidePanel from "../../components/CaseSidePanel";
import { selectedRowStyle } from "../../components/styles";
import StatCard, { Badge, type Tone } from "../../components/StatCard";

interface Stage {
  label: string;
  count: number;
  hint: string;
  /** CaseReplyStatus values shown when the card is clicked. */
  filter: string;
  tone?: Tone;
}

const STATUS_LABELS: Record<string, string> = {
  AwaitingReply: "Not checked yet",
  VerificationPending: "Couldn't check — retrying",
  VerificationFailed: "Couldn't check",
  NoReplyFound: "No reply yet",
  Replied: "Reply found",
};

const STATUS_TONES: Record<string, Tone> = {
  AwaitingReply: "neutral",
  NoReplyFound: "warn",
  VerificationFailed: "bad",
  VerificationPending: "bad",
  Replied: "good",
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
    { label: "Reply found", count: overview.replyFound, hint: "reply confirmed in Sent — reminders stop", filter: "Replied" },
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
            <div className="section-title" style={{ marginTop: 8 }}>Open Cases by reply</div>
            <div className="stat-grid">
              {stages.map((stage) => (
                <StatCard key={stage.label} label={stage.label} value={stage.count} hint={stage.hint} tone={stage.tone}
                  active={filter === stage.filter} onClick={() => setFilter(filter === stage.filter ? "" : stage.filter)} />
              ))}
              <StatCard label="Couldn't check" value={overview.couldNotCheck}
                hint="mailbox unreachable — never counted as “no reply”; retried automatically"
                tone={overview.couldNotCheck > 0 ? "bad" : "neutral"}
                active={filter === "VerificationFailed,VerificationPending"}
                onClick={() => setFilter(filter === "VerificationFailed,VerificationPending" ? "" : "VerificationFailed,VerificationPending")} />
            </div>

            <div className="section-title">Sent folders checked</div>
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
                        ? <><Badge tone="bad">Can't read Sent folder</Badge><div style={hintStyle}>{m.lastProblem}</div></>
                        : <Badge tone="good">OK</Badge>}
                    </td>
                  </tr>
                ))}
                {overview.mailboxes.length === 0 && <tr><td style={tdStyle} colSpan={4}>No mailbox has open Cases to check.</td></tr>}
              </tbody>
            </table>
          </>
        )}

        <div className="section-head">
          <div>
            <h2 style={sectionTitle}>Check now</h2>
            <p style={mutedStyle}>
              Runs by itself every 5 minutes. Check Now looks in the Sent folders immediately (up to 100 Cases, least recently
              checked first) — useful right after an employee says they replied.
            </p>
          </div>
          <button className="btn-primary" onClick={runNow} disabled={running}>
            {running ? "Checking Sent folders..." : "Check Now"}
          </button>
        </div>
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

        <div className="section-head">
          <h2 style={sectionTitle}>Open Cases {filterLabel ? `— ${filterLabel}` : ""} ({shown.length})</h2>
          {filter && <button onClick={() => setFilter("")}>Show all</button>}
        </div>
        <CaseTable items={shown} empty="No open Cases here." selectedId={selectedId} onSelect={setSelectedId} />
      </div>

      {selectedId && <CaseSidePanel caseId={selectedId} onClose={() => setSelectedId(null)} onChanged={load} />}
    </div>
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
              <Badge tone={STATUS_TONES[c.replyStatus]}>{STATUS_LABELS[c.replyStatus] ?? c.replyStatus}</Badge>
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
const sectionTitle: React.CSSProperties = { fontSize: 15, fontWeight: 600, margin: "0 0 4px" };
const tableStyle: React.CSSProperties = { width: "100%", borderCollapse: "separate", borderSpacing: 0 };
const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
