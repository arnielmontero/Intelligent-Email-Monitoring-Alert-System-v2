import { useState } from "react";
import type { ConversationMessageDto } from "../api/types";
import AiCost from "./AiCost";

/**
 * Shows an inquiry's emails the way a mail program shows a thread: one card per email, oldest first,
 * numbered, each saying in plain words who wrote it ("Customer's first message", "Our reply", ...).
 * Between two cards it says how long the other side took to answer. Quoted earlier text that mail
 * programs append to every reply is hidden by default, so each card only shows what is new.
 */
export default function EmailThread({ messages }: { messages: ConversationMessageDto[] }) {
  if (messages.length === 0) {
    return <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>No emails yet.</p>;
  }

  const firstAgentIndex = messages.findIndex((m) => m.role === "Agent");
  const lastExternal = [...messages].reverse().find((m) => m.role !== "Internal");
  const waitingFor = lastExternal?.role === "Customer" ? "us" : lastExternal?.role === "Agent" ? "customer" : null;

  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 8, flexWrap: "wrap", marginBottom: 12 }}>
        <span style={{ fontSize: 13, color: "var(--color-text-muted)" }}>
          {messages.length} email{messages.length === 1 ? "" : "s"} · started {formatDate(messages[0].at)}
        </span>
        {waitingFor && (
          <span
            style={{
              fontSize: 12,
              fontWeight: 600,
              padding: "3px 10px",
              borderRadius: 999,
              border: `1px solid ${waitingFor === "us" ? "var(--color-warning)" : "var(--color-border)"}`,
              color: waitingFor === "us" ? "var(--color-warning)" : "var(--color-text-muted)",
            }}
          >
            {waitingFor === "us" ? "Waiting for our reply" : "Waiting for the customer"}
          </span>
        )}
      </div>

      <ol style={{ listStyle: "none", margin: 0, padding: 0 }}>
        {messages.map((m, i) => (
          <li key={m.id}>
            {i > 0 && <Gap previous={messages[i - 1]} current={m} />}
            <EmailCard
              message={m}
              number={i + 1}
              title={titleFor(m, i, firstAgentIndex)}
              showSubject={i === 0 || normalize(m.subject) !== normalize(messages[i - 1].subject)}
              isLatest={i === messages.length - 1}
            />
          </li>
        ))}
      </ol>
    </div>
  );
}

function titleFor(m: ConversationMessageDto, index: number, firstAgentIndex: number) {
  if (m.role === "Internal") return "Internal email (not sent to the customer)";
  if (m.role === "Agent") return index === firstAgentIndex ? "Our first reply" : "Our reply";
  return index === 0 ? "Customer's first message (the inquiry)" : "Customer replied";
}

const ROLE_STYLE: Record<ConversationMessageDto["role"], { color: string; background: string; border: string }> = {
  Customer: { color: "var(--color-success)", background: "var(--color-surface)", border: "1px solid var(--color-border)" },
  Agent: { color: "var(--color-accent)", background: "var(--color-accent-soft)", border: "1px solid var(--color-border)" },
  Internal: { color: "var(--color-warning)", background: "var(--color-surface-alt)", border: "1px dashed var(--color-warning)" },
};

