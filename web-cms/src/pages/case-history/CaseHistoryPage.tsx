import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { apiClient } from "../../api/client";
import { CASE_EVENT_TYPE_LABELS } from "../../api/types";
import type { CaseEventSearchResultDto, EmailAccountDto, EmployeeDto, PagedResult } from "../../api/types";
import Pagination from "../../components/Pagination";
import CaseSidePanel from "../../components/CaseSidePanel";
import { selectedRowStyle } from "../../components/styles";

const PAGE_SIZE = 25;

/// Requirements §66 (Case History, append-only), §89 (Case Investigation), §86 (HISTORY & AUDIT →
/// Case History & Logs). Global search across every Case; open a Case for its full content.
export default function CaseHistoryPage() {
  const [searchParams] = useSearchParams();
  const [result, setResult] = useState<PagedResult<CaseEventSearchResultDto> | null>(null);
  const [mailboxes, setMailboxes] = useState<EmailAccountDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [selectedCaseId, setSelectedCaseId] = useState<string | null>(null);

  const [search, setSearch] = useState(searchParams.get("search") ?? "");
  const [mailboxId, setMailboxId] = useState("");
  const [ownerId, setOwnerId] = useState("");
  const [eventType, setEventType] = useState("");
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");

  async function load(page = 1) {
    setError(null);
    try {
      const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      if (search.trim()) params.set("search", search.trim());
      if (mailboxId) params.set("emailAccountId", mailboxId);
      if (ownerId) params.set("ownerEmployeeId", ownerId);
      if (eventType) params.set("eventType", eventType);
      if (fromDate) params.set("from", new Date(`${fromDate}T00:00:00`).toISOString());
      if (toDate) params.set("to", new Date(`${toDate}T23:59:59`).toISOString());
      const res = await apiClient.get<PagedResult<CaseEventSearchResultDto>>(`/cases/events?${params.toString()}`);
      setResult(res.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view Case History (requires Supervisor role or above)."
        : "Failed to load Case History.");
    }
  }

  useEffect(() => {
    apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=0").then((r) => setMailboxes(r.data)).catch(() => undefined);
    apiClient.get<EmployeeDto[]>("/employees").then((r) => setEmployees(r.data)).catch(() => undefined);
  }, []);

  // Filters apply as soon as they change; typing in the search box waits for a short pause.
  useEffect(() => {
    const handle = setTimeout(() => load(1), 350);
    return () => clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, mailboxId, ownerId, eventType, fromDate, toDate]);

  const activeFilters = [
    search.trim() && `“${search.trim()}”`,
    mailboxId && mailboxes.find((m) => m.id === mailboxId)?.emailAddress,
    ownerId && employees.find((e) => e.id === ownerId)?.fullName,
    eventType && CASE_EVENT_TYPE_LABELS[Number(eventType) as keyof typeof CASE_EVENT_TYPE_LABELS],
    fromDate && `from ${fromDate}`,
    toDate && `to ${toDate}`,
  ].filter(Boolean) as string[];

  function clearFilters() {
    setSearch("");
    setMailboxId("");
    setOwnerId("");
    setEventType("");
    setFromDate("");
    setToDate("");
  }

  return (
    <div style={{ display: "flex", gap: 24 }}>
    <div style={{ flex: 1, minWidth: 0 }}>
      <h1>Case History &amp; Logs</h1>
      <p style={mutedStyle}>
        Every event across every Case (email received, status changes, employee actions, reminders, escalations, reply
        verification). Append-only. Click a row to see that Case's emails, AI summary and full timeline.
      </p>

      {error && <div className="form-error">{error}</div>}

      <form
        onSubmit={(e) => { e.preventDefault(); load(1); }}
        style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12, alignItems: "end", marginBottom: 16 }}
      >
        <div style={{ ...fieldStyle, gridColumn: "span 2" }}>
          <label>Search</label>
          <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Case number, subject, customer email/name, mailbox, owner, event text…" />
        </div>
        <div style={fieldStyle}>
          <label>Mailbox</label>
          <select value={mailboxId} onChange={(e) => setMailboxId(e.target.value)}>
            <option value="">All mailboxes</option>
            {mailboxes.map((m) => <option key={m.id} value={m.id}>{m.emailAddress}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label>Owner</label>
          <select value={ownerId} onChange={(e) => setOwnerId(e.target.value)}>
            <option value="">All owners</option>
            {employees.map((emp) => <option key={emp.id} value={emp.id}>{emp.fullName}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label>Event type</label>
          <select value={eventType} onChange={(e) => setEventType(e.target.value)}>
            <option value="">All</option>
            {Object.entries(CASE_EVENT_TYPE_LABELS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label>From</label>
          <input type="date" value={fromDate} onChange={(e) => setFromDate(e.target.value)} />
        </div>
        <div style={fieldStyle}>
          <label>To</label>
          <input type="date" value={toDate} onChange={(e) => setToDate(e.target.value)} />
        </div>
        <div style={{ display: "flex", gap: 8 }}>
          <button type="button" onClick={clearFilters} disabled={activeFilters.length === 0}>Clear filters</button>
        </div>
      </form>

      {!result ? (
        <p>Loading...</p>
      ) : (
        <>
          <div style={{ ...mutedStyle, marginBottom: 8 }}>
            <strong style={{ color: "var(--color-text)" }}>{result.totalCount.toLocaleString()}</strong> event{result.totalCount === 1 ? "" : "s"}
            {activeFilters.length > 0 ? <> matching {activeFilters.join(" · ")}</> : " (no filters)"}
          </div>
          <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <thead>
              <tr>
                <th style={thStyle}>When</th>
                <th style={thStyle}>Case</th>
                <th style={thStyle}>Customer</th>
                <th style={thStyle}>Mailbox → Owner</th>
                <th style={thStyle}>Event</th>
                <th style={thStyle}>Detail</th>
                <th style={thStyle}>By</th>
              </tr>
            </thead>
            <tbody>
              {result.items.map((e) => (
                <tr
                  key={e.id}
                  onClick={() => setSelectedCaseId(e.caseId)}
                  title="Show this Case"
                  style={{ cursor: "pointer", ...(selectedCaseId === e.caseId ? selectedRowStyle : {}) }}
                >
                  <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>{new Date(e.occurredAt).toLocaleString()}</td>
                  <td style={{ ...tdStyle, maxWidth: 320 }}>
<span className="case-link">{e.caseNumber}</span>
                    <div style={hintStyle} title={e.caseSubject}>{e.caseSubject}</div>
                  </td>
                  <td style={tdStyle}>
                    {e.customerDisplayName ?? e.customerEmailAddress}
                    {e.customerDisplayName && <div style={hintStyle}>{e.customerEmailAddress}</div>}
                  </td>
                  <td style={tdStyle}>
                    {e.mailboxAddress}
                    <div style={hintStyle}>{e.ownerEmployeeName ?? "Unassigned"}</div>
                  </td>
                  <td style={tdStyle}>{CASE_EVENT_TYPE_LABELS[e.eventType]}</td>
                  <td style={tdStyle}>{e.detail}</td>
                  <td style={tdStyle}>{e.actorEmployeeName ?? "System"}</td>
                </tr>
              ))}
              {result.items.length === 0 && <tr><td style={tdStyle} colSpan={7}>No events match these filters.</td></tr>}
            </tbody>
          </table>
          {result.totalCount > 0 && (
            <Pagination page={result.page} totalPages={result.totalPages} totalCount={result.totalCount} pageSize={result.pageSize} onChange={load} />
          )}
        </>
      )}
    </div>
    {selectedCaseId && <CaseSidePanel caseId={selectedCaseId} onClose={() => setSelectedCaseId(null)} onChanged={() => load(result?.page ?? 1)} />}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const fieldStyle: React.CSSProperties = { display: "flex", flexDirection: "column", gap: 4, minWidth: 0 };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "top" };
