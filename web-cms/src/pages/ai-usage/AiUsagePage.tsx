import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AiProviderKeyCheck, AiUsageCallDto, AiUsageSummaryDto, PagedResult } from "../../api/types";
import Pagination from "../../components/Pagination";
import { Badge } from "../../components/StatCard";

const PAGE_SIZE = 10;

const PURPOSE_LABELS: Record<string, string> = {
  EmailClassification: "Email classification",
  ModelTest: "Model test",
  ProfileTest: "Profile test",
};

function usd(value: number | null | undefined, digits = 4) {
  if (value === null || value === undefined) return "—";
  if (value === 0) return "$0";
  return value < 0.0001 ? `$${value.toFixed(6)}` : `$${value.toFixed(digits)}`;
}

function number(value: number | null | undefined) {
  return value === null || value === undefined ? "—" : value.toLocaleString();
}

export default function AiUsagePage() {
  const [summary, setSummary] = useState<AiUsageSummaryDto | null>(null);
  const [account, setAccount] = useState<AiProviderKeyCheck | null>(null);
  const [calls, setCalls] = useState<PagedResult<AiUsageCallDto> | null>(null);
  const [models, setModels] = useState<string[]>([]);
  const [page, setPage] = useState(1);
  const [model, setModel] = useState("");
  const [purpose, setPurpose] = useState("");
  const [status, setStatus] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function loadCalls(nextPage: number) {
    try {
      const params = new URLSearchParams({ page: String(nextPage), pageSize: String(PAGE_SIZE) });
      if (model) params.set("model", model);
      if (purpose) params.set("purpose", purpose);
      if (status) params.set("succeeded", status === "ok" ? "true" : "false");
      const res = await apiClient.get<PagedResult<AiUsageCallDto>>(`/ai-usage/calls?${params.toString()}`);
      setCalls(res.data);
      setPage(res.data.page);
    } catch (err: any) {
      setError(err.response?.status === 403
        ? "You do not have permission to view AI usage (requires Administrator)."
        : "Failed to load AI calls.");
    }
  }

  useEffect(() => {
    apiClient.get<AiUsageSummaryDto>("/ai-usage/summary").then((r) => setSummary(r.data)).catch(() => undefined);
    apiClient.get<AiProviderKeyCheck>("/ai-usage/account").then((r) => setAccount(r.data)).catch(() => undefined);
    apiClient.get<string[]>("/ai-usage/models").then((r) => setModels(r.data)).catch(() => undefined);
  }, []);

  useEffect(() => {
    loadCalls(1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model, purpose, status]);

  return (
    <div style={{ maxWidth: 1200 }}>
      <h1>AI Usage &amp; Cost</h1>
      <p style={mutedStyle}>
        Every OpenRouter call is recorded — including retries, fallbacks, failures and test calls — with the cost OpenRouter
        reports for it.{summary && ` Days are counted in ${summary.timeZone} (System Settings).`}
      </p>

      {error && <div className="form-error">{error}</div>}

      <div style={cardGridStyle}>
        {summary?.periods.map((p) => (
          <div key={p.label} className="stat-card">
            <div className="stat-label">{p.label}</div>
            <div className="stat-value">{usd(p.costUsd)}</div>
            <div style={hintStyle}>
              {number(p.calls)} call{p.calls === 1 ? "" : "s"} · {number(p.tokens)} tokens
              {p.failedCalls > 0 && <> · <span style={{ color: "var(--color-danger)" }}>{p.failedCalls} failed</span></>}
            </div>
            {p.callsWithoutCost > 0 && <div style={hintStyle}>{p.callsWithoutCost} call(s) had no cost reported</div>}
          </div>
        ))}
        <div className="stat-card">
          <div className="stat-label">OpenRouter account</div>
          {!account ? (
            <div style={hintStyle}>Loading…</div>
          ) : account.succeeded ? (
            <>
              <div className="stat-value">{usd(account.usageUsd, 2)}</div>
              <div style={hintStyle}>
                used on this key{account.limitUsd !== null ? ` of ${usd(account.limitUsd, 2)} limit` : " (no spending limit)"}
                {account.limitRemainingUsd !== null && ` · ${usd(account.limitRemainingUsd, 2)} left`}
              </div>
            </>
          ) : (
            <div style={{ ...hintStyle, color: "var(--color-danger)", marginTop: 6 }}>{account.message}</div>
          )}
        </div>
      </div>

      <h2 style={sectionTitle}>Cost by model (last 30 days)</h2>
      <table style={tableStyle}>
        <thead>
          <tr>
            <th style={thStyle}>Model</th>
            <th style={thRight}>Calls</th>
            <th style={thRight}>Failed</th>
            <th style={thRight}>Tokens</th>
            <th style={thRight}>Cost</th>
            <th style={thRight}>Avg / call</th>
          </tr>
        </thead>
        <tbody>
          {summary?.byModelLast30Days.map((m) => (
            <tr key={m.modelIdentifier}>
              <td style={tdStyle}>{m.modelIdentifier}</td>
              <td style={tdRight}>{number(m.calls)}</td>
              <td style={tdRight}>{m.failedCalls > 0 ? <span style={{ color: "var(--color-danger)" }}>{m.failedCalls}</span> : 0}</td>
              <td style={tdRight}>{number(m.tokens)}</td>
              <td style={tdRight}>{usd(m.costUsd)}</td>
              <td style={tdRight}>{usd(m.averageCostPerCall, 6)}</td>
            </tr>
          ))}
          {summary && summary.byModelLast30Days.length === 0 && (
            <tr><td style={tdStyle} colSpan={6}>No AI calls in the last 30 days.</td></tr>
          )}
        </tbody>
      </table>

      <h2 style={sectionTitle}>Calls</h2>
      <div style={{ display: "flex", gap: 12, alignItems: "end", flexWrap: "wrap", marginBottom: 12 }}>
        <div style={fieldStyle}>
          <label>Model</label>
          <select value={model} onChange={(e) => setModel(e.target.value)}>
            <option value="">All models</option>
            {models.map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label>Purpose</label>
          <select value={purpose} onChange={(e) => setPurpose(e.target.value)}>
            <option value="">All</option>
            {Object.entries(PURPOSE_LABELS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label>Result</label>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            <option value="ok">Succeeded</option>
            <option value="failed">Failed</option>
          </select>
        </div>
      </div>

      <table style={tableStyle}>
        <thead>
          <tr>
            <th style={thStyle}>When</th>
            <th style={thStyle}>Model</th>
            <th style={thStyle}>Purpose</th>
            <th style={thStyle}>Result</th>
            <th style={thRight}>Tokens (in / out)</th>
            <th style={thRight}>Cost</th>
            <th style={thRight}>Duration</th>
          </tr>
        </thead>
        <tbody>
          {calls?.items.map((c) => (
            <tr key={c.id}>
              <td style={tdStyle}>{new Date(c.occurredAt).toLocaleString()}</td>
              <td style={tdStyle}>{c.modelIdentifier}</td>
              <td style={tdStyle}>
                {PURPOSE_LABELS[c.purpose] ?? c.purpose}
                {c.emailSubject && <div style={hintStyle} title={c.emailSubject}>“{truncate(c.emailSubject, 48)}”</div>}
              </td>
              <td style={tdStyle}>
                {c.succeeded
                  ? <Badge tone="good">OK</Badge>
                  : <span title={c.errorMessage ?? undefined}><Badge tone="bad">Failed</Badge> <span style={hintStyle}>{truncate(c.errorMessage ?? "", 60)}</span></span>}
              </td>
              <td style={tdRight}>
                {c.totalTokens === null ? "—" : `${number(c.promptTokens)} / ${number(c.completionTokens)}`}
              </td>
              <td style={tdRight}>{usd(c.costUsd, 6)}</td>
              <td style={tdRight}>{(c.durationMs / 1000).toFixed(1)}s</td>
            </tr>
          ))}
          {calls && calls.items.length === 0 && <tr><td style={tdStyle} colSpan={7}>No AI calls recorded yet.</td></tr>}
        </tbody>
      </table>

      {calls && calls.totalCount > 0 && (
        <Pagination
          page={page}
          totalPages={calls.totalPages}
          totalCount={calls.totalCount}
          pageSize={calls.pageSize}
          onChange={(p) => loadCalls(p)}
        />
      )}
    </div>
  );
}

function truncate(text: string, max: number) {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

const mutedStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 13 };
const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 15, fontWeight: 600, margin: "28px 0 12px" };
const cardGridStyle: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(190px, 1fr))", gap: 12, marginTop: 16 };
const fieldStyle: React.CSSProperties = { display: "flex", flexDirection: "column", gap: 4, minWidth: 180 };
const tableStyle: React.CSSProperties = { width: "100%", borderCollapse: "separate", borderSpacing: 0 };
const thStyle: React.CSSProperties = { textAlign: "left" };
const thRight: React.CSSProperties = { ...thStyle, textAlign: "right" };
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
const tdRight: React.CSSProperties = { ...tdStyle, textAlign: "right", whiteSpace: "nowrap" };
