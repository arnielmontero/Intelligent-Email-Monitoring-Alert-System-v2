/**
 * "What happens, step by step" for the Inquiry supervision timers. It redraws from the values being typed (not only
 * the saved ones), so an administrator sees the effect of a change before saving it. The arithmetic mirrors the
 * supervision engine: the first-reply deadline, then a reminder every interval, then management one interval after
 * the last reminder, then the next escalation level after the repeat period.
 */
export interface SupervisionValues {
  firstReplyHours: number;
  reminderIntervalHours: number;
  reminders: number;
  nextLevelDays: number;
  followUpDays: number;
  customerWaitDays: number;
  internalWaitDays: number;
  resolutionTargetDays: number;
}

interface Props {
  values: SupervisionValues;
  /** Length of one business day in hours (24 when working hours are not restricted). */
  businessDayHours: number;
  /** Test mode: every hour is a minute and every day is 24 minutes. */
  testMode: boolean;
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;
const round1 = (n: number) => Number(n.toFixed(1));

export default function SupervisionTimeline({ values: v, businessDayHours, testMode }: Props) {
  const hours = (h: number) => (testMode ? plural(h, "minute") : plural(h, "business hour"));
  const days = (d: number, kind: "business" | "calendar") =>
    testMode ? plural(d * 24, "minute") : plural(d, `${kind} day`);

  // Time from the email arriving, in business hours, for each step on the agent's ladder.
  const after = (h: number) => {
    if (testMode || h < businessDayHours) return hours(h);
    return `${hours(h)} (about ${round1(h / businessDayHours)} working days)`;
  };

  const steps: { at: string; text: string; tone: "start" | "warn" | "bad" }[] = [
    { at: "Start", text: "A customer's email arrives and the agent is notified.", tone: "start" },
    { at: `After ${after(v.firstReplyHours)}`, text: "Deadline for the agent's first reply. If nobody has replied, the reminders begin.", tone: "warn" },
  ];
  for (let k = 1; k <= v.reminders; k++) {
    const last = k === v.reminders;
    steps.push({
      at: `After ${after(v.firstReplyHours + k * v.reminderIntervalHours)}`,
      text: `Reminder ${k} of ${v.reminders} to the agent${last ? ": the final warning before management is told." : "."}`,
      tone: "warn",
    });
  }
  steps.push({
    at: `After ${after(v.firstReplyHours + (v.reminders + 1) * v.reminderIntervalHours)}`,
    text: v.reminders === 0
      ? "No reminders are sent: management is told (escalation level 1)."
      : "Still unanswered: management is told (escalation level 1).",
    tone: "bad",
  });
  steps.push({
    at: `${days(v.nextLevelDays, "business")} later`,
    text: "Still neglected: the next escalation level is told, and again for each further level of the escalation policy.",
    tone: "bad",
  });

  const dot = { start: "var(--color-accent)", warn: "var(--color-warning)", bad: "var(--color-danger)" } as const;

  return (
    <div style={panel}>
      <h3 style={{ fontSize: 15, margin: "0 0 2px" }}>What happens, step by step</h3>
      <p style={muted}>
        This redraws as you type, before you save. Times count from the moment the customer's email arrives
        {testMode ? "." : <>; <strong>business</strong> hours and days skip nights, weekends and holidays.</>}
      </p>

      <ol style={{ listStyle: "none", margin: "12px 0 0", padding: 0 }}>
        {steps.map((s, i) => (
          <li key={i} style={{ display: "grid", gridTemplateColumns: "230px 1fr", gap: 14, padding: "6px 0", alignItems: "start" }}>
            <span style={{ display: "flex", alignItems: "center", gap: 8, fontWeight: 600, fontSize: 13 }}>
              <span aria-hidden="true" style={{ width: 10, height: 10, borderRadius: 999, background: dot[s.tone], flexShrink: 0 }} />
              {s.at}
            </span>
            <span style={{ fontSize: 13 }}>{s.text}</span>
          </li>
        ))}
      </ol>

      <h4 style={{ fontSize: 13, margin: "16px 0 6px" }}>Other situations</h4>
      <ul style={{ margin: 0, paddingLeft: 18, fontSize: 13, lineHeight: 1.6 }}>
        <li>
          The customer writes again, or the conversation stalls, and nobody answers for <strong>{days(v.followUpDays, "business")}</strong>:
          the same reminder and escalation steps begin.
        </li>
        <li>
          The agent has replied and the customer stays silent for <strong>{days(v.customerWaitDays, "calendar")}</strong>:
          the agent is told a follow-up may be needed. The inquiry is never closed automatically.
        </li>
        <li>
          The agent is waiting on another department and nothing happens for <strong>{days(v.internalWaitDays, "business")}</strong>:
          the agent is asked for an update.
        </li>
        <li>
          Still unresolved after <strong>{days(v.resolutionTargetDays, "business")}</strong>: marked overdue, and management is
          told one reminder interval later if the agent still owes the next step.
        </li>
      </ul>
    </div>
  );
}

const panel: React.CSSProperties = {
  border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, background: "var(--color-surface)", margin: "0 0 18px", maxWidth: 900,
};
const muted: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13, margin: 0 };
