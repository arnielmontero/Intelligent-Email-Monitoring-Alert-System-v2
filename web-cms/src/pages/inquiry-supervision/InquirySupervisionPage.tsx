import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../../api/client";
import type {
  AgentInquiryCountsDto,
  EmployeeDto,
  InquiryOverviewDto,
  InquiryRowDto,
  InquirySupervisionDto,
  PagedResult,
} from "../../api/types";
import EmailThread from "../../components/EmailThread";
import Pagination from "../../components/Pagination";
import StatCard from "../../components/StatCard";
import { selectedRowStyle } from "../../components/styles";
import { useAuthStore } from "../../store/authStore";

/** The columns of the per-agent table; each is also a list filter ("bucket"). */
const BUCKETS: Array<{ key: string; label: string; count: (a: AgentInquiryCountsDto) => number; help: string }> = [
  { key: "awaiting-initial", label: "Awaiting first reply", count: (a) => a.awaitingInitialResponse, help: "New inquiries the agent has not answered yet." },
  { key: "follow-up", label: "Customer follow-up", count: (a) => a.customerFollowUp, help: "The customer wrote again; the agent owes a response." },
  { key: "inactive", label: "Inactive", count: (a) => a.inactive, help: "A wait expired or the conversation stalled; the agent owes the next step." },
  { key: "awaiting-customer", label: "Awaiting customer", count: (a) => a.awaitingCustomer, help: "The agent replied; waiting for the customer." },
  { key: "awaiting-internal", label: "Awaiting internal", count: (a) => a.awaitingInternal, help: "Waiting on another department." },
  { key: "potentially-resolved", label: "Potentially resolved", count: (a) => a.potentiallyResolved, help: "The AI thinks it is resolved; waiting for the agent to confirm." },
  { key: "overdue", label: "Overdue", count: (a) => a.overdue, help: "Past the resolution target and still open." },
  { key: "escalated", label: "Escalated", count: (a) => a.escalated, help: "Management has been told." },
  { key: "open", label: "All open", count: (a) => a.openTotal, help: "Every inquiry still under supervision." },
  { key: "resolved", label: "Resolved (30 days)", count: (a) => a.resolvedLast30Days, help: "Confirmed resolved in the last 30 days." },
];

const AGENT_OWES = new Set(["AwaitingInitialResponse", "CustomerFollowUpReceived", "Inactive"]);

function when(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString() : "—";
}

