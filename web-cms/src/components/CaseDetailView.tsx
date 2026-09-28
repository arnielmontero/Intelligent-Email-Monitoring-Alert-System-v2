import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import type { CaseCompletionReason, CaseDetailDto, CaseEmailDto } from "../api/types";
import {
  CASE_COMPLETION_REASONS,
  CASE_EVENT_TYPE_LABELS,
  CASE_MATCH_SIGNAL_LABELS,
  CASE_REPLY_STATUS_LABELS,
  CASE_WORK_STATUS_LABELS,
  REPLY_MATCH_SIGNAL_LABELS,
  REPLY_VERIFICATION_OUTCOME_LABELS,
} from "../api/types";
import { useAuthStore } from "../store/authStore";

const WORK_STATUS_COLORS: Record<number, string> = {
  1: "#b45309", 2: "#2563eb", 6: "#dc2626", 7: "#dc2626", 8: "#166534", 9: "var(--color-pill-neutral)", 11: "#7c3aed",
};

interface CaseDetailViewProps {
  caseId: string;
  /** Narrow layout for side panels. */
  compact?: boolean;
  /** Called after the Case was changed here (e.g. completed), so a list can refresh. */
  onChanged?: () => void;
}

/// Requirements §89 Case Investigation — everything about one Case: what the customer wrote, why
/// the AI made it a Case, who owns it, and the full chronological timeline. Used by the full Case
/// page and by the side panels on Cases and Case History & Logs.
export default function CaseDetailView({ caseId, compact = false, onChanged }: CaseDetailViewProps) {
  const id = caseId;
  const isAdmin = useAuthStore((s) => s.hasRole("SuperAdministrator", "Administrator"));
  const [detail, setDetail] = useState<CaseDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [reason, setReason] = useState<CaseCompletionReason>(0);
  const [comment, setComment] = useState("");

  async function load() {
    try {
      const res = await apiClient.get<CaseDetailDto>(`/cases/${id}`);
      setDetail(res.data);
    } catch (err: any) {
      setError(err.response?.status === 404 ? "Case not found." : "Failed to load the Case.");
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  async function complete() {
    setError(null);
    try {
      await apiClient.post(`/cases/${id}/complete`, { reason, comment: comment || null });
      setComment("");
      await load();
      onChanged?.();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to complete the Case.");
    }
  }

  if (!detail) return error ? <div className="form-error">{error}</div> : <p>Loading...</p>;

  const c = detail.case;
  const isOpen = c.workStatus !== 8 && c.workStatus !== 9;

  return (
    <div style={{ maxWidth: compact ? undefined : 1100 }}>
      {compact
        ? <h2 style={{ fontSize: 18, margin: "0 0 6px" }}>{c.caseNumber} — {c.subject}</h2>
        : <h1 style={{ marginBottom: 4 }}>{c.caseNumber} — {c.subject}</h1>}
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 16 }}>
        <span style={{ ...pillStyle, background: WORK_STATUS_COLORS[c.workStatus] ?? "var(--color-pill-neutral)" }}>{CASE_WORK_STATUS_LABELS[c.workStatus]}</span>
        <span style={{ ...pillStyle, background: c.replyStatus === 5 ? "#166534" : "var(--color-pill-neutral)" }}>Reply: {CASE_REPLY_STATUS_LABELS[c.replyStatus]}</span>
        {c.reopenCount > 0 && <span style={{ ...pillStyle, background: "var(--color-pill-neutral)" }}>Reopened {c.reopenCount}×</span>}
      </div>

      {error && <div className="form-error">{error}</div>}

      <div style={{ ...infoGridStyle, gridTemplateColumns: compact ? "1fr 1fr" : infoGridStyle.gridTemplateColumns }}>
        <Info label="Customer" value={c.customerDisplayName ? `${c.customerDisplayName} <${c.customerEmailAddress}>` : c.customerEmailAddress} />
        <Info label="Mailbox (received at)" value={c.emailAccountAddress} />
        <Info label="Owner" value={c.ownerEmployeeName ?? "Unassigned"} />
        <Info label="First email" value={new Date(c.firstEmailReceivedAt).toLocaleString()} />
        <Info label="Last activity" value={new Date(c.lastActivityAt).toLocaleString()} />
        {c.completedAt && (
          <Info
            label="Completed"
            value={`${new Date(c.completedAt).toLocaleString()} — ${CASE_COMPLETION_REASONS.find((r) => r.value === c.completionReason)?.label ?? ""}${c.completionComment ? `: ${c.completionComment}` : ""}`}
          />
        )}
      </div>

      <h2 style={compact ? compactSectionTitle : sectionTitle}>Emails ({detail.emails.length})</h2>
      {detail.emails.map((e, i) => <EmailCard key={e.emailMessageId} email={e} defaultOpen={i === detail.emails.length - 1} />)}

      <h2 style={compact ? compactSectionTitle : sectionTitle}>Timeline</h2>
      <ol style={{ listStyle: "none", padding: 0, margin: 0, borderLeft: "2px solid var(--color-border)" }}>
        {detail.history.map((h) => (
          <li key={h.id} style={{ padding: "6px 0 10px 16px", position: "relative" }}>
            <span style={dotStyle} />
            <div style={hintStyle}>{new Date(h.occurredAt).toLocaleString()} · {CASE_EVENT_TYPE_LABELS[h.eventType]}</div>
            <div>{h.detail}</div>
          </li>
        ))}
      </ol>

      {detail.notifications && detail.notifications.length > 0 && (
        <>
          <h2 style={compact ? compactSectionTitle : sectionTitle}>Notifications sent</h2>
          <table style={tableStyle}>
            <thead>
              <tr><th style={thStyle}>When</th><th style={thStyle}>To</th><th style={thStyle}>Type</th><th style={thStyle}>Status</th><th style={thStyle}>Message</th></tr>
            </thead>
            <tbody>
              {detail.notifications.map((n) => (
                <tr key={n.id}>
                  <td style={tdStyle}>{new Date(n.createdAt).toLocaleString()}</td>
                  <td style={tdStyle}>{n.employeeName}</td>
                  <td style={tdStyle}>{n.type}</td>
                  <td style={tdStyle}>{n.status}{n.acknowledgedAt && <div style={hintStyle}>ack {new Date(n.acknowledgedAt).toLocaleString()}</div>}</td>
                  <td style={tdStyle}>{n.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      {detail.verificationAttempts.length > 0 && (
        <>
          <h2 style={compact ? compactSectionTitle : sectionTitle}>Reply verification</h2>
          <table style={tableStyle}>
            <thead>
              <tr><th style={thStyle}>When</th><th style={thStyle}>Outcome</th><th style={thStyle}>Detail</th></tr>
            </thead>
            <tbody>
              {detail.verificationAttempts.map((a) => (
                <tr key={a.id}>
                  <td style={tdStyle}>{new Date(a.attemptedAt).toLocaleString()}</td>
                  <td style={tdStyle}>{REPLY_VERIFICATION_OUTCOME_LABELS[a.outcome]}</td>
                  <td style={tdStyle}>
                    {a.outcome === 0 ? `Matched via ${REPLY_MATCH_SIGNAL_LABELS[a.matchSignal]}` : ""}
                    {a.errorDetail ?? a.matchDetail ?? ""}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      {isAdmin && isOpen && (
        <section style={{ ...panelStyle, marginTop: 24 }}>
          <h2 style={{ fontSize: 16, marginTop: 0 }}>Complete this Case</h2>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <select value={reason} onChange={(e) => setReason(Number(e.target.value) as CaseCompletionReason)}>
              {CASE_COMPLETION_REASONS.map((r) => <option key={r.value} value={r.value}>{r.label}</option>)}
            </select>
            <input value={comment} onChange={(e) => setComment(e.target.value)} placeholder="Optional comment" style={{ flex: 1, minWidth: 240 }} />
            <button className="btn-primary" onClick={complete}>Mark completed</button>
          </div>
        </section>
      )}
    </div>
  );
}

function EmailCard({ email, defaultOpen }: { email: CaseEmailDto; defaultOpen: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  const cls = email.classification;
  return (
    <div style={{ ...panelStyle, marginBottom: 10 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, cursor: "pointer" }} onClick={() => setOpen(!open)}>
        <div style={{ minWidth: 0 }}>
          <strong>{email.subject || "(no subject)"}</strong>
          <div style={hintStyle}>
            From {email.fromDisplayName ? `${email.fromDisplayName} <${email.fromAddress}>` : email.fromAddress} · {new Date(email.receivedAt).toLocaleString()}
          </div>
        </div>
        <span style={hintStyle}>{open ? "Hide ▲" : "Show ▼"}</span>
      </div>

      {cls && (
        <div style={{ ...aiBoxStyle }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <strong style={{ fontSize: 13 }}>AI</strong>
            <span style={{ ...pillStyle, background: cls.decision === "Important" ? "#b45309" : "var(--color-pill-neutral)" }}>{cls.decision}</span>
            {cls.category && <span style={{ ...pillStyle, background: "var(--color-pill-neutral)" }}>{cls.category}</span>}
            {cls.priority && <span style={{ ...pillStyle, background: cls.priority === "High" ? "#dc2626" : "var(--color-pill-neutral)" }}>{cls.priority} priority</span>}
            {cls.confidence !== null && <span style={hintStyle}>{Math.round(cls.confidence * 100)}% confident</span>}
            {cls.aiModel && <span style={hintStyle}>· {cls.aiModel}</span>}
          </div>
          {cls.summary && <div style={{ marginTop: 6 }}>{cls.summary}</div>}
          {(cls.legitimate !== null || cls.responseExpected !== null) && (
            <div style={{ ...hintStyle, marginTop: 6 }}>
              {cls.legitimate !== null && <>Legitimate: <strong style={{ color: cls.legitimate ? "var(--color-success)" : "var(--color-danger)" }}>{cls.legitimate ? "Yes" : "No"}</strong></>}
              {cls.legitimate !== null && cls.responseExpected !== null && " · "}
              {cls.responseExpected !== null && <>Needs response: <strong>{cls.responseExpected ? "Yes" : "No"}</strong></>}
            </div>
          )}
          {cls.decisionReason && <div style={{ ...hintStyle, marginTop: 4 }}>Why: {cls.decisionReason}</div>}
        </div>
      )}

      {open && (
        <>
          <div style={{ ...hintStyle, marginTop: 10 }}>
            To: {email.toAddresses?.split(";").join(", ") || "—"}
            {email.ccAddresses && <> · Cc: {email.ccAddresses.split(";").join(", ")}</>}
            {email.attachmentCount > 0 && <> · {email.attachmentCount} attachment{email.attachmentCount === 1 ? "" : "s"}</>}
            {" "}· Linked to this Case via {CASE_MATCH_SIGNAL_LABELS[email.matchSignal]}
          </div>
          <pre style={bodyStyle}>{email.body?.trim() || "(no text content)"}</pre>
          {email.bodyFromHtml && <div style={hintStyle}>Shown as plain text — the email had HTML content only.</div>}
        </>
      )}
    </div>
  );
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div style={hintStyle}>{label}</div>
      <div>{value}</div>
    </div>
  );
}

const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 18, margin: "28px 0 12px" };
const compactSectionTitle: React.CSSProperties = { fontSize: 15, margin: "20px 0 10px" };
const pillStyle: React.CSSProperties = { color: "#fff", fontSize: 12, padding: "2px 10px", borderRadius: 999 };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 14, background: "var(--color-surface)" };
const aiBoxStyle: React.CSSProperties = { marginTop: 10, padding: "8px 10px", borderRadius: 6, background: "var(--color-surface-alt)", fontSize: 13 };
const infoGridStyle: React.CSSProperties = { ...panelStyle, display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: 14 };
const bodyStyle: React.CSSProperties = {
  whiteSpace: "pre-wrap", wordBreak: "break-word", fontFamily: "inherit", fontSize: 14, lineHeight: 1.5,
  background: "var(--color-bg)", border: "1px solid var(--color-border)", borderRadius: 6, padding: 12, marginTop: 10, maxHeight: 480, overflow: "auto",
};
const dotStyle: React.CSSProperties = { position: "absolute", left: -6, top: 10, width: 10, height: 10, borderRadius: 999, background: "var(--color-accent)" };
const tableStyle: React.CSSProperties = { width: "100%", borderCollapse: "collapse" };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "top" };
