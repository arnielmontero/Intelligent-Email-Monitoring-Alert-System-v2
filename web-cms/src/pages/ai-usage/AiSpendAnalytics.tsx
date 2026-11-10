import { useEffect, useState } from "react";
import { apiClient } from "../../api/client";
import type { AiAnalyticsDto } from "../../api/types";
import ColumnChart, { type ChartPoint, type ChartSeries } from "../../components/ColumnChart";
import { compact, longDate, number, purposeLabel, shortDate, usd, usdTick } from "./format";

const RANGES = [
  { days: 7, label: "Last 7 days" },
  { days: 30, label: "Last 30 days" },
  { days: 90, label: "Last 90 days" },
];

const SPEND_SERIES: ChartSeries[] = [{ key: "cost", label: "Spend (USD)", color: "var(--series-1)" }];
const TOKEN_SERIES: ChartSeries[] = [
  { key: "in", label: "Input tokens (what we send)", color: "var(--series-2)" },
  { key: "out", label: "Output tokens (what the AI answers)", color: "var(--series-3)" },
];

/**
 * Spend and token analytics for the AI Usage & Cost page. One date range scopes everything below it: the
 * headline numbers, the two daily charts, where the money goes (by model and by purpose) and how much the
 * built-in no-AI filter saves. Every chart value is also in a table (the "Show as table" switch).
 */
