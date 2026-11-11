import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AiProviderKeyCheck, AiUsageCallDto, AiUsageSummaryDto, PagedResult } from "../../api/types";
import Pagination from "../../components/Pagination";
import { Badge, StatLabel } from "../../components/StatCard";
import { InfoTip, TipLabel } from "../../components/help/Tooltip";
import AiSpendAnalytics from "./AiSpendAnalytics";
import { PURPOSE_LABELS, number, usd } from "./format";

const PAGE_SIZE = 10;

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
            <StatLabel help={`AI spend for ${p.label.toLowerCase()}, with the number of calls and tokens. Failed calls are shown in red.`}>{p.label}</StatLabel>
            <div className="stat-value">{usd(p.costUsd)}</div>
            <div style={hintStyle}>
              {number(p.calls)} call{p.calls === 1 ? "" : "s"} · {number(p.tokens)} tokens
              {p.failedCalls > 0 && <> · <span style={{ color: "var(--color-danger)" }}>{p.failedCalls} failed</span></>}
            </div>
            {p.callsWithoutCost > 0 && <div style={hintStyle}>{p.callsWithoutCost} call(s) had no cost reported</div>}
          </div>
        ))}
        <div className="stat-card">
          <StatLabel help="Live figures from OpenRouter for the API key in use: how much it has spent in total and how much is left if a spending limit is set.">OpenRouter account</StatLabel>
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

      <AiSpendAnalytics />

      <h2 style={{ ...sectionTitle, display: "flex", alignItems: "center" }}>Calls<InfoTip text="Every single request sent to the AI, newest first. Use the filters to find failures or a particular model." /></h2>
      <div style={{ display: "flex", gap: 12, alignItems: "end", flexWrap: "wrap", marginBottom: 12 }}>
        <div style={fieldStyle}>
          <label><TipLabel tip="Show only calls answered by one AI model.">Model</TipLabel></label>
          <select value={model} onChange={(e) => setModel(e.target.value)}>
            <option value="">All models</option>
            {models.map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label><TipLabel tip="Show only calls made for one reason, e.g. email classification or a resolution check.">Purpose</TipLabel></label>
          <select value={purpose} onChange={(e) => setPurpose(e.target.value)}>
            <option value="">All</option>
            {Object.entries(PURPOSE_LABELS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </div>
        <div style={fieldStyle}>
          <label><TipLabel tip="Show only calls that worked, or only ones that failed. Hover a failed badge to read the error.">Result</TipLabel></label>
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
            <th style={thRight}>Tokens (in / out)<InfoTip text="Text sent to the AI / text it wrote back, in tokens. Cost is based on these." /></th>
            <th style={thRight}>Cost</th>
            <th style={thRight}>Duration<InfoTip text="How long the AI took to answer, in seconds." /></th>
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
