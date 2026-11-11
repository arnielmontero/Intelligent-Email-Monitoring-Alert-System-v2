import { Fragment, useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type {
  ClassificationPriority,
  EmployeeDto,
  EscalationGroupDto,
  EscalationLevelDto,
  EscalationPolicyDto,
  EscalationRecipientType,
  SaveEscalationLevelRequest,
  SaveEscalationPolicyRequest,
  TestEscalationPolicyResult,
} from "../../api/types";
import { CLASSIFICATION_PRIORITY_LABELS, ESCALATION_RECIPIENT_TYPE_LABELS } from "../../api/types";
import { InfoTip, TipLabel } from "../../components/help/Tooltip";

function timeSpanToHours(ts: string): number {
  // .NET serializes TimeSpan as "d.hh:mm:ss" for durations >= 1 day, "hh:mm:ss" otherwise.
  let days = 0;
  let timeStr = ts;
  const dotIdx = ts.indexOf(".");
  if (dotIdx !== -1 && dotIdx < ts.indexOf(":")) {
    days = Number(ts.substring(0, dotIdx));
    timeStr = ts.substring(dotIdx + 1);
  }
  const parts = timeStr.split(":");
  const hours = Number(parts[0] ?? 0);
  const minutes = Number(parts[1] ?? 0);
  return days * 24 + hours + minutes / 60;
}

function hoursToTimeSpan(hours: number): string {
  const totalMinutes = Math.round(hours * 60);
  const days = Math.floor(totalMinutes / (60 * 24));
  const remainingMinutes = totalMinutes % (60 * 24);
  const h = Math.floor(remainingMinutes / 60);
  const m = remainingMinutes % 60;
  if (days > 0) {
    return `${days}.${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:00`;
  }
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:00`;
}

interface LevelFormState {
  level: number;
  delayHours: number;
  recipientType: EscalationRecipientType;
  specificEmployeeId: string;
  specificGroupId: string;
}

interface FormState {
  name: string;
  description: string;
  enabled: boolean;
  isDefault: boolean;
  classificationProfileId: string;
  categories: string;
  priority: "" | ClassificationPriority;
  triggerReminderCount: number;
  gracePeriodHours: number;
  cooldownHours: number;
  maximumLevel: number;
  channel: string;
  levels: LevelFormState[];
}

const emptyLevel: LevelFormState = {
  level: 1,
  delayHours: 0,
  recipientType: 1,
  specificEmployeeId: "",
  specificGroupId: "",
};

const emptyForm: FormState = {
  name: "",
  description: "",
  enabled: true,
  isDefault: false,
  classificationProfileId: "",
  categories: "",
  priority: "",
  triggerReminderCount: 3,
  gracePeriodHours: 48,
  cooldownHours: 24,
  maximumLevel: 3,
  channel: "Email",
  levels: [{ ...emptyLevel }],
};

export default function EscalationPoliciesPage() {
  const [policies, setPolicies] = useState<EscalationPolicyDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [groups, setGroups] = useState<EscalationGroupDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [showForm, setShowForm] = useState(false);

  const [testCaseIds, setTestCaseIds] = useState<Record<string, string>>({});
  const [testResults, setTestResults] = useState<Record<string, TestEscalationPolicyResult | null>>({});
  const [testRunning, setTestRunning] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    try {
      const [policiesRes, employeesRes, groupsRes] = await Promise.all([
        apiClient.get<EscalationPolicyDto[]>("/escalation-policies"),
        apiClient.get<EmployeeDto[]>("/employees"),
        apiClient.get<EscalationGroupDto[]>("/escalation-groups"),
      ]);
      setPolicies(policiesRes.data);
      setEmployees(employeesRes.data);
      setGroups(groupsRes.data);
    } catch {
      setError("Failed to load Escalation Policies.");
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

  function startEdit(p: EscalationPolicyDto) {
    setForm({
      name: p.name,
      description: p.description ?? "",
      enabled: p.enabled,
      isDefault: p.isDefault,
      classificationProfileId: p.classificationProfileId ?? "",
      categories: p.categories ?? "",
      priority: p.priority ?? "",
      triggerReminderCount: p.triggerReminderCount,
      gracePeriodHours: timeSpanToHours(p.gracePeriod),
      cooldownHours: timeSpanToHours(p.cooldown),
      maximumLevel: p.maximumLevel,
      channel: p.channel,
      levels: p.levels.length > 0
        ? p.levels.map((l: EscalationLevelDto) => ({
            level: l.level,
            delayHours: timeSpanToHours(l.delayAfterPreviousLevel),
            recipientType: l.recipientType,
            specificEmployeeId: l.specificEmployeeId ?? "",
            specificGroupId: l.specificGroupId ?? "",
          }))
        : [{ ...emptyLevel }],
    });
    setEditingId(p.id);
    setShowForm(true);
  }

  function addLevelRow() {
    setForm({ ...form, levels: [...form.levels, { ...emptyLevel, level: form.levels.length + 1 }] });
  }

  function removeLevelRow(index: number) {
    setForm({ ...form, levels: form.levels.filter((_, i) => i !== index) });
  }

  function updateLevelRow(index: number, updates: Partial<LevelFormState>) {
    setForm({
      ...form,
      levels: form.levels.map((l, i) => (i === index ? { ...l, ...updates } : l)),
    });
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    const levels: SaveEscalationLevelRequest[] = form.levels.map((l) => ({
      level: l.level,
      delayAfterPreviousLevel: hoursToTimeSpan(l.delayHours),
      recipientType: l.recipientType,
      specificEmployeeId: l.recipientType === 3 ? (l.specificEmployeeId || null) : null,
      specificGroupId: l.recipientType === 4 ? (l.specificGroupId || null) : null,
    }));

    const payload: SaveEscalationPolicyRequest = {
      name: form.name,
      description: form.description || null,
      enabled: form.enabled,
      isDefault: form.isDefault,
      classificationProfileId: form.classificationProfileId || null,
      categories: form.categories || null,
      priority: form.priority === "" ? null : form.priority,
      triggerReminderCount: form.triggerReminderCount,
      gracePeriod: hoursToTimeSpan(form.gracePeriodHours),
      cooldown: hoursToTimeSpan(form.cooldownHours),
      maximumLevel: form.maximumLevel,
      channel: form.channel,
      levels,
    };

    try {
      if (editingId) {
        await apiClient.put(`/escalation-policies/${editingId}`, payload);
      } else {
        await apiClient.post("/escalation-policies", payload);
      }
      setShowForm(false);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save Escalation Policy.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await apiClient.delete(`/escalation-policies/${id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete Escalation Policy.");
    }
  }

  async function handleToggleEnabled(p: EscalationPolicyDto) {
    setError(null);
    try {
      const payload: SaveEscalationPolicyRequest = {
        name: p.name,
        description: p.description,
        enabled: !p.enabled,
        isDefault: p.isDefault,
        classificationProfileId: p.classificationProfileId,
        categories: p.categories,
        priority: p.priority,
        triggerReminderCount: p.triggerReminderCount,
        gracePeriod: p.gracePeriod,
        cooldown: p.cooldown,
        maximumLevel: p.maximumLevel,
        channel: p.channel,
        levels: p.levels.map((l) => ({
          level: l.level,
          delayAfterPreviousLevel: l.delayAfterPreviousLevel,
          recipientType: l.recipientType,
          specificEmployeeId: l.specificEmployeeId,
          specificGroupId: l.specificGroupId,
        })),
      };
      await apiClient.put(`/escalation-policies/${p.id}`, payload);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update Escalation Policy.");
    }
  }

  async function handleTest(policyId: string) {
    setError(null);
    const caseId = testCaseIds[policyId];
    if (!caseId) {
      setError("Enter a Case ID to test.");
      return;
    }
    setTestRunning(policyId);
    try {
      const res = await apiClient.post<TestEscalationPolicyResult>(`/escalation-policies/${policyId}/test?caseId=${encodeURIComponent(caseId)}`);
      setTestResults({ ...testResults, [policyId]: res.data });
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to test policy.");
    } finally {
      setTestRunning(null);
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Escalation Policies</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        When a Case still has no reply after its reminders: after how many reminders, the grace period, and up to 3
        levels with a recipient each. The Case is marked Escalated and the owner gets an Escalation pop-up; the
        recipient is recorded in Escalation History but is not contacted automatically yet. One policy is the default.
      </p>

      {error && <div className="form-error">{error}</div>}

      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Default<InfoTip text="The policy used when no other matches the Case." /></th>
            <th style={thStyle}>Trigger Count<InfoTip text="How many reminders must go unanswered before escalating." /></th>
            <th style={thStyle}>Grace Period<InfoTip text="Extra hours to wait after the last reminder before escalating." /></th>
            <th style={thStyle}>Cooldown<InfoTip text="Minimum hours between two escalations of the same Case." /></th>
            <th style={thStyle}>Max Level<InfoTip text="The highest level it can escalate to (up to 3)." /></th>
            <th style={thStyle}>Channel<InfoTip text="How the recipient is told, e.g. email." /></th>
            <th style={thStyle}># Levels<InfoTip text="How many escalation steps are set up." /></th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {policies.map((p) => (
            <Fragment key={p.id}>
              <tr>
                <td style={tdStyle}>
                  {p.name}
                  {p.description && <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>{p.description}</div>}
                </td>
                <td style={tdStyle}>{p.enabled ? "Yes" : "No"}</td>
                <td style={tdStyle}>{p.isDefault ? "Yes" : ""}</td>
                <td style={tdStyle}>{p.triggerReminderCount}</td>
                <td style={tdStyle}>{timeSpanToHours(p.gracePeriod)}h</td>
                <td style={tdStyle}>{timeSpanToHours(p.cooldown)}h</td>
                <td style={tdStyle}>{p.maximumLevel}</td>
                <td style={tdStyle}>{p.channel}</td>
                <td style={tdStyle}>{p.levels.length}</td>
                <td style={tdStyle}>
                  <button onClick={() => startEdit(p)} style={{ marginRight: 8 }} data-tip="Change this policy's timings and levels.">Edit</button>
                  <button onClick={() => handleToggleEnabled(p)} style={{ marginRight: 8 }} data-tip="Turn this policy on or off without deleting it.">{p.enabled ? "Disable" : "Enable"}</button>
                  <button onClick={() => handleDelete(p.id)} data-tip="Delete this policy permanently.">Delete</button>
                </td>
              </tr>
              <tr>
                <td style={tdStyle} colSpan={10}>
                  <div style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 13 }}>
                    <strong>Test Policy:</strong><InfoTip text="Paste a Case ID to see who this policy would escalate it to and why. Nothing is sent or recorded." />
                    <input
                      placeholder="Case ID (guid)"
                      value={testCaseIds[p.id] ?? ""}
                      onChange={(e) => setTestCaseIds({ ...testCaseIds, [p.id]: e.target.value })}
                      style={{ width: 300 }}
                    />
                    <button onClick={() => handleTest(p.id)} disabled={testRunning === p.id} data-tip="Dry-run the policy against the Case ID on the left.">
                      {testRunning === p.id ? "Testing..." : "Test"}
                    </button>
                    {testResults[p.id] && (
                      <span>
                        {testResults[p.id]!.wouldEscalate ? "Would escalate" : "Would NOT escalate"}
                        {testResults[p.id]!.eligibleLevel != null && ` — Level ${testResults[p.id]!.eligibleLevel}`}
                        {testResults[p.id]!.recipientDisplay && ` — Recipient: ${testResults[p.id]!.recipientDisplay}`}
                        {" — "}{testResults[p.id]!.reason}
                      </span>
                    )}
                  </div>
                </td>
              </tr>
            </Fragment>
          ))}
          {policies.length === 0 && (
            <tr><td style={tdStyle} colSpan={10}>No Escalation Policies configured.</td></tr>
          )}
        </tbody>
      </table>

      {!showForm && <button onClick={startCreate} data-tip="Create new escalation rules.">Add Escalation Policy</button>}

      {showForm && (
        <form onSubmit={handleSubmit} style={{ display: "flex", flexDirection: "column", gap: 16, maxWidth: 900 }}>
          <h2 style={{ fontSize: 16 }}>{editingId ? "Edit Policy" : "Add Policy"}</h2>

          <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end" }}>
            <div>
              <label><TipLabel tip="A short name, e.g. Standard escalation.">Name</TipLabel></label><br />
              <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
            </div>
            <div>
              <label><TipLabel tip="Optional note about when this policy is used.">Description</TipLabel></label><br />
              <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
            </div>
            <div>
              <label><TipLabel tip="Escalate after this many reminders were sent with no reply. Example: 2.">Trigger reminder count</TipLabel></label><br />
              <input type="number" min={1} value={form.triggerReminderCount}
                onChange={(e) => setForm({ ...form, triggerReminderCount: Number(e.target.value) })} style={{ width: 80 }} />
            </div>
            <div>
              <label><TipLabel tip="Extra hours after the last reminder before escalating, so the agent can still act.">Grace period (hours)</TipLabel></label><br />
              <input type="number" min={0} step={0.5} value={form.gracePeriodHours}
                onChange={(e) => setForm({ ...form, gracePeriodHours: Number(e.target.value) })} style={{ width: 80 }} />
            </div>
            <div>
              <label><TipLabel tip="Wait at least this long before escalating the same Case again (to the next level).">Cooldown (hours)</TipLabel></label><br />
              <input type="number" min={0} step={0.5} value={form.cooldownHours}
                onChange={(e) => setForm({ ...form, cooldownHours: Number(e.target.value) })} style={{ width: 80 }} />
            </div>
            <div>
              <label><TipLabel tip="The highest level to escalate to, 1 to 3.">Maximum level</TipLabel></label><br />
              <input type="number" min={1} max={3} value={form.maximumLevel}
                onChange={(e) => setForm({ ...form, maximumLevel: Number(e.target.value) })} style={{ width: 80 }} />
            </div>
            <div>
              <label><TipLabel tip="How recipients are told. Escalation emails are sent through Outbound Email.">Channel</TipLabel></label><br />
              <input value={form.channel} onChange={(e) => setForm({ ...form, channel: e.target.value })} style={{ width: 120 }} />
            </div>
            <div>
              <label><TipLabel tip="Only use this policy for Cases from mailboxes with this profile. Leave blank for all.">Classification Profile ID (optional)</TipLabel></label><br />
              <input value={form.classificationProfileId} onChange={(e) => setForm({ ...form, classificationProfileId: e.target.value })}
                placeholder="(none)" style={{ width: 220 }} />
            </div>
            <div>
              <label><TipLabel tip="Only use this policy for these email categories, comma-separated. Leave blank for all.">Categories (optional)</TipLabel></label><br />
              <input value={form.categories} onChange={(e) => setForm({ ...form, categories: e.target.value })} style={{ width: 160 }} />
            </div>
            <div>
              <label><TipLabel tip="Only use this policy for Cases of this priority. Leave blank for all.">Priority (optional)</TipLabel></label><br />
              <select value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value === "" ? "" : (Number(e.target.value) as ClassificationPriority) })}>
                <option value="">(none)</option>
                {(Object.keys(CLASSIFICATION_PRIORITY_LABELS) as unknown as ClassificationPriority[]).map((v) => (
                  <option key={v} value={v}>{CLASSIFICATION_PRIORITY_LABELS[Number(v) as ClassificationPriority]}</option>
                ))}
              </select>
            </div>
            <div>
              <label><input type="checkbox" checked={form.enabled}
                onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled</label>
            </div>
            <div>
              <label><input type="checkbox" checked={form.isDefault}
                onChange={(e) => setForm({ ...form, isDefault: e.target.checked })} /> Default policy<InfoTip text="Use this policy whenever no other one matches the Case." /></label>
            </div>
          </div>

          <div>
            <h3 style={{ fontSize: 14, display: "flex", alignItems: "center" }}>Levels<InfoTip text="Who is told at each step. Level 1 goes first (e.g. supervisor); if the Case is still neglected, the next level is told (e.g. department manager)." /></h3>
            <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
              <thead>
                <tr>
                  <th style={thStyle}>Level</th>
                  <th style={thStyle}>Delay (hours)<InfoTip text="Hours after the previous level before this one is used." /></th>
                  <th style={thStyle}>Recipient Type<InfoTip text="Who to tell: the Case owner, their supervisor, the department manager, a named employee or a group." /></th>
                  <th style={thStyle}>Specific Employee / Group<InfoTip text="Pick the person or group when the recipient type asks for one." /></th>
                  <th style={thStyle}></th>
                </tr>
              </thead>
              <tbody>
                {form.levels.map((l, i) => (
                  <tr key={i}>
                    <td style={tdStyle}>
                      <input type="number" min={1} max={3} value={l.level}
                        onChange={(e) => updateLevelRow(i, { level: Number(e.target.value) })} style={{ width: 60 }} />
                    </td>
                    <td style={tdStyle}>
                      <input type="number" min={0} step={0.5} value={l.delayHours}
                        onChange={(e) => updateLevelRow(i, { delayHours: Number(e.target.value) })} style={{ width: 80 }} />
                    </td>
                    <td style={tdStyle}>
                      <select value={l.recipientType}
                        onChange={(e) => updateLevelRow(i, { recipientType: Number(e.target.value) as EscalationRecipientType })}>
                        {(Object.keys(ESCALATION_RECIPIENT_TYPE_LABELS) as unknown as EscalationRecipientType[]).map((v) => (
                          <option key={v} value={v}>{ESCALATION_RECIPIENT_TYPE_LABELS[Number(v) as EscalationRecipientType]}</option>
                        ))}
                      </select>
                    </td>
                    <td style={tdStyle}>
                      {l.recipientType === 3 && (
                        <select value={l.specificEmployeeId} onChange={(e) => updateLevelRow(i, { specificEmployeeId: e.target.value })}>
                          <option value="">(select employee)</option>
                          {employees.map((emp) => (
                            <option key={emp.id} value={emp.id}>{emp.fullName}</option>
                          ))}
                        </select>
                      )}
                      {l.recipientType === 4 && (
                        <select value={l.specificGroupId} onChange={(e) => updateLevelRow(i, { specificGroupId: e.target.value })}>
                          <option value="">(select group)</option>
                          {groups.map((g) => (
                            <option key={g.id} value={g.id}>{g.name}</option>
                          ))}
                        </select>
                      )}
                    </td>
                    <td style={tdStyle}>
                      <button type="button" onClick={() => removeLevelRow(i)} disabled={form.levels.length <= 1}>Remove</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <button type="button" onClick={addLevelRow} style={{ marginTop: 8 }} data-tip="Add another, higher escalation step.">Add Level</button>
          </div>

          <div>
            <button type="submit" style={{ marginRight: 8 }} data-tip="Save the policy.">Save</button>
            <button type="button" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
