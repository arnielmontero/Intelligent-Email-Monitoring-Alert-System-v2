import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { SystemSettingDto } from "../../api/types";
import IgnoredSendersEditor from "../system-settings/IgnoredSendersEditor";
import EmailRulesTable from "./EmailRulesTable";
import { CONFIGURATION_GROUPS } from "./groups";

/// Which emails become work: the rules the AI uses to judge legitimacy and whether a reply is
/// needed, plus senders that are always ignored.
export default function SystemConfigurationPage() {
  const [settings, setSettings] = useState<SystemSettingDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [loaded, setLoaded] = useState(false);

  function apply(data: SystemSettingDto[]) {
    const mine = data.filter((s) => CONFIGURATION_GROUPS.includes(s.group));
    setSettings(mine);
  }

  useEffect(() => {
    apiClient.get<SystemSettingDto[]>("/system-settings")
      .then((r) => apply(r.data))
      .catch((err) => setError(err.response?.status === 403
        ? "You do not have permission to manage System Configuration (requires Administrator)."
        : "Failed to load System Configuration."))
      .finally(() => setLoaded(true));
  }, []);

  const byKey = (key: string) => settings.find((s) => s.key === key);
  const toggle = byKey("ai.require_response_for_case");
  const senderList = settings.find((s) => s.type === "SenderList");

  async function save(values: Record<string, string>, message: string) {
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const res = await apiClient.put<SystemSettingDto[]>("/system-settings", { values });
      apply(res.data);
      setNotice(message);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save.");
    } finally {
      setSaving(false);
    }
  }

  if (!loaded) return <p>Loading...</p>;

  return (
    <div style={{ maxWidth: 1100 }}>
      <h1>System Configuration</h1>
      <p style={mutedStyle}>
        Decides which emails become work. For every new email the AI judges whether it is <strong>relevant</strong>, a{" "}
        <strong>legitimate</strong> business email, and whether it <strong>needs a response</strong> — using the rules below.
        Only email that passes becomes a Case with alerts and reminders; the rest is stored with the reason.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      <h2 style={sectionTitle}>Email rules (AI)</h2>
      <p style={hintStyle}>
        Add, edit or delete rules any time — each change saves immediately and applies to email classified from then on
        (email already classified keeps its result). Test them on the Email Classification page with a sample email.
      </p>

      <EmailRulesTable settings={settings} onSaved={apply} />

      {toggle && (
        <div style={{ ...panelStyle, marginTop: 16 }}>
          <label style={{ display: "flex", gap: 10, alignItems: "center", color: "var(--color-text)", fontSize: 14 }}>
            <input
              type="checkbox"
              checked={toggle.value === "true"}
              disabled={saving}
              onChange={(e) => save({ [toggle.key]: e.target.checked ? "true" : "false" },
                e.target.checked ? "Only email that needs a response becomes a Case." : "Every relevant, legitimate email becomes a Case.")}
            />
            <strong>{toggle.label}</strong>
          </label>
          <div style={{ ...hintStyle, marginTop: 4 }}>{toggle.description}</div>
        </div>
      )}

      {senderList && (
        <>
          <h2 style={sectionTitle}>Email filtering</h2>
          <IgnoredSendersEditor setting={senderList} onSaved={apply} />
        </>
      )}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 15, fontWeight: 600, margin: "28px 0 12px" };

const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, background: "var(--color-surface)" };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 16 };
