import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmployeeDto, EscalationGroupDto, SaveEscalationGroupRequest } from "../../api/types";

interface FormState {
  name: string;
  employeeIds: string[];
}

const emptyForm: FormState = { name: "", employeeIds: [] };

export default function EscalationGroupsPage() {
  const [groups, setGroups] = useState<EscalationGroupDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [showForm, setShowForm] = useState(false);

  async function load() {
    setLoading(true);
    try {
      const [groupsRes, employeesRes] = await Promise.all([
        apiClient.get<EscalationGroupDto[]>("/escalation-groups"),
        apiClient.get<EmployeeDto[]>("/employees"),
      ]);
      setGroups(groupsRes.data);
      setEmployees(employeesRes.data);
    } catch {
      setError("Failed to load Escalation Groups.");
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

  function startEdit(g: EscalationGroupDto) {
    setForm({ name: g.name, employeeIds: g.members.map((m) => m.employeeId) });
    setEditingId(g.id);
    setShowForm(true);
  }

  function toggleEmployee(id: string) {
    setForm((prev) => ({
      ...prev,
      employeeIds: prev.employeeIds.includes(id)
        ? prev.employeeIds.filter((e) => e !== id)
        : [...prev.employeeIds, id],
    }));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    const payload: SaveEscalationGroupRequest = {
      name: form.name,
      employeeIds: form.employeeIds,
    };

    try {
      if (editingId) {
        await apiClient.put(`/escalation-groups/${editingId}`, payload);
      } else {
        await apiClient.post("/escalation-groups", payload);
      }
      setShowForm(false);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save Escalation Group.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await apiClient.delete(`/escalation-groups/${id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete Escalation Group.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Escalation Groups</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        A named group of employees that an escalation level can name as its recipient (for example "Sales managers").
        The group is recorded in Escalation History; its members are not contacted automatically yet.
        A group that is in use by an escalation policy can't be deleted.
      </p>

      {error && <div className="form-error">{error}</div>}

      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Members</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <tr key={g.id}>
              <td style={tdStyle}>{g.name}</td>
              <td style={tdStyle}>{g.members.map((m) => m.employeeName).join(", ") || "(none)"}</td>
              <td style={tdStyle}>
                <button onClick={() => startEdit(g)} style={{ marginRight: 8 }}>Edit</button>
                <button onClick={() => handleDelete(g.id)}>Delete</button>
              </td>
            </tr>
          ))}
          {groups.length === 0 && (
            <tr><td style={tdStyle} colSpan={3}>No Escalation Groups configured.</td></tr>
          )}
        </tbody>
      </table>

      {!showForm && <button onClick={startCreate}>Add Escalation Group</button>}

      {showForm && (
        <form onSubmit={handleSubmit} style={{ display: "flex", flexDirection: "column", gap: 12, maxWidth: 480 }}>
          <h2 style={{ fontSize: 16 }}>{editingId ? "Edit Group" : "Add Group"}</h2>
          <div>
            <label>Name</label><br />
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required style={{ width: "100%" }} />
          </div>
          <div>
            <label>Members</label>
            <div style={{ maxHeight: 240, overflowY: "auto", border: "1px solid var(--color-border)", borderRadius: 4, padding: 8, marginTop: 4 }}>
              {employees.map((emp) => (
                <label key={emp.id} style={{ display: "block", padding: "2px 0" }}>
                  <input
                    type="checkbox"
                    checked={form.employeeIds.includes(emp.id)}
                    onChange={() => toggleEmployee(emp.id)}
                  /> {emp.fullName}
                </label>
              ))}
              {employees.length === 0 && <span style={{ color: "var(--color-text-muted)" }}>No employees found.</span>}
            </div>
          </div>
          <div>
            <button type="submit" style={{ marginRight: 8 }}>Save</button>
            <button type="button" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
