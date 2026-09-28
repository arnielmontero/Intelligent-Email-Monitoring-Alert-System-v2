import { useEffect, useMemo, useState } from "react";
import { apiClient } from "../../api/client";
import type { SystemSettingDto } from "../../api/types";
import IgnoredSendersEditor from "./IgnoredSendersEditor";
import { CONFIGURATION_GROUPS, HIDDEN_GROUPS } from "../system-configuration/groups";

export default function SystemSettingsPage() {
  const [settings, setSettings] = useState<SystemSettingDto[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
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
  }, []);

  const changed = useMemo(
    () => settings.filter((s) => s.type !== "SenderList" && (draft[s.key] ?? s.value).trim() !== s.value),
    [settings, draft],
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
    if (!window.confirm(`Reset "${setting.label}" to its default (${setting.defaultValue})?`)) return;
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

  return (
    <div>
      <h1>System Settings</h1>
      <p style={mutedStyle}>
        Every change is recorded in the Audit Log with the old and new value. Email rules and ignored senders are on the{" "}
        <a href="/system-configuration">System Configuration</a> page.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      {groups.map(([group, items]) => (
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
          {!items.every((s) => s.type === "SenderList") && <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <tbody>
              {items.map((s) => {
                const dirty = (draft[s.key] ?? s.value).trim() !== s.value;
                return (
                  <tr key={s.key}>
                    <td style={{ ...tdStyle, width: "35%" }}>
                      <div>{s.label}{dirty && <span style={{ color: "var(--color-accent)" }}> •</span>}</div>
                      <div style={mutedStyle}>{s.description}</div>
                    </td>
                    <td style={tdStyle}>
                      <input
                        type={s.type === "Integer" ? "number" : "text"}
                        min={s.min ?? undefined}
                        max={s.max ?? undefined}
                        value={draft[s.key] ?? ""}
                        onChange={(e) => setDraft((d) => ({ ...d, [s.key]: e.target.value }))}
                        list={s.type === "TimeZone" ? "iemas-timezones" : undefined}
                        style={{ width: s.type === "Integer" ? 120 : 280 }}
                      />
                      {s.type === "Integer" && group === "Retention" && <span style={mutedStyle}> days</span>}
                    </td>
                    <td style={{ ...tdStyle, ...mutedStyle, width: "25%" }}>
                      Default: {s.defaultValue || "(empty)"}
                      {s.isOverridden && s.updatedByEmail && (
                        <div>Set by {s.updatedByEmail}{s.updatedAt ? ` on ${new Date(s.updatedAt).toLocaleString()}` : ""}</div>
                      )}
                    </td>
                    <td style={{ ...tdStyle, width: 90 }}>
                      {s.isOverridden && <button onClick={() => reset(s)}>Reset</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>}
        </section>
      ))}

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
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 16 };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "top" };
