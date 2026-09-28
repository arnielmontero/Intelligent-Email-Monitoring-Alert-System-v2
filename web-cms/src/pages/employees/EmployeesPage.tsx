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
      <h1>Employees &amp; Ownership</h1>

      {error && <div className="form-error">{error}</div>}

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

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
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
          {employees.map((emp) => (
            <tr key={emp.id}>
              <td style={tdStyle}>{emp.fullName}</td>
              <td style={tdStyle}>{emp.email}</td>
              <td style={tdStyle}>{emp.departmentName ?? "—"}</td>
              <td style={tdStyle}>{emp.supervisorEmployeeName ?? "—"}</td>
              <td style={tdStyle}>{emp.managerEmployeeName ?? "—"}</td>
              <td style={tdStyle}>{emp.isActive ? "Yes" : "No"}</td>
              <td style={tdStyle}>
                <div style={{ display: "flex", gap: 8 }}>
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

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "8px" };
