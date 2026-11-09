import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../../api/client";
import type { SetupCheckItemDto } from "../../api/types";

/** Where each part of the system is configured, with a live check of whether it is ready. */
const SECTIONS: Array<{ title: string; items: Array<{ label: string; to: string; what: string }> }> = [
  {
    title: "Email",
    items: [
      { label: "Agent mailboxes", to: "/email-accounts", what: "Add each agent's mailbox (IMAP server, login, password), its owner, and the date monitoring starts." },
      { label: "Outbound email (management escalations)", to: "/outbound-email", what: "The SMTP account the system sends escalation emails from." },
      { label: "Agents (people)", to: "/employees", what: "Add or edit agents, set their supervisor and department, activate or deactivate them." },
    ],
  },
  {
    title: "AI",
    items: [
      { label: "OpenRouter API key and models", to: "/ai-models", what: "Enter or replace the API key (never shown again), test the connection, choose and test the models." },
      { label: "AI usage and cost", to: "/ai-usage", what: "What the AI calls cost." },
    ],
  },
  {
    title: "Deadlines and escalation",
    items: [
      { label: "Business hours, working days, holidays", to: "/reminder-policies", what: "Edit the default policy: time zone, opening hours, weekends and the holiday list. All deadlines count in this business time." },
      { label: "Time limits and thresholds", to: "/system-settings", what: "Initial response (4 h), reminder interval (2 h), reminders before escalation, idle, customer wait, resolution target, AI confidence. Group: Inquiry supervision." },
      { label: "Who management is", to: "/escalation-policies", what: "Escalation levels and who each level emails (supervisor, department manager, a named person or a group)." },
      { label: "Escalation groups", to: "/escalation-groups", what: "Named lists of people for a level to email." },
      { label: "Notification messages", to: "/notifications", what: "The wording of every pop-up the agents receive." },
    ],
  },
  {
    title: "Detection",
    items: [
      { label: "Internal company domains and inquiry phrases", to: "/system-settings", what: "Group: Inquiry detection. Internal domains (sawo.com) never start a new inquiry; phrase lists drive the no-AI screening." },
      { label: "Ignored senders and email rules", to: "/system-configuration", what: "Senders to drop completely, and the rules the AI follows." },
    ],
  },
];

export default function SetupPage() {
  const [checks, setChecks] = useState<SetupCheckItemDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiClient.get<SetupCheckItemDto[]>("/inquiries/setup-check")
      .then((r) => setChecks(r.data))
      .catch(() => setError("Could not run the setup check (requires Supervisor or above)."));
  }, []);

  const ready = checks?.every((c) => c.ok);

  return (
    <div>
      <h1>Setup &amp; Configuration</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13, maxWidth: 820 }}>
        Everything needed to run inquiry supervision is configured from the screens below - no files to edit. The checklist inspects
        the real configuration right now. Secrets such as the API key and mailbox passwords are stored encrypted and are never shown again after saving.
      </p>
      {error && <div className="form-error">{error}</div>}

      {checks && (
        <section style={{ marginBottom: 28 }}>
          <h2 style={{ fontSize: 18 }}>
            Readiness{" "}
            <span style={{ fontSize: 13, color: "#fff", background: ready ? "var(--color-success)" : "var(--color-warning)", borderRadius: 10, padding: "2px 10px" }}>
              {ready ? "Ready" : `${checks.filter((c) => !c.ok).length} to fix`}
            </span>
          </h2>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, maxWidth: 980 }}>
            <tbody>
              {checks.map((c) => (
                <tr key={c.key}>
                  <td style={{ width: 28, fontWeight: 700, color: c.ok ? "var(--color-success)" : "var(--color-danger)" }}>{c.ok ? "✓" : "✗"}</td>
                  <td style={{ width: "32%" }}>{c.label}</td>
                  <td style={{ color: "var(--color-text-muted)", fontSize: 13 }}>{c.detail}</td>
                  <td style={{ width: 90 }}>{!c.ok && <Link to={c.link}>Fix</Link>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {SECTIONS.map((section) => (
        <section key={section.title} style={{ marginBottom: 24 }}>
          <h2 style={{ fontSize: 18 }}>{section.title}</h2>
          <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, maxWidth: 980 }}>
            <tbody>
              {section.items.map((item) => (
                <tr key={item.label}>
                  <td style={{ width: "32%" }}><Link to={item.to}>{item.label}</Link></td>
                  <td style={{ color: "var(--color-text-muted)", fontSize: 13 }}>{item.what}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ))}
    </div>
  );
}
