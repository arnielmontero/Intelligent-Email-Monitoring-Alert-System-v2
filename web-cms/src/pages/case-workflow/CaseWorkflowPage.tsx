import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { CaseRunResult, CaseWorkflowItemDto, CaseWorkflowOverviewDto } from "../../api/types";

interface Stage {
  label: string;
  count: number;
  hint: string;
  link?: string;
  tone?: "warn" | "bad" | "good";
}

/// The path every customer email takes, with how many are at each stage right now, and the email → Case step in detail.
export default function CaseWorkflowPage() {
  const [overview, setOverview] = useState<CaseWorkflowOverviewDto | null>(null);
  const [waiting, setWaiting] = useState<CaseWorkflowItemDto[]>([]);
  const [recent, setRecent] = useState<CaseWorkflowItemDto[]>([]);
  const [result, setResult] = useState<CaseRunResult | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const [o, w, r] = await Promise.all([
        apiClient.get<CaseWorkflowOverviewDto>("/case-workflow/overview"),
        apiClient.get<CaseWorkflowItemDto[]>("/case-workflow/waiting"),
        apiClient.get<CaseWorkflowItemDto[]>("/case-workflow/recent?take=15"),
      ]);
      setOverview(o.data);
      setWaiting(w.data);
      setRecent(r.data);
    } catch (err: any) {
      setError(err.response?.status === 403 ? "You do not have permission to view the Case Workflow (requires Administrator)." : "Failed to load the Case Workflow.");
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
      const res = await apiClient.post<CaseRunResult>("/case-workflow/run?batchSize=50");
      setResult(res.data);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run.");
    } finally {
      setRunning(false);
    }
  }

  const intake: Stage[] = overview ? [
    { label: "Email received", count: overview.receivedLast24Hours, hint: "last 24 hours", link: "/email-monitoring" },
    { label: "AI check", count: overview.awaitingAiCheck, hint: "waiting to be read by the AI", link: "/email-classification" },
    { label: "Waiting for a Case", count: overview.waitingForCase, hint: "important, not yet a Case", tone: overview.waitingForCase > 0 ? "warn" : undefined },
  ] : [];

  const work: Stage[] = overview ? [
    { label: "Action required", count: overview.actionRequired, hint: "new, not started", link: "/cases?status=1", tone: overview.actionRequired > 0 ? "warn" : undefined },
    { label: "In progress", count: overview.inProgress, hint: "employee is working on it", link: "/cases?status=2" },
    { label: "Waiting", count: overview.waitingOnCustomer + overview.waitingInternally, hint: `${overview.waitingOnCustomer} on customer · ${overview.waitingInternally} internal`, link: "/cases?status=3" },
    { label: "Completed", count: overview.completedLast7Days, hint: "last 7 days", link: "/cases?status=8", tone: "good" },
  ] : [];

  const side: Stage[] = overview ? [
    { label: "Not work", count: overview.notWorkLast24Hours, hint: "AI: no Case needed (last 24 h)" },
    { label: "Needs review", count: overview.needsReview, hint: "AI was unsure — a person decides", link: "/email-classification", tone: overview.needsReview > 0 ? "warn" : undefined },
    { label: "Overdue", count: overview.overdue, hint: "no reply in time", link: "/cases?status=6", tone: overview.overdue > 0 ? "bad" : undefined },
    { label: "Escalated", count: overview.escalated, hint: "supervisor/manager informed", link: "/cases?status=7", tone: overview.escalated > 0 ? "bad" : undefined },
  ] : [];

  return (
    <div style={{ maxWidth: 1200 }}>
      <h1>Case Workflow</h1>
      <p style={mutedStyle}>
        The path every customer email takes, with how many are at each stage right now (updates every 30 seconds).
        Click a stage to see those Cases.
      </p>
      {error && <div className="form-error">{error}</div>}

      {overview && (
        <>
          <h2 style={sectionTitle}>1. From email to Case</h2>
          <StageRow stages={intake} />
          <h2 style={sectionTitle}>2. Working the Case</h2>
          <StageRow stages={work} />
          <h2 style={sectionTitle}>Side paths</h2>
          <div style={sideGrid}>{side.map((s) => <StageCard key={s.label} stage={s} />)}</div>
        </>
      )}

      <h2 style={sectionTitle}>Emails waiting to become a Case ({waiting.length})</h2>
      <p style={mutedStyle}>
        Important emails the AI approved that are not in a Case yet. This happens automatically every 2 minutes;
        Run Now does it immediately and shows what happened to each email.
      </p>
      <button className="btn-primary" onClick={runNow} disabled={running} style={{ marginBottom: 12 }}>
        {running ? "Running..." : "Run Now"}
      </button>
      {!result && <EmailTable items={waiting} empty="Nothing waiting — every important email is already in a Case." />}

      {result && (
        <>
          <div style={{ ...mutedStyle, margin: "4px 0 8px" }}>
            Processed <strong>{result.consideredCount}</strong> email{result.consideredCount === 1 ? "" : "s"}:{" "}
            {result.createdCount} new Case{result.createdCount === 1 ? "" : "s"}, {result.updatedCount} added to existing Cases,
            {" "}{result.reopenedCount} reopened ({result.durationMs} ms).
          </div>
          <EmailTable items={result.items ?? []} empty="Nothing was waiting." />
        </>
      )}

      <h2 style={sectionTitle}>Recently turned into Cases</h2>
      <EmailTable items={recent} empty="No emails have been turned into Cases yet." />
    </div>
  );
}

