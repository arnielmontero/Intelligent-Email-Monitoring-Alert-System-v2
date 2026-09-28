import { useEffect, useState } from "react";
import { Link, useLocation } from "react-router-dom";
import { NAV_SECTIONS, helpAnchor } from "../../components/layout/navigation";
import NavIcon from "../../components/layout/NavIcon";

const FLOW = [
  { title: "Email arrives", text: "IEMAS checks each monitored mailbox every 2 minutes." },
  { title: "AI reads it", text: "Is it relevant, a legitimate business email, and does it need a reply?" },
  { title: "Case created", text: "Only email that needs work becomes a Case, owned by the mailbox's owner." },
  { title: "Employee alerted", text: "A pop-up appears on the owner's PC through the Windows Agent." },
  { title: "Reminders", text: "If no reply is found in the Sent folder, reminders follow." },
  { title: "Escalation", text: "Still no reply? The supervisor or manager is told." },
];

const SETUP = [
  { text: "Add the employee", path: "/employees" },
  { text: "Add their mailbox and set them as its owner", path: "/email-accounts" },
  { text: "Paste the OpenRouter API key and test it", path: "/ai-models" },
  { text: "Install the IEMAS Agent on their PC and register it", path: "/agents" },
  { text: "Approve the PC under Windows Agents", path: "/agents" },
  { text: "Review which emails count as work", path: "/system-configuration" },
];

export default function HelpPage() {
  const location = useLocation();
  const [query, setQuery] = useState("");

  // Jump to the entry the header's "Help for this page" link points at.
  useEffect(() => {
    if (!location.hash) return;
    const target = document.getElementById(location.hash.slice(1));
    if (target) {
      target.scrollIntoView({ behavior: "smooth", block: "start" });
      target.animate?.([{ backgroundColor: "rgba(59,130,246,0.25)" }, { backgroundColor: "transparent" }], { duration: 1800 });
    }
  }, [location.hash]);

  const term = query.trim().toLowerCase();
  const sections = NAV_SECTIONS.filter((s) => s.id !== "help")
    .map((s) => ({
      ...s,
      items: s.items.filter((i) => !term || `${s.label} ${i.label} ${i.help} ${(i.tasks ?? []).join(" ")}`.toLowerCase().includes(term)),
    }))
    .filter((s) => s.items.length > 0);

  return (
    <div style={{ maxWidth: 980 }}>
      <h1>Help</h1>
      <div style={{ ...cardStyle, marginBottom: 8 }}>
        <strong style={{ fontSize: 16 }}>IEMAS = Intelligent Email Monitoring &amp; Alert System</strong>
        <p style={{ margin: "6px 0 0", lineHeight: 1.55 }}>
          IEMAS watches your company mailboxes, uses AI to spot customer emails that need a reply, alerts the right employee on
          their PC, reminds them until a reply is actually sent, and escalates to a supervisor if nobody responds — so no
          customer email is forgotten.
        </p>
      </div>
      <p style={mutedStyle}>What each part of IEMAS does. Use “Help for this page” at the top of any page to jump straight to its entry.</p>

      {!term && (
        <>
          <h2 style={sectionTitle}>How IEMAS works</h2>
          <ol style={flowStyle}>
            {FLOW.map((step, i) => (
              <li key={step.title} style={flowStepStyle}>
                <div style={flowNumberStyle}>{i + 1}</div>
                <strong>{step.title}</strong>
                <div style={hintStyle}>{step.text}</div>
              </li>
            ))}
          </ol>

          <h2 style={sectionTitle}>Getting started</h2>
          <ol style={{ paddingLeft: 20, lineHeight: 1.9, margin: 0 }}>
            {SETUP.map((step) => (
              <li key={step.text}>{step.text} — <Link to={step.path}>open</Link></li>
            ))}
          </ol>
        </>
      )}

      <h2 style={sectionTitle}>Menu guide</h2>
      <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search help, e.g. reminder, API key, owner…"
        style={{ width: "100%", maxWidth: 420, marginBottom: 16 }} />

      {sections.map((section) => (
        <section key={section.id} style={{ marginBottom: 24 }}>
          <h3 style={groupTitleStyle}><NavIcon name={section.icon} /> {section.label}</h3>
          <div style={{ display: "grid", gap: 10 }}>
            {section.items.map((item) => (
              <article key={item.path} id={helpAnchor(item.path)} style={cardStyle}>
                <div style={{ display: "flex", justifyContent: "space-between", gap: 12, alignItems: "baseline" }}>
                  <strong style={{ fontSize: 15 }}>{item.label}</strong>
                  <Link to={item.path} style={{ fontSize: 13, whiteSpace: "nowrap" }}>Open page →</Link>
                </div>
                <p style={{ margin: "6px 0 0", lineHeight: 1.55 }}>{item.help}</p>
                {item.tasks && (
                  <ul style={{ margin: "8px 0 0", paddingLeft: 18, color: "var(--color-text-muted)", fontSize: 13, lineHeight: 1.7 }}>
                    {item.tasks.map((task) => <li key={task}>{task}</li>)}
                  </ul>
                )}
              </article>
            ))}
          </div>
        </section>
      ))}
      {sections.length === 0 && <p style={mutedStyle}>Nothing matches “{query}”.</p>}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12, marginTop: 2 };
const sectionTitle: React.CSSProperties = { fontSize: 15, fontWeight: 600, margin: "28px 0 12px" };
const groupTitleStyle: React.CSSProperties = { fontSize: 15, display: "flex", alignItems: "center", gap: 8, margin: "0 0 10px" };
const cardStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: "12px 14px", background: "var(--color-surface)", scrollMarginTop: 16 };
const flowStyle: React.CSSProperties = { listStyle: "none", padding: 0, margin: 0, display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 10 };
const flowStepStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 12, background: "var(--color-surface)" };
const flowNumberStyle: React.CSSProperties = {
  width: 24, height: 24, borderRadius: 999, background: "var(--color-accent)", color: "#fff",
  display: "flex", alignItems: "center", justifyContent: "center", fontSize: 12, fontWeight: 700, marginBottom: 6,
};
