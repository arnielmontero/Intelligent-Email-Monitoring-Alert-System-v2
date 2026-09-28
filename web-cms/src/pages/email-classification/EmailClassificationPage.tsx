import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { ClassificationProfileDto, TestClassificationResult } from "../../api/types";

export default function EmailClassificationPage() {
  const [profiles, setProfiles] = useState<ClassificationProfileDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [categories, setCategories] = useState("");
  const [includeDefinitions, setIncludeDefinitions] = useState("");
  const [excludeDefinitions, setExcludeDefinitions] = useState("");

  const [testProfileId, setTestProfileId] = useState("");
  const [testSubject, setTestSubject] = useState("");
  const [testContent, setTestContent] = useState("");
  const [testRunning, setTestRunning] = useState(false);
  const [testResult, setTestResult] = useState<TestClassificationResult | null>(null);

  async function load() {
    setLoading(true);
    try {
      const res = await apiClient.get<ClassificationProfileDto[]>("/classification-profiles");
      setProfiles(res.data);
    } catch {
      setError("Failed to load classification profiles.");
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
      await apiClient.post("/classification-profiles", {
        name,
        description: description || null,
        enabled: true,
        categories,
        includeDefinitions,
        excludeDefinitions,
        exampleSubject: null,
        exampleContent: null,
        expectedClassification: null,
        highConfidenceThreshold: null,
        mediumConfidenceThreshold: null,
        treatMediumConfidenceAsReviewRequired: null,
      });
      setName("");
      setDescription("");
      setCategories("");
      setIncludeDefinitions("");
      setExcludeDefinitions("");
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to create classification profile.");
    }
  }

  async function handleToggleEnabled(profile: ClassificationProfileDto) {
    try {
      await apiClient.put(`/classification-profiles/${profile.id}`, { ...profile, enabled: !profile.enabled });
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to update classification profile.");
    }
  }

  async function handleDelete(id: string) {
    try {
      await apiClient.delete(`/classification-profiles/${id}`);
      await load();
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to delete classification profile.");
    }
  }

  async function handleTest(e: React.FormEvent) {
    e.preventDefault();
    setTestRunning(true);
    setTestResult(null);
    setError(null);
    try {
      const res = await apiClient.post<TestClassificationResult>("/classification-profiles/test", {
        subject: testSubject,
        content: testContent,
        classificationProfileId: testProfileId || null,
      });
      setTestResult(res.data);
    } catch (err: any) {
      setError(err.response?.data?.message ?? "Failed to run test classification.");
    } finally {
      setTestRunning(false);
    }
  }

  if (loading) return <p>Loading...</p>;

  return (
    <div>
      <h1>Email Classification</h1>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Classification profiles tell the AI what kind of email matters (categories, and words to include or exclude) and how
        sure it must be. Assign a profile to a mailbox on the Email Accounts page.
      </p>

      {error && <div className="form-error">{error}</div>}

      <h2 style={{ fontSize: 16, marginTop: 24 }}>Profiles</h2>
      <table style={{ width: "100%", borderCollapse: "separate", borderSpacing: 0, marginBottom: 24 }}>
        <thead>
          <tr>
            <th style={thStyle}>Name</th>
            <th style={thStyle}>Enabled</th>
            <th style={thStyle}>Categories</th>
            <th style={thStyle}></th>
          </tr>
        </thead>
        <tbody>
          {profiles.map((p) => (
            <tr key={p.id}>
              <td style={tdStyle}>
                {p.name}
                {p.description && <div style={{ fontSize: 12, color: "var(--color-text-muted)" }}>{p.description}</div>}
              </td>
              <td style={tdStyle}>{p.enabled ? "Yes" : "No"}</td>
              <td style={tdStyle}>{p.categories.split("\n").filter(Boolean).length} categories</td>
              <td style={tdStyle}>
                <button onClick={() => handleToggleEnabled(p)} style={{ marginRight: 8 }}>
                  {p.enabled ? "Disable" : "Enable"}
                </button>
                <button onClick={() => handleDelete(p.id)}>Delete</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <h2 style={{ fontSize: 16 }}>New Profile</h2>
      <form onSubmit={handleCreate} style={{ marginBottom: 32, display: "flex", flexDirection: "column", gap: 8, maxWidth: 480 }}>
        <div>
          <label>Name</label><br />
          <input value={name} onChange={(e) => setName(e.target.value)} required style={{ width: "100%" }} />
        </div>
        <div>
          <label>Description</label><br />
          <input value={description} onChange={(e) => setDescription(e.target.value)} style={{ width: "100%" }} />
        </div>
        <div>
          <label>Categories (one per line)</label><br />
          <textarea value={categories} onChange={(e) => setCategories(e.target.value)} rows={4} style={{ width: "100%" }} />
        </div>
        <div>
          <label>Include definitions (one per line)</label><br />
          <textarea value={includeDefinitions} onChange={(e) => setIncludeDefinitions(e.target.value)} rows={4} style={{ width: "100%" }} />
        </div>
        <div>
          <label>Exclude definitions (one per line)</label><br />
          <textarea value={excludeDefinitions} onChange={(e) => setExcludeDefinitions(e.target.value)} rows={4} style={{ width: "100%" }} />
        </div>
        <button type="submit">Create Profile</button>
      </form>

      <h2 style={{ fontSize: 16 }}>Test classification</h2>
      <p style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Never creates a real notification or Case — runs the same deterministic filter + AI
        pipeline against ad-hoc subject/content only.
      </p>
      <form onSubmit={handleTest} style={{ display: "flex", flexDirection: "column", gap: 8, maxWidth: 480 }}>
        <div>
          <label>Profile</label><br />
          <select value={testProfileId} onChange={(e) => setTestProfileId(e.target.value)} style={{ width: "100%" }}>
            <option value="">(none)</option>
            {profiles.map((p) => (
              <option key={p.id} value={p.id}>{p.name}</option>
            ))}
          </select>
        </div>
        <div>
          <label>Subject</label><br />
          <input value={testSubject} onChange={(e) => setTestSubject(e.target.value)} style={{ width: "100%" }} />
        </div>
        <div>
          <label>Content</label><br />
          <textarea value={testContent} onChange={(e) => setTestContent(e.target.value)} rows={4} style={{ width: "100%" }} />
        </div>
        <button type="submit" disabled={testRunning}>{testRunning ? "Testing..." : "Test"}</button>
      </form>

      {testResult && (
        <div style={{ marginTop: 16, padding: 12, border: "1px solid var(--color-border)", borderRadius: 4 }}>
          {testResult.error ? (
            <p>✗ {testResult.error}</p>
          ) : (
            <>
              <p><strong>Relevant:</strong> {testResult.relevant ? "Yes" : "No"}</p>
              <p><strong>Category:</strong> {testResult.category}</p>
              <p><strong>Priority:</strong> {testResult.priority}</p>
              <p><strong>Confidence:</strong> {(testResult.confidence * 100).toFixed(0)}%</p>
              <p><strong>Summary:</strong> {testResult.summary}</p>
              <p><strong>Deterministic filter:</strong> {testResult.deterministicFilterOutcome}</p>
              <p><strong>Final decision:</strong> {testResult.finalDecision}</p>
            </>
          )}
        </div>
      )}
    </div>
  );
}

const thStyle: React.CSSProperties = { textAlign: "left" };
const tdStyle: React.CSSProperties = {};
