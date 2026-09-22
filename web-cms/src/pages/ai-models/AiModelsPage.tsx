import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AiModelDto, TestAiModelResult } from "../../api/types";

export default function AiModelsPage() {
  const [models, setModels] = useState<AiModelDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [testingId, setTestingId] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, TestAiModelResult>>({});

  const [modelIdentifier, setModelIdentifier] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [fallbackOrder, setFallbackOrder] = useState(0);

  async function load() {
    setLoading(true);
    try {
      const res = await apiClient.get<AiModelDto[]>("/ai-models");
      setModels(res.data);
    } catch {
      setError("Failed to load AI model configuration.");
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
      await apiClient.post("/ai-models", {
        provider: "OpenRouter",
        modelIdentifier,
        displayName,
        enabled: true,
        isDefault: models.length === 0,
        taskCapability: "EmailClassification",
        timeoutSeconds: 30,
        maxRetries: 2,
        fallbackOrder,
      });
      setModelIdentifier("");
      setDisplayName("");
      setFallbackOrder(0);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to add AI model.");
    }
  }

  async function handleToggleEnabled(model: AiModelDto) {
    try {
      await apiClient.put(`/ai-models/${model.id}`, { ...model, enabled: !model.enabled });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update AI model.");
    }
  }

  async function handleSetDefault(model: AiModelDto) {
    try {
      await apiClient.put(`/ai-models/${model.id}`, { ...model, isDefault: true });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to set default AI model.");
    }
  }

  async function handleDelete(id: string) {
    try {
      await apiClient.delete(`/ai-models/${id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete AI model.");
    }
  }

  async function handleTest(id: string) {
    setTestingId(id);
    setError(null);
    try {
      const res = await apiClient.post<TestAiModelResult>(`/ai-models/${id}/test`);
      setTestResults((prev) => ({ ...prev, [id]: res.data }));
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to test AI model.");
    } finally {
      setTestingId(null);
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>AI Models</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Requirements §82 — the AI model catalog is never hardcoded. Enabled models for the
        EmailClassification task are tried in FallbackOrder, lowest first; the OpenRouter API key
        itself is configured server-side only (never shown or returned here).
      </p>

      {error && <div className="form-error">{error}</div>}

      <table style={{ width: "100%", borderCollapse: "collapse", marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Model</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Default</th>
            <th style={thStyle}>Fallback Order</th>
            <th style={thStyle}>Last Test</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {models.map((m) => {
            const result = testResults[m.id];
            return (
              <tr key={m.id}>
                <td style={tdStyle}>
                  {m.displayName}
                  <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>{m.provider} / {m.modelIdentifier}</div>
                </td>
                <td style={tdStyle}>{m.enabled ? "Yes" : "No"}</td>
                <td style={tdStyle}>{m.isDefault ? "Yes" : <button onClick={() => handleSetDefault(m)}>Set Default</button>}</td>
                <td style={tdStyle}>{m.fallbackOrder}</td>
                <td style={tdStyle}>
                  {result ? (result.succeeded ? `✓ OK (${result.durationMs}ms)` : `✗ ${result.errorMessage}`) : "Not tested this session"}
                </td>
                <td style={tdStyle}>
                  <button onClick={() => handleTest(m.id)} disabled={testingId !== null} style={{ marginRight: 8 }}>
                    {testingId === m.id ? "Testing..." : "Test"}
                  </button>
                  <button onClick={() => handleToggleEnabled(m)} style={{ marginRight: 8 }}>
                    {m.enabled ? "Disable" : "Enable"}
                  </button>
                  <button onClick={() => handleDelete(m.id)}>Delete</button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      <h2 style={{ fontSize: 16 }}>Add Model</h2>
      <form onSubmit={handleCreate} style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "end" }}>
        <div>
          <label>Model identifier (OpenRouter)</label><br />
          <input value={modelIdentifier} onChange={(e) => setModelIdentifier(e.target.value)} placeholder="openai/gpt-4o-mini" required />
        </div>
        <div>
          <label>Display name</label><br />
          <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </div>
        <div>
          <label>Fallback order</label><br />
          <input type="number" value={fallbackOrder} onChange={(e) => setFallbackOrder(Number(e.target.value))} style={{ width: 80 }} />
        </div>
        <button type="submit">Add</button>
      </form>
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid #334155", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid #1e293b", padding: "8px" };
