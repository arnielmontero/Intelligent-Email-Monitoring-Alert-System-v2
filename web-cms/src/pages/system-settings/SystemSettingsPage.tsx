import { useEffect, useMemo, useState } from "react";
import { apiClient } from "../../api/client";
import type { ReminderPolicyDto, SystemSettingDto } from "../../api/types";
import IgnoredSendersEditor from "./IgnoredSendersEditor";
import SupervisionTimeline, { type SupervisionValues } from "./SupervisionTimeline";
import { CONFIGURATION_GROUPS, HIDDEN_GROUPS } from "../system-configuration/groups";

const SUPERVISION_GROUP = "Inquiry supervision";

const K = {
  enabled: "inquiry.supervision_enabled",
  firstReply: "inquiry.first_response_business_hours",
  interval: "inquiry.reminder_interval_business_hours",
  reminders: "inquiry.reminders_before_escalation",
  nextLevel: "inquiry.escalation_repeat_business_days",
  followUp: "inquiry.idle_business_days",
  customerWait: "inquiry.customer_wait_calendar_days",
  internalWait: "inquiry.internal_wait_business_days",
  target: "inquiry.resolution_target_business_days",
  aiEnabled: "inquiry.ai_resolution_enabled",
  aiConfidence: "inquiry.ai_resolution_min_confidence_percent",
  testMode: "inquiry.test_clock_minutes",
  replyRetention: "inquiry.agent_reply_retention_days",
};

/** The supervision settings, grouped by what they decide rather than listed in one long run. */
const SUPERVISION_SECTIONS: { title: string; hint?: string; keys: string[] }[] = [
  { title: "", keys: [K.enabled] },
  {
    title: "Deadlines, reminders and escalation",
    hint: "How quickly the agent must answer, how often they are reminded, and when management is told.",
    keys: [K.firstReply, K.interval, K.reminders, K.nextLevel, K.followUp],
  },
  {
    title: "Waiting periods and targets",
    hint: "What happens when the ball is in someone else's court, and when an inquiry counts as overdue.",
    keys: [K.customerWait, K.internalWait, K.target],
  },
  { title: "AI and testing", keys: [K.aiEnabled, K.aiConfidence, K.testMode, K.replyRetention] },
];

function parseHours(timeSpan: string): number {
  const [h, m] = timeSpan.split(":").map(Number);
  return (h || 0) + (m || 0) / 60;
}

function clock(timeSpan: string) {
  return timeSpan.slice(0, 5);
}

/** What "business time" currently means: it comes from the enabled default Reminder Policy. */
function describeBusinessTime(policy: ReminderPolicyDto | null | undefined): { label: string; text: string; dayHours: number; warning: boolean } {
  if (!policy) {
    return {
      label: "Business time:",
      text: "08:00 to 17:00, Monday to Friday, in the default time zone. (No default Reminder Policy is enabled, so this built-in working day is used.)",
      dayHours: 9,
      warning: false,
    };
  }
  const days = policy.excludeWeekends ? "Monday to Friday" : "every day, weekends included";
  const holidays = policy.holidays.length > 0 ? `, ${policy.holidays.length} holiday${policy.holidays.length === 1 ? "" : "s"} skipped` : "";
  if (!policy.restrictToBusinessHours) {
    return {
      label: "Check your business time:",
      text: `working hours are not restricted (${days}${holidays}), so "business hours" count around the clock, nights included.`,
      dayHours: 24,
      warning: true,
    };
  }
  const start = parseHours(policy.businessHoursStart);
  const end = parseHours(policy.businessHoursEnd);
  return {
    label: "Business time:",
    text: `${clock(policy.businessHoursStart)} to ${clock(policy.businessHoursEnd)}, ${days} (${policy.timeZoneId})${holidays}.`,
    dayHours: Math.max(1, end - start),
    warning: false,
  };
}

const SINGULAR: Record<string, string> = {
  "business hours": "business hour",
  "business days": "business day",
  "calendar days": "calendar day",
  reminders: "reminder",
  days: "day",
  points: "point",
  characters: "character",
};

