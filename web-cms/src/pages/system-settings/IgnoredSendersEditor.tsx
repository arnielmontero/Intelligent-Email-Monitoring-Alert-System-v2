import { useMemo, useState } from "react";
import { apiClient } from "../../api/client";
import type { SystemSettingDto } from "../../api/types";

interface Props {
  setting: SystemSettingDto;
  onSaved: (settings: SystemSettingDto[]) => void;
}

/** Add / edit / delete ignored domains and addresses. Every change saves immediately. */
export default function IgnoredSendersEditor({ setting, onSaved }: Props) {
  const entries = useMemo(() => setting.value.split("\n").map((e) => e.trim()).filter(Boolean), [setting.value]);
  const [newEntry, setNewEntry] = useState("");
  const [editing, setEditing] = useState<string | null>(null);
  const [editValue, setEditValue] = useState("");
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function saveList(next: string[], message: string) {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const res = await apiClient.put<SystemSettingDto[]>("/system-settings", { values: { [setting.key]: next.join("\n") } });
      onSaved(res.data);
      setNotice(message);
      return true;
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save the ignored senders list.");
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function add(e: React.FormEvent) {
    e.preventDefault();
    const value = newEntry.trim();
    if (!value) return;
    if (entries.includes(value.toLowerCase().replace(/^@|^\*\./, ""))) {
      setError(`"${value}" is already on the list.`);
      return;
    }
    if (await saveList([...entries, value], `Added ${value}. Its email is now ignored.`)) setNewEntry("");
  }

  async function saveEdit(original: string) {
    const value = editValue.trim();
    if (!value || value === original) {
      setEditing(null);
      return;
    }
    if (await saveList(entries.map((e) => (e === original ? value : e)), `Changed ${original} to ${value}.`)) setEditing(null);
  }

  async function remove(entry: string) {
    if (!window.confirm(`Stop ignoring ${entry}? Its waiting email goes back into processing.`)) return;
    await saveList(entries.filter((e) => e !== entry), `Removed ${entry}.`);
  }

  const visible = entries.filter((e) => e.includes(filter.trim().toLowerCase()));

  return (
    <div style={panelStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, flexWrap: "wrap", alignItems: "baseline" }}>
        <div>
          <strong>{setting.label}</strong> <span style={mutedStyle}>({entries.length})</span>
          <div style={{ ...mutedStyle, maxWidth: 760 }}>{setting.description}</div>
        </div>
        {setting.isOverridden && setting.updatedByEmail && (
          <div style={mutedStyle}>Last changed by {setting.updatedByEmail}{setting.updatedAt ? ` on ${new Date(setting.updatedAt).toLocaleString()}` : ""}</div>
        )}
      </div>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      <form onSubmit={add} style={{ display: "flex", gap: 8, margin: "12px 0", flexWrap: "wrap" }}>
        <input
          value={newEntry}
          onChange={(e) => setNewEntry(e.target.value)}
          placeholder="Domain (sawo.com) or address (noreply@shop.com)"
          style={{ flex: "1 1 320px", maxWidth: 420 }}
          disabled={busy}
        />
        <button type="submit" className="btn-primary" disabled={busy || !newEntry.trim()}>+ Add</button>
        {entries.length > 8 && (
          <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter list…" style={{ marginLeft: "auto", width: 220 }} />
        )}
      </form>

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>Domain / address</th>
            <th style={thStyle}>Type</th>
            <th style={thStyle}>What is ignored</th>
            <th style={{ ...thStyle, width: 170 }}></th>
          </tr>
        </thead>
        <tbody>
          {visible.map((entry) => {
            const isAddress = entry.includes("@");
            return (
              <tr key={entry}>
                <td style={tdStyle}>
                  {editing === entry ? (
                    <input
                      value={editValue}
                      autoFocus
                      onChange={(e) => setEditValue(e.target.value)}
                      onKeyDown={(e) => {
                        if (e.key === "Enter") saveEdit(entry);
                        if (e.key === "Escape") setEditing(null);
                      }}
                      style={{ width: "100%", maxWidth: 360 }}
                    />
                  ) : (
                    <code style={{ fontSize: 14 }}>{entry}</code>
                  )}
                </td>
                <td style={tdStyle}>{isAddress ? "Address" : "Domain"}</td>
                <td style={{ ...tdStyle, ...mutedStyle }}>
                  {isAddress ? `Only ${entry}` : `Anyone @${entry} and its subdomains (e.g. @mail.${entry})`}
                </td>
                <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                  {editing === entry ? (
                    <div style={{ display: "flex", gap: 6 }}>
                      <button className="btn-primary" disabled={busy} onClick={() => saveEdit(entry)}>Save</button>
                      <button onClick={() => setEditing(null)}>Cancel</button>
                    </div>
                  ) : (
                    <div style={{ display: "flex", gap: 6 }}>
                      <button disabled={busy} onClick={() => { setEditing(entry); setEditValue(entry); }}>Edit</button>
                      <button className="btn-danger" disabled={busy} onClick={() => remove(entry)}>Delete</button>
                    </div>
                  )}
                </td>
              </tr>
            );
          })}
          {entries.length === 0 && (
            <tr><td style={tdStyle} colSpan={4}>No ignored senders — every sender's email is processed.</td></tr>
          )}
          {entries.length > 0 && visible.length === 0 && (
            <tr><td style={tdStyle} colSpan={4}>Nothing matches “{filter}”.</td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, background: "var(--color-surface)" };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, margin: "8px 0" };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "middle" };