export default function AiSpendAnalytics() {
  const [days, setDays] = useState(30);
  const [data, setData] = useState<AiAnalyticsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showTable, setShowTable] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    apiClient.get<AiAnalyticsDto>(`/ai-usage/analytics?days=${days}`)
      .then((r) => { if (!cancelled) { setData(r.data); setError(null); } })
      .catch(() => { if (!cancelled) setError("Failed to load the spending analytics."); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [days]);

  const spendPoints: ChartPoint[] = (data?.daily ?? []).map((d) => ({
    id: d.date,
    title: longDate(d.date),
    axisLabel: shortDate(d.date),
    values: { cost: d.costUsd },
    details: [`${number(d.calls)} call${d.calls === 1 ? "" : "s"}${d.failedCalls > 0 ? ` · ${d.failedCalls} failed` : ""}`],
  }));
  const tokenPoints: ChartPoint[] = (data?.daily ?? []).map((d) => ({
    id: d.date,
    title: longDate(d.date),
    axisLabel: shortDate(d.date),
    values: { in: d.promptTokens, out: d.completionTokens },
    details: [`${number(d.promptTokens + d.completionTokens)} tokens in total`],
  }));

  const t = data?.totals;
  const e = data?.efficiency;
  const f = data?.forecast;

  return (
    <section aria-label="Spending analytics">
      <h2 style={sectionTitle}>Spending analytics</h2>

      {/* One filter row above everything it scopes. */}
      <div style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap", marginBottom: 14 }}>
        <div role="group" aria-label="Date range" style={{ display: "inline-flex", border: "1px solid var(--color-border)", borderRadius: 8, overflow: "hidden" }}>
          {RANGES.map((r) => (
            <button
              key={r.days}
              type="button"
              aria-pressed={days === r.days}
              onClick={() => setDays(r.days)}
              style={{
                padding: "6px 14px", border: "none", cursor: "pointer", fontSize: 13,
                background: days === r.days ? "var(--color-accent-soft)" : "transparent",
                color: days === r.days ? "var(--color-text)" : "var(--color-text-muted)",
                fontWeight: days === r.days ? 600 : 400,
              }}
            >
              {days === r.days ? "✓ " : ""}{r.label}
            </button>
          ))}
        </div>
        {data && <span style={hintStyle}>{longDate(data.from)} to {longDate(data.to)} · days in {data.timeZone}</span>}
      </div>

      {error && <div className="form-error">{error}</div>}
      {!data && !error && <p style={hintStyle}>Loading…</p>}

      {data && t && e && f && (
        // While a new range loads, the previous numbers stay in place, dimmed, instead of jumping.
        <div style={{ opacity: loading ? 0.55 : 1, transition: "opacity 120ms" }} aria-busy={loading}>
          <div style={cardGridStyle}>
            <div className="stat-card">
              <div className="stat-label">Spend</div>
              <div className="stat-value">{usd(t.costUsd)}</div>
              <div style={hintStyle}>{number(t.calls)} call{t.calls === 1 ? "" : "s"} in {data.days} days</div>
              {t.callsWithoutCost > 0 && <div style={hintStyle}>{t.callsWithoutCost} had no cost reported</div>}
            </div>
            <div className="stat-card">
              <div className="stat-label">Tokens</div>
              <div className="stat-value">{compact(t.totalTokens)}</div>
              <div style={hintStyle}>{compact(t.promptTokens)} in · {compact(t.completionTokens)} out</div>
            </div>
            <div className="stat-card">
              <div className="stat-label">Average per call</div>
              <div className="stat-value">{usd(t.averageCostPerCallUsd, 6)}</div>
              <div style={hintStyle}>{compact(t.averageTokensPerCall)} tokens · {(t.averageDurationMs / 1000).toFixed(1)}s</div>
            </div>
            <div className="stat-card">
              <div className="stat-label">Wasted on failed calls</div>
              <div className="stat-value">{usd(t.failedCostUsd)}</div>
              <div style={hintStyle}>
                {t.failedCalls === 0 ? "No failed calls" : <><span style={{ color: "var(--color-danger)" }}>▲ {number(t.failedCalls)} failed</span> of {number(t.calls)}</>}
              </div>
            </div>
            <div className="stat-card">
              <div className="stat-label">Projected this month</div>
              <div className="stat-value">{usd(f.projectedMonthUsd)}</div>
              <div style={hintStyle}>
                {usd(f.monthToDateUsd)} so far · running cost {usd(f.dailyAverageLast7DaysUsd, 5)}/day (last 7 days, not counting one-off tests)
              </div>
            </div>
          </div>

          <div style={chartGridStyle}>
            <div style={panelStyle}>
              <h3 style={chartTitle}>Spend per day</h3>
              <ColumnChart
                points={spendPoints}
                series={SPEND_SERIES}
                formatValue={(v) => usd(v, 5)}
                formatTick={usdTick}
                ariaLabel={`Spend per day over the last ${data.days} days`}
                emptyMessage="No spend in this range"
              />
            </div>
            <div style={panelStyle}>
              <h3 style={chartTitle}>Tokens per day</h3>
              <ColumnChart
                points={tokenPoints}
                series={TOKEN_SERIES}
                formatValue={(v) => number(v)}
                formatTick={compact}
                ariaLabel={`Input and output tokens per day over the last ${data.days} days`}
                emptyMessage="No tokens used in this range"
              />
            </div>
          </div>

          <div style={{ margin: "10px 0 0" }}>
            <button type="button" onClick={() => setShowTable((v) => !v)} style={linkButton} aria-expanded={showTable}>
              {showTable ? "Hide the daily table" : "Show as table"}
            </button>
          </div>
          {showTable && (
            <div style={{ overflowX: "auto", marginTop: 8, maxHeight: 360 }}>
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Day</th>
                    <th style={thRight}>Calls</th>
                    <th style={thRight}>Failed</th>
                    <th style={thRight}>Input tokens</th>
                    <th style={thRight}>Output tokens</th>
                    <th style={thRight}>Spend</th>
                  </tr>
                </thead>
                <tbody>
                  {[...data.daily].reverse().map((d) => (
                    <tr key={d.date}>
                      <td style={tdStyle}>{longDate(d.date)}</td>
                      <td style={tdRight}>{number(d.calls)}</td>
                      <td style={tdRight}>{d.failedCalls}</td>
                      <td style={tdRight}>{number(d.promptTokens)}</td>
                      <td style={tdRight}>{number(d.completionTokens)}</td>
                      <td style={tdRight}>{usd(d.costUsd, 6)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <h2 style={sectionTitle}>Where the money goes</h2>
          <div style={chartGridStyle}>
            <div style={panelStyle}>
              <h3 style={chartTitle}>By model</h3>
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>Model</th>
                    <th style={thRight}>Calls</th>
                    <th style={thRight}>Tokens in/out</th>
                    <th style={thRight}>Avg / call</th>
                    <th style={thRight}>Spend</th>
                  </tr>
                </thead>
                <tbody>
                  {data.byModel.map((m) => (
                    <tr key={m.modelIdentifier}>
                      <td style={tdStyle}>
                        {m.modelIdentifier}
                        <ShareBar percent={m.sharePercent} />
                      </td>
                      <td style={tdRight}>
                        {number(m.calls)}
                        {m.failedCalls > 0 && <div style={{ ...hintStyle, color: "var(--color-danger)" }}>{m.failedCalls} failed</div>}
                      </td>
                      <td style={tdRight}>{compact(m.promptTokens)} / {compact(m.completionTokens)}</td>
                      <td style={tdRight}>{usd(m.averageCostPerCallUsd, 6)}</td>
                      <td style={tdRight}>{usd(m.costUsd)}<div style={hintStyle}>{m.sharePercent}%</div></td>
                    </tr>
                  ))}
                  {data.byModel.length === 0 && <tr><td style={tdStyle} colSpan={5}>No AI calls in this range.</td></tr>}
                </tbody>
              </table>
            </div>

            <div style={panelStyle}>
              <h3 style={chartTitle}>By purpose</h3>
              <table style={tableStyle}>
                <thead>
                  <tr>
                    <th style={thStyle}>What the call was for</th>
                    <th style={thRight}>Calls</th>
                    <th style={thRight}>Tokens</th>
                    <th style={thRight}>Spend</th>
                  </tr>
                </thead>
                <tbody>
                  {data.byPurpose.map((p) => (
                    <tr key={p.purpose}>
                      <td style={tdStyle}>
                        {purposeLabel(p.purpose)}
                        <ShareBar percent={p.sharePercent} />
                      </td>
                      <td style={tdRight}>{number(p.calls)}</td>
                      <td style={tdRight}>{compact(p.tokens)}</td>
                      <td style={tdRight}>{usd(p.costUsd)}<div style={hintStyle}>{p.sharePercent}%</div></td>
                    </tr>
                  ))}
                  {data.byPurpose.length === 0 && <tr><td style={tdStyle} colSpan={4}>No AI calls in this range.</td></tr>}
                </tbody>
              </table>
              <p style={{ ...hintStyle, margin: "10px 0 0", lineHeight: 1.5 }}>
                Only “Email classification” and “Resolution check” are running costs. Model tests and model analysis are one-off checks you started yourself.
              </p>
            </div>
          </div>

          <h2 style={sectionTitle}>What the built-in filter saves</h2>
          <div style={panelStyle}>
            <p style={{ ...hintStyle, margin: "0 0 10px", lineHeight: 1.5 }}>
              IEMAS first checks every email with its own keyword filter, which costs nothing. The AI is only asked when that filter is unsure.
              The more emails the filter settles on its own, the less you spend.
            </p>
            {e.emailsClassified === 0 ? (
              <p style={hintStyle}>No emails were classified in this range.</p>
            ) : (
              <>
                <div
                  role="img"
                  aria-label={`${e.withoutAiPercent}% of ${e.emailsClassified} emails were decided without AI`}
                  style={{ display: "flex", gap: 2, height: 8, borderRadius: 999, overflow: "hidden" }}
                >
                  {e.emailsWithoutAi > 0 && <div style={{ flex: e.emailsWithoutAi, background: "var(--series-3)" }} />}
                  {e.emailsWithAi > 0 && <div style={{ flex: e.emailsWithAi, background: "var(--series-1)" }} />}
                </div>
                <div style={{ display: "flex", gap: 20, flexWrap: "wrap", margin: "8px 0 14px", fontSize: 12, color: "var(--color-text-muted)" }}>
                  <span style={legendItem}><span aria-hidden="true" style={{ ...swatch, background: "var(--series-3)" }} />Decided by the filter, no AI: <strong style={{ color: "var(--color-text)" }}>{number(e.emailsWithoutAi)}</strong> ({e.withoutAiPercent}%)</span>
                  <span style={legendItem}><span aria-hidden="true" style={{ ...swatch, background: "var(--series-1)" }} />Needed the AI: <strong style={{ color: "var(--color-text)" }}>{number(e.emailsWithAi)}</strong></span>
                </div>
                <div style={cardGridStyle}>
                  <div className="stat-card">
                    <div className="stat-label">Emails classified</div>
                    <div className="stat-value">{number(e.emailsClassified)}</div>
                  </div>
                  <div className="stat-card">
                    <div className="stat-label">Cost per 1,000 emails</div>
                    <div className="stat-value">{usd(e.costPer1000EmailsUsd)}</div>
                    <div style={hintStyle}>all classification spend, spread over every email</div>
                  </div>
                  <div className="stat-card">
                    <div className="stat-label">Cost per AI-checked email</div>
                    <div className="stat-value">{usd(e.costPerAiEmailUsd, 6)}</div>
                    <div style={hintStyle}>including retries and fallbacks</div>
                  </div>
                  <div className="stat-card">
                    <div className="stat-label">Estimated saved</div>
                    <div className="stat-value">{usd(e.estimatedSavedUsd)}</div>
                    <div style={hintStyle}>if the filter had sent those emails to the AI</div>
                  </div>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </section>
  );
}

/** A thin share-of-total bar under a row label; the figures beside it carry the numbers. */
function ShareBar({ percent }: { percent: number }) {
  return (
    <div aria-hidden="true" style={{ height: 6, borderRadius: 999, background: "var(--color-surface-alt)", marginTop: 5, maxWidth: 260 }}>
      <div style={{ width: `${Math.min(100, Math.max(percent > 0 ? 1.5 : 0, percent))}%`, height: "100%", borderRadius: 999, background: "var(--series-1)" }} />
    </div>
  );
}

const hintStyle: React.CSSProperties = { color: "var(--color-text-muted)", fontSize: 12 };
const sectionTitle: React.CSSProperties = { fontSize: 15, fontWeight: 600, margin: "28px 0 12px" };
const chartTitle: React.CSSProperties = { fontSize: 14, fontWeight: 600, margin: "0 0 10px" };
const cardGridStyle: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(190px, 1fr))", gap: 12 };
const chartGridStyle: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(440px, 1fr))", gap: 14, marginTop: 14 };
const panelStyle: React.CSSProperties = { border: "1px solid var(--color-border)", borderRadius: 8, padding: 14, background: "var(--color-surface)", minWidth: 0 };
const linkButton: React.CSSProperties = { background: "transparent", border: "none", padding: 0, cursor: "pointer", color: "var(--color-link)", fontSize: 13 };
const legendItem: React.CSSProperties = { display: "inline-flex", alignItems: "center", gap: 6 };
const swatch: React.CSSProperties = { width: 10, height: 10, borderRadius: 2, display: "inline-block" };
const tableStyle: React.CSSProperties = { width: "100%", borderCollapse: "separate", borderSpacing: 0 };
const thStyle: React.CSSProperties = { textAlign: "left" };
const thRight: React.CSSProperties = { ...thStyle, textAlign: "right", paddingLeft: 14, whiteSpace: "nowrap" };
const tdStyle: React.CSSProperties = { verticalAlign: "top" };
const tdRight: React.CSSProperties = { ...tdStyle, textAlign: "right", whiteSpace: "nowrap", paddingLeft: 14 };