/** "1 business day", "2 business days": the unit agrees with the number beside it. */
function unitFor(unit: string | null | undefined, n: number) {
  if (!unit) return "";
  return n === 1 ? SINGULAR[unit] ?? unit : unit;
}

function friendlyDefault(s: SystemSettingDto) {
  if (s.type === "RuleList" || s.type === "SenderList") return s.defaultValue ? `${s.defaultValue.split("\n").length} entries` : "(empty)";
  if (s.type === "Boolean") return s.defaultValue === "true" ? "On" : "Off";
  if (!s.defaultValue) return "(empty)";
  return s.unit ? `${s.defaultValue} ${unitFor(s.unit, Number(s.defaultValue))}` : s.defaultValue;
}

export default function SystemSettingsPage() {
  const [settings, setSettings] = useState<SystemSettingDto[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [policy, setPolicy] = useState<ReminderPolicyDto | null | undefined>(undefined);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  function apply(data: SystemSettingDto[]) {
    const mine = data.filter((s) => !CONFIGURATION_GROUPS.includes(s.group) && !HIDDEN_GROUPS.includes(s.group));
    setSettings(mine);
    setDraft(Object.fromEntries(mine.map((s) => [s.key, s.value])));
  }

  async function load() {
    try {
      const res = await apiClient.get<SystemSettingDto[]>("/system-settings");
      apply(res.data);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to manage System Settings (requires Administrator)."
        : "Failed to load System Settings.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // The working day the supervision timers count in lives on the default Reminder Policy; show it here.
    apiClient.get<ReminderPolicyDto[]>("/reminder-policies")
      .then((r) => setPolicy(r.data.find((p) => p.enabled && p.isDefault) ?? null))
      .catch(() => setPolicy(undefined));
  }, []);

  // A group made only of sender lists (Email filtering) has its own editor; a sender list inside a mixed
  // group (Inquiry detection: internal domains) is edited here like the other settings.
  const ownEditorKeys = useMemo(() => {
    const byGroup = new Map<string, SystemSettingDto[]>();
    settings.forEach((s) => byGroup.set(s.group, [...(byGroup.get(s.group) ?? []), s]));
    return new Set(Array.from(byGroup.values()).filter((items) => items.every((s) => s.type === "SenderList")).flat().map((s) => s.key));
  }, [settings]);

  const changed = useMemo(
    () => settings.filter((s) => !ownEditorKeys.has(s.key) && (draft[s.key] ?? s.value).trim() !== s.value),
    [settings, draft, ownEditorKeys],
  );

  const groups = useMemo(() => {
    const map = new Map<string, SystemSettingDto[]>();
    settings.forEach((s) => map.set(s.group, [...(map.get(s.group) ?? []), s]));
    return Array.from(map.entries());
  }, [settings]);

  async function save() {
    setError(null);
    setNotice(null);
    setSaving(true);
    try {
      const values = Object.fromEntries(changed.map((s) => [s.key, draft[s.key]]));
      const res = await apiClient.put<SystemSettingDto[]>("/system-settings", { values });
      apply(res.data);
      setNotice(`Saved ${changed.length} setting${changed.length === 1 ? "" : "s"}.`);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save settings.");
    } finally {
      setSaving(false);
    }
  }

  async function reset(setting: SystemSettingDto) {
    if (!window.confirm(`Reset "${setting.label}" to its default (${friendlyDefault(setting)})?`)) return;
    setError(null);
    setNotice(null);
    try {
      await apiClient.delete(`/system-settings/${encodeURIComponent(setting.key)}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to reset setting.");
    }
  }

  if (loading) return <p>Loading...</p>;

  const byKey = new Map(settings.map((s) => [s.key, s]));
  // The number being typed, falling back to the saved/default value while the box is empty or invalid.
  const num = (key: string) => {
    const s = byKey.get(key);
    const raw = draft[key];
    const n = raw === undefined || raw.trim() === "" ? NaN : Number(raw);
    return Number.isFinite(n) && n >= 0 ? n : Number(s?.value ?? s?.defaultValue ?? 0);
  };
  const testMode = (draft[K.testMode] ?? byKey.get(K.testMode)?.value) === "true";
  const business = describeBusinessTime(policy);
  const supervisionOn = (draft[K.enabled] ?? byKey.get(K.enabled)?.value) === "true";
  const timelineValues: SupervisionValues = {
    firstReplyHours: num(K.firstReply),
    reminderIntervalHours: num(K.interval),
    reminders: Math.min(10, Math.round(num(K.reminders))),
    nextLevelDays: num(K.nextLevel),
    followUpDays: num(K.followUp),
    customerWaitDays: num(K.customerWait),
    internalWaitDays: num(K.internalWait),
    resolutionTargetDays: num(K.target),
  };

  function renderRow(s: SystemSettingDto) {
    const dirty = (draft[s.key] ?? s.value).trim() !== s.value;
    const value = draft[s.key] ?? "";
    const range = s.type === "Integer" && s.min !== null && s.max !== null ? `${s.min} to ${s.max}${s.unit ? ` ${s.unit}` : ""}` : null;
    return (
      <tr key={s.key}>
        <td style={{ ...tdStyle, width: "38%" }}>
          <div style={{ fontWeight: 500 }}>{s.label}{dirty && <span style={{ color: "var(--color-accent)" }} title="Changed, not saved yet"> •</span>}</div>
          <div style={mutedStyle}>{s.description}</div>
        </td>
        <td style={tdStyle}>
          {s.type === "Boolean" ? (
            <label style={{ display: "inline-flex", gap: 8, alignItems: "center" }}>
              <input
                type="checkbox"
                checked={(draft[s.key] ?? s.value) === "true"}
                onChange={(e) => setDraft((d) => ({ ...d, [s.key]: e.target.checked ? "true" : "false" }))}
              />
              {(draft[s.key] ?? s.value) === "true" ? "On" : "Off"}
            </label>
          ) : s.type === "RuleList" || s.type === "SenderList" || s.type === "LongText" ? (
            <textarea
              rows={Math.min(10, Math.max(3, value.split("\n").length + 1))}
              value={value}
              placeholder="One per line"
              onChange={(e) => setDraft((d) => ({ ...d, [s.key]: e.target.value }))}
              style={{ width: 380 }}
            />
          ) : (
            <>
              <span style={{ display: "inline-flex", alignItems: "center", gap: 8 }}>
                <input
                  type={s.type === "Integer" ? "number" : "text"}
                  min={s.min ?? undefined}
                  max={s.max ?? undefined}
                  value={value}
                  aria-label={s.unit ? `${s.label} (${s.unit})` : s.label}
                  onChange={(e) => setDraft((d) => ({ ...d, [s.key]: e.target.value }))}
                  list={s.type === "TimeZone" ? "iemas-timezones" : undefined}
                  style={{ width: s.type === "Integer" ? 90 : 280 }}
                />
                {s.type === "Integer" && s.unit && <span style={unitStyle}>{unitFor(s.unit, Number(value))}</span>}
              </span>
              {range && <div style={mutedStyle}>Allowed: {range}</div>}
            </>
          )}
        </td>
        <td style={{ ...tdStyle, ...mutedStyle, width: "22%" }}>
          Default: {friendlyDefault(s)}
          {s.isOverridden && s.updatedByEmail && (
            <div>Set by {s.updatedByEmail}{s.updatedAt ? ` on ${new Date(s.updatedAt).toLocaleString()}` : ""}</div>
          )}
        </td>
        <td style={{ ...tdStyle, width: 90 }}>
          {s.isOverridden && <button onClick={() => reset(s)} title={`Go back to ${friendlyDefault(s)}`}>Reset</button>}
        </td>
      </tr>
    );
  }

  function table(rows: SystemSettingDto[]) {
    return (
      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
        <tbody>{rows.map(renderRow)}</tbody>
      </table>
    );
  }

  return (
    <div>
      <h1>System Settings</h1>
      <p style={mutedStyle}>
        Every change is recorded in the Audit Log with the old and new value. Email rules and ignored senders are on the{" "}
        <a href="/system-configuration">Email Rules</a> page.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      {testMode && (
        <div role="alert" style={testBannerStyle}>
          <strong>TEST MODE is on.</strong> Every supervision time below runs 60 times faster: 1 hour counts as 1 minute and 1 day as 24 minutes,
          and business hours, weekends and holidays are ignored. Real customers would be reminded and escalated within minutes.
          Turn it off before real use.
        </div>
      )}

      {groups.map(([group, items]) => {
        const isSupervision = group === SUPERVISION_GROUP;
        return (
          <section key={group} style={{ marginBottom: 28 }}>
            <h2 style={{ fontSize: 18 }}>{group}</h2>

            {items.every((s) => s.type === "SenderList") && items.map((s) => (
              <IgnoredSendersEditor key={s.key} setting={s} onSaved={apply} />
            ))}

            {group === "Retention" && (
              <p style={mutedStyle}>
                Retention periods are recorded policy only. No automatic clean-up acts on them yet — they don't delete data.
              </p>
            )}

            {isSupervision
              ? SUPERVISION_SECTIONS.map((section, i) => {
                  const rows = section.keys.map((k) => byKey.get(k)).filter((s): s is SystemSettingDto => !!s);
                  // Anything added to this group later still shows up, in the last section.
                  const extra = i === SUPERVISION_SECTIONS.length - 1
                    ? items.filter((s) => !SUPERVISION_SECTIONS.some((sec) => sec.keys.includes(s.key)))
                    : [];
                  const all = [...rows, ...extra];
                  if (all.length === 0) return null;
                  return (
                    <div key={section.title || "switch"} style={{ margin: "0 0 14px" }}>
                      {section.title && <h3 style={{ fontSize: 15, margin: "18px 0 2px" }}>{section.title}</h3>}
                      {section.hint && <p style={{ ...mutedStyle, margin: "0 0 4px" }}>{section.hint}</p>}
                      {table(all)}
                      {/* After the on/off switch: what "business time" means here, and the live step-by-step picture. */}
                      {i === 0 && (
                        <div style={{ marginTop: 14 }}>
                          <div style={{ ...infoBox, ...(business.warning ? { borderColor: "var(--color-warning)" } : null) }}>
                            <div>
                              <strong>{business.label}</strong> {business.text}{" "}
                              <a href="/reminder-policies">Change it on Reminder Policies</a>.
                            </div>
                            <div style={{ ...mutedStyle, marginTop: 6 }}>
                              <strong>Business hours / business days</strong> only count working time. <strong>Calendar days</strong> count every day, weekends included.
                            </div>
                          </div>
                          {supervisionOn
                            ? <SupervisionTimeline values={timelineValues} businessDayHours={business.dayHours} testMode={testMode} />
                            : <p style={mutedStyle}>Supervision is off, so these timers are not used: the older reminder and escalation engines apply.</p>}
                        </div>
                      )}
                    </div>
                  );
                })
              : !items.every((s) => s.type === "SenderList") && table(items)}
          </section>
        );
      })}

      <datalist id="iemas-timezones">
        {supportedTimeZones().map((tz) => <option key={tz} value={tz} />)}
      </datalist>

      <div style={{ display: "flex", gap: 8 }}>
        <button disabled={saving || changed.length === 0} onClick={save}>
          {saving ? "Saving..." : `Save changes${changed.length ? ` (${changed.length})` : ""}`}
        </button>
        {changed.length > 0 && (
          <button onClick={() => setDraft(Object.fromEntries(settings.map((s) => [s.key, s.value])))}>Discard</button>
        )}
      </div>
    </div>
  );
}

function supportedTimeZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  return intl.supportedValuesOf ? ["UTC", ...intl.supportedValuesOf("timeZone")] : ["UTC"];
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const unitStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13, whiteSpace: "nowrap" };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 16 };
const testBannerStyle: React.CSSProperties = { background: "var(--color-danger)", color: "#fff", padding: "10px 14px", borderRadius: 6, margin: "0 0 18px", maxWidth: 900, fontSize: 13, lineHeight: 1.5 };
const infoBox: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: "10px 14px", margin: "0 0 14px", maxWidth: 900, background: "var(--color-surface)", fontSize: 13 };
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
