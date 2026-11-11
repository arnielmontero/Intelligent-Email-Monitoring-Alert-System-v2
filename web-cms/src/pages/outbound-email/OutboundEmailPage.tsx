import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { EmailAccountDto, TestConnectionResult } from "../../api/types";
import { InfoTip, TipLabel } from "../../components/help/Tooltip";

/// Requirements §18 (Outbound Email Configuration — internal IEMAS notification emails only,
/// never a customer mailbox), §19 (inbound/outbound separation kept as two distinct
/// EmailAccount.Purpose values on the same backend entity/endpoint), §86 (EMAIL MANAGEMENT nav ->
/// Outbound Email). Uses the same EmailAccountsController/EmailAccountService the Email Accounts
/// page uses, filtered to Purpose=Outbound (1) - the backend already enforces "monitoring cannot be
/// enabled on an outbound account" (EmailAccountService.cs) so this page never even shows that
/// toggle, rather than relying on the user to leave it unchecked.
export default function OutboundEmailPage() {
  const [accounts, setAccounts] = useState<EmailAccountDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, TestConnectionResult>>({});
  const [testingId, setTestingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);

  const [fromName, setFromName] = useState("IEMAS Notification System");
  const [emailAddress, setEmailAddress] = useState("");
  const [host, setHost] = useState("");
  const [port, setPort] = useState(587);
  const [encryption, setEncryption] = useState("STARTTLS");
  const [username, setUsername] = useState("");
  const [secret, setSecret] = useState("");

  async function load() {
    setLoading(true);
    try {
      const res = await apiClient.get<EmailAccountDto[]>("/email-accounts?purpose=1");
      setAccounts(res.data);
    } catch {
      setError("Failed to load outbound email configuration.");
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
        displayName: fromName,
        purpose: 1, // Outbound
        kind: 0,
        protocol: 2, // SMTP
        host,
        port,
        encryption,
        username,
        authMethod: 0,
        secret,
        ownerEmployeeId: null, // §18 — no personal owner for the outbound notification account
        classificationProfileName: null,
        monitoringEnabled: false, // §18/§19 — never monitor the outbound mailbox as a customer mailbox
      });
      setEmailAddress("");
      setHost("");
      setUsername("");
      setSecret("");
      setShowForm(false);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save outbound email configuration.");
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

  async function handleDelete(account: EmailAccountDto) {
    if (!window.confirm(`Permanently delete ${account.emailAddress} and its stored credential? This cannot be undone.`)) return;
    setError(null);
    try {
      await apiClient.delete(`/email-accounts/${account.id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete outbound account.");
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Outbound Email</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        An SMTP mailbox set aside for IEMAS's own outgoing email. For now it is only stored and connection-tested —
        IEMAS does not send any email yet; employees are alerted by pop-ups on their PC. It is never checked for
        customer email. Passwords are encrypted and never displayed after saving.
      </p>

      {error && <div className="form-error">{error}</div>}

      {accounts.length === 0 && !showForm && (
        <p style={{ color: "var(--color-text-muted)" }}>No outbound email account configured yet.</p>
      )}

      <button onClick={() => setShowForm((s) => !s)} style={{ marginBottom: 16 }} data-tip="Set up the mailbox IEMAS uses to send escalation emails to managers.">
        {showForm ? "Cancel" : "+ Configure Outbound Email"}
      </button>

      {showForm && (
        <form onSubmit={handleCreate} style={{ marginBottom: 24, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "end", background: "var(--color-surface)", padding: 16, borderRadius: 8 }}>
          <div>
            <label><TipLabel tip="The sender name managers see, e.g. IEMAS Alerts.">From Name</TipLabel></label><br />
            <input value={fromName} onChange={(e) => setFromName(e.target.value)} required />
          </div>
          <div>
            <label><TipLabel tip="The address escalation emails are sent from. Use a dedicated no-reply mailbox.">From Email</TipLabel></label><br />
            <input type="email" value={emailAddress} onChange={(e) => setEmailAddress(e.target.value)} required placeholder="iemas-notification@sawo.com" />
          </div>
          <div>
            <label><TipLabel tip="The outgoing mail server from your email provider, e.g. smtp.office365.com.">SMTP Host</TipLabel></label><br />
            <input value={host} onChange={(e) => setHost(e.target.value)} required placeholder="smtp.sawo.com" />
          </div>
          <div>
            <label><TipLabel tip="Usually 587 (STARTTLS) or 465 (SSL).">Port</TipLabel></label><br />
            <input type="number" value={port} onChange={(e) => setPort(Number(e.target.value))} required style={{ width: 80 }} />
          </div>
          <div>
            <label><TipLabel tip="How the connection is secured. Match what your provider says for the port you chose.">Encryption</TipLabel></label><br />
            <select value={encryption} onChange={(e) => setEncryption(e.target.value)}>
              <option value="STARTTLS">STARTTLS</option>
              <option value="SSL/TLS">SSL/TLS</option>
              <option value="NONE">None</option>
            </select>
          </div>
          <div>
            <label><TipLabel tip="The login for the sending mailbox, usually its full email address.">Username</TipLabel></label><br />
            <input value={username} onChange={(e) => setUsername(e.target.value)} required />
          </div>
          <div>
            <label><TipLabel tip="The mailbox password or app password. Stored encrypted and never shown again.">Password</TipLabel></label><br />
            <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} required autoComplete="new-password" />
          </div>
          <button type="submit" data-tip="Save the settings. Use Test Connection afterwards to check they work.">Save</button>
        </form>
      )}

      {accounts.length > 0 && (
        <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0 }}>
          <thead>
            <tr>
              <th style={thStyle}>From Name</th>
              <th style={thStyle}>From Email</th>
              <th style={thStyle}>SMTP Host:Port</th>
              <th style={thStyle}>Credential<InfoTip text="Whether a password is stored. It is encrypted and never displayed." /></th>
              <th style={thStyle}>Last Test<InfoTip text="Result of the last Test Connection." /></th>
              <th style={thStyle}></th>
            </tr>
          </thead>
          <tbody>
            {accounts.map((acc) => {
              const testResult = testResults[acc.id];
              return (
                <tr key={acc.id}>
                  <td style={tdStyle}>{acc.displayName ?? "—"}</td>
                  <td style={tdStyle}>{acc.emailAddress}</td>
                  <td style={tdStyle}>{acc.host}:{acc.port}</td>
                  <td style={tdStyle}>{acc.hasCredential ? "Configured" : "Not set"}</td>
                  <td style={tdStyle}>
                    {testResult
                      ? (testResult.succeeded ? "✓ Success" : `✗ ${testResult.errorMessage}`)
                      : (acc.lastTestedAt ? (acc.lastTestSucceeded ? "✓ Success" : `✗ ${acc.lastTestError}`) : "Not tested")}
                  </td>
                  <td style={tdStyle}>
                    <button onClick={() => handleTestConnection(acc.id)} disabled={testingId === acc.id} data-tip="Log in to the SMTP server with the saved settings to check they work. No email is sent.">
                      {testingId === acc.id ? "Testing..." : "Test Connection"}
                    </button>{" "}
                    <button onClick={() => handleToggleActive(acc)} data-tip="Disable stops escalation emails being sent from this account. Enable turns it back on.">{acc.isActive ? "Disable" : "Enable"}</button>
                    <button
                      className="btn-danger"
                      disabled={acc.isActive}
                      title={acc.isActive ? "Disable this account before deleting" : "Permanently delete this account"}
                      onClick={() => handleDelete(acc)}
                      style={{ marginLeft: 8 }}
                    >
                      Delete
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
