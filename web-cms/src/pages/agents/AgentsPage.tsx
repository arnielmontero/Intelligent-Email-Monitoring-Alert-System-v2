import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AgentDto, AgentLogDto, EmployeeDto } from "../../api/types";
import { AGENT_REGISTRATION_STATUS_LABELS, AGENT_CONNECTION_STATUS_LABELS, AGENT_LOG_EVENT_TYPE_LABELS } from "../../api/types";

export default function AgentsPage() {
  const [agents, setAgents] = useState<AgentDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<string>("");

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [logs, setLogs] = useState<AgentLogDto[] | null>(null);

  const [approveEmployeeId, setApproveEmployeeId] = useState<Record<string, string>>({});

  async function load() {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      if (statusFilter !== "") params.set("status", statusFilter);
      const [agentsRes, employeesRes] = await Promise.all([
        apiClient.get<AgentDto[]>(`/agents?${params.toString()}`),
        apiClient.get<EmployeeDto[]>("/employees"),
      ]);
      setAgents(agentsRes.data);
      setEmployees(employeesRes.data);
    } catch {
      setError("Failed to load Agents.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [statusFilter]);

  async function handleApprove(agent: AgentDto) {
    const employeeId = approveEmployeeId[agent.id];
    if (!employeeId) {
      setError("Select an employee before approving.");
      return;
    }
    setError(null);
    try {
      await apiClient.post(`/agents/${agent.id}/approve`, { employeeId });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to approve Agent.");
    }
  }

  async function handleReject(agent: AgentDto) {
    const reason = window.prompt("Rejection reason:");
    if (reason === null) return;
    setError(null);
    try {
      await apiClient.post(`/agents/${agent.id}/reject`, { reason });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to reject Agent.");
    }
  }

  async function handleRevoke(agent: AgentDto) {
    const reason = window.prompt("Revocation reason:");
    if (reason === null) return;
    setError(null);
    try {
      await apiClient.post(`/agents/${agent.id}/revoke`, { reason });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to revoke Agent.");
    }
  }

  async function openLogs(agentId: string) {
    setSelectedId(agentId);
    setLogs(null);
    try {
      const res = await apiClient.get<AgentLogDto[]>(`/agents/${agentId}/logs`);
      setLogs(res.data);
    } catch {
      setError("Failed to load Agent logs.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div style={{ display: "flex", gap: 24 }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <h1>Windows Agents</h1>
        <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
          Requirements §68-§71 — Agent registration is server-approved: only the server ever
          generates a Registration Key, only after an Administrator approves the request, and it
          is collected automatically by the Agent, never entered manually.
        </p>

        {error && <div className="form-error">{error}</div>}

        <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} style={{ marginBottom: 16 }}>
          <option value="">All statuses</option>
          {Object.entries(AGENT_REGISTRATION_STATUS_LABELS).map(([value, label]) => (
            <option key={value} value={value}>{label}</option>
          ))}
        </select>

        <table style={{ width: "100%", borderCollapse: "collapse" }}>
          <thead>
            <tr>
              <th style={thStyle}>Client Name</th>
              <th style={thStyle}>Enrollment Email</th>
              <th style={thStyle}>Employee</th>
              <th style={thStyle}>Registration</th>
              <th style={thStyle}>Connection</th>
              <th style={thStyle}>Last Connected</th>
              <th style={thStyle}>Version</th>
              <th style={thStyle}></th>
            </tr>
          </thead>
          <tbody>
            {agents.map((a) => (
              <tr key={a.id} style={{ background: selectedId === a.id ? "#1e293b" : undefined }}>
                <td style={tdStyle}>{a.clientName}</td>
                <td style={tdStyle}>{a.enrollmentEmailAddress}</td>
                <td style={tdStyle}>{a.employeeName ?? "(unassigned)"}</td>
                <td style={tdStyle}>{AGENT_REGISTRATION_STATUS_LABELS[a.registrationStatus]}</td>
                <td style={tdStyle}>{AGENT_CONNECTION_STATUS_LABELS[a.connectionStatus]}</td>
                <td style={tdStyle}>{a.lastConnectedAt ? new Date(a.lastConnectedAt).toLocaleString() : "Never"}</td>
                <td style={tdStyle}>{a.agentVersion ?? "-"}</td>
                <td style={tdStyle}>
                  {a.registrationStatus === 0 && (
                    <>
                      <select
                        value={approveEmployeeId[a.id] ?? ""}
                        onChange={(e) => setApproveEmployeeId((prev) => ({ ...prev, [a.id]: e.target.value }))}
                        style={{ marginRight: 8 }}
                      >
                        <option value="">Select employee...</option>
                        {employees.map((emp) => (
                          <option key={emp.id} value={emp.id}>{emp.fullName}</option>
                        ))}
                      </select>
                      <button onClick={() => handleApprove(a)} style={{ marginRight: 8 }}>Approve</button>
                      <button onClick={() => handleReject(a)} style={{ marginRight: 8 }}>Reject</button>
                    </>
                  )}
                  {a.registrationStatus === 1 && (
                    <button onClick={() => handleRevoke(a)} style={{ marginRight: 8 }}>Revoke</button>
                  )}
                  <button onClick={() => openLogs(a.id)}>Logs</button>
                </td>
              </tr>
            ))}
            {agents.length === 0 && (
              <tr><td style={tdStyle} colSpan={8}>No registered Agents.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {selectedId && (
        <div style={{ width: 380, flexShrink: 0, borderLeft: "1px solid #334155", paddingLeft: 24 }}>
          <h2 style={{ fontSize: 16 }}>Technical Agent Log (§67)</h2>
          {logs === null && <p>Loading...</p>}
          {logs && (
            <ul style={{ paddingLeft: 16, fontSize: 13 }}>
              {logs.map((l) => (
                <li key={l.id} style={{ marginBottom: 8 }}>
                  <strong>{AGENT_LOG_EVENT_TYPE_LABELS[l.eventType]}</strong>
                  {l.detail && <div>{l.detail}</div>}
                  <div style={{ color: "var(--color-text-muted)" }}>
                    {new Date(l.occurredAt).toLocaleString()}{l.ipAddress ? ` · ${l.ipAddress}` : ""}
                  </div>
                </li>
              ))}
              {logs.length === 0 && <li>No log entries yet.</li>}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
