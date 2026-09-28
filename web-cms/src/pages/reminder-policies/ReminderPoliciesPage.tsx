import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { ReminderPolicyDto } from "../../api/types";

function timeSpanToHours(ts: string): number {
  const parts = ts.split(":");
  const hours = Number(parts[0] ?? 0);
  const minutes = Number(parts[1] ?? 0);
  return hours + minutes / 60;
}

function hoursToTimeSpan(hours: number): string {
  const totalMinutes = Math.round(hours * 60);
  const h = Math.floor(totalMinutes / 60);
  const m = totalMinutes % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:00`;
}

interface FormState {
  name: string;
  description: string;
  enabled: boolean;
  isDefault: boolean;
  initialDelayHours: number;
  reminderIntervalHours: number;
  maxReminders: number;
  minimumIntervalHours: number;
  restrictToBusinessHours: boolean;
  businessHoursStart: string;
  businessHoursEnd: string;
  excludeWeekends: boolean;
  timeZoneId: string;
  expirationDays: number | "";
}

const emptyForm: FormState = {
  name: "",
  description: "",
  enabled: true,
  isDefault: false,
  initialDelayHours: 4,
  reminderIntervalHours: 24,
  maxReminders: 3,
  minimumIntervalHours: 1,
  restrictToBusinessHours: true,
  businessHoursStart: "08:00",
  businessHoursEnd: "17:00",
  excludeWeekends: true,
  timeZoneId: "UTC",
  expirationDays: 3,
};

export default function ReminderPoliciesPage() {
  const [policies, setPolicies] = useState<ReminderPolicyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [showForm, setShowForm] = useState(false);

  async function load() {
    setLoading(true);
    try {
      const res = await apiClient.get<ReminderPolicyDto[]>("/reminder-policies");
      setPolicies(res.data);
    } catch {
      setError("Failed to load Reminder Policies.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  function startCreate() {
    setForm(emptyForm);
    setEditingId(null);
    setShowForm(true);
  }

  function startEdit(p: ReminderPolicyDto) {
    setForm({
      name: p.name,
      description: p.description ?? "",
      enabled: p.enabled,
      isDefault: p.isDefault,
      initialDelayHours: timeSpanToHours(p.initialDelay),
      reminderIntervalHours: timeSpanToHours(p.reminderInterval),
      maxReminders: p.maxReminders,
      minimumIntervalHours: timeSpanToHours(p.minimumInterval),
      restrictToBusinessHours: p.restrictToBusinessHours,
      businessHoursStart: p.businessHoursStart.slice(0, 5),
      businessHoursEnd: p.businessHoursEnd.slice(0, 5),
      excludeWeekends: p.excludeWeekends,
      timeZoneId: p.timeZoneId,
      expirationDays: p.expirationWindow ? Math.round(timeSpanToHours(p.expirationWindow) / 24) : "",
    });
    setEditingId(p.id);
    setShowForm(true);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    const payload = {
      name: form.name,
      description: form.description || null,
      enabled: form.enabled,
      isDefault: form.isDefault,
      classificationProfileId: null,
      initialDelay: hoursToTimeSpan(form.initialDelayHours),
      reminderInterval: hoursToTimeSpan(form.reminderIntervalHours),
      maxReminders: form.maxReminders,
      minimumInterval: hoursToTimeSpan(form.minimumIntervalHours),
      restrictToBusinessHours: form.restrictToBusinessHours,
      businessHoursStart: `${form.businessHoursStart}:00`,
      businessHoursEnd: `${form.businessHoursEnd}:00`,
      excludeWeekends: form.excludeWeekends,
      timeZoneId: form.timeZoneId,
      expirationWindow: form.expirationDays === "" ? null : hoursToTimeSpan(Number(form.expirationDays) * 24),
      escalationThresholdReminderCount: null,
      holidays: [],
    };

    try {
      if (editingId) {
        await apiClient.put(`/reminder-policies/${editingId}`, payload);
      } else {
        await apiClient.post("/reminder-policies", payload);
      }
      setShowForm(false);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save Reminder Policy.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await apiClient.delete(`/reminder-policies/${id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete Reminder Policy.");
    }
  }

  async function handleToggleEnabled(p: ReminderPolicyDto) {
    setError(null);
    try {
      await apiClient.put(`/reminder-policies/${p.id}`, {
        name: p.name, description: p.description, enabled: !p.enabled, isDefault: p.isDefault,
        classificationProfileId: p.classificationProfileId,
        initialDelay: p.initialDelay, reminderInterval: p.reminderInterval, maxReminders: p.maxReminders,
        minimumInterval: p.minimumInterval, restrictToBusinessHours: p.restrictToBusinessHours,
        businessHoursStart: p.businessHoursStart, businessHoursEnd: p.businessHoursEnd,
        excludeWeekends: p.excludeWeekends, timeZoneId: p.timeZoneId, expirationWindow: p.expirationWindow,
        escalationThresholdReminderCount: p.escalationThresholdReminderCount,
        holidays: p.holidays.map((h) => ({ id: h.id, date: h.date, label: h.label })),
      });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update Reminder Policy.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Reminder Policies</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        How often employees are reminded about a Case that still needs a reply: first reminder, time between reminders,
        maximum number, business hours, weekends and holidays. One policy is the default.
      </p>

      {error && <div className="form-error">{error}</div>}

      <table style={{ width: "100%", borderCollapse: "collapse", marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Default</th>
            <th style={thStyle}>Initial Delay</th>
            <th style={thStyle}>Interval</th>
            <th style={thStyle}>Max Reminders</th>
            <th style={thStyle}>Business Hours</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {policies.map((p) => (
            <tr key={p.id}>
              <td style={tdStyle}>
                {p.name}
                {p.description && <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>{p.description}</div>}
              </td>
              <td style={tdStyle}>{p.enabled ? "Yes" : "No"}</td>
              <td style={tdStyle}>{p.isDefault ? "Yes" : ""}</td>
              <td style={tdStyle}>{timeSpanToHours(p.initialDelay)}h</td>
              <td style={tdStyle}>{timeSpanToHours(p.reminderInterval)}h</td>
              <td style={tdStyle}>{p.maxReminders}</td>
              <td style={tdStyle}>
                {p.restrictToBusinessHours ? `${p.businessHoursStart.slice(0, 5)}-${p.businessHoursEnd.slice(0, 5)}` : "Any time"}
                {p.excludeWeekends ? ", no weekends" : ""}
              </td>
              <td style={tdStyle}>
                <button onClick={() => startEdit(p)} style={{ marginRight: 8 }}>Edit</button>
                <button onClick={() => handleToggleEnabled(p)} style={{ marginRight: 8 }}>{p.enabled ? "Disable" : "Enable"}</button>
                <button onClick={() => handleDelete(p.id)}>Delete</button>
              </td>
            </tr>
          ))}
          {policies.length === 0 && (
            <tr><td style={tdStyle} colSpan={8}>No Reminder Policies configured.</td></tr>
          )}
        </tbody>
      </table>

      {!showForm && <button onClick={startCreate}>Add Reminder Policy</button>}

      {showForm && (
        <form onSubmit={handleSubmit} style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end", maxWidth: 900 }}>
          <h2 style={{ width: "100%", fontSize: 16 }}>{editingId ? "Edit Policy" : "Add Policy"}</h2>
          <div>
            <label>Name</label><br />
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </div>
          <div>
            <label>Description</label><br />
            <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          <div>
            <label>Initial delay (hours)</label><br />
            <input type="number" min={0} step={0.5} value={form.initialDelayHours}
              onChange={(e) => setForm({ ...form, initialDelayHours: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label>Reminder interval (hours)</label><br />
            <input type="number" min={0.1} step={0.5} value={form.reminderIntervalHours}
              onChange={(e) => setForm({ ...form, reminderIntervalHours: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label>Max reminders</label><br />
            <input type="number" min={1} value={form.maxReminders}
              onChange={(e) => setForm({ ...form, maxReminders: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label>Minimum interval (hours)</label><br />
            <input type="number" min={0} step={0.5} value={form.minimumIntervalHours}
              onChange={(e) => setForm({ ...form, minimumIntervalHours: Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label>Expiration (days, blank = never)</label><br />
            <input type="number" min={0} value={form.expirationDays}
              onChange={(e) => setForm({ ...form, expirationDays: e.target.value === "" ? "" : Number(e.target.value) })} style={{ width: 80 }} />
          </div>
          <div>
            <label>Time zone</label><br />
            <input value={form.timeZoneId} onChange={(e) => setForm({ ...form, timeZoneId: e.target.value })} style={{ width: 160 }} />
          </div>
          <div>
            <label><input type="checkbox" checked={form.restrictToBusinessHours}
              onChange={(e) => setForm({ ...form, restrictToBusinessHours: e.target.checked })} /> Restrict to business hours</label>
          </div>
          {form.restrictToBusinessHours && (
            <>
              <div>
                <label>Start</label><br />
                <input type="time" value={form.businessHoursStart} onChange={(e) => setForm({ ...form, businessHoursStart: e.target.value })} />
              </div>
              <div>
                <label>End</label><br />
                <input type="time" value={form.businessHoursEnd} onChange={(e) => setForm({ ...form, businessHoursEnd: e.target.value })} />
              </div>
            </>
          )}
          <div>
            <label><input type="checkbox" checked={form.excludeWeekends}
              onChange={(e) => setForm({ ...form, excludeWeekends: e.target.checked })} /> Exclude weekends</label>
          </div>
          <div>
            <label><input type="checkbox" checked={form.enabled}
              onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled</label>
          </div>
          <div>
            <label><input type="checkbox" checked={form.isDefault}
              onChange={(e) => setForm({ ...form, isDefault: e.target.checked })} /> Default policy</label>
          </div>
          <div style={{ width: "100%" }}>
            <button type="submit" style={{ marginRight: 8 }}>Save</button>
            <button type="button" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px" };