export default function InquirySupervisionPage() {
  const isAdmin = useAuthStore((s) => s.hasRole("SuperAdministrator", "Administrator"));
  const [overview, setOverview] = useState<InquiryOverviewDto | null>(null);
  const [list, setList] = useState<PagedResult<InquiryRowDto> | null>(null);
  const [bucket, setBucket] = useState("open");
  const [employeeId, setEmployeeId] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  async function loadOverview() {
    try {
      setOverview((await apiClient.get<InquiryOverviewDto>("/inquiries/overview")).data);
    } catch {
      setError("Failed to load the inquiry overview.");
    }
  }

  async function loadList() {
    const params = new URLSearchParams({ bucket, page: String(page), pageSize: "20" });
    if (employeeId) params.set("employeeId", employeeId);
    if (search) params.set("search", search);
    try {
      setList((await apiClient.get<PagedResult<InquiryRowDto>>(`/inquiries?${params.toString()}`)).data);
    } catch {
      setError("Failed to load inquiries.");
    }
  }

  function reload() {
    loadOverview();
    loadList();
  }

  useEffect(() => {
    loadOverview();
    const timer = window.setInterval(loadOverview, 60_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    loadList();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bucket, employeeId, page]);

  function pick(nextBucket: string, nextEmployee: string | null) {
    setBucket(nextBucket);
    setEmployeeId(nextEmployee);
    setPage(1);
  }

  async function runNow() {
    setRunning(true);
    try {
      await apiClient.post("/inquiries/run");
      reload();
    } catch {
      setError("The supervision run failed.");
    } finally {
      setRunning(false);
    }
  }

  const rows = list?.items ?? [];
  const agentName = employeeId ? overview?.agents.find((a) => a.employeeId === employeeId)?.agentName : null;

  return (
    <div style={{ display: "flex", gap: 24 }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
          <h1>Inquiry Supervision</h1>
          {isAdmin && (
            <button onClick={runNow} disabled={running}>{running ? "Running…" : "Check now"}</button>
          )}
        </div>
        <p style={{ color: "var(--color-text-muted)", fontSize: 13, maxWidth: 900 }}>
          Every customer inquiry is followed until the agent confirms it is resolved. A first reply does not end it.
          The agent is reminded when a deadline passes; management is emailed when an inquiry stays neglected.
          Click a number to list those inquiries.
        </p>
        {error && <div className="form-error">{error}</div>}

        {overview && <SummaryCards overview={overview} bucket={bucket} employeeId={employeeId} onPick={(b) => pick(b, null)} />}

        {overview && (
          <div style={{ overflowX: "auto", marginBottom: 24 }}>
            <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, fontSize: 13 }}>
              <thead>
                <tr>
                  <th style={{ textAlign: "left" }}>Agent</th>
                  {BUCKETS.map((b) => (
                    <th key={b.key} title={b.help} style={{ textAlign: "right", whiteSpace: "nowrap" }}>{b.label}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {[...overview.agents, overview.totals].map((a) => {
                  const isTotal = a === overview.totals;
                  return (
                    <tr key={isTotal ? "total" : a.employeeId ?? "unassigned"} style={isTotal ? { fontWeight: 600 } : undefined}>
                      <td>{a.agentName}</td>
                      {BUCKETS.map((b) => {
                        const n = b.count(a);
                        const alert = n > 0 && (b.key === "overdue" || b.key === "escalated");
                        return (
                          <td key={b.key} style={{ textAlign: "right" }}>
                            {n === 0 ? (
                              <span style={{ color: "var(--color-text-muted)" }}>0</span>
                            ) : (
                              <button
                                className="link-button"
                                onClick={() => pick(b.key, isTotal ? null : a.employeeId)}
                                style={{ background: "none", border: "none", padding: 0, cursor: "pointer", fontWeight: 600, color: alert ? "var(--color-danger)" : "var(--color-link)" }}
                              >
                                {n}
                              </button>
                            )}
                          </td>
                        );
                      })}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 12, alignItems: "center" }}>
          <select value={bucket} onChange={(e) => pick(e.target.value, employeeId)}>
            {BUCKETS.map((b) => <option key={b.key} value={b.key}>{b.label}</option>)}
            <option value="awaiting-agent">Awaiting agent (any)</option>
            <option value="active">Active conversations</option>
            <option value="all">Everything</option>
          </select>
          {agentName && (
            <span style={{ fontSize: 13 }}>
              Agent: <strong>{agentName}</strong>{" "}
              <button onClick={() => pick(bucket, null)} style={{ fontSize: 12 }}>All agents</button>
            </span>
          )}
          <form onSubmit={(e) => { e.preventDefault(); if (page === 1) loadList(); else setPage(1); }} style={{ display: "flex", gap: 8 }}>
            <input placeholder="Search case #, subject, customer" value={search} onChange={(e) => setSearch(e.target.value)} style={{ minWidth: 240 }} />
            <button type="submit">Search</button>
          </form>
        </div>

        <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
          <thead>
            <tr>
              <th style={{ textAlign: "left" }}>Case #</th>
              <th style={{ textAlign: "left" }}>Customer</th>
              <th style={{ textAlign: "left" }}>Subject</th>
              <th style={{ textAlign: "left" }}>Agent</th>
              <th style={{ textAlign: "left" }}>Stage</th>
              <th style={{ textAlign: "left" }}>Since</th>
              <th style={{ textAlign: "left" }}>Resolution target</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} onClick={() => setSelectedId(r.id)} style={{ cursor: "pointer", ...(selectedId === r.id ? selectedRowStyle : {}) }}>
                <td style={{ whiteSpace: "nowrap" }}><span className="case-link">{r.caseNumber}</span></td>
                <td>{r.customer}</td>
                <td>{r.subject}</td>
                <td>{r.agentName ?? "(unassigned)"}</td>
                <td>
                  <StagePill stage={r.stage} label={r.stageLabel} />
                  {r.isOverdue && <Pill color="var(--color-danger)">Overdue</Pill>}
                  {r.escalations > 0 && <Pill color="var(--color-warning)">Escalated ×{r.escalations}</Pill>}
                </td>
                <td style={{ whiteSpace: "nowrap" }}>{when(r.stageChangedAt)}</td>
                <td style={{ whiteSpace: "nowrap" }}>{when(r.resolutionDueAt)}</td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={7}>No inquiries here.</td></tr>}
          </tbody>
        </table>
        {list && list.totalCount > 0 && (
          <Pagination page={list.page} totalPages={list.totalPages} totalCount={list.totalCount} pageSize={list.pageSize} onChange={setPage} />
        )}
      </div>

      {selectedId && <InquiryPanel caseId={selectedId} onClose={() => setSelectedId(null)} onChanged={reload} />}
    </div>
  );
}

function SummaryCards({ overview, bucket, employeeId, onPick }: { overview: InquiryOverviewDto; bucket: string; employeeId: string | null; onPick: (b: string) => void }) {
  const t = overview.totals;
  const awaitingAgent = t.awaitingInitialResponse + t.customerFollowUp + t.inactive;
  const active = t.awaitingCustomer + t.awaitingInternal;
  const on = (b: string) => bucket === b && employeeId === null;
  return (
    <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(170px, 1fr))", gap: 12, marginBottom: 20 }}>
      <StatCard label="Detected (all time)" value={overview.totalDetected} hint="Every inquiry ever recorded" onClick={() => onPick("all")} active={on("all")} />
      <StatCard label="New" value={t.awaitingInitialResponse} hint="No first reply yet" tone={t.awaitingInitialResponse ? "warn" : "neutral"} onClick={() => onPick("awaiting-initial")} active={on("awaiting-initial")} />
      <StatCard label="Awaiting agent" value={awaitingAgent} hint="The agent owes a reply" tone={awaitingAgent ? "warn" : "neutral"} onClick={() => onPick("awaiting-agent")} active={on("awaiting-agent")} />
      <StatCard label="Active conversations" value={active} hint="Waiting on customer or internal" tone="info" onClick={() => onPick("active")} active={on("active")} />
      <StatCard label="Potentially resolved" value={t.potentiallyResolved} hint="Waiting for the agent to confirm" tone="info" onClick={() => onPick("potentially-resolved")} active={on("potentially-resolved")} />
      <StatCard label="Unresolved" value={t.openTotal} hint="Still under supervision" onClick={() => onPick("open")} active={on("open")} />
      <StatCard label="Overdue" value={t.overdue} hint="Past the resolution target" tone={t.overdue ? "bad" : "neutral"} onClick={() => onPick("overdue")} active={on("overdue")} />
      <StatCard label="Escalated" value={t.escalated} hint="Management has been told" tone={t.escalated ? "bad" : "neutral"} onClick={() => onPick("escalated")} active={on("escalated")} />
      <StatCard label="Resolved" value={t.resolvedLast30Days} hint="Confirmed, last 30 days" tone="good" onClick={() => onPick("resolved")} active={on("resolved")} />
    </div>
  );
}

function Pill({ color, children }: { color: string; children: React.ReactNode }) {
  return (
    <span style={{ display: "inline-block", marginLeft: 6, padding: "1px 8px", borderRadius: 10, fontSize: 11, color: "#fff", background: color }}>
      {children}
    </span>
  );
}

function StagePill({ stage, label }: { stage: string; label: string }) {
  const color = AGENT_OWES.has(stage)
    ? "var(--color-warning)"
    : stage === "Resolved"
      ? "var(--color-success)"
      : stage === "PotentiallyResolved"
        ? "var(--color-accent)"
        : "var(--color-pill-neutral)";
  return <span style={{ display: "inline-block", padding: "1px 8px", borderRadius: 10, fontSize: 12, color: "#fff", background: color }}>{label}</span>;
}

function InquiryPanel({ caseId, onClose, onChanged }: { caseId: string; onClose: () => void; onChanged: () => void }) {
  const [data, setData] = useState<InquirySupervisionDto | null>(null);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [reassignTo, setReassignTo] = useState("");
  const [comment, setComment] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  async function load() {
    setData((await apiClient.get<InquirySupervisionDto>(`/inquiries/${caseId}`)).data);
  }

  useEffect(() => {
    setData(null);
    setMessage(null);
    load().catch(() => setMessage("Failed to load the inquiry."));
    apiClient.get<EmployeeDto[]>("/employees").then((r) => setEmployees(r.data.filter((e) => e.isActive))).catch(() => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [caseId]);

  async function act(path: string, body: object, done: string) {
    setMessage(null);
    try {
      await apiClient.post(`/inquiries/${caseId}/${path}`, body);
      setMessage(done);
      setComment("");
      await load();
      onChanged();
    } catch (err: unknown) {
      const text = (err as { response?: { data?: { message?: string } } }).response?.data?.message;
      setMessage(text ?? "The action failed.");
    }
  }

  const closed = data?.stage === "Resolved" || data?.stage === "Closed";

  return (
    <aside style={panelStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 8 }}>
        <h2 style={{ margin: 0, fontSize: 18 }}>{data?.caseNumber ?? "Inquiry"}</h2>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <Link to={`/cases/${caseId}`} style={{ fontSize: 13 }}>Case page</Link>
          <button onClick={onClose}>Close</button>
        </div>
      </div>
      {!data && <p>Loading…</p>}
      {data && (
        <>
          <p style={{ marginTop: 12 }}>
            <StagePill stage={data.stage} label={data.stageLabel} /> since {when(data.stageChangedAt)}
            {data.overdueSince && <Pill color="var(--color-danger)">Overdue since {when(data.overdueSince)}</Pill>}
          </p>
          <h3 style={{ fontSize: 15, marginBottom: 2 }}>Email conversation</h3>
          <p style={{ color: "var(--color-text-muted)", fontSize: 12, margin: "0 0 10px" }}>
            The customer's first message, then every reply, oldest first.
          </p>
          <EmailThread messages={data.conversation} />
          <div style={{ height: 16 }} />
          <dl style={{ display: "grid", gridTemplateColumns: "auto 1fr", gap: "4px 12px", fontSize: 13, margin: "8px 0 16px" }}>
            <dt>First reply</dt><dd style={{ margin: 0 }}>{when(data.firstAgentResponseAt)}</dd>
            <dt>Last customer message</dt><dd style={{ margin: 0 }}>{when(data.lastCustomerMessageAt)}</dd>
            <dt>Last agent message</dt><dd style={{ margin: 0 }}>{when(data.lastAgentMessageAt)}</dd>
            <dt>Last internal activity</dt><dd style={{ margin: 0 }}>{when(data.lastInternalActivityAt)}</dd>
            <dt>Resolution target</dt><dd style={{ margin: 0 }}>{when(data.resolutionDueAt)}</dd>
            <dt>This stage</dt><dd style={{ margin: 0 }}>{data.episodeRemindersSent} reminder(s), {data.episodeEscalations} escalation(s)</dd>
            <dt>AI check</dt>
            <dd style={{ margin: 0 }}>
              {data.aiVerdict
                ? `${data.aiVerdict} (${Math.round((data.aiConfidence ?? 0) * 100)} %) — ${data.aiReason ?? ""}`
                : "Not assessed yet"}
            </dd>
          </dl>

          {!closed && (
            <section style={{ borderTop: "1px solid var(--color-border)", paddingTop: 12, marginBottom: 16 }}>
              <textarea placeholder="Note (optional)" value={comment} onChange={(e) => setComment(e.target.value)} rows={2} style={{ width: "100%", marginBottom: 8 }} />
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                <button onClick={() => act("confirm-resolved", { comment }, "Marked resolved. Supervision has ended.")}>Confirm resolved</button>
                {data.stage === "PotentiallyResolved" && (
                  <button onClick={() => act("reject-resolution", { comment }, "Kept open. Supervision continues.")}>Not resolved yet</button>
                )}
              </div>
              <div style={{ display: "flex", gap: 8, marginTop: 8 }}>
                <select value={reassignTo} onChange={(e) => setReassignTo(e.target.value)}>
                  <option value="">Reassign to…</option>
                  {employees.map((e) => <option key={e.id} value={e.id}>{e.fullName}</option>)}
                </select>
                <button disabled={!reassignTo} onClick={() => act("reassign", { employeeId: reassignTo }, "Reassigned.")}>Reassign</button>
              </div>
            </section>
          )}
          {message && <p style={{ fontSize: 13 }}>{message}</p>}

        </>
      )}
    </aside>
  );
}

const panelStyle: React.CSSProperties = {
  width: 540,
  flexShrink: 0,
  borderLeft: "1px solid var(--color-border)",
  paddingLeft: 24,
  position: "sticky",
  top: 16,
  alignSelf: "flex-start",
  maxHeight: "calc(100vh - 110px)",
  overflowY: "auto",
};
