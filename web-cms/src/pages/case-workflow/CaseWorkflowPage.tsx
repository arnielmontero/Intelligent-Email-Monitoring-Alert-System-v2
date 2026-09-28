import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { CaseRunResult, CaseWorkflowItemDto, CaseWorkflowOverviewDto, PagedResult } from "../../api/types";
import Pagination from "../../components/Pagination";
import StatCard, { Badge, type Tone } from "../../components/StatCard";

type SortKey = "received" | "from" | "subject" | "mailbox" | "case" | "processed";

interface Stage {
  label: string;
  count: number;
  hint: string;
  link?: string;
  tone?: Tone;
}

/// The path every customer email takes, with how many are at each stage right now, and the email → Case step in detail.
export default function CaseWorkflowPage() {
  const [overview, setOverview] = useState<CaseWorkflowOverviewDto | null>(null);
  const [waiting, setWaiting] = useState<CaseWorkflowItemDto[]>([]);
  const [recent, setRecent] = useState<PagedResult<CaseWorkflowItemDto> | null>(null);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState<SortKey>("processed");
  const [desc, setDesc] = useState(true);
  // The 30-second refresh reads the current table settings, not the ones from when it was scheduled.
  const recentQuery = useRef({ page, search, sort, desc });
  recentQuery.current = { page, search, sort, desc };
  const [result, setResult] = useState<CaseRunResult | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      const [o, w] = await Promise.all([
        apiClient.get<CaseWorkflowOverviewDto>("/case-workflow/overview"),
        apiClient.get<CaseWorkflowItemDto[]>("/case-workflow/waiting"),
        loadRecent(),
      ]);
      setOverview(o.data);
      setWaiting(w.data);
    } catch (err: any) {
      setError(err.response?.status === 403 ? "You do not have permission to view the Case Workflow (requires Administrator)." : "Failed to load the Case Workflow.");
    }
  }

  async function loadRecent() {
    const q = recentQuery.current;
    const params = new URLSearchParams({ page: String(q.page), pageSize: "10", sort: q.sort, desc: String(q.desc) });
    if (q.search.trim()) params.set("search", q.search.trim());
    const r = await apiClient.get<PagedResult<CaseWorkflowItemDto>>(`/case-workflow/recent?${params}`);
    setRecent(r.data);
  }

  useEffect(() => {
    load();
    const timer = setInterval(load, 30000);
    return () => clearInterval(timer);
  }, []);

  // Search waits for a pause in typing; page and sort changes load straight away.
  useEffect(() => {
    const timer = setTimeout(() => loadRecent().catch(() => setError("Failed to load the Case Workflow.")), 300);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page, search, sort, desc]);

  function changeSort(key: SortKey) {
    if (key === sort) setDesc(!desc);
    else {
      setSort(key);
      setDesc(key === "received" || key === "processed");
    }
    setPage(1);
  }

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
    { label: "Completed", count: overview.completedLast7Days, hint: "last 7 days", link: "/cases?status=8" },
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
          <div className="section-title" style={{ marginTop: 8 }}>Step 1 · From email to Case</div>
          <StageGrid stages={intake} />
          <div className="section-title">Step 2 · Working the Case</div>
          <StageGrid stages={work} />
          <div className="section-title">Needs attention / set aside</div>
          <StageGrid stages={side} />
        </>
      )}

      <div className="section-head">
        <div>
          <h2 style={sectionTitle}>Emails waiting to become a Case ({waiting.length})</h2>
          <p style={mutedStyle}>
            Important emails the AI approved that are not in a Case yet. This happens automatically every 2 minutes;
            Run Now does it immediately and shows what happened to each email.
          </p>
        </div>
        <button className="btn-primary" onClick={runNow} disabled={running}>
          {running ? "Running..." : "Run Now"}
        </button>
      </div>
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

      <div className="section-head">
        <h2 style={sectionTitle}>Emails turned into Cases{recent ? ` (${recent.totalCount})` : ""}</h2>
        <input
          type="search"
          value={search}
          onChange={(e) => { setSearch(e.target.value); setPage(1); }}
          placeholder="Search sender, subject, mailbox or case #"
          style={{ width: 320 }}
        />
      </div>
      <EmailTable
        items={recent?.items ?? []}
        empty={search.trim() ? `Nothing matches “${search.trim()}”.` : "No emails have been turned into Cases yet."}
        sort={{ key: sort, desc, onChange: changeSort }}
      />
      {recent && recent.totalCount > 0 && (
        <Pagination page={recent.page} totalPages={recent.totalPages} totalCount={recent.totalCount} pageSize={recent.pageSize} onChange={setPage} />
      )}
    </div>
  );
}

function StageGrid({ stages }: { stages: Stage[] }) {
  return (
    <div className="stat-grid">
      {stages.map((stage) => (
        <StatCard key={stage.label} label={stage.label} value={stage.count} hint={stage.hint} tone={stage.tone} to={stage.link} />
      ))}
    </div>
  );
}

interface SortState {
  key: SortKey;
  desc: boolean;
  onChange: (key: SortKey) => void;
}

function EmailTable({ items, empty, sort }: { items: CaseWorkflowItemDto[]; empty: string; sort?: SortState }) {
  const header = (label: string, key: SortKey) => {
    if (!sort) return <th style={thStyle}>{label}</th>;
    const active = sort.key === key;
    return (
      <th style={thStyle} aria-sort={active ? (sort.desc ? "descending" : "ascending") : "none"}>
        <button type="button" onClick={() => sort.onChange(key)} style={sortButtonStyle} title={`Sort by ${label.toLowerCase()}`}>
          {label} <span style={{ opacity: active ? 1 : 0.35 }}>{active && !sort.desc ? "▲" : "▼"}</span>
        </button>
      </th>
    );
  };

  return (
    <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
      <thead>
        <tr>
          {header("Received", "received")}
          {header("From", "from")}
          {header("Subject", "subject")}
          {header("Mailbox", "mailbox")}
          {header("Case", "case")}
          {header("Processed", "processed")}
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
              <Badge tone={OUTCOME_TONES[i.outcome]}>{i.outcome}</Badge>
              {i.caseId && <Link to={`/cases/${i.caseId}`} className="case-link" style={{ marginLeft: 8 }}>{i.caseNumber}</Link>}
              {i.detail && <div style={hintStyle}>{i.detail}</div>}
            </td>
            <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>{i.processedAt ? new Date(i.processedAt).toLocaleString() : "—"}</td>
          </tr>
        ))}
        {items.length === 0 && <tr><td style={tdStyle} colSpan={6}>{empty}</td></tr>}
      </tbody>
    </table>
  );
}

const OUTCOME_TONES: Record<string, Tone> = {
  "New Case": "good",
  "Added to existing Case": "info",
  "Reopened Case": "warn",
  Skipped: "neutral",
  Waiting: "warn",
};

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 15, fontWeight: 600, margin: "0 0 4px" };
const thStyle: React.CSSProperties = { textAlign: "left" };
const sortButtonStyle: React.CSSProperties = {
  background: "none", border: "none", padding: 0, font: "inherit", fontWeight: 600, color: "var(--color-text)", cursor: "pointer", whiteSpace: "nowrap",
};
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
