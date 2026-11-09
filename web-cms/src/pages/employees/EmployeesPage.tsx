import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { DepartmentDto, EmployeeDto } from "../../api/types";

export default function EmployeesPage() {
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [departments, setDepartments] = useState<DepartmentDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [departmentId, setDepartmentId] = useState("");
  const [supervisorEmployeeId, setSupervisorEmployeeId] = useState("");

  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<EditForm>(emptyEditForm);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  async function load() {
    setLoading(true);
    try {
      const [empRes, deptRes] = await Promise.all([
        apiClient.get<EmployeeDto[]>("/employees"),
        apiClient.get<DepartmentDto[]>("/departments"),
      ]);
      setEmployees(empRes.data);
      setDepartments(deptRes.data);
    } catch {
      setError("Failed to load employees or departments.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await apiClient.post("/employees", {
        fullName,
        email,
        departmentId: departmentId || null,
        supervisorEmployeeId: supervisorEmployeeId || null,
      });
      setFullName("");
      setEmail("");
      setDepartmentId("");
      setSupervisorEmployeeId("");
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to create employee.");
    }
  }

  function startEdit(emp: EmployeeDto) {
    setError(null);
    setNotice(null);
    setEditingId(emp.id);
    setEditForm({
      fullName: emp.fullName,
      email: emp.email,
      isActive: emp.isActive,
      departmentId: emp.departmentId ?? "",
      supervisorEmployeeId: emp.supervisorEmployeeId ?? "",
    });
  }

  async function saveEdit(emp: EmployeeDto) {
    setError(null);
    setNotice(null);
    setSaving(true);
    try {
      await apiClient.put(`/employees/${emp.id}`, {
        fullName: editForm.fullName,
        email: editForm.email,
        isActive: editForm.isActive,
        departmentId: editForm.departmentId || null,
        supervisorEmployeeId: editForm.supervisorEmployeeId || null,
      });
      setEditingId(null);
      setNotice(`Saved ${editForm.fullName}.`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update employee.");
    } finally {
      setSaving(false);
    }
  }

  async function handleToggleActive(emp: EmployeeDto) {
    setError(null);
    try {
      await apiClient.post(`/employees/${emp.id}/${emp.isActive ? "deactivate" : "activate"}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update employee status.");
    }
  }

  async function handleDelete(emp: EmployeeDto) {
    if (!window.confirm(`Permanently delete ${emp.fullName} (${emp.email})? This cannot be undone.`)) return;
    setError(null);
    try {
      await apiClient.delete(`/employees/${emp.id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete employee.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Employees</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        The people who own mailboxes and Cases, with their department and supervisor (used for escalation). A Super
        Administrator sign-in such as admin@sawo.com is for access only and can't be used as an employee's email.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={{ color: "var(--color-success)", marginBottom: 12 }}>{notice}</div>}

      <form onSubmit={handleCreate} style={{ marginBottom: 24, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "end" }}>
        <div>
          <label>Full name</label><br />
          <input value={fullName} onChange={(e) => setFullName(e.target.value)} required />
        </div>
        <div>
          <label>Email</label><br />
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </div>
        <div>
          <label>Department</label><br />
          <select value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
            <option value="">—</option>
            {departments.map((d) => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
        </div>
        <div>
          <label>Supervisor</label><br />
          <select value={supervisorEmployeeId} onChange={(e) => setSupervisorEmployeeId(e.target.value)}>
            <option value="">—</option>
            {employees.map((e) => (
              <option key={e.id} value={e.id}>{e.fullName}</option>
            ))}
          </select>
        </div>
        <button type="submit">Add Employee</button>
      </form>

      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Email</th>
            <th style={thStyle}>Department</th>
            <th style={thStyle}>Supervisor</th>
            <th style={thStyle}>Resolved Manager</th>
            <th style={thStyle}>Active</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {employees.map((emp) => editingId === emp.id ? (
            <tr key={emp.id} style={{ background: "var(--color-surface)" }}>
              <td style={tdStyle} colSpan={7}>
                <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "end" }}>
                  <div>
                    <label>Full name</label><br />
                    <input value={editForm.fullName} onChange={(e) => setEditForm({ ...editForm, fullName: e.target.value })} required />
                  </div>
                  <div>
                    <label>Email</label><br />
                    <input type="email" value={editForm.email} onChange={(e) => setEditForm({ ...editForm, email: e.target.value })} required />
                  </div>
                  <div>
                    <label>Department</label><br />
                    <select value={editForm.departmentId} onChange={(e) => setEditForm({ ...editForm, departmentId: e.target.value })}>
                      <option value="">—</option>
                      {departments.map((d) => (
                        <option key={d.id} value={d.id}>{d.name}</option>
                      ))}
                    </select>
                  </div>
                  <div>
                    <label>Supervisor</label><br />
                    <select value={editForm.supervisorEmployeeId} onChange={(e) => setEditForm({ ...editForm, supervisorEmployeeId: e.target.value })}>
                      <option value="">—</option>
                      {employees
                        .filter((o) => o.id !== emp.id && (o.isActive || o.id === editForm.supervisorEmployeeId))
                        .map((o) => (
                          <option key={o.id} value={o.id}>{o.fullName}{o.isActive ? "" : " (inactive)"}</option>
                        ))}
                    </select>
                  </div>
                  <div>
                    <label>
                      <input type="checkbox" checked={editForm.isActive} onChange={(e) => setEditForm({ ...editForm, isActive: e.target.checked })} />{" "}
                      Active
                    </label>
                  </div>
                </div>
                <p style={{ color: "var(--color-text-muted)", fontSize: 12, margin: "8px 0 0" }}>
                  The resolved manager comes from the department's manager. Changing the supervisor affects who gets escalations for this employee's Cases.
                </p>
                <div style={{ display: "flex", gap: 8, marginTop: 12 }}>
                  <button className="btn-primary" disabled={saving} onClick={() => saveEdit(emp)}>{saving ? "Saving..." : "Save"}</button>
                  <button disabled={saving} onClick={() => setEditingId(null)}>Cancel</button>
                </div>
              </td>
            </tr>
          ) : (
            <tr key={emp.id}>
              <td style={tdStyle}>{emp.fullName}</td>
              <td style={tdStyle}>{emp.email}</td>
              <td style={tdStyle}>{emp.departmentName ?? "—"}</td>
              <td style={tdStyle}>{emp.supervisorEmployeeName ?? "—"}</td>
              <td style={tdStyle}>{emp.managerEmployeeName ?? "—"}</td>
              <td style={tdStyle}>{emp.isActive ? "Yes" : "No"}</td>
              <td style={tdStyle}>
                <div style={{ display: "flex", gap: 8 }}>
                  <button onClick={() => startEdit(emp)} disabled={editingId !== null}>Edit</button>
                  <button onClick={() => handleToggleActive(emp)} style={{ minWidth: 80 }}>
                    {emp.isActive ? "Deactivate" : "Activate"}
                  </button>
                  <button
                    className="btn-danger"
                    disabled={emp.isActive}
                    title={emp.isActive ? "Deactivate this employee before deleting" : "Permanently delete this employee"}
                    onClick={() => handleDelete(emp)}
                  >
                    Delete
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

interface EditForm {
  fullName: string;
  email: string;
  isActive: boolean;
  departmentId: string;
  supervisorEmployeeId: string;
}

const emptyEditForm: EditForm = { fullName: "", email: "", isActive: true, departmentId: "", supervisorEmployeeId: "" };

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
