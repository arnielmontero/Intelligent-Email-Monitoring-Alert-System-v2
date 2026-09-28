import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { CaseDto, PagedResult } from "../../api/types";
import { CASE_WORK_STATUS_LABELS, CASE_REPLY_STATUS_LABELS } from "../../api/types";
import CaseSidePanel from "../../components/CaseSidePanel";
import Pagination from "../../components/Pagination";
import { selectedRowStyle } from "../../components/styles";


export default function CasesPage() {
  const [result, setResult] = useState<PagedResult<CaseDto> | null>(null);
  const [page, setPage] = useState(1);
  const cases = result?.items ?? [];
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [searchParams] = useSearchParams();
  const [statusFilter, setStatusFilter] = useState<string>(searchParams.get("status") ?? "");
  const [search, setSearch] = useState("");

  const [selectedId, setSelectedId] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    try {
      const params = new URLSearchParams({ page: String(page), pageSize: "15" });
      if (statusFilter !== "") params.set("workStatus", statusFilter);
      if (search) params.set("search", search);
      const res = await apiClient.get<PagedResult<CaseDto>>(`/cases?${params.toString()}`);
      setResult(res.data);
    } catch {
      setError("Failed to load cases.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [statusFilter, page]);

  function handleSearchSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (page === 1) load();
    else setPage(1);
  }

  if (loading && !result) return <p>Loading...</p>;

  return (
    <div style={{ display: "flex", gap: 24 }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1>Cases / Work Topics</h1>
        <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
          A Case is one piece of customer work, with all the emails that belong to it. Cases are created automatically
          from important email; click a row to see its emails, AI summary and timeline.
        </p>

        {error && <div className="form-error">{error}</div>}

        <form onSubmit={handleSearchSubmit} style={{ display: "flex", gap: 8, marginBottom: 16, flexWrap: "wrap" }}>
          <select value={statusFilter} onChange={(e) => { setStatusFilter(e.target.value); setPage(1); }}>
            <option value="">All statuses</option>
            {Object.entries(CASE_WORK_STATUS_LABELS).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
          <input
            placeholder="Search case #, subject, customer email"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            style={{ minWidth: 260 }}
          />
          <button type="submit">Search</button>
        </form>

        <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
          <thead>
            <tr>
              <th style={thStyle}>Case #</th>
              <th style={thStyle}>Customer</th>
              <th style={thStyle}>Subject</th>
              <th style={thStyle}>Owner</th>
              <th style={thStyle}>Work Status</th>
              <th style={thStyle}>Reply Status</th>
              <th style={thStyle}>Emails</th>
              <th style={thStyle}>Last Activity</th>
            </tr>
          </thead>
          <tbody>
            {cases.map((c) => (
              <tr
                key={c.id}
                onClick={() => setSelectedId(c.id)}
                style={{ cursor: "pointer", ...(selectedId === c.id ? selectedRowStyle : {}) }}
              >
                <td style={{ ...tdStyle, whiteSpace: "nowrap" }}><span className="case-link">{c.caseNumber}</span></td>
                <td style={tdStyle}>{c.customerDisplayName ?? c.customerEmailAddress}</td>
                <td style={tdStyle}>{c.subject}</td>
                <td style={tdStyle}>{c.ownerEmployeeName ?? "(unassigned)"}</td>
                <td style={tdStyle}>{CASE_WORK_STATUS_LABELS[c.workStatus]}</td>
                <td style={tdStyle}>{CASE_REPLY_STATUS_LABELS[c.replyStatus]}</td>
                <td style={tdStyle}>{c.emailCount}</td>
                <td style={tdStyle}>{new Date(c.lastActivityAt).toLocaleString()}</td>
              </tr>
            ))}
            {cases.length === 0 && (
              <tr><td style={tdStyle} colSpan={8}>No cases found.</td></tr>
            )}
          </tbody>
        </table>
        {result && result.totalCount > 0 && (
          <Pagination page={result.page} totalPages={result.totalPages} totalCount={result.totalCount} pageSize={result.pageSize} onChange={setPage} />
        )}
      </div>

      {selectedId && <CaseSidePanel caseId={selectedId} onClose={() => setSelectedId(null)} onChanged={load} />}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
