import { useState } from "react";
import { apiClient } from "../../api/client";
import type { SystemSettingDto } from "../../api/types";

type Category = "legit" | "response";
type Direction = "yes" | "no";

/** Each (category, direction) pair is stored as its own setting list on the server. */
const KEYS: Record<Category, Record<Direction, string>> = {
  legit: { yes: "ai.legitimacy_rules", no: "ai.not_legitimate_rules" },
  response: { yes: "ai.response_rules", no: "ai.no_response_rules" },
};

const CATEGORY_LABELS: Record<Category, string> = {
  legit: "Legitimate email rules",
  response: "Needs-a-response rules",
};

const DIRECTION_LABELS: Record<Category, Record<Direction, string>> = {
  legit: { yes: "✓ Is legitimate", no: "✗ Not legitimate" },
  response: { yes: "✓ Needs a response", no: "✗ No response needed" },
};

interface Rule {
  category: Category;
  direction: Direction;
  text: string;
}

interface Props {
  settings: SystemSettingDto[];
  onSaved: (settings: SystemSettingDto[]) => void;
}

/** One table of AI email rules; each rule belongs to Legitimate email rules or Needs-a-response rules. */
export default function EmailRulesTable({ settings, onSaved }: Props) {
  const valueOf = (key: string) => settings.find((s) => s.key === key)?.value ?? "";
  const rules: Rule[] = (["legit", "response"] as Category[]).flatMap((category) =>
    (["yes", "no"] as Direction[]).flatMap((direction) =>
      valueOf(KEYS[category][direction]).split("\n").map((t) => t.trim()).filter(Boolean)
        .map((text) => ({ category, direction, text }))));

  const [form, setForm] = useState<Rule>({ category: "legit", direction: "no", text: "" });
  const [editing, setEditing] = useState<Rule | null>(null);
  const [editForm, setEditForm] = useState<Rule>({ category: "legit", direction: "no", text: "" });
  const [filter, setFilter] = useState<"all" | Category>("all");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  /** Saves only the lists that changed. */
  async function saveRules(next: Rule[], message: string) {
    const values: Record<string, string> = {};
    for (const category of ["legit", "response"] as Category[]) {
      for (const direction of ["yes", "no"] as Direction[]) {
        const key = KEYS[category][direction];
        const joined = next.filter((r) => r.category === category && r.direction === direction).map((r) => r.text).join("\n");
        if (joined !== valueOf(key)) values[key] = joined;
      }
    }
    if (Object.keys(values).length === 0) return true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const res = await apiClient.put<SystemSettingDto[]>("/system-settings", { values });
      onSaved(res.data);
      setNotice(message);
      return true;
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save the rules.");
      return false;
    } finally {
      setBusy(false);
    }
  }

  const same = (a: Rule, b: Rule) => a.category === b.category && a.direction === b.direction && a.text === b.text;

  async function add(e: React.FormEvent) {
    e.preventDefault();
    const text = form.text.trim();
    if (!text) return;
    if (rules.some((r) => r.category === form.category && r.text.toLowerCase() === text.toLowerCase())) {
      setError("That rule is already in this category.");
      return;
    }
    if (await saveRules([...rules, { ...form, text }], `Added to ${CATEGORY_LABELS[form.category]}.`)) setForm({ ...form, text: "" });
  }

  async function saveEdit() {
    if (!editing) return;
    const text = editForm.text.trim();
    if (!text) return;
    if (await saveRules(rules.map((r) => (same(r, editing) ? { ...editForm, text } : r)), "Rule updated.")) setEditing(null);
  }

  async function remove(rule: Rule) {
    if (!window.confirm(`Delete this rule?\n\n${rule.text}`)) return;
    await saveRules(rules.filter((r) => !same(r, rule)), "Rule deleted.");
  }

  async function restoreDefaults() {
    if (!window.confirm("Replace all email rules with the default rules?")) return;
    setBusy(true);
    setError(null);
    try {
      for (const key of Object.values(KEYS).flatMap((k) => Object.values(k))) {
        await apiClient.delete(`/system-settings/${encodeURIComponent(key)}`);
      }
      const res = await apiClient.get<SystemSettingDto[]>("/system-settings");
      onSaved(res.data);
      setNotice("Default rules restored.");
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to restore defaults.");
    } finally {
      setBusy(false);
    }
  }

  const visible = rules.filter((r) => filter === "all" || r.category === filter);
  const anyOverridden = Object.values(KEYS).flatMap((k) => Object.values(k)).some((key) => settings.find((s) => s.key === key)?.isOverridden);

  return (
    <div style={panelStyle}>
      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      <form onSubmit={add} style={{ display: "grid", gridTemplateColumns: "220px 200px 1fr auto", gap: 8, alignItems: "end", marginBottom: 14 }}>
        <div style={fieldStyle}>
          <label>Category</label>
          <select value={form.category} disabled={busy}
            onChange={(e) => setForm({ ...form, category: e.target.value as Category })}>
            <option value="legit">{CATEGORY_LABELS.legit}</option>
            <option value="response">{CATEGORY_LABELS.response}</option>
          </select>
        </div>
        <div style={fieldStyle}>
          <label>Rule means</label>
          <select value={form.direction} disabled={busy}
            onChange={(e) => setForm({ ...form, direction: e.target.value as Direction })}>
            <option value="yes">{DIRECTION_LABELS[form.category].yes}</option>
            <option value="no">{DIRECTION_LABELS[form.category].no}</option>
          </select>
        </div>
        <div style={fieldStyle}>
          <label>Rule</label>
          <input value={form.text} maxLength={300} disabled={busy}
            onChange={(e) => setForm({ ...form, text: e.target.value })}
            placeholder={form.category === "legit" ? "e.g. Newsletters from suppliers" : "e.g. The sender asks for a delivery date"} />
        </div>
        <button type="submit" className="btn-primary" disabled={busy || !form.text.trim()}>+ Add rule</button>
      </form>

      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 8, marginBottom: 6 }}>
        <div style={{ display: "flex", gap: 6 }}>
          {(["all", "legit", "response"] as const).map((f) => (
            <button key={f} className={filter === f ? "btn-primary" : undefined} onClick={() => setFilter(f)} style={{ fontSize: 12 }}>
              {f === "all" ? `All (${rules.length})` : `${CATEGORY_LABELS[f]} (${rules.filter((r) => r.category === f).length})`}
            </button>
          ))}
        </div>
        {anyOverridden && <button onClick={restoreDefaults} disabled={busy} style={{ fontSize: 12 }}>Restore default rules</button>}
      </div>

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={{ ...thStyle, width: 210 }}>Category</th>
            <th style={{ ...thStyle, width: 190 }}>Rule means</th>
            <th style={thStyle}>Rule</th>
            <th style={{ ...thStyle, width: 150 }}></th>
          </tr>
        </thead>
        <tbody>
          {visible.map((rule) => {
            const isEditing = editing !== null && same(editing, rule);
            const tone = rule.direction === "yes" ? "var(--color-success)" : "var(--color-danger)";
            return (
              <tr key={`${rule.category}-${rule.direction}-${rule.text}`}>
                {isEditing ? (
                  <>
                    <td style={tdStyle}>
                      <select value={editForm.category} onChange={(e) => setEditForm({ ...editForm, category: e.target.value as Category })} style={{ width: "100%" }}>
                        <option value="legit">{CATEGORY_LABELS.legit}</option>
                        <option value="response">{CATEGORY_LABELS.response}</option>
                      </select>
                    </td>
                    <td style={tdStyle}>
                      <select value={editForm.direction} onChange={(e) => setEditForm({ ...editForm, direction: e.target.value as Direction })} style={{ width: "100%" }}>
                        <option value="yes">{DIRECTION_LABELS[editForm.category].yes}</option>
                        <option value="no">{DIRECTION_LABELS[editForm.category].no}</option>
                      </select>
                    </td>
                    <td style={tdStyle}>
                      <input value={editForm.text} autoFocus maxLength={300} style={{ width: "100%" }}
                        onChange={(e) => setEditForm({ ...editForm, text: e.target.value })}
                        onKeyDown={(e) => { if (e.key === "Enter") saveEdit(); if (e.key === "Escape") setEditing(null); }} />
                    </td>
                    <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                      <div style={{ display: "flex", gap: 6 }}>
                        <button className="btn-primary" disabled={busy} onClick={saveEdit}>Save</button>
                        <button onClick={() => setEditing(null)}>Cancel</button>
                      </div>
                    </td>
                  </>
                ) : (
                  <>
                    <td style={tdStyle}>{CATEGORY_LABELS[rule.category]}</td>
                    <td style={{ ...tdStyle, color: tone, fontWeight: 600 }}>{DIRECTION_LABELS[rule.category][rule.direction]}</td>
                    <td style={tdStyle}>{rule.text}</td>
                    <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                      <div style={{ display: "flex", gap: 6 }}>
                        <button disabled={busy} onClick={() => { setEditing(rule); setEditForm(rule); }}>Edit</button>
                        <button className="btn-danger" disabled={busy} onClick={() => remove(rule)}>Delete</button>
                      </div>
                    </td>
                  </>
                )}
              </tr>
            );
          })}
          {visible.length === 0 && <tr><td style={tdStyle} colSpan={4}>No rules here — the AI uses its own judgement.</td></tr>}
        </tbody>
      </table>
    </div>
  );
}

const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, background: "var(--color-surface)" };
const fieldStyle: React.CSSProperties = { display: "flex", flexDirection: "column", gap: 4, minWidth: 0 };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, marginBottom: 12 };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px", verticalAlign: "middle" };