function EmailCard({ message: m, number, title, showSubject, isLatest }: {
  message: ConversationMessageDto;
  number: number;
  title: string;
  showSubject: boolean;
  isLatest: boolean;
}) {
  const [showAll, setShowAll] = useState(false);
  const style = ROLE_STYLE[m.role];
  const full = (m.text ?? "").trim();
  // If the person typed their answer BELOW the quoted original (common in Outlook), there is no "new" text above
  // the quote; show the whole email rather than an empty card.
  const fresh = stripQuoted(full) || full;
  const hasHidden = fresh.length < full.length;
  const body = showAll ? full : fresh;

  return (
    <article style={{ background: style.background, border: style.border, borderRadius: 8, padding: "10px 12px" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
        <span
          aria-label={`Email ${number}`}
          style={{
            width: 22, height: 22, borderRadius: "50%", flexShrink: 0, display: "inline-flex", alignItems: "center", justifyContent: "center",
            fontSize: 12, fontWeight: 700, color: "#fff", background: style.color,
          }}
        >
          {number}
        </span>
        <strong style={{ fontSize: 14, color: style.color }}>{title}</strong>
        {isLatest && (
          <span style={{ fontSize: 11, padding: "1px 8px", borderRadius: 999, border: "1px solid var(--color-border)", color: "var(--color-text-muted)" }}>
            Latest
          </span>
        )}
        {/* What AI spent checking this customer email (our replies and internal mail cost nothing, so they show none). */}
        {m.aiCost && <span style={{ marginLeft: "auto" }}><AiCost cost={m.aiCost} /></span>}
      </div>

      <div style={{ fontSize: 12, color: "var(--color-text-muted)", margin: "6px 0 0", lineHeight: 1.6 }}>
        <div><strong>From:</strong> {m.fromName ? `${m.fromName} <${m.from}>` : m.from}</div>
        <div><strong>To:</strong> {m.to || "—"}{m.cc ? <> · <strong>Cc:</strong> {m.cc}</> : null}</div>
        <div><strong>Date:</strong> {formatDate(m.at)}{m.attachments > 0 && ` · ${m.attachments} attachment${m.attachments === 1 ? "" : "s"}`}</div>
        {showSubject && <div><strong>Subject:</strong> {m.subject || "(no subject)"}</div>}
      </div>

      <div style={{ fontSize: 13, whiteSpace: "pre-wrap", lineHeight: 1.5, marginTop: 8, overflowWrap: "anywhere" }}>
        {m.text == null ? <em style={{ color: "var(--color-text-muted)" }}>(The text of this email was not stored.)</em> : body || <em>(empty)</em>}
      </div>

      {hasHidden && (
        <button
          type="button"
          onClick={() => setShowAll((v) => !v)}
          style={{ marginTop: 6, fontSize: 12, padding: "2px 8px", background: "transparent", color: "var(--color-link)", border: "none", cursor: "pointer" }}
        >
          {showAll ? "Hide earlier quoted text" : "Show earlier quoted text"}
        </button>
      )}
    </article>
  );
}

/** The line between two emails: who answered and how long it took, in plain words. */
function Gap({ previous, current }: { previous: ConversationMessageDto; current: ConversationMessageDto }) {
  const wait = humanDuration(new Date(current.at).getTime() - new Date(previous.at).getTime());
  let text = `${wait} later`;
  if (current.role === "Agent" && previous.role === "Customer") text = `We answered ${wait} later`;
  else if (current.role === "Customer" && previous.role === "Agent") text = `The customer answered ${wait} later`;

  return (
    <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 0 6px 10px", fontSize: 12, color: "var(--color-text-muted)" }}>
      <span aria-hidden="true" style={{ width: 2, height: 18, background: "var(--color-border)" }} />
      <span aria-hidden="true">↓</span>
      {text}
    </div>
  );
}

/** Cuts the earlier conversation that mail programs quote under a reply ("On ... wrote:", "-----Original Message-----", "> ..."). */
function stripQuoted(text: string): string {
  const lines = text.split(/\r?\n/);
  const markers = [
    /^On .+wrote:\s*$/i,
    /^-{2,}\s*Original Message\s*-{2,}/i,
    /^_{10,}\s*$/,
    /^From:\s.+/i,
    /^>/,
  ];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (i > 0 && markers.some((re) => re.test(line))) {
      return lines.slice(0, i).join("\n").trim();
    }
  }
  return text;
}

function normalize(subject: string) {
  return subject.replace(/^\s*((re|fw|fwd|aw|sv|vs)\s*:\s*)+/i, "").trim().toLowerCase();
}

function formatDate(value: string) {
  return new Date(value).toLocaleString(undefined, { weekday: "short", day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

function humanDuration(ms: number) {
  const minutes = Math.max(0, Math.round(ms / 60_000));
  if (minutes < 1) return "less than a minute";
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? "" : "s"}`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    const rest = minutes % 60;
    return `${hours} hour${hours === 1 ? "" : "s"}${rest ? ` ${rest} min` : ""}`;
  }
  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return `${days} day${days === 1 ? "" : "s"}${restHours ? ` ${restHours} hour${restHours === 1 ? "" : "s"}` : ""}`;
}
