import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type {
  CaseDto,
  CaseDetailDto,
  CaseCompletionReason,
} from "../../api/types";
import { CASE_WORK_STATUS_LABELS, CASE_REPLY_STATUS_LABELS, CASE_MATCH_SIGNAL_LABELS, CASE_EVENT_TYPE_LABELS, REPLY_VERIFICATION_OUTCOME_LABELS, REPLY_MATCH_SIGNAL_LABELS } from "../../api/types";

const COMPLETION_REASONS: Array<{ value: CaseCompletionReason; label: string }> = [
  { value: 0, label: "Customer Request Resolved" },
  { value: 1, label: "Employee Responded" },
  { value: 2, label: "Phone Call Handled" },
  { value: 3, label: "Handled Outside Email" },
  { value: 4, label: "No Response Required" },
  { value: 5, label: "Duplicate" },
  { value: 6, label: "Incorrect Classification" },
  { value: 7, label: "Cancelled by Admin" },
  { value: 8, label: "Cancelled by Employee" },
  { value: 9, label: "Other" },
];

export default function CasesPage() {
  const [cases, setCases] = useState<CaseDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string>("");
  const [search, setSearch] = useState("");

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<CaseDetailDto | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);

  const [completionReason, setCompletionReason] = useState<CaseCompletionReason>(1);
  const [completionComment, setCompletionComment] = useState("");

  async function load() {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      if (statusFilter !== "") params.set("workStatus", statusFilter);
      if (search) params.set("search", search);
      const res = await apiClient.get<CaseDto[]>(`/cases?${params.toString()}`);
      setCases(res.data);
    } catch {
      setError("Failed to load cases.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [statusFilter]);

  async function openDetail(id: string) {
    setSelectedId(id);
    setDetailLoading(true);
    setDetail(null);
    try {
      const res = await apiClient.get<CaseDetailDto>(`/cases/${id}`);
      setDetail(res.data);
    } catch {
      setError("Failed to load case detail.");
    } finally {
      setDetailLoading(false);
    }
  }

  async function handleComplete() {
    if (!selectedId) return;
    setError(null);
    try {
      await apiClient.post(`/cases/${selectedId}/complete`, {
        reason: completionReason,
        comment: completionComment || null,
      });
      setCompletionComment("");
      await Promise.all([load(), openDetail(selectedId)]);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to complete case.");
    }
  }

  function handleSearchSubmit(e: React.FormEvent) {
    e.preventDefault();
    load();
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div style={{ display: "flex", gap: 24 }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1>Cases / Work Topics</h1>
        <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
          Requirements §32-§41 — a Case is the business work record; email is the communication
          evidence linked to it. Created automatically from AI-classified Important email (§20,
          §33); never matched by subject alone (§34).
        </p>

        {error && <div className="form-error">{error}</div>}

        <form onSubmit={handleSearchSubmit} style={{ display: "flex", gap: 8, marginBottom: 16, flexWrap: "wrap" }}>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
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

        <table style={{ width: "100%", borderCollapse: "collapse" }}>
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
                onClick={() => openDetail(c.id)}
                style={{ cursor: "pointer", background: selectedId === c.id ? "#1e293b" : undefined }}
              >
                <td style={tdStyle}>{c.caseNumber}</td>
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
      </div>

      {selectedId && (
        <div style={{ width: 420, flexShrink: 0, borderLeft: "1px solid #334155", paddingLeft: 24 }}>
          <h2 style={{ fontSize: 16 }}>Case Detail</h2>
          {detailLoading && <p>Loading...</p>}
          {detail && (
            <>
              <p><strong>{detail.case.caseNumber}</strong> — {detail.case.subject}</p>
              <p style={{ fontSize: 13, color: "var(--color-text-muted)" }}>
                {detail.case.customerDisplayName ?? detail.case.customerEmailAddress} via {detail.case.emailAccountAddress}
              </p>
              <p>Work Status: <strong>{CASE_WORK_STATUS_LABELS[detail.case.workStatus]}</strong></p>
              <p>Reply Status: {CASE_REPLY_STATUS_LABELS[detail.case.replyStatus]}</p>
              <p>Owner: {detail.case.ownerEmployeeName ?? "(unassigned)"}</p>
              {detail.case.reopenCount > 0 && <p>Reopened {detail.case.reopenCount} time(s)</p>}
              {detail.case.completedAt && (
                <p>Completed {new Date(detail.case.completedAt).toLocaleString()} — {detail.case.completionComment}</p>
              )}

              <h3 style={{ fontSize: 14, marginTop: 16 }}>Linked Emails ({detail.emails.length})</h3>
              <ul style={{ paddingLeft: 16, fontSize: 13 }}>
                {detail.emails.map((e) => (
                  <li key={e.emailMessageId} style={{ marginBottom: 8 }}>
                    <div>{e.subject}</div>
                    <div style={{ color: "var(--color-text-muted)" }}>
                      {e.fromAddress} · {new Date(e.receivedAt).toLocaleString()} · matched via {CASE_MATCH_SIGNAL_LABELS[e.matchSignal]}
                    </div>
                  </li>
                ))}
              </ul>

              {detail.verificationAttempts.length > 0 && (
                <>
                  <h3 style={{ fontSize: 14, marginTop: 16 }}>Reply Verification (§42)</h3>
                  <ul style={{ paddingLeft: 16, fontSize: 13 }}>
                    {detail.verificationAttempts.map((a) => (
                      <li key={a.id} style={{ marginBottom: 6 }}>
                        <strong>{REPLY_VERIFICATION_OUTCOME_LABELS[a.outcome]}</strong>
                        {a.outcome === 0 && ` — matched via ${REPLY_MATCH_SIGNAL_LABELS[a.matchSignal]}`}
                        {a.errorDetail && ` — ${a.errorDetail}`}
                        <div style={{ color: "var(--color-text-muted)" }}>{new Date(a.attemptedAt).toLocaleString()}</div>
                      </li>
                    ))}
                  </ul>
                </>
              )}

              <h3 style={{ fontSize: 14, marginTop: 16 }}>History (§66/§89)</h3>
              <ul style={{ paddingLeft: 16, fontSize: 13 }}>
                {detail.history.map((h) => (
                  <li key={h.id} style={{ marginBottom: 6 }}>
                    <strong>{CASE_EVENT_TYPE_LABELS[h.eventType]}</strong> — {h.detail}
                    <div style={{ color: "var(--color-text-muted)" }}>{new Date(h.occurredAt).toLocaleString()}</div>
                  </li>
                ))}
              </ul>

              {detail.case.workStatus !== 8 && detail.case.workStatus !== 9 && (
                <div style={{ marginTop: 16 }}>
                  <h3 style={{ fontSize: 14 }}>Complete Case (§48)</h3>
                  <select value={completionReason} onChange={(e) => setCompletionReason(Number(e.target.value) as CaseCompletionReason)} style={{ width: "100%", marginBottom: 8 }}>
                    {COMPLETION_REASONS.map((r) => (
                      <option key={r.value} value={r.value}>{r.label}</option>
                    ))}
                  </select>
                  <textarea
                    placeholder="Optional comment"
                    value={completionComment}
                    onChange={(e) => setCompletionComment(e.target.value)}
                    rows={2}
                    style={{ width: "100%", marginBottom: 8 }}
                  />
                  <button onClick={handleComplete}>Mark Completed</button>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
