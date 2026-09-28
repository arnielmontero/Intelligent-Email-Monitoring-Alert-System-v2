import { useEffect, useMemo, useState } from "react";
import { apiClient } from "../../api/client";
import type { AiCatalogModel, AiModelDto, AiProviderKeyCheck, AiProviderSettingsDto, TestAiModelResult } from "../../api/types";

interface ModelForm {
  modelIdentifier: string;
  displayName: string;
  fallbackOrder: number;
  timeoutSeconds: number;
  maxRetries: number;
}

const EMPTY_FORM: ModelForm = { modelIdentifier: "", displayName: "", fallbackOrder: 0, timeoutSeconds: 30, maxRetries: 2 };

function formatPrice(model: AiCatalogModel | undefined) {
  if (!model || model.promptPricePerMillion === null) return null;
  if (model.promptPricePerMillion === 0 && model.completionPricePerMillion === 0) return "Free";
  return `$${model.promptPricePerMillion} in / $${model.completionPricePerMillion} out per 1M tokens`;
}

export default function AiModelsPage() {
  const [models, setModels] = useState<AiModelDto[]>([]);
  const [catalog, setCatalog] = useState<AiCatalogModel[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [testingId, setTestingId] = useState<string | null>(null);
  const [testResults, setTestResults] = useState<Record<string, TestAiModelResult>>({});

  const [form, setForm] = useState<ModelForm>(EMPTY_FORM);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<ModelForm>(EMPTY_FORM);

  const catalogById = useMemo(() => new Map(catalog.map((m) => [m.id, m])), [catalog]);

  async function loadModels() {
    try {
      const res = await apiClient.get<AiModelDto[]>("/ai-models");
      setModels(res.data);
    } catch {
      setError("Failed to load AI model configuration.");
    } finally {
      setLoading(false);
    }
  }

  async function loadCatalog() {
    try {
      const res = await apiClient.get<AiCatalogModel[]>("/ai-models/provider/catalog");
      setCatalog(res.data);
    } catch {
      setCatalog([]);
    }
  }

  useEffect(() => {
    loadModels();
    loadCatalog();
  }, []);

  function pickFromCatalog(target: "add" | "edit", identifier: string) {
    const entry = catalogById.get(identifier);
    const update = (f: ModelForm) => ({
      ...f,
      modelIdentifier: identifier,
      displayName: f.displayName.trim() === "" && entry ? entry.name : f.displayName,
    });
    if (target === "add") setForm(update);
    else setEditForm(update);
  }

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await apiClient.post("/ai-models", {
        provider: "OpenRouter",
        modelIdentifier: form.modelIdentifier.trim(),
        displayName: form.displayName,
        enabled: true,
        isDefault: models.length === 0,
        taskCapability: "EmailClassification",
        timeoutSeconds: form.timeoutSeconds,
        maxRetries: form.maxRetries,
        fallbackOrder: form.fallbackOrder,
      });
      setForm(EMPTY_FORM);
      await loadModels();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to add AI model.");
    }
  }

  function startEdit(m: AiModelDto) {
    setEditingId(m.id);
    setEditForm({
      modelIdentifier: m.modelIdentifier,
      displayName: m.displayName,
      fallbackOrder: m.fallbackOrder,
      timeoutSeconds: m.timeoutSeconds,
      maxRetries: m.maxRetries,
    });
  }

  async function saveEdit(m: AiModelDto) {
    setError(null);
    try {
      await apiClient.put(`/ai-models/${m.id}`, {
        displayName: editForm.displayName,
        enabled: m.enabled,
        isDefault: m.isDefault,
        taskCapability: m.taskCapability,
        timeoutSeconds: editForm.timeoutSeconds,
        maxRetries: editForm.maxRetries,
        fallbackOrder: editForm.fallbackOrder,
        modelIdentifier: editForm.modelIdentifier.trim(),
      });
      setEditingId(null);
      await loadModels();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save AI model.");
    }
  }

  async function updateModel(m: AiModelDto, changes: Partial<AiModelDto>, failure: string) {
    setError(null);
    try {
      await apiClient.put(`/ai-models/${m.id}`, { ...m, ...changes });
      await loadModels();
    } catch (err: any) {
      setError(err.response?.data?.message ?? failure);
    }
  }

  async function handleDelete(m: AiModelDto) {
    if (!window.confirm(`Delete the model "${m.displayName}" (${m.modelIdentifier})?`)) return;
    setError(null);
    try {
      await apiClient.delete(`/ai-models/${m.id}`);
      await loadModels();
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

  const addPrice = formatPrice(catalogById.get(form.modelIdentifier.trim()));

  return (
    <div style={{ maxWidth: 1200 }}>
      <h1>AI Models</h1>
      <p style={mutedStyle}>
        The AI models used to read incoming email. If one fails, the next is tried in fallback order (lowest first).
        The API key is stored encrypted and is never shown again after saving.
      </p>

      {error && <div className="form-error">{error}</div>}

      <ProviderPanel onSaved={loadCatalog} />

      <h2 style={sectionTitle}>Models</h2>
      <table style={{ width: "100%", borderCollapse: "collapse", marginBottom: 28 }}>
        <thead>
          <tr>
            <th style={thStyle}>Model</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Default</th>
            <th style={thStyle}>Fallback</th>
            <th style={thStyle}>Timeout / Retries</th>
            <th style={thStyle}>Last test</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {models.map((m) => {
            const result = testResults[m.id];
            if (editingId === m.id) {
              const editPrice = formatPrice(catalogById.get(editForm.modelIdentifier.trim()));
              return (
                <tr key={m.id} style={{ background: "var(--color-surface)" }}>
                  <td style={tdStyle} colSpan={7}>
                    <div style={gridStyle}>
                      <Field label="Model (OpenRouter identifier)" wide>
                        <input
                          list="openrouter-models"
                          value={editForm.modelIdentifier}
                          onChange={(e) => pickFromCatalog("edit", e.target.value)}
                          style={fullWidth}
                          required
                        />
                        {editPrice && <div style={hintStyle}>{editPrice}</div>}
                      </Field>
                      <Field label="Display name">
                        <input value={editForm.displayName} onChange={(e) => setEditForm({ ...editForm, displayName: e.target.value })} style={fullWidth} />
                      </Field>
                      <Field label="Fallback order">
                        <input type="number" value={editForm.fallbackOrder} onChange={(e) => setEditForm({ ...editForm, fallbackOrder: Number(e.target.value) })} style={fullWidth} />
                      </Field>
                      <Field label="Timeout (seconds)">
                        <input type="number" min={1} max={300} value={editForm.timeoutSeconds} onChange={(e) => setEditForm({ ...editForm, timeoutSeconds: Number(e.target.value) })} style={fullWidth} />
                      </Field>
                      <Field label="Retries">
                        <input type="number" min={0} max={10} value={editForm.maxRetries} onChange={(e) => setEditForm({ ...editForm, maxRetries: Number(e.target.value) })} style={fullWidth} />
                      </Field>
                    </div>
                    <div style={{ display: "flex", gap: 8, marginTop: 12 }}>
                      <button className="btn-primary" onClick={() => saveEdit(m)}>Save</button>
                      <button onClick={() => setEditingId(null)}>Cancel</button>
                    </div>
                  </td>
                </tr>
              );
            }
            return (
              <tr key={m.id}>
                <td style={tdStyle}>
                  {m.displayName}
                  <div style={hintStyle}>{m.provider} / {m.modelIdentifier}</div>
                  {formatPrice(catalogById.get(m.modelIdentifier)) && <div style={hintStyle}>{formatPrice(catalogById.get(m.modelIdentifier))}</div>}
                </td>
                <td style={tdStyle}>{m.enabled ? "Yes" : "No"}</td>
                <td style={tdStyle}>
                  {m.isDefault ? "Yes" : <button onClick={() => updateModel(m, { isDefault: true }, "Failed to set default AI model.")}>Set default</button>}
                </td>
                <td style={tdStyle}>{m.fallbackOrder}</td>
                <td style={tdStyle}>{m.timeoutSeconds}s / {m.maxRetries}</td>
                <td style={tdStyle}>
                  {result
                    ? <span style={{ color: result.succeeded ? "var(--color-success)" : "var(--color-danger)" }}>
                        {result.succeeded ? `✓ OK (${result.durationMs}ms)` : `✗ ${result.errorMessage}`}
                      </span>
                    : <span style={hintStyle}>Not tested this session</span>}
                </td>
                <td style={{ ...tdStyle, whiteSpace: "nowrap" }}>
                  <div style={{ display: "flex", gap: 6 }}>
                    <button onClick={() => handleTest(m.id)} disabled={testingId !== null}>{testingId === m.id ? "Testing..." : "Test"}</button>
                    <button onClick={() => startEdit(m)}>Edit</button>
                    <button onClick={() => updateModel(m, { enabled: !m.enabled }, "Failed to update AI model.")}>{m.enabled ? "Disable" : "Enable"}</button>
                    <button className="btn-danger" onClick={() => handleDelete(m)}>Delete</button>
                  </div>
                </td>
              </tr>
            );
          })}
          {models.length === 0 && <tr><td style={tdStyle} colSpan={7}>No models configured.</td></tr>}
        </tbody>
      </table>

      <h2 style={sectionTitle}>Add model</h2>
      <form onSubmit={handleCreate} style={panelStyle}>
        <div style={gridStyle}>
          <Field label="Model (OpenRouter identifier)" wide>
            <input
              list="openrouter-models"
              value={form.modelIdentifier}
              onChange={(e) => pickFromCatalog("add", e.target.value)}
              placeholder={catalog.length ? `Search ${catalog.length} models, e.g. openai/gpt-4o-mini` : "e.g. openai/gpt-4o-mini"}
              style={fullWidth}
              required
            />
            <div style={hintStyle}>
              {addPrice ?? (catalog.length ? "Type to search OpenRouter's model list." : "Model list unavailable — you can still type an identifier.")}
            </div>
          </Field>
          <Field label="Display name">
            <input value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} placeholder="Filled from the model list" style={fullWidth} />
          </Field>
          <Field label="Fallback order">
            <input type="number" value={form.fallbackOrder} onChange={(e) => setForm({ ...form, fallbackOrder: Number(e.target.value) })} style={fullWidth} />
          </Field>
          <Field label="Timeout (seconds)">
            <input type="number" min={1} max={300} value={form.timeoutSeconds} onChange={(e) => setForm({ ...form, timeoutSeconds: Number(e.target.value) })} style={fullWidth} />
          </Field>
          <Field label="Retries">
            <input type="number" min={0} max={10} value={form.maxRetries} onChange={(e) => setForm({ ...form, maxRetries: Number(e.target.value) })} style={fullWidth} />
          </Field>
        </div>
        <div style={{ marginTop: 12 }}>
          <button type="submit" className="btn-primary">Add model</button>
        </div>
      </form>

      <datalist id="openrouter-models">
        {catalog.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
      </datalist>
    </div>
  );
}

function ProviderPanel({ onSaved }: { onSaved: () => void }) {
  const [settings, setSettings] = useState<AiProviderSettingsDto | null>(null);
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [check, setCheck] = useState<AiProviderKeyCheck | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  function apply(data: AiProviderSettingsDto) {
    setSettings(data);
    setBaseUrl(data.baseUrl);
  }

  useEffect(() => {
    apiClient.get<AiProviderSettingsDto>("/ai-models/provider").then((r) => apply(r.data)).catch(() => setError("Failed to load OpenRouter settings."));
  }, []);

  async function save(clearApiKey = false) {
    setError(null);
    setNotice(null);
    setCheck(null);
    setSaving(true);
    try {
      const res = await apiClient.put<AiProviderSettingsDto>("/ai-models/provider", {
        baseUrl: baseUrl.trim() === settings?.defaultBaseUrl ? null : baseUrl,
        apiKey: clearApiKey ? null : apiKey,
        clearApiKey,
      });
      apply(res.data);
      setApiKey("");
      setNotice(clearApiKey ? "API key removed." : "Saved.");
      onSaved();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to save OpenRouter settings.");
    } finally {
      setSaving(false);
    }
  }

  async function test() {
    setError(null);
    setNotice(null);
    setTesting(true);
    try {
      const res = await apiClient.post<AiProviderKeyCheck>("/ai-models/provider/test");
      setCheck(res.data);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Connection test failed.");
    } finally {
      setTesting(false);
    }
  }

  if (!settings) return error ? <div className="form-error">{error}</div> : null;

  const keyStatus =
    settings.keySource === "CMS" ? `Configured here — ends in ${settings.apiKeyHint}`
    : settings.keySource === "Environment" ? `From server .env (OPENROUTER_API_KEY) — ends in ${settings.apiKeyHint}`
    : "Not configured — every email goes to manual review until a key is set";

  return (
    <section style={panelStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", flexWrap: "wrap", gap: 8 }}>
        <h2 style={{ ...sectionTitle, margin: 0 }}>OpenRouter connection</h2>
        <span style={{ ...pillStyle, background: settings.hasApiKey ? "#166534" : "var(--color-danger)" }}>
          {settings.hasApiKey ? "API key set" : "No API key"}
        </span>
      </div>
      <p style={{ ...hintStyle, marginTop: 4 }}>
        Get a key at openrouter.ai → Keys. A key saved here overrides the server's .env value and takes effect immediately.
      </p>

      {error && <div className="form-error">{error}</div>}
      {notice && <div style={noticeStyle}>{notice}</div>}

      <div style={gridStyle}>
        <Field label="API base URL" wide>
          <input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder={settings.defaultBaseUrl} style={fullWidth} />
          <div style={hintStyle}>Default: {settings.defaultBaseUrl}</div>
        </Field>
        <Field label="API key" wide>
          <input
            type="password"
            autoComplete="off"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            placeholder={settings.hasApiKey ? "Leave blank to keep the current key" : "sk-or-v1-..."}
            style={fullWidth}
          />
          <div style={hintStyle}>{keyStatus}</div>
        </Field>
      </div>

      <div style={{ display: "flex", gap: 8, marginTop: 12, flexWrap: "wrap", alignItems: "center" }}>
        <button className="btn-primary" disabled={saving} onClick={() => save()}>{saving ? "Saving..." : "Save"}</button>
        <button disabled={testing || !settings.hasApiKey} onClick={test}>{testing ? "Testing..." : "Test connection"}</button>
        {settings.keySource === "CMS" && (
          <button className="btn-danger" onClick={() => window.confirm("Remove the API key saved here?") && save(true)}>Remove key</button>
        )}
        {check && (
          <span style={{ color: check.succeeded ? "var(--color-success)" : "var(--color-danger)", fontSize: 13 }}>
            {check.succeeded ? "✓" : "✗"} {check.message}
          </span>
        )}
      </div>
      {settings.updatedByEmail && (
        <div style={{ ...hintStyle, marginTop: 8 }}>
          Last changed by {settings.updatedByEmail}{settings.updatedAt ? ` on ${new Date(settings.updatedAt).toLocaleString()}` : ""}
        </div>
      )}
    </section>
  );
}

function Field({ label, wide, children }: { label: string; wide?: boolean; children: React.ReactNode }) {
  return (
    <div style={{ gridColumn: wide ? "span 2" : undefined, display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
      <label>{label}</label>
      {children}
    </div>
  );
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 18, margin: "24px 0 12px" };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 16, background: "var(--color-surface)", marginBottom: 8 };
const gridStyle: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12, marginTop: 12 };
const fullWidth: React.CSSProperties = { width: "100%" };
const noticeStyle: React.CSSProperties = { background: "#166534", color: "#fff", padding: "8px 12px", borderRadius: 6, margin: "8px 0" };
const pillStyle: React.CSSProperties = { color: "#fff", fontSize: 12, padding: "2px 10px", borderRadius: 999 };
const thStyle: React.CSSProperties = { textAlign: "left", borderBottom: "1px solid var(--color-border)", padding: "8px" };
const tdStyle: React.CSSProperties = { borderBottom: "1px solid var(--color-row-border)", padding: "10px 8px", verticalAlign: "top" };
