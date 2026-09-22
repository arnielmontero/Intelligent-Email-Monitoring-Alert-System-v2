import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto, EmployeeDto, TestConnectionResult } from "../../api/types";

const PROTOCOL_LABELS = ["IMAP", "Microsoft Graph"];
const AUTH_METHOD_LABELS = ["Password", "OAuth2", "App Password"];

export default function EmailAccountsPage() {
  const [accounts, setAccounts] = useState<EmailAccountDto[]>([]);
  const [employees, setEmployees] = useState<EmployeeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, TestConnectionResult>>({});
  const [testingId, setTestingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);

  const [emailAddress, setEmailAddress] = useState("");
  const [host, setHost] = useState("");
  const [port, setPort] = useState(993);
  const [encryption, setEncryption] = useState("SSL/TLS");
  const [username, setUsername] = useState("");
  const [secret, setSecret] = useState("");
  const [ownerEmployeeId, setOwnerEmployeeId] = useState("");
  const [classificationProfileName, setClassificationProfileName] = useState("");
  const [monitoringEnabled, setMonitoringEnabled] = useState(true);

  async function load() {
    setLoading(true);
    try {
      const [accRes, empRes] = await Promise.all([
        apiClient.get<EmailAccountDto[]>("/email-accounts"),
        apiClient.get<EmployeeDto[]>("/employees"),
      ]);
      setAccounts(accRes.data);
      setEmployees(empRes.data);
    } catch {
      setError("Failed to load email accounts.");
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
      await apiClient.post("/email-accounts", {
        emailAddress,
        displayName: null,
        purpose: 0, // Inbound — outbound accounts are configured separately (§18)
        kind: 0,
        protocol: 0, // IMAP only implemented in Phase 2
        host,
        port,
        encryption,
        username,
        authMethod: 0,
        secret,
        ownerEmployeeId: ownerEmployeeId || null,
        classificationProfileName: classificationProfileName || null,
        monitoringEnabled,
      });
      setEmailAddress("");
      setHost("");
      setUsername("");
      setSecret("");
      setOwnerEmployeeId("");
      setClassificationProfileName("");
      setShowForm(false);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to create email account.");
    }
  }

  async function handleTestConnection(id: string) {
    setTestingId(id);
    try {
      const res = await apiClient.post<TestConnectionResult>(`/email-accounts/${id}/test-connection`);
      setTestResults((prev) => ({ ...prev, [id]: res.data }));
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to test connection.");
    } finally {
      setTestingId(null);
    }
  }

  async function handleToggleActive(account: EmailAccountDto) {
    try {
      await apiClient.post(`/email-accounts/${account.id}/${account.isActive ? "deactivate" : "activate"}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update account status.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Email Accounts</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Monitored inbound mailboxes (§14). Credentials are encrypted at rest and never displayed
        after saving — only whether one is configured.
      </p>

      {error && <div className="form-error">{error}</div>}

      <button onClick={() => setShowForm((s) => !s)} style={{ marginBottom: 16 }}>
        {showForm ? "Cancel" : "+ Add Email Account"}
      </button>

      {showForm && (
        <form onSubmit={handleCreate} style={{ marginBottom: 24, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "end", background: "var(--color-surface)", padding: 16, borderRadius: 8 }}>
          <div>
            <label>Email address</label><br />
            <input type="email" value={emailAddress} onChange={(e) => setEmailAddress(e.target.value)} required />
          </div>
          <div>
            <label>Host</label><br />
            <input value={host} onChange={(e) => setHost(e.target.value)} required placeholder="imap.example.com" />
          </div>
          <div>
            <label>Port</label><br />
            <input type="number" value={port} onChange={(e) => setPort(Number(e.target.value))} required style={{ width: 80 }} />
          </div>
          <div>
            <label>Encryption</label><br />
            <select value={encryption} onChange={(e) => setEncryption(e.target.value)}>
              <option value="SSL/TLS">SSL/TLS</option>
              <option value="STARTTLS">STARTTLS</option>
              <option value="NONE">None</option>
            </select>
          </div>
          <div>
            <label>Username</label><br />
            <input value={username} onChange={(e) => setUsername(e.target.value)} required />
          </div>
          <div>
            <label>Password / App Password</label><br />
            <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} required autoComplete="new-password" />
          </div>
          <div>
            <label>Owner employee</label><br />
            <select value={ownerEmployeeId} onChange={(e) => setOwnerEmployeeId(e.target.value)}>
              <option value="">—</option>
              {employees.map((emp) => (
                <option key={emp.id} value={emp.id}>{emp.fullName}</option>
              ))}
            </select>
          </div>
          <div>
            <label>Classification profile</label><br />
            <input value={classificationProfileName} onChange={(e) => setClassificationProfileName(e.target.value)} placeholder="Sales" />
          </div>
          <div>
            <label>
              <input type="checkbox" checked={monitoringEnabled} onChange={(e) => setMonitoringEnabled(e.target.checked)} /> Monitoring enabled
            </label>
          </div>
          <button type="submit">Save</button>
        </form>
      )}

      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <thead>
          <tr>
            <th style={thStyle}>Email</th>
            <th style={thStyle}>Protocol</th>
            <th style={thStyle}>Host:Port</th>
            <th style={thStyle}>Owner</th>
            <th style={thStyle}>Monitoring</th>
            <th style={thStyle}>Credential</th>
            <th style={thStyle}>Last Test</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {accounts.map((acc) => {
            const testResult = testResults[acc.id];
            return (
              <tr key={acc.id}>
                <td style={tdStyle}>{acc.emailAddress}</td>
                <td style={tdStyle}>{PROTOCOL_LABELS[acc.protocol]} / {AUTH_METHOD_LABELS[acc.authMethod]}</td>
                <td style={tdStyle}>{acc.host}:{acc.port}</td>
                <td style={tdStyle}>{acc.ownerEmployeeName ?? "—"}</td>
                <td style={tdStyle}>{acc.monitoringEnabled ? "Enabled" : "Disabled"}</td>
                <td style={tdStyle}>{acc.hasCredential ? "Configured" : "Not set"}</td>
                <td style={tdStyle}>
                  {testResult
                    ? (testResult.succeeded ? "✓ Success" : `✗ ${testResult.errorMessage}`)
                    : (acc.lastTestedAt ? (acc.lastTestSucceeded ? "✓ Success" : `✗ ${acc.lastTestError}`) : "Not tested")}
                </td>
                <td style={tdStyle}>
                  <button onClick={() => handleTestConnection(acc.id)} disabled={testingId === acc.id}>
                    {testingId === acc.id ? "Testing..." : "Test"}
                  </button>{" "}
                  <button onClick={() => handleToggleActive(acc)}>{acc.isActive ? "Deactivate" : "Activate"}</button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