function StageRow({ stages }: { stages: Stage[] }) {
  return (
    <div style={{ display: "flex", alignItems: "stretch", gap: 6, flexWrap: "wrap" }}>
      {stages.map((stage, i) => (
        <div key={stage.label} style={{ display: "flex", alignItems: "center", gap: 6, flex: "1 1 170px" }}>
          <StageCard stage={stage} />
          {i < stages.length - 1 && <span style={arrowStyle} aria-hidden="true">→</span>}
        </div>
      ))}
    </div>
  );
}

function StageCard({ stage }: { stage: Stage }) {
  const border = stage.tone === "bad" ? "var(--color-danger)" : stage.tone === "warn" ? "var(--color-warning)" : stage.tone === "good" ? "var(--color-success)" : "var(--color-border)";
  const body = (
    <div style={{ ...cardStyle, borderColor: border }}>
      <div style={{ fontSize: 26, fontWeight: 700, color: "var(--color-text)" }}>{stage.count}</div>
      <div style={{ fontWeight: 600, color: "var(--color-text)" }}>{stage.label}</div>
      <div style={hintStyle}>{stage.hint}</div>
    </div>
  );
  return stage.link
    ? <Link to={stage.link} style={{ flex: 1, textDecoration: "none" }}>{body}</Link>
    : <div style={{ flex: 1 }}>{body}</div>;
}

function EmailTable({ items, empty }: { items: CaseWorkflowItemDto[]; empty: string }) {
  return (
    <table style={{ width: "100%", borderCollapse: "collapse" }}>
      <thead>
        <tr>
          <th style={thStyle}>Received</th>
          <th style={thStyle}>From</th>
          <th style={thStyle}>Subject</th>
          <th style={thStyle}>Mailbox</th>
          <th style={thStyle}>What happened</th>
        </tr>
      </thead>
      <tbody>
        {items.map((i) => (
          <tr key={`${i.emailMessageId}-${i.outcome}`}>
            <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>{new Date(i.receivedAt).toLocaleString()}</td>
            <td style={tdStyle}>{i.fromAddress}</td>
            <td style={tdStyle}>{i.subject || "(no subject)"}</td>
            <td style={tdStyle}>{i.mailbox}</td>
            <td style={tdStyle}>
              <strong style={{ color: OUTCOME_COLORS[i.outcome] ?? "var(--color-text)" }}>{i.outcome}</strong>
              {i.caseId && <> · <Link to={`/cases/${i.caseId}`}>{i.caseNumber}</Link></>}
              {i.detail && <div style={hintStyle}>{i.detail}</div>}
            </td>
          </tr>
        ))}
        {items.length === 0 && <tr><td style={tdStyle} colSpan={5}>{empty}</td></tr>}
      </tbody>
    </table>
  );
}

const OUTCOME_COLORS: Record<string, string> = {
  "New Case": "var(--color-success)",
  "Added to existing Case": "var(--color-link)",
  "Reopened Case": "var(--color-warning)",
  Skipped: "var(--color-text-muted)",
  Waiting: "var(--color-warning)",
};

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 16, margin: "24px 0 10px" };
const cardStyle: React.CSSProperties = { border: "1px solid", borderRadius: 8, padding: "10px 12px", background: "var(--color-surface)", height: "100%" };
const sideGrid: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 10 };
const arrowStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 20 };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "top" };
